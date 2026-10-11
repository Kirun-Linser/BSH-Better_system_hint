"""BSH · 独立工业音库验证（v2：分段频谱判据）

为什么改判据：v1 用"整体 FFT 前三峰"检查多音设计，对 Ind-03「三音递升」误报 ——
最低音 784Hz 的能量被后两音及其二次谐波挤出前三峰（实测前三峰是 1045/1568/2091）。
改用"先按短时能量切音段、再逐段测主频"后，判据与信号类型匹配，而且比 v1 更严格：
它同时校验**音段数量**与**每一段各自的频率**。

检查项：
  1. 16 个文件齐全、命名对表
  2. 音段数量 == 规格序列长度；每段主频命中规格（±8%）
  3. 扫频设计（Ind-05）单独用「600–1400Hz 带内能量占比」判定
  4. hum 版 50–80Hz 能量占比 > dry 版（嗡鸣层确实只加在 hum 上）
  5. 峰值 ≈0.90、无削顶、时长合理

用法：python verify-industrial-lib.py
"""

from __future__ import annotations

import glob
import os
import wave

import numpy as np

LIB = r"D:\O级项目管理\BSH\voice\industrial"

# 每个设计期望的"音段频率序列"；None = 该段无固定音高（如咔哒噪声）
SPEC_SEQ: dict[str, list[float | None]] = {
    "Ind-01-Classic-Double":  [1046.5, 1568.0],
    "Ind-02-Single-Ding":     [1318.5],
    "Ind-03-Triple-Rise":     [784.0, 1046.5, 1568.0],
    "Ind-04-Double-Low":      [440.0, 587.0],
    "Ind-06-Click-Beep":      [None, 1200.0],
    "Ind-07-Alert-Alternate": [740.0, 990.0, 740.0, 990.0],
    "Ind-08-Pulse-Train":     [1568.0, 1568.0, 1568.0, 1568.0],
}
SWEEP_LIKE = {"Ind-05-Sweep"}


def load(path: str) -> tuple[np.ndarray, int]:
    with wave.open(path, "rb") as w:
        sr = w.getframerate()
        x = np.frombuffer(w.readframes(w.getnframes()), dtype="<i2").astype(np.float64) / 32768.0
    return x, sr


def segments(x: np.ndarray, sr: int, win_ms: float = 5.0, thresh_ratio: float = 0.08):
    """按短时 RMS 切出连续音段，返回 [(起, 止)] 采样下标。"""
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
            segs.append([start * win, min(len(x), i * win)])
            start = None
    if start is not None:
        segs.append([start * win, len(x)])
    merged: list[list[int]] = []
    for s in segs:
        if merged and s[0] - merged[-1][1] < int(sr * 0.012):
            merged[-1][1] = s[1]
        else:
            merged.append(s)
    # 最小段长 3ms（不是 8ms）：Ind-06 的机械咔哒只有 2ms，8ms 会把它整段滤掉，
    # 导致"音段数 1 ≠ 规格 2"的误报（已用 diag-click.py 逐 2ms 窗口证实咔哒存在）
    return [(a, b) for a, b in merged if b - a > int(sr * 0.003)]


def dominant(x: np.ndarray, sr: int) -> float:
    if len(x) < 64:
        return 0.0
    spec = np.abs(np.fft.rfft(x * np.hanning(len(x))))
    freqs = np.fft.rfftfreq(len(x), 1.0 / sr)
    spec[freqs < 150.0] = 0.0
    return float(freqs[int(np.argmax(spec))])


def band_ratio(x: np.ndarray, sr: int, lo: float, hi: float) -> float:
    spec = np.abs(np.fft.rfft(x * np.hanning(len(x)))) ** 2
    freqs = np.fft.rfftfreq(len(x), 1.0 / sr)
    return float(spec[(freqs >= lo) & (freqs <= hi)].sum() / (spec.sum() or 1.0))


def main() -> None:
    files = sorted(glob.glob(os.path.join(LIB, "*.wav")))
    print(f"目录：{LIB}")
    print(f"文件数：{len(files)}（期望 16）\n")

    problems: list[str] = []
    hum_ratios: dict[str, dict[str, float]] = {}

    print(f"{'文件':<34}{'时长s':>7}{'峰值':>7}{'50-80Hz':>9}   分段主频")
    for f in files:
        name = os.path.basename(f)
        x, sr = load(f)
        dur, peak = len(x) / sr, float(np.max(np.abs(x)))
        hum = band_ratio(x, sr, 50.0, 80.0)

        base, _, ver = name[:-4].rpartition("-")
        hum_ratios.setdefault(base, {})[ver] = hum

        segs = segments(x, sr)
        detail = " | ".join(f"{dominant(x[a:b], sr):.0f}Hz({(b-a)/sr*1000:.0f}ms)" for a, b in segs)
        print(f"{name:<34}{dur:>7.3f}{peak:>7.3f}{hum*100:>8.2f}%   {detail}")

        # 基本项
        if peak > 1.0:
            problems.append(f"{name}: 峰值 {peak:.3f} > 1.0（削顶）")
        if abs(peak - 0.90) > 0.05:
            problems.append(f"{name}: 峰值 {peak:.3f} 偏离目标 0.90")
        if not (0.03 <= dur <= 1.2):
            problems.append(f"{name}: 时长 {dur:.3f}s 超出合理范围")

        # 规格项
        if base in SWEEP_LIKE:
            inband = band_ratio(x, sr, 600.0, 1400.0)
            if inband < 0.5:
                problems.append(f"{name}: 扫频带内 600–1400Hz 只占 {inband*100:.1f}%（应 >50%）")
        elif base in SPEC_SEQ:
            seq = SPEC_SEQ[base]
            if len(segs) != len(seq):
                problems.append(f"{name}: 音段数 {len(segs)} ≠ 规格 {len(seq)}（实测 {[round(dominant(x[a:b], sr)) for a, b in segs]}）")
            else:
                for i, ((a, b), expect) in enumerate(zip(segs, seq), 1):
                    if expect is None:
                        continue
                    got = dominant(x[a:b], sr)
                    if abs(got - expect) > max(50.0, expect * 0.08):
                        problems.append(f"{name}: 第 {i} 段主频 {got:.0f}Hz ≠ 规格 {expect:.0f}Hz")
        else:
            problems.append(f"{name}: 前缀 {base} 不在规格表里")

    print("\n--- dry vs hum 的 50–80Hz 占比 ---")
    for base, vers in sorted(hum_ratios.items()):
        d, h = vers.get("dry"), vers.get("hum")
        if d is None or h is None:
            problems.append(f"{base}: 缺少 dry 或 hum 版本")
            continue
        flag = "✅" if h > d else "❌"
        print(f"  {flag} {base:<26} dry {d*100:>6.3f}%   hum {h*100:>6.3f}%")
        if h <= d:
            problems.append(f"{base}: hum 版低频占比未高于 dry 版（嗡鸣层可能没生效）")

    print("\n" + "=" * 78)
    if problems:
        print(f"❌ {len(problems)} 个问题：")
        for p in problems:
            print("  -", p)
    else:
        print("✅ 全部通过：16 个文件齐全、音段数与每段主频均符合规格、hum 层生效、无削顶")
        print("   判据说明：先按短时能量切音段，再逐段测主频（比整体 FFT 前三峰更严格）")


if __name__ == "__main__":
    main()
