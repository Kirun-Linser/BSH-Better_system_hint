"""BSH · 音色混合的"正确做法"：单一音源 + 长时谱 EQ 塑形

为什么不再用帧级混合（对照实验结论，2026-10-11 实测）：
  两个不同录音（Aria / Michelle）读同一句话的语速差**实测 67–210ms**，且相位互不相关。
  · 不做长度对齐 → 错位随句子累积，听感是**回声**；
  · 做了长度对齐 → 两源完全重叠，相位干涉 → **梳状滤波**（发闷/金属感，实测 RMS 反而更低）。
  两条路都有结构性缺陷。

本脚本改走"音色移植"的正路：
  1. 取**单一音源**（默认 Michelle，占用户权重 65%）—— 时间结构只有一份 ⇒ **不可能有回声**
  2. 计算两段语音的**长时平均幅度谱**（LTA，只统计有声帧）
  3. EQ 曲线 = (Aria_LTA / Michelle_LTA) ** strength，strength=0.35（对应用户选的 35% 权重）
  4. 把 EQ 乘到 Michelle 的 STFT 幅度上（**相位保持 Michelle 不变**）→ ISTFT

⇒ 得到"Michelle 的嗓音 + 35% 的 Aria 频谱色彩"，且全程只有一个声源。

用法：python make-eva-eq.py [事件名...]   默认 SystemExclamation / DeviceConnect
输出：voice/compare/（相对仓库根）
"""

from __future__ import annotations

import importlib.util
import sys
import wave
from pathlib import Path

import numpy as np

TOOLS = Path(__file__).resolve().parent
OUT = Path(__file__).resolve().parent.parent / "voice" / "compare"

spec = importlib.util.spec_from_file_location("meb", TOOLS / "make-edge-batch.py")
meb = importlib.util.module_from_spec(spec)
spec.loader.exec_module(meb)

STRENGTH = 0.35          # 朝 Aria 方向塑形的强度（对应用户选的 35% 权重）


def lta(x: np.ndarray) -> np.ndarray:
    """长时平均幅度谱（只统计有声帧，避免静音帧把谱拉平）。"""
    mag = np.abs(meb.stft(x))
    energy = mag.sum(axis=1)
    if energy.max() <= 0:
        return mag.mean(axis=0)
    voiced = mag[energy > energy.max() * 0.05]
    return (voiced.mean(axis=0) if len(voiced) else mag.mean(axis=0))


def smooth(v: np.ndarray, k: int = 9) -> np.ndarray:
    """移动平均平滑，避免 EQ 逐 bin 抖动产生金属味。"""
    if k <= 1:
        return v
    ker = np.ones(k) / k
    return np.convolve(v, ker, mode="same")


def eq_transplant(base: np.ndarray, color: np.ndarray, strength: float) -> np.ndarray:
    """把 color 的音色按 strength 塑形到 base 上（单一音源，无回声）。"""
    eps = 1e-9
    ratio = (lta(color) + eps) / (lta(base) + eps)
    eq = smooth(np.clip(ratio ** strength, 0.25, 4.0))
    S = meb.stft(base)
    y = meb.istft(S * eq[None, :], len(base))
    return y / (np.max(np.abs(y)) or 1.0) * meb.PEAK


def save(name: str, x: np.ndarray) -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    with wave.open(str(OUT / f"{name}.wav"), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(meb.SR)
        w.writeframes((np.clip(x, -1, 1) * 32767).astype("<i2").tobytes())
    print(f"  {name:<40} {len(x)/meb.SR:5.2f}s  峰值 {np.max(np.abs(x)):.3f}  RMS {np.sqrt(np.mean(x**2)):.4f}")


def main() -> None:
    names = sys.argv[1:] or ["SystemExclamation", "DeviceConnect"]
    for name in names:
        a_path = meb.ARIA_DIR / f"{name}.wav"
        d_path = meb.MICH_DIR / f"{name}.wav"
        if not a_path.exists() or not d_path.exists():
            print(f"  跳过 {name}（缺源）")
            continue
        a = meb.trim(meb.read_wav(a_path))
        d = meb.trim(meb.read_wav(d_path))
        print(f"{name}: aria {len(a)/meb.SR:.3f}s  michelle {len(d)/meb.SR:.3f}s")
        save(f"{name}__D-EQ塑形(Michelle底+35%Aria色)", eq_transplant(d, a, STRENGTH))
        print()


if __name__ == "__main__":
    main()
