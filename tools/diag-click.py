"""BSH · 临时诊断：Ind-06-Click-Beep 开头到底有没有那个 2ms 咔哒

逐 2ms 窗口打印 RMS 与峰值，用来判定：
  · 前几毫秒有显著能量 → 咔哒存在，是验证脚本的"8ms 最小段长"把它滤掉了（判据问题）
  · 前几毫秒与静默段一样平 → 咔哒没生成（产品缺陷）
"""

from __future__ import annotations

import wave

import numpy as np

P = r"D:\O级项目管理\BSH\voice\industrial\Ind-06-Click-Beep-dry.wav"

with wave.open(P, "rb") as w:
    sr = w.getframerate()
    x = np.frombuffer(w.readframes(w.getnframes()), dtype="<i2").astype(np.float64) / 32768.0

print(f"文件：{P}")
print(f"采样率 {sr}  总时长 {len(x)/sr*1000:.1f} ms  峰值 {np.max(np.abs(x)):.4f}\n")

win = max(1, int(sr * 0.002))          # 2ms 窗口
print(f"{'时刻(ms)':>9}{'窗口RMS':>11}{'窗口峰值':>11}   {'':<20}")
for i in range(0, min(len(x), int(sr * 0.06)), win):
    seg = x[i:i + win]
    if len(seg) == 0:
        break
    rms = float(np.sqrt(np.mean(seg ** 2)))
    peak = float(np.max(np.abs(seg)))
    bar = "#" * min(40, int(rms * 200))
    print(f"{i/sr*1000:>9.1f}{rms:>11.5f}{peak:>11.5f}   {bar}")

head = x[: int(sr * 0.006)]
print(f"\n前 6ms：RMS {np.sqrt(np.mean(head**2)):.5f}  峰值 {np.max(np.abs(head)):.5f}")
quiet = x[int(sr * 0.006):int(sr * 0.020)]
print(f"6–20ms（应为静默）：RMS {np.sqrt(np.mean(quiet**2)):.5f}  峰值 {np.max(np.abs(quiet)):.5f}")
tail = x[int(sr * 0.025):]
print(f"25ms 之后（1200Hz 音）：RMS {np.sqrt(np.mean(tail**2)):.5f}  峰值 {np.max(np.abs(tail)):.5f}")
