"""BSH · 混合两个 Edge 神经音色（用户要求：选项 1 与 4 的混合）

  选项 1 = Aria 原调（F0≈237Hz，明亮清晰）
  选项 4 = Michelle 降 10Hz（F0≈200Hz，成熟沉稳）

**为什么不用简单的时域相加**：两者基频差约 37 Hz（≈3 个半音），直接相加会形成
两个独立声源，听感是"两个人同时念同一句话"，而不是一种新音色。

**改用 STFT 幅度谱混合**：
  · 保留 Aria 的**相位** → 音高、语调、节奏全部沿用 Aria（不会被拉出双音）
  · 幅度谱按权重融合 → 频谱包络是两者的加权平均，即"音色"被混合
这样得到的是**单一音色**，而不是两个声音的叠加。

输出：voice/edge-mix/（相对仓库根）（50/50、65/35 偏 Aria、35/65 偏 Michelle）

用法：python mix-edge-voices.py
"""

from __future__ import annotations

import glob
import os
import wave
from pathlib import Path

import numpy as np

SRC = Path(__file__).resolve().parent.parent / "voice" / "edge-tts"
OUT = Path(__file__).resolve().parent.parent / "voice" / "edge-mix"
FFT = 1024
HOP = 256
WEIGHTS = {"Mix-50-50": 0.50, "Mix-65A-35D": 0.65, "Mix-35A-65D": 0.35}


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


def trim(x: np.ndarray, sr: int, thr: float = 0.02) -> np.ndarray:
    """掐掉首尾静音，便于对齐。"""
    env = np.abs(x)
    idx = np.where(env > thr * env.max())[0]
    return x[idx[0]:idx[-1]] if len(idx) > 100 else x


def stft(x: np.ndarray) -> np.ndarray:
    win = np.hanning(FFT)
    n = 1 + max(0, (len(x) - FFT) // HOP)
    out = np.empty((n, FFT // 2 + 1), dtype=np.complex128)
    for i in range(n):
        out[i] = np.fft.rfft(x[i * HOP: i * HOP + FFT] * win)
    return out


def istft(spec: np.ndarray, length: int) -> np.ndarray:
    win = np.hanning(FFT)
    y = np.zeros(length + FFT)
    wsum = np.zeros(length + FFT)
    for i in range(spec.shape[0]):
        seg = np.fft.irfft(spec[i], n=FFT) * win
        y[i * HOP: i * HOP + FFT] += seg
        wsum[i * HOP: i * HOP + FFT] += win ** 2
    # ⚠️ 边缘绝不能用 1e-6 兜底做除法：那里窗和趋近 0，浮点残差除以极小数会炸出巨大尖峰
    # （唯一实测：混音 y 的 min=-31.64，而语音本体只有 ±1.75），随后"按峰值归一化"
    # 会把整段语音压成 1/37 —— 听起来近乎静音，峰值却显示 0.85，极具迷惑性。
    # 与 make-edge-batch.py 同一处修复：只在窗和足够大的位置归一化。
    out = np.zeros(length)
    w = wsum[:length]
    valid = w > 0.05 * (w.max() or 1.0)
    out[valid] = y[:length][valid] / w[valid]
    return out


def main() -> None:
    a_path = SRC / "A-Aria-p0.wav"
    d_path = SRC / "D-Michelle-m10.wav"
    if not a_path.exists() or not d_path.exists():
        hits = sorted(glob.glob(str(SRC / "*.wav")))
        raise SystemExit("缺少源文件：\n  " + str(a_path) + f"\n  {d_path}\n现有：" + ", ".join(os.path.basename(h) for h in hits))

    a, sr = read_wav(a_path)
    d, _ = read_wav(d_path)
    a, d = trim(a, sr), trim(d, sr)
    print(f"Aria     {len(a)/sr:.2f}s   Michelle {len(d)/sr:.2f}s")

    sa, sd = stft(a), stft(d)
    n = min(sa.shape[0], sd.shape[0])          # 帧数取短的
    sa, sd = sa[:n], sd[:n]

    mag_a, phase_a = np.abs(sa), np.angle(sa)
    mag_d = np.abs(sd)
    length = min(len(a), len(d))

    OUT.mkdir(parents=True, exist_ok=True)
    for f in OUT.glob("*.wav"):
        f.unlink()

    # 先存一份"纯 Aria（trim 后）"作为对照
    y0 = istft(mag_a * np.exp(1j * phase_a), length)
    y0 = y0 / (np.max(np.abs(y0)) or 1) * 0.85
    write_wav(OUT / "00-Aria-only.wav", y0, sr)
    print(f"  00-Aria-only         {len(y0)/sr:.2f}s")

    for name, w in WEIGHTS.items():
        mag = w * mag_a + (1.0 - w) * mag_d
        y = istft(mag * np.exp(1j * phase_a), length)
        y = y / (np.max(np.abs(y)) or 1) * 0.85
        write_wav(OUT / f"{name}.wav", y, sr)
        print(f"  {name:<20} {len(y)/sr:.2f}s  (Aria {w:.0%} / Michelle {1-w:.0%})")

    print(f"\n→ {OUT}")


if __name__ == "__main__":
    main()
