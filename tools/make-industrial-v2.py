"""BSH · 工业音第三批：把两种理解各做几个，让用户指认方向

前两批都做成"起手提示音"（语音前的一小段电子音），被否两次。回头看用户原话是
「语音中再加一点点工业化**背景音**」—— 可能指的是**铺在语音下面的持续背景层**，
而不是起手音。这轮两种都做，一次听完定方向：

  理解 A（起手音）：语音开始前播一小段工业提示音 —— 4 种材质
      A1 金属敲击 / A2 气动排气 / A3 继电器咔哒 / A4 工业铃
  理解 B（背景层）：语音播报全程垫一层很轻的工业环境音 —— 3 种
      B1 机器低鸣 / B2 厂房环境噪 / B3 低鸣+节律咔哒

输出：D:\\O级项目管理\\BSH\\voice\\industrial-v2\\
B 组直接叠在真实语音（out\\DeviceConnect.wav）上，方便判断"能不能和语音共存"。

用法：python make-industrial-v2.py
"""

from __future__ import annotations

import math
import wave
from pathlib import Path

import numpy as np

SR = 22050
ROOT = Path(r"D:\O级项目管理\BSH\voice")
OUT = ROOT / "industrial-v2"
VOICE = ROOT / "out" / "DeviceConnect.wav"
RNG = np.random.default_rng(20261009)


# ───────────────────────── 基础 ─────────────────────────

def biquad(x: np.ndarray, sr: int, freq: float, kind: str, q: float = 0.707) -> np.ndarray:
    w0 = 2.0 * math.pi * freq / sr
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


def modal(x: np.ndarray, sr: int, freq: float, decay: float) -> np.ndarray:
    """二阶模态谐振器 —— 用来模拟金属体的固有振动（敲击声的"当"）。"""
    w0 = 2.0 * math.pi * freq / sr
    r = math.exp(-1.0 / max(1e-6, decay * sr))
    a1, a2 = -2.0 * r * math.cos(w0), r * r
    y = np.zeros_like(x)
    y1 = y2 = 0.0
    for i in range(len(x)):
        yi = x[i] - a1 * y1 - a2 * y2
        y2, y1 = y1, yi
        y[i] = yi
    return y * (1.0 - r) * 4.0


def env_ar(n: int, sr: int, attack: float, release: float, curve: float = 1.0) -> np.ndarray:
    e = np.ones(n)
    a = min(n, max(1, int(sr * attack)))
    r = min(n - a, max(0, int(sr * release)))
    if a:
        e[:a] = np.linspace(0, 1, a) ** (1.0 / max(curve, 1e-3))
    if r:
        e[n - r:] = np.linspace(1, 0, r) ** curve
    return e


def impulse(n: int, width: int = 24) -> np.ndarray:
    x = np.zeros(n)
    w = min(width, n)
    x[:w] = RNG.normal(0, 1, w) * np.hanning(w)
    return x


def save(name: str, x: np.ndarray, peak: float = 0.85) -> None:
    p = float(np.max(np.abs(x))) or 1.0
    x = np.clip(x / p * peak, -1, 1)
    with wave.open(str(OUT / f"{name}.wav"), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes((x * 32767).astype("<i2").tobytes())
    print(f"  {name:<22} {len(x)/SR:.2f}s")


# ─────────────────── 理解 A：起手音（材质向） ───────────────────

def a1_metal_clank() -> np.ndarray:
    """金属敲击：噪声脉冲同时激励 3 个金属模态（不规则比例 → 像铁块而不是乐音）。"""
    n = int(SR * 0.35)
    ex = impulse(n, 40)
    modes = [(430.0, 0.10), (960.0, 0.07), (1730.0, 0.045)]
    y = sum(modal(ex, SR, f, d) for f, d in modes)
    return y * env_ar(n, SR, 0.0005, 0.30, 1.6)


def a2_air_release() -> np.ndarray:
    """气动排气：带通白噪声，"嗤——"一声。"""
    n = int(SR * 0.45)
    noise = RNG.normal(0, 1, n)
    y = biquad(noise, SR, 3200.0, "lp")
    y = biquad(y, SR, 900.0, "hp")
    return y * env_ar(n, SR, 0.012, 0.42, 1.8)


def a3_relay_click() -> np.ndarray:
    """继电器：两声干脆的咔哒（第二声略轻）。"""
    def one(gain: float) -> np.ndarray:
        n = int(SR * 0.05)
        y = modal(impulse(n, 20), SR, 1500.0, 0.006) + impulse(n, 10) * 0.6
        return y * gain
    return np.concatenate([one(1.0), np.zeros(int(SR * 0.06)), one(0.7)])


def a4_bell() -> np.ndarray:
    """工业铃：金属模态长衰减，"叮——"。"""
    n = int(SR * 0.80)
    ex = impulse(n, 60)
    y = sum(modal(ex, SR, f, d) for f, d in [(660.0, 0.55), (1320.0, 0.35), (1980.0, 0.22)])
    return y * env_ar(n, SR, 0.001, 0.75, 2.2)


# ─────────────────── 理解 B：背景层（铺在语音下） ───────────────────

def load_voice() -> np.ndarray:
    if not VOICE.exists():
        raise SystemExit(f"找不到语音样本：{VOICE}")
    with wave.open(str(VOICE), "rb") as w:
        return np.frombuffer(w.readframes(w.getnframes()), dtype="<i2").astype(np.float64) / 32768.0


def hum_layer(n: int, gain: float = 0.05) -> np.ndarray:
    """机器低鸣：55Hz + 110Hz + 缓慢起伏（0.3Hz LFO）。"""
    t = np.arange(n) / SR
    lfo = 1.0 + 0.25 * np.sin(2 * math.pi * 0.3 * t)
    base = np.sin(2 * math.pi * 55.0 * t) + 0.45 * np.sin(2 * math.pi * 110.0 * t)
    y = base * lfo * gain
    a = min(n, int(SR * 0.15))
    y[:a] *= np.linspace(0, 1, a)
    y[-a:] *= np.linspace(1, 0, a)
    return y


def room_layer(n: int, gain: float = 0.035) -> np.ndarray:
    """厂房环境噪：低通 1.2kHz 的宽带噪声。"""
    noise = biquad(RNG.normal(0, 1, n), SR, 1200.0, "lp")
    a = min(n, int(SR * 0.2))
    e = np.ones(n)
    e[:a] = np.linspace(0, 1, a)
    e[-a:] = np.linspace(1, 0, a)
    return noise * e * gain


def rhythm_layer(n: int, gain: float = 0.045) -> np.ndarray:
    """低鸣 + 每 0.9 秒一记极轻的远处敲击。"""
    y = hum_layer(n, gain)
    step = int(SR * 0.9)
    for start in range(int(SR * 0.4), n - 2000, step):
        seg = modal(impulse(2000, 30), SR, 520.0, 0.08)
        y[start:start + 2000] += seg * gain * 0.9
    return y


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    for f in OUT.glob("*.wav"):
        f.unlink()

    print("理解 A · 起手音（材质向）")
    save("A1-Metal-Clank", a1_metal_clank())
    save("A2-Air-Release", a2_air_release())
    save("A3-Relay-Click", a3_relay_click())
    save("A4-Bell", a4_bell())

    print("\n理解 B · 背景层（已叠在真实语音 DeviceConnect 上）")
    voice = load_voice()
    n = len(voice)
    v = voice / (float(np.max(np.abs(voice))) or 1.0) * 0.85
    save("B1-Hum-Voice", v + hum_layer(n))
    save("B2-Room-Voice", v + room_layer(n))
    save("B3-Rhythm-Voice", v + rhythm_layer(n))

    print(f"\n共 7 个 → {OUT}")


if __name__ == "__main__":
    main()
