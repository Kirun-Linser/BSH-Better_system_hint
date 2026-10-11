"""BSH · EVA 风格语音处理（把播报语音做成红警 EVA 那种"基地广播"质感）

用户锚点链：工业背景音 → 红警建筑完工 → 红警基地被攻击 → **「EVA 语音」**
结论：用户要的不是外挂音效，而是**语音本身的味道**。

红警 EVA 的听感拆解成五个可执行的技术动作：
  1. **降调** —— Zira 偏亮偏年轻，EVA 更沉、更"冷"。用重采样降 1.5–3.5 个半音
     （代价：时长按同比例变长约 9–22%，对 2–3 秒的播报可接受）
  2. **广播 EQ** —— 带通 250–5000 Hz，模拟基地扬声器；低频轰鸣与齿音都削掉
  3. **轻微饱和** —— tanh 软限幅，模拟扬声器非线性（这是"电子感"的来源）
  4. **短混响** —— 三抽头 20–80ms，像在舱室/走廊里播报
  5. **极轻底噪** —— 低通白噪 -55 dB 左右，去掉"数字静音"的干涩

输出：D:\\O级项目管理\\BSH\\voice\\eva\\（两句话 × 轻/中/重 + 原始对照）

用法：python make-eva-voice.py
"""

from __future__ import annotations

import math
import wave
from pathlib import Path

import numpy as np

SR = 22050
VOICE = Path(r"D:\O级项目管理\BSH\voice\out")
EVA = Path(r"D:\O级项目管理\BSH\voice\ra-alert\eva-line.wav")
OUT = Path(r"D:\O级项目管理\BSH\voice\eva")
RNG = np.random.default_rng(20261009)

# 三档：(降调半音, 饱和驱动, 混响混合, 底噪电平)
PRESETS = {
    "Light": dict(semitones=-1.5, drive=1.15, rev=0.12, noise=0.0010),
    "Mid":   dict(semitones=-2.5, drive=1.35, rev=0.20, noise=0.0020),
    "Heavy": dict(semitones=-3.5, drive=1.60, rev=0.30, noise=0.0030),
}


def read_wav(p: Path) -> tuple[np.ndarray, int]:
    with wave.open(str(p), "rb") as w:
        sr = w.getframerate()
        x = np.frombuffer(w.readframes(w.getnframes()), dtype="<i2").astype(np.float64) / 32768.0
        if w.getnchannels() > 1:
            x = x.reshape(-1, w.getnchannels()).mean(axis=1)
    return x, sr


def write_wav(p: Path, x: np.ndarray, sr: int) -> None:
    p.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(p), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(sr)
        w.writeframes((np.clip(x, -1, 1) * 32767).astype("<i2").tobytes())


def pitch_shift(x: np.ndarray, semitones: float) -> np.ndarray:
    """重采样降调（音高与时长同比例变化）。semitones<0 → 更低更慢。"""
    factor = 2.0 ** (semitones / 12.0)
    n_out = int(len(x) / factor)
    idx = np.linspace(0, len(x) - 1, n_out)
    return np.interp(idx, np.arange(len(x)), x)


def biquad(x: np.ndarray, freq: float, kind: str, q: float = 0.707) -> np.ndarray:
    w0 = 2.0 * math.pi * freq / SR
    cw, sw = math.cos(w0), math.sin(w0)
    alpha = sw / (2.0 * q)
    if kind == "lp":
        b0, b1, b2 = (1 - cw) / 2, 1 - cw, (1 - cw) / 2
    else:
        b0, b1, b2 = (1 + cw) / 2, -(1 + cw), (1 + cw) / 2
    a0, a1, a2 = 1 + alpha, -2 * cw, 1 - alpha
    b0, b1, b2, a1, a2 = b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0
    y = np.empty_like(x)
    x1 = x2 = y1 = y2 = 0.0
    for i, xi in enumerate(x):
        yi = b0 * xi + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2
        x2, x1 = x1, xi
        y2, y1 = y1, yi
        y[i] = yi
    return y


def broadcast_eq(x: np.ndarray) -> np.ndarray:
    """基地扬声器：250–5000 Hz 带通。"""
    return biquad(biquad(x, 250.0, "hp"), 5000.0, "lp")


def saturate(x: np.ndarray, drive: float) -> np.ndarray:
    """tanh 软饱和 —— 电子感/扬声器非线性。"""
    return np.tanh(x * drive) / math.tanh(drive)


def short_reverb(x: np.ndarray, mix: float,
                 taps=((0.021, 0.55), (0.047, 0.32), (0.079, 0.20))) -> np.ndarray:
    """三抽头短混响，按 mix 与原声混合。"""
    wet = np.zeros(len(x) + int(SR * 0.12))
    wet[: len(x)] += x
    for d, g in taps:
        k = int(SR * d)
        wet[k:k + len(x)] += x * g
    wet = wet[: len(x)]
    return (1.0 - mix) * x + mix * wet


def add_noise_floor(x: np.ndarray, level: float) -> np.ndarray:
    n = biquad(RNG.normal(0, 1, len(x)), 3000.0, "lp")
    return x + n / (np.max(np.abs(n)) or 1.0) * level


def eva_ize(x: np.ndarray, p: dict) -> np.ndarray:
    y = pitch_shift(x, p["semitones"])
    y = broadcast_eq(y)
    y = saturate(y, p["drive"])
    y = short_reverb(y, p["rev"])
    y = add_noise_floor(y, p["noise"])
    peak = float(np.max(np.abs(y))) or 1.0
    return y / peak * 0.88


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    for f in OUT.glob("*.wav"):
        f.unlink()

    sources = {
        "Line-BaseUnderAttack": EVA if EVA.exists() else None,
        "Line-DeviceConnected": VOICE / "DeviceConnect.wav",
    }

    for tag, path in sources.items():
        if path is None or not path.exists():
            print(f"  跳过 {tag}（源文件不存在：{path}）")
            continue
        x, sr = read_wav(path)
        x = x / (float(np.max(np.abs(x))) or 1.0) * 0.88
        write_wav(OUT / f"{tag}__00-Original.wav", x, sr)          # 对照
        print(f"  {tag}__00-Original       {len(x)/sr:.2f}s")
        for name, preset in PRESETS.items():
            y = eva_ize(x, preset)
            write_wav(OUT / f"{tag}__{name}.wav", y, sr)
            print(f"  {tag}__{name:<18} {len(y)/sr:.2f}s  "
                  f"(降调 {preset['semitones']} 半音 / 饱和 {preset['drive']} / 混响 {preset['rev']})")

    print(f"\n→ {OUT}")


if __name__ == "__main__":
    main()
