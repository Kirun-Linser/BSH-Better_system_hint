"""BSH · 红警「基地被攻击」警报音（第五批）

用户锚点演进：
  第三批"都不行" → 第四批给了「红警建筑制造好了那种」（上扬、完成感）→ 仍不对
  → 最终锚点：「**红警基地被攻击那种**」

所以方向从"完成提示"整个翻转为"**警报**"：
  · 急促、重复的短音（2–4 声，间隔很短），不是单发长音
  · 硬起音（约 3ms）—— 警报要抓注意力，不能柔和
  · 音色比纯正弦粗（叠 3、5 次谐波），有"电子警报器"的毛边
  · 中高音区（0.9–1.4 kHz），但用低通把 >6 kHz 的齿感压掉，避免"尖锐"
  · 轻微短混响 —— 像基地广播系统传出来的

另附一个**组合版**：警报音 + EVA 语音「Base under attack」
（EVA 语音由 make-eva-line.ps1 用 SAPI Zira 先生成到 eva-line.wav；
 若该文件不存在，脚本自动跳过组合版。）

输出：D:\\O级项目管理\\BSH\\voice\\ra-alert\\

用法：python make-ra-alert.py
"""

from __future__ import annotations

import math
import wave
from pathlib import Path

import numpy as np

SR = 22050
OUT = Path(r"D:\O级项目管理\BSH\voice\ra-alert")
EVA = OUT / "eva-line.wav"
PEAK = 0.85


def alarm_tone(fc: float, dur: float, attack: float = 0.003, release: float = 0.045) -> np.ndarray:
    """警报单音：叠 3、5 次谐波做出"电子警报器"的毛边，硬起快落。"""
    n = int(SR * dur)
    t = np.arange(n) / SR
    w = (np.sin(2 * math.pi * fc * t)
         + 0.35 * np.sin(4 * math.pi * fc * t)
         + 0.18 * np.sin(6 * math.pi * fc * t))
    env = np.ones(n)
    a = min(n, max(1, int(SR * attack)))
    r = min(n - a, max(1, int(SR * release)))
    env[:a] = np.linspace(0, 1, a) ** 0.7
    if r:
        env[n - r:] = np.linspace(1, 0, r) ** 1.4
    return w * env


def sweep_down(f0: float, f1: float, dur: float) -> np.ndarray:
    """下行滑音（警报"坠落"感）。"""
    n = int(SR * dur)
    t = np.arange(n) / SR
    k = (f1 - f0) / dur
    phase = 2 * math.pi * (f0 * t + 0.5 * k * t * t)
    w = np.sin(phase) + 0.30 * np.sin(2 * phase)
    env = np.ones(n)
    a = max(1, int(SR * 0.004))
    env[:a] = np.linspace(0, 1, a) ** 0.7
    env[-int(SR * 0.06):] *= np.linspace(1, 0, int(SR * 0.06))
    return w * env


def gap(dur: float) -> np.ndarray:
    return np.zeros(int(SR * dur))


def lowpass(x: np.ndarray, freq: float, q: float = 0.707) -> np.ndarray:
    w0 = 2.0 * math.pi * freq / SR
    cw, sw = math.cos(w0), math.sin(w0)
    alpha = sw / (2.0 * q)
    b0, b1, b2 = (1 - cw) / 2, 1 - cw, (1 - cw) / 2
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


def reverb(x: np.ndarray, taps=((0.027, 0.28), (0.051, 0.17), (0.083, 0.10))) -> np.ndarray:
    y = x.copy()
    for d, g in taps:
        k = int(SR * d)
        if 0 < k < len(x):
            y[k:] += x[: len(x) - k] * g
    return y


# ───────────────────────── 5 种 ─────────────────────────

def al1_double_urgent() -> np.ndarray:
    """两声急促：1319 Hz × 2，间隔 65ms —— 最典型的"紧急通报"。"""
    return np.concatenate([alarm_tone(1318.5, 0.115), gap(0.065), alarm_tone(1318.5, 0.150)])


def al2_triple_urgent() -> np.ndarray:
    """三声连击：1175 Hz × 3，间隔 50ms。"""
    parts = []
    for i in range(3):
        parts.append(alarm_tone(1174.7, 0.085))
        if i < 2:
            parts.append(gap(0.05))
    return np.concatenate(parts)


def al3_down_sweep() -> np.ndarray:
    """下行警报：1400 → 880 Hz 下滑。"""
    return sweep_down(1400.0, 880.0, 0.38)


def al4_two_tone_siren() -> np.ndarray:
    """双音交替警笛：988 / 1319 交替 4 声。"""
    parts = []
    for i, fc in enumerate((987.8, 1318.5, 987.8, 1318.5)):
        parts.append(alarm_tone(fc, 0.10))
        if i < 3:
            parts.append(gap(0.055))
    return np.concatenate(parts)


def al5_with_eva() -> np.ndarray | None:
    """警报音 + EVA 语音「Base under attack」。"""
    if not EVA.exists():
        return None
    with wave.open(str(EVA), "rb") as w:
        sr = w.getframerate()
        line = np.frombuffer(w.readframes(w.getnframes()), dtype="<i2").astype(np.float64) / 32768.0
    if sr != SR:                                    # 简单线性重采样到 22050
        idx = np.linspace(0, len(line) - 1, int(len(line) * SR / sr))
        line = np.interp(idx, np.arange(len(line)), line)
    line = line / (float(np.max(np.abs(line))) or 1.0) * 0.80
    head = al1_double_urgent()
    return np.concatenate([head, gap(0.06), line])


DESIGNS = {
    "Alarm-01-Double-Urgent":  al1_double_urgent,
    "Alarm-02-Triple-Urgent":  al2_triple_urgent,
    "Alarm-03-Down-Sweep":     al3_down_sweep,
    "Alarm-04-Two-Tone-Siren": al4_two_tone_siren,
}


def save(name: str, x: np.ndarray) -> None:
    x = lowpass(x, 5200.0)          # 压掉最刺耳的一段，保留警报的"毛边"
    x = reverb(x)
    p = float(np.max(np.abs(x))) or 1.0
    x = np.clip(x / p * PEAK, -1, 1)
    with wave.open(str(OUT / f"{name}.wav"), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes((x * 32767).astype("<i2").tobytes())
    print(f"  {name:<26} {len(x)/SR:.2f}s  峰值 {np.max(np.abs(x)):.2f}")


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    for f in OUT.glob("Alarm-*.wav"):
        f.unlink()

    for name, fn in DESIGNS.items():
        save(name, fn())

    combo = al5_with_eva()
    if combo is None:
        print("  （跳过 Alarm-05：还没有 eva-line.wav，先跑 make-eva-line.ps1）")
    else:
        save("Alarm-05-With-EVA-Voice", combo)

    print(f"\n→ {OUT}")


if __name__ == "__main__":
    main()
