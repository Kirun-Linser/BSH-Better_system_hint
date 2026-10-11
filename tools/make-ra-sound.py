"""BSH · 红警风格"建筑完工"提示音（第四批：FM 合成）

用户给的锚点：「红警建筑制造好了那种」。

锚点分析（决定了这批的技术路线）：
  · 红警（Westwood）那代音效的招牌是 **FM 合成** —— 载波被一个**非整数比例**的
    调制波调相，产生金属/钟琴般的丰富边带。前几批用"正弦 + 二次谐波"，
    谐波结构太干净，所以怎么调都不像。
  · 建筑完工音的特征：**快起音（约 5ms）+ 指数衰减 + 短混响**，
    音高偏中高（约 0.8–1.6 kHz），听感是"事情办妥了"的上扬或收束。

本脚本用 FM + 调制指数衰减（音色从"金属"渐变到"纯净"）+ 三抽头短混响，
做 4 种变体。输出：D:\\O级项目管理\\BSH\\voice\\ra\\

用法：python make-ra-sound.py
"""

from __future__ import annotations

import math
import wave
from pathlib import Path

import numpy as np

SR = 22050
OUT = Path(r"D:\O级项目管理\BSH\voice\ra")
PEAK = 0.85


def fm_tone(fc: float, radio: float, index: float, dur: float,
            decay: float, attack: float = 0.005, vib: float = 0.0) -> np.ndarray:
    """FM 单音。

    fc      载波频率
    radio   调制/载波频率比（非整数 → 金属感；整数 → 乐音感）
    index   调制指数（越大边带越多越"亮"）
    decay   指数衰减时间常数（秒）
    vib     颤音深度（0 = 无）
    """
    n = int(SR * dur)
    t = np.arange(n) / SR
    fm = fc * radio

    mod_index = index * np.exp(-t / max(1e-6, decay * 0.30))     # 调制指数也衰减 → 音色渐变
    vib_term = vib * np.sin(2 * math.pi * 5.5 * t) if vib else 0.0
    phase = 2 * math.pi * fc * t + mod_index * np.sin(2 * math.pi * fm * t) + vib_term
    y = np.sin(phase)

    env = np.exp(-t / max(1e-6, decay))
    a = min(n, max(1, int(SR * attack)))
    env[:a] *= np.linspace(0, 1, a) ** 1.5                        # 快起音但不"啪"
    return y * env


def reverb(x: np.ndarray, taps=((0.031, 0.32), (0.057, 0.20), (0.089, 0.12))) -> np.ndarray:
    """三抽头短混响 —— 控制台 / 舱室内那种轻微空间感。"""
    y = x.copy()
    for d, g in taps:
        k = int(SR * d)
        if 0 < k < len(x):
            y[k:] += x[: len(x) - k] * g
    return y


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


def gap(dur: float) -> np.ndarray:
    return np.zeros(int(SR * dur))


# ───────────────────────── 4 种变体 ─────────────────────────

def ra1_double_up() -> np.ndarray:
    """两音上行：784 → 1175 Hz。经典"完成"的上扬结构。"""
    a = fm_tone(784.0, radio=3.5, index=2.6, dur=0.20, decay=0.13)
    b = fm_tone(1174.7, radio=3.5, index=2.6, dur=0.34, decay=0.24)
    return np.concatenate([a, gap(0.035), b])


def ra2_single_bell() -> np.ndarray:
    """单音钟琴：1046.5 Hz，金属比例 1.414，长衰减。"""
    return fm_tone(1046.5, radio=1.414, index=4.2, dur=0.60, decay=0.26)


def ra3_triple_confirm() -> np.ndarray:
    """三连确认：988 → 1175 → 1568 Hz，快节奏收束。"""
    parts = []
    for fc, d, dc in ((987.8, 0.13, 0.075), (1174.7, 0.13, 0.075), (1568.0, 0.26, 0.16)):
        parts.append(fm_tone(fc, radio=2.0, index=2.0, dur=d, decay=dc))
        parts.append(gap(0.045))
    return np.concatenate(parts[:-1])


def ra4_eva_chirp() -> np.ndarray:
    """EVA 式电子提示：带颤音的 FM 音 + 轻微上滑。"""
    n = int(SR * 0.40)
    t = np.arange(n) / SR
    fc = 880.0 + 320.0 * (t / t[-1])                       # 轻微上滑 880 → 1200
    mod_index = 3.0 * np.exp(-t / 0.09)
    phase = 2 * math.pi * np.cumsum(fc) / SR + mod_index * np.sin(2 * math.pi * fc * 2.2 * t)
    y = np.sin(phase)
    env = np.exp(-t / 0.30)
    a = int(SR * 0.006)
    env[:a] *= np.linspace(0, 1, a) ** 1.5
    return y * env


DESIGNS = {
    "RA-01-Double-Up":       ra1_double_up,
    "RA-02-Single-Bell":     ra2_single_bell,
    "RA-03-Triple-Confirm":  ra3_triple_confirm,
    "RA-04-EVA-Chirp":       ra4_eva_chirp,
}


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    for f in OUT.glob("*.wav"):
        f.unlink()

    for name, fn in DESIGNS.items():
        x = fn()
        x = lowpass(x, 6000.0)          # 只削掉最刺耳的一段，保留 FM 的亮度
        x = reverb(x)
        p = float(np.max(np.abs(x))) or 1.0
        x = np.clip(x / p * PEAK, -1, 1)
        with wave.open(str(OUT / f"{name}.wav"), "wb") as w:
            w.setnchannels(1)
            w.setsampwidth(2)
            w.setframerate(SR)
            w.writeframes((x * 32767).astype("<i2").tobytes())
        print(f"  {name:<22} {len(x)/SR:.2f}s  峰值 {np.max(np.abs(x)):.2f}")

    print(f"\n共 {len(DESIGNS)} 个 → {OUT}")
    print("技术：FM 合成（非整数调制比 → 金属边带）+ 调制指数衰减 + 三抽头短混响")


if __name__ == "__main__":
    main()
