"""BSH · 混音方案对照（诊断"有回音"）

用户 2026-10-11 反馈混音"有回音"。三个候选：
  A. 旧混音（按帧直接混合，未做长度对齐）—— 预期能听出错位回声
  B. 新混音（先重采样对齐长度，再按帧混合）
  C. 纯 Michelle 单音色（结构上不可能有回声，作为听觉基准）

用法：python compare-mix.py [事件名...]   默认 SystemExclamation / DeviceConnect
输出：D:\\O级项目管理\\BSH\\voice\\compare\\
"""

from __future__ import annotations

import importlib.util
import sys
from pathlib import Path

import numpy as np
import wave

TOOLS = Path(r"D:\O级项目管理\BSH\tools")
OUT = Path(r"D:\O级项目管理\BSH\voice\compare")

spec = importlib.util.spec_from_file_location("meb", TOOLS / "make-edge-batch.py")
meb = importlib.util.module_from_spec(spec)
spec.loader.exec_module(meb)


def mix_old(a: np.ndarray, d: np.ndarray) -> np.ndarray:
    """旧实现：不做长度对齐（保留用于对照）。"""
    sa, sd = meb.stft(a), meb.stft(d)
    n = min(sa.shape[0], sd.shape[0])
    mag = meb.W_ARIA * np.abs(sa[:n]) + (1 - meb.W_ARIA) * np.abs(sd[:n])
    y = meb.istft(mag * np.exp(1j * np.angle(sa[:n])), min(len(a), len(d)))
    return y / (np.max(np.abs(y)) or 1.0) * meb.PEAK


def save(name: str, x: np.ndarray) -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    p = OUT / f"{name}.wav"
    with wave.open(str(p), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(meb.SR)
        w.writeframes((np.clip(x, -1, 1) * 32767).astype("<i2").tobytes())
    print(f"  {name:<34} {len(x)/meb.SR:5.2f}s  峰值 {np.max(np.abs(x)):.3f}  RMS {np.sqrt(np.mean(x**2)):.4f}")


def main() -> None:
    names = sys.argv[1:] or ["SystemExclamation", "DeviceConnect"]
    for name in names:
        a_path = meb.ARIA_DIR / f"{name}.wav"
        d_path = meb.MICH_DIR / f"{name}.wav"
        if not a_path.exists() or not d_path.exists():
            print(f"  跳过 {name}（缺源：{a_path.exists()=} {d_path.exists()=}）")
            continue
        a = meb.trim(meb.read_wav(a_path))
        d = meb.trim(meb.read_wav(d_path))
        print(f"{name}: aria {len(a)/meb.SR:.3f}s  michelle {len(d)/meb.SR:.3f}s  差 {abs(len(a)-len(d))/meb.SR*1000:.0f}ms")
        save(f"{name}__A-旧混音(未对齐)", mix_old(a, d))
        save(f"{name}__B-新混音(已对齐)", meb.mix_35_65(a, d))
        # C：纯 Michelle，归一化到同样的峰值口径
        c = d / (np.max(np.abs(d)) or 1.0) * meb.PEAK
        save(f"{name}__C-纯Michelle", c)
        print()

    print(f"→ {OUT}")


if __name__ == "__main__":
    main()
