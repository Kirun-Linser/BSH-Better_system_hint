"""BSH · 柔和工业提示音（第二批设计）

为什么重做：第一批（voice/industrial/）用户反馈"太尖锐、太短"。根因有三条，全部改掉：
  1. 基频都在 1046–1570 Hz（高频区，听感"嘀嘀"刺耳）
     → 新版整体下沉一个八度，基频落在 200–550 Hz
  2. 每个音叠了 0.25 倍二次谐波（更亮更尖）
     → 新版只叠 0.15 倍
  3. 起音只有 4ms（硬起，瞬态尖）
     → 新版起音 15–25ms、释放 80–150ms，并整体过一遍 2.5 kHz 低通
时长也从 0.10–0.36 秒延长到 0.40–0.58 秒。

输出：D:\\O级项目管理\\BSH\\voice\\industrial-soft\\*.wav（22050 Hz / 16-bit / 单声道，峰值 0.85）

用法：python make-industrial-soft.py
"""

from __future__ import annotations

import math
import wave
from pathlib import Path

import numpy as np

SR = 22050
OUT = Path(r"D:\O级项目管理\BSH\voice\industrial-soft")
PEAK = 0.85
LP_HZ = 2500.0          # 低通：削掉刺耳的高频
HARM = 0.15             # 二次谐波比例（第一批是 0.25）


# ───────────────────────── 基础工具 ─────────────────────────

def biquad_lp(x: np.ndarray, sr: int, freq: float, q: float = 0.707) -> np.ndarray:
    """RBJ 二阶低通（手写，环境无 scipy）。"""
    w0 = 2.0 * math.pi * freq / sr
    cos_w0, sin_w0 = math.cos(w0), math.sin(w0)
    alpha = sin_w0 / (2.0 * q)
    b0, b1, b2 = (1 - cos_w0) / 2, 1 - cos_w0, (1 - cos_w0) / 2
    a0, a1, a2 = 1 + alpha, -2 * cos_w0, 1 - alpha
    b0, b1, b2, a1, a2 = b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0

    y = np.empty_like(x)
    x1 = x2 = y1 = y2 = 0.0
    for i, xi in enumerate(x):
        yi = b0 * xi + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2
        x2, x1 = x1, xi
        y2, y1 = y1, yi
        y[i] = yi
    return y


def ar_env(n: int, sr: int, attack: float, release: float) -> np.ndarray:
    """慢起音 + 平滑释放的包络（比 4ms 硬起柔和得多）。"""
    env = np.ones(n)
    a = max(1, int(sr * attack))
    r = max(1, int(sr * release))
    a = min(a, n)
    r = min(r, n - a) if n - a > 1 else 0
    if a > 0:
        env[:a] = np.sin(np.linspace(0, math.pi / 2, a)) ** 2      # 平方正弦起音，无拐点
    if r > 0:
        env[n - r:] = np.cos(np.linspace(0, math.pi / 2, r)) ** 2
    return env


def tone(freq: float, dur: float, attack: float = 0.02, release: float = 0.10,
         harm: float = HARM) -> np.ndarray:
    """柔和单音：基频 + 一点二次谐波，慢起音。"""
    n = int(SR * dur)
    t = np.arange(n) / SR
    w = np.sin(2 * math.pi * freq * t) + harm * np.sin(4 * math.pi * freq * t)
    return w * ar_env(n, SR, attack, release)


def buzz(freq: float, dur: float, attack: float = 0.02, release: float = 0.10) -> np.ndarray:
    """蜂鸣器感：方波（奇次谐波），靠低通把棱角磨圆，听感"闷响"而非"尖叫"。"""
    n = int(SR * dur)
    t = np.arange(n) / SR
    w = np.sign(np.sin(2 * math.pi * freq * t)) * 0.6
    return w * ar_env(n, SR, attack, release)


def sweep(f0: float, f1: float, dur: float) -> np.ndarray:
    """线性扫频（相位积分，避免相位跳变）。"""
    n = int(SR * dur)
    t = np.arange(n) / SR
    k = (f1 - f0) / dur
    phase = 2 * math.pi * (f0 * t + 0.5 * k * t * t)
    w = np.sin(phase) + HARM * np.sin(2 * phase)
    return w * ar_env(n, SR, 0.03, 0.15)


def silence(dur: float) -> np.ndarray:
    return np.zeros(int(SR * dur))


def click(dur: float = 0.006) -> np.ndarray:
    """机械咔哒：极短噪声脉冲，带汉宁窗（柔和版，不是硬切）。"""
    n = int(SR * dur)
    rng = np.random.default_rng(20261009)
    noise = rng.normal(0, 1, n)
    return noise * np.hanning(n)


# ───────────────────────── 8 种设计 ─────────────────────────

def d01() -> np.ndarray:
    """低沉双音：392 → 523 Hz。"""
    return np.concatenate([tone(392, 0.16, release=0.07), silence(0.04),
                           tone(523, 0.20, release=0.09)])


def d02() -> np.ndarray:
    """闷响单音：330 Hz 长衰减，像远处设备"咚"一声。"""
    return tone(330, 0.40, attack=0.025, release=0.22)


def d03() -> np.ndarray:
    """低音递升三连：262 → 330 → 392 Hz。"""
    return np.concatenate([tone(262, 0.12, release=0.05), silence(0.03),
                           tone(330, 0.12, release=0.05), silence(0.03),
                           tone(392, 0.14, release=0.06)])


def d04() -> np.ndarray:
    """极闷双音：196 → 262 Hz，低八度，厚重。"""
    return np.concatenate([tone(196, 0.22, attack=0.03, release=0.12), silence(0.05),
                           tone(262, 0.22, attack=0.03, release=0.12)])


def d05() -> np.ndarray:
    """柔和扫频：250 → 700 Hz（不再扫到 1.4k）。"""
    return sweep(250.0, 700.0, 0.45)


def d06() -> np.ndarray:
    """蜂鸣器：220 Hz 方波 + 低通，工业机柜那种闷响。"""
    return buzz(220.0, 0.35, attack=0.03, release=0.14)


def d07() -> np.ndarray:
    """柔和警报：349 / 440 Hz 交替三声（比第一批的 740/990 低一个八度多）。"""
    return np.concatenate([tone(349, 0.16, release=0.07), silence(0.05),
                           tone(440, 0.16, release=0.07), silence(0.05),
                           tone(349, 0.18, release=0.09)])


def d08() -> np.ndarray:
    """机械三连脉冲：294 Hz 短脉冲 + 咔哒起手。"""
    return np.concatenate([click(), silence(0.012),
                           tone(294, 0.09, attack=0.012, release=0.04), silence(0.07),
                           tone(294, 0.09, attack=0.012, release=0.04), silence(0.07),
                           tone(294, 0.11, attack=0.012, release=0.05)])


DESIGNS = {
    "Soft-01-Double-Low":  d01,
    "Soft-02-Deep-Ding":   d02,
    "Soft-03-Low-Rise":    d03,
    "Soft-04-Very-Low":    d04,
    "Soft-05-Gentle-Sweep": d05,
    "Soft-06-Buzzer":      d06,
    "Soft-07-Soft-Alert":  d07,
    "Soft-08-Mech-Pulse":  d08,
}


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    for old in OUT.glob("*.wav"):
        old.unlink()

    for name, fn in DESIGNS.items():
        x = fn()
        x = biquad_lp(x, SR, LP_HZ)                 # 统一过低通，削掉刺耳高频
        peak = float(np.max(np.abs(x))) or 1.0
        x = x / peak * PEAK
        pcm = (np.clip(x, -1.0, 1.0) * 32767).astype("<i2")
        path = OUT / f"{name}.wav"
        with wave.open(str(path), "wb") as w:
            w.setnchannels(1)
            w.setsampwidth(2)
            w.setframerate(SR)
            w.writeframes(pcm.tobytes())
        print(f"  {name:<22} {len(x)/SR:.2f}s  峰值 {np.max(np.abs(x)):.2f}")

    print(f"\n共 {len(DESIGNS)} 个 → {OUT}")


if __name__ == "__main__":
    main()
