"""BSH · 工业音"分段频谱"检验（定性用）

整体 FFT 只能看到"有哪些频率"，看不出"第几声是什么频率"。
本脚本先按短时能量把音频切成一个个音段，再对**每段单独**做 FFT —— 
这样就能判定"递升三连"到底是 784→1046→1568，还是某个音被写错了。

用法：python inspect-industrial.py [文件名...]
默认检查全部多音设计（01 / 03 / 04 / 07）。
"""

from __future__ import annotations

import glob
import os
import sys
import wave

import numpy as np

LIB = r"D:\O级项目管理\BSH\voice\industrial"
MULTI = ["Ind-01-Classic-Double", "Ind-03-Triple-Rise", "Ind-04-Double-Low", "Ind-07-Alert-Alternate"]


def load(path: str) -> tuple[np.ndarray, int]:
    with wave.open(path, "rb") as w:
        sr = w.getframerate()
        x = np.frombuffer(w.readframes(w.getnframes()), dtype="<i2").astype(np.float64) / 32768.0
    return x, sr


def segments(x: np.ndarray, sr: int, win_ms: float = 5.0, thresh_ratio: float = 0.08):
    """按短时 RMS 切出连续音段：返回 [(起, 止), ...] 采样下标。"""
    win = max(1, int(sr * win_ms / 1000.0))
    rms = np.array([np.sqrt(np.mean(x[i:i + win] ** 2)) for i in range(0, max(1, len(x) - win), win)])
    if rms.max() <= 0:
        return []
    active = rms > rms.max() * thresh_ratio
    segs, start = [], None
    for i, a in enumerate(active):
        if a and start is None:
            start = i
        elif not a and start is not None:
            segs.append((start * win, min(len(x), i * win)))
            start = None
    if start is not None:
        segs.append((start * win, len(x)))
    # 合并间隔小于 12ms 的碎片（同一个音被包络切开的）
    merged = []
    for s in segs:
        if merged and s[0] - merged[-1][1] < int(sr * 0.012):
            merged[-1] = (merged[-1][0], s[1])
        else:
            merged.append(list(s))
    return [tuple(m) for m in merged if m[1] - m[0] > int(sr * 0.008)]


def dominant(x: np.ndarray, sr: int) -> tuple[float, float]:
    """返回 (主频 Hz, 该峰幅度)。"""
    if len(x) < 64:
        return 0.0, 0.0
    spec = np.abs(np.fft.rfft(x * np.hanning(len(x))))
    freqs = np.fft.rfftfreq(len(x), 1.0 / sr)
    spec[freqs < 150.0] = 0.0          # 忽略低频嗡鸣
    k = int(np.argmax(spec))
    return float(freqs[k]), float(spec[k])


def main() -> None:
    names = sys.argv[1:] or MULTI
    for name in names:
        for ver in ("dry", "hum"):
            path = os.path.join(LIB, f"{name}-{ver}.wav")
            if not os.path.exists(path):
                print(f"缺文件：{path}")
                continue
            x, sr = load(path)
            segs = segments(x, sr)
            parts = []
            for i, (a, b) in enumerate(segs, 1):
                f, _ = dominant(x[a:b], sr)
                parts.append(f"#{i} {f:7.1f}Hz ({(b-a)/sr*1000:5.1f}ms)")
            print(f"{name}-{ver:<4} 总 {len(x)/sr:.3f}s  音段 {len(segs)} 个：  " + " | ".join(parts))
        print()


if __name__ == "__main__":
    main()
