"""BSH · "声音更轻"的三个实现手法对照（不改音量）

用户 2026-10-11 反馈：「声音再轻一点，**不是音量减小**」——
即要**音色**更轻盈，而不是把音量调小。中文里"声音轻"通常指"不厚重、不冲"，
所以这里避开音量，改从三处入手：

  V1 减降调：Michelle 原为 pitch −10Hz（偏低沉），把整段升调约 6Hz（重采样 3%）
            ⇒ 声音更亮、更"飘"，厚度下降；代价是时长缩短约 3%
  V2 频谱倾斜：低频（<250Hz）−3dB、高频（>5kHz）+2dB，中频不动
            ⇒ 削掉"胸声"的厚重感、加一点空气感，响度基本不变
  V3 两者叠加

对照组 00 = 当前生产版本（D 版 EQ 塑形，pitch −10Hz，无倾斜）。

用法：python make-soft-variants.py [事件名...]   默认 SystemExclamation / AppGPFault
输出：D:\\O级项目管理\\BSH\\voice\\soft\\
"""

from __future__ import annotations

import importlib.util
import sys
import wave
from pathlib import Path

import numpy as np

TOOLS = Path(r"D:\O级项目管理\BSH\tools")
OUT = Path(r"D:\O级项目管理\BSH\voice\soft")

spec = importlib.util.spec_from_file_location("meb", TOOLS / "make-edge-batch.py")
meb = importlib.util.module_from_spec(spec)
spec.loader.exec_module(meb)


def pitch_up(x: np.ndarray, hz_at: float = 200.0, delta_hz: float = 6.0) -> np.ndarray:
    """把 200Hz 附近升高 delta_hz（用重采样近似，时长同比例缩短）。"""
    factor = 1.0 + delta_hz / hz_at                 # 3%
    n_out = int(len(x) / factor)
    idx = np.linspace(0, len(x) - 1, n_out)
    return np.interp(idx, np.arange(len(x)), x)


def spectral_tilt(x: np.ndarray, low_db: float = -3.0, high_db: float = 2.0,
                  f_low: float = 250.0, f_high: float = 5000.0) -> np.ndarray:
    """频谱倾斜：低频衰减、高频提升，中频保持（对响度影响小）。"""
    S = meb.stft(x)
    freqs = np.fft.rfftfreq(meb.FFT, 1.0 / meb.SR)
    g = np.ones_like(freqs)
    lo = freqs <= f_low
    hi = freqs >= f_high
    g[lo] = 10 ** (low_db / 20.0)
    g[hi] = 10 ** (high_db / 20.0)
    mid = (~lo) & (~hi)                             # 用对数频率线性过渡
    if mid.any():
        t = (np.log(freqs[mid] / f_low)) / np.log(f_high / f_low)
        db = low_db + (high_db - low_db) * t
        g[mid] = 10 ** (db / 20.0)
    y = meb.istft(S * g[None, :], len(x))
    return y / (np.max(np.abs(y)) or 1.0) * meb.PEAK


def save(name: str, x: np.ndarray) -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    with wave.open(str(OUT / f"{name}.wav"), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(meb.SR)
        w.writeframes((np.clip(x, -1, 1) * 32767).astype("<i2").tobytes())
    print(f"  {name:<44} {len(x)/meb.SR:5.2f}s  RMS {np.sqrt(np.mean(x**2)):.4f}")


def main() -> None:
    names = sys.argv[1:] or ["SystemExclamation", "AppGPFault"]
    for name in names:
        a_path, d_path = meb.ARIA_DIR / f"{name}.wav", meb.MICH_DIR / f"{name}.wav"
        if not a_path.exists() or not d_path.exists():
            print(f"  跳过 {name}（缺源）")
            continue
        a = meb.trim(meb.read_wav(a_path))
        d = meb.trim(meb.read_wav(d_path))
        base = meb.compress_pauses(meb.make_voice(a, d))
        print(f"{name}:")
        save(f"{name}__00-当前版本", base)
        save(f"{name}__V1-减降调(升6Hz)", meb.compress_pauses(meb.make_voice(pitch_up(a), pitch_up(d))))
        save(f"{name}__V2-频谱倾斜(低-3dB_高+2dB)", spectral_tilt(base))
        save(f"{name}__V3-减降调+倾斜", spectral_tilt(meb.compress_pauses(meb.make_voice(pitch_up(a), pitch_up(d)))))
        print()

    print(f"→ {OUT}")


if __name__ == "__main__":
    main()
