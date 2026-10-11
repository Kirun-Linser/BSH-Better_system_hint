#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""BSH 工业提示音生成器 —— 8 种设计 x 2 个版本（dry / hum）= 16 个 WAV。

用途：既能单独当音效播放，也能当作机械女声播报的「起手提示」嵌到语音前面。

输出目录：D:\\O级项目管理\\BSH\\voice\\industrial\\
文件名  ：<前缀>-dry.wav（纯电子音） / <前缀>-hum.wav（叠 62Hz 低频嗡鸣）

设计规格（8 种）：
  01 Ind-01-Classic-Double   1046.5Hz 75ms -> 30ms 静默 -> 1568Hz 95ms（经典双音）
  02 Ind-02-Single-Ding      1318.5Hz 120ms，指数衰减包络（叮——）
  03 Ind-03-Triple-Rise      784 / 1046.5 / 1568 Hz 各 60ms，间隔 20ms（递升三连）
  04 Ind-04-Double-Low       440Hz 100ms -> 30ms 静默 -> 587Hz 110ms（低沉机械）
  05 Ind-05-Sweep            600 -> 1400 Hz 线性扫频 220ms（电子扫描）
  06 Ind-06-Click-Beep       2ms 白噪声脉冲（机械咔哒）+ 20ms 静默 + 1200Hz 90ms 音
  07 Ind-07-Alert-Alternate  740 / 990 Hz 交替 4 声，各 70ms，间隔 25ms（设备报警）
  08 Ind-08-Pulse-Train      1568Hz 四连脉冲，各 30ms，间隔 40ms（数据脉冲）

技术约束：
  * 22050 Hz / 16-bit / 单声道，标准库 wave 写盘（不引第三方音频库）
  * 每个音两端 >=4ms 淡入淡出防爆音（<8ms 的极短脉冲改用整段 Hann 窗，见 click() 注释）
  * 每个音叠 0.25 倍二次谐波，更「电子」，不用纯正弦
  * dry 版纯电子音；hum 版额外叠 62Hz 嗡鸣（幅度 0.02，100ms 淡入 / 150ms 淡出）
  * 全部生成完毕后统一归一化：每个文件峰值 0.90（<=1.0，无削顶）
  * 只依赖 numpy（本机环境无 scipy）

参考（只读，未做任何改动）：tools/robotize.py 的 industrial_layer()（75ms 1046.5Hz + 30ms 静默
+ 95ms 1568Hz，外加 62Hz 嗡鸣），本脚本把那一小段拆成可独立播放的 8 种设计。

用法：
  python make-industrial.py            # 生成 16 个 wav（写到 voice\\industrial\\）
  python make-industrial.py --analyze  # 把已生成的 wav 读回来做验收分析
                                       # （时长 / 峰值 / FFT 前三主频 / 50-80Hz 能量占比 / 削顶检查）
"""

from __future__ import annotations

import argparse
import math
import sys
import wave
from pathlib import Path

import numpy as np

# ----------------------------------------------------------------- 全局常量
SR = 22050                     # 采样率（Hz）
HARMONIC = 0.25                # 二次谐波幅度
FADE_MS = 4.0                  # 每个音两端淡入淡出长度（ms）
PEAK = 0.90                    # 统一归一化目标峰值
HUM_HZ = 62.0                  # 嗡鸣基频（Hz），与 robotize.py 的 indu_hum_hz 一致
HUM_AMP = 0.02                 # 嗡鸣幅度
HUM_FADE_IN_MS = 100.0         # 嗡鸣淡入（ms）
HUM_FADE_OUT_MS = 150.0        # 嗡鸣淡出（ms）
CLICK_GAIN = 0.6               # 机械咔哒白噪声的幅度（低于 1.0，避免抢走归一化峰值）
SEED = 20261009                # 白噪声固定种子 -> 同一份代码每次生成结果完全一致
OUT_DIR = Path(r"D:\O级项目管理\BSH\voice\industrial")


# ----------------------------------------------------------------- 基础波形工具
def _n(ms: float) -> int:
    """毫秒 -> 采样点数（至少 1 点）。"""
    return max(1, int(round(SR * ms / 1000.0)))


def _fade(w: np.ndarray, ms: float = FADE_MS) -> np.ndarray:
    """两端线性淡入淡出（默认各 4ms），防爆音。

    样本太短（k <= 2f）时不动它 —— 那种情况调用方应改用 _hann() 整段加窗。
    """
    k = len(w)
    f = _n(ms)
    if k > 2 * f:
        w[:f] *= np.linspace(0.0, 1.0, f)
        w[-f:] *= np.linspace(1.0, 0.0, f)
    return w


def _hann(k: int) -> np.ndarray:
    """整段 Hann 窗（手写，不依赖 np.hanning），等效两端渐入渐出。"""
    return 0.5 - 0.5 * np.cos(2.0 * np.pi * np.arange(k) / k)


def tone(freq: float, ms: float, decay_tau_ms: float | None = None) -> np.ndarray:
    """一个电子音：基频 + 0.25 倍二次谐波，两端 4ms 淡入淡出。

    decay_tau_ms 给定时改套指数衰减包络（叮—— 的那种余韵），衰减完仍会走两端淡出。
    """
    k = _n(ms)
    t = np.arange(k) / SR
    w = np.sin(2.0 * np.pi * freq * t) + HARMONIC * np.sin(2.0 * np.pi * 2.0 * freq * t)
    if decay_tau_ms is not None:
        w = w * np.exp(-t / (decay_tau_ms / 1000.0))
    return _fade(w)


def sweep(f0: float, f1: float, ms: float) -> np.ndarray:
    """线性扫频 f0 -> f1：相位按频率积分写成二次式，同样叠二次谐波 + 4ms 淡入淡出。"""
    k = _n(ms)
    t = np.arange(k) / SR
    T = k / SR
    ph = 2.0 * np.pi * (f0 * t + (f1 - f0) / (2.0 * T) * t * t)
    return _fade(np.sin(ph) + HARMONIC * np.sin(2.0 * ph))


def click(ms: float = 2.0) -> np.ndarray:
    """机械咔哒：白噪声脉冲，整段 Hann 窗。

    2ms 比 4ms 淡入淡出还短，物理上放不下 4ms，改用整段 Hann 窗
    （等效两端各 1ms 渐入渐出），同样不会有爆音；固定随机种子保证可复现。
    """
    k = _n(ms)
    rng = np.random.default_rng(SEED)
    return rng.uniform(-1.0, 1.0, k) * _hann(k) * CLICK_GAIN


def silence(ms: float) -> np.ndarray:
    """静默间隔。"""
    return np.zeros(_n(ms))


# ----------------------------------------------------------------- 8 种设计
def build_01() -> np.ndarray:
    """01 经典双音：1046.5Hz 75ms -> 30ms 静默 -> 1568Hz 95ms（robotize 同款）。"""
    return np.concatenate([tone(1046.5, 75.0), silence(30.0), tone(1568.0, 95.0)])


def build_02() -> np.ndarray:
    """02 单声叮：1318.5Hz 120ms，指数衰减包络（tau = 35ms）。"""
    return tone(1318.5, 120.0, decay_tau_ms=35.0)


def build_03() -> np.ndarray:
    """03 递升三连：784 / 1046.5 / 1568 Hz 各 60ms，间隔 20ms。"""
    segs = []
    for i, f in enumerate((784.0, 1046.5, 1568.0)):
        if i:
            segs.append(silence(20.0))
        segs.append(tone(f, 60.0))
    return np.concatenate(segs)


def build_04() -> np.ndarray:
    """04 低沉双音：440Hz 100ms -> 30ms 静默 -> 587Hz 110ms。"""
    return np.concatenate([tone(440.0, 100.0), silence(30.0), tone(587.0, 110.0)])


def build_05() -> np.ndarray:
    """05 电子扫描：600 -> 1400 Hz 线性扫频 220ms。"""
    return sweep(600.0, 1400.0, 220.0)


def build_06() -> np.ndarray:
    """06 咔哒 + 提示音：2ms 白噪声脉冲 + 20ms 静默 + 1200Hz 90ms。"""
    return np.concatenate([click(2.0), silence(20.0), tone(1200.0, 90.0)])


def build_07() -> np.ndarray:
    """07 设备报警：740 / 990 Hz 交替 4 声，各 70ms，间隔 25ms。"""
    segs = []
    for i, f in enumerate((740.0, 990.0, 740.0, 990.0)):
        if i:
            segs.append(silence(25.0))
        segs.append(tone(f, 70.0))
    return np.concatenate(segs)


def build_08() -> np.ndarray:
    """08 数据脉冲：1568Hz 四连脉冲，各 30ms，间隔 40ms。"""
    segs = []
    for i in range(4):
        if i:
            segs.append(silence(40.0))
        segs.append(tone(1568.0, 30.0))
    return np.concatenate(segs)


# 设计表：编号 / 文件名前缀 / 说明 / 规格时长(ms) / 设计主频（用于自检标注）/ 生成函数
DESIGNS = [
    dict(idx="01", prefix="Ind-01-Classic-Double",  desc="经典双音 1046.5+1568",   ms=200.0, expect=[1046.5, 1568.0], build=build_01),
    dict(idx="02", prefix="Ind-02-Single-Ding",     desc="单声叮 1318.5 指数衰减", ms=120.0, expect=[1318.5],         build=build_02),
    dict(idx="03", prefix="Ind-03-Triple-Rise",     desc="递升三连 784/1046.5/1568", ms=220.0, expect=[784.0, 1046.5, 1568.0], build=build_03),
    dict(idx="04", prefix="Ind-04-Double-Low",      desc="低沉双音 440+587",       ms=240.0, expect=[440.0, 587.0],   build=build_04),
    dict(idx="05", prefix="Ind-05-Sweep",           desc="线性扫频 600->1400",     ms=220.0, expect=[600.0, 1400.0],  build=build_05, is_sweep=True),
    dict(idx="06", prefix="Ind-06-Click-Beep",      desc="咔哒 + 1200 提示音",     ms=112.0, expect=[1200.0],         build=build_06),
    dict(idx="07", prefix="Ind-07-Alert-Alternate", desc="报警交替 740/990",       ms=355.0, expect=[740.0, 990.0],   build=build_07),
    dict(idx="08", prefix="Ind-08-Pulse-Train",     desc="数据脉冲 1568 四连",     ms=240.0, expect=[1568.0],         build=build_08),
]


# ----------------------------------------------------------------- 嗡鸣 / 归一化 / 写盘
def add_hum(x: np.ndarray) -> np.ndarray:
    """叠一层 62Hz 低频嗡鸣（机柜 / 电机底噪）：幅度 0.02，100ms 淡入 / 150ms 淡出。"""
    n = len(x)
    t = np.arange(n) / SR
    hum = np.sin(2.0 * np.pi * HUM_HZ * t)
    env = np.ones(n)
    fi = min(_n(HUM_FADE_IN_MS), n)
    fo = min(_n(HUM_FADE_OUT_MS), n)
    if fi > 0:
        env[:fi] *= np.linspace(0.0, 1.0, fi)
    if fo > 0:
        env[-fo:] *= np.linspace(1.0, 0.0, fo)
    return x + hum * env * HUM_AMP


def normalize_peak(x: np.ndarray) -> np.ndarray:
    """归一化到峰值 0.90：每个文件各自归一，于是 16 个文件峰值统一为 0.90。"""
    p = float(np.max(np.abs(x))) if len(x) else 0.0
    return x * (PEAK / p) if p > 0 else x


def write_wav(path: Path, x: np.ndarray) -> None:
    """16-bit 单声道 PCM 写盘（标准库 wave）。"""
    pcm = np.clip(np.round(x * 32767.0), -32768.0, 32767.0).astype("<i2")
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(pcm.tobytes())


def read_wav(path: Path) -> tuple[np.ndarray, int]:
    """读回 wav -> (float 归一化到 [-1,1) 的样本, 采样率)。"""
    with wave.open(str(path), "rb") as w:
        sr, n = w.getframerate(), w.getnframes()
        raw = w.readframes(n)
    return np.frombuffer(raw, dtype="<i2").astype(np.float64) / 32768.0, sr


# ----------------------------------------------------------------- 生成
def generate() -> int:
    """生成 8 种设计 x 2 个版本，返回写出的文件数。"""
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    count = 0
    for d in DESIGNS:
        base = d["build"]()                                    # 只算一次，两个版本同源
        print(f"\n{d['idx']} {d['prefix']}（{d['desc']}，规格 {d['ms']:.0f}ms）")
        for suffix, sig in (("-dry", base.copy()), ("-hum", add_hum(base.copy()))):
            path = OUT_DIR / f"{d['prefix']}{suffix}.wav"
            write_wav(path, normalize_peak(sig))
            count += 1
            print(f"  [OK] {path.name:34s} {path.stat().st_size:>7d} 字节 / 22050Hz 16-bit 单声道")
    print(f"\n共写出 {count} 个 wav -> {OUT_DIR}")
    return count


# ----------------------------------------------------------------- 验收分析
def _spectrum(x: np.ndarray, sr: int) -> tuple[np.ndarray, np.ndarray, float]:
    """整段 Hann 窗 FFT（零填充到 >= 信号长度的 2 的幂），返回 (频率轴, 幅度, 分辨率 Hz)。"""
    n = len(x)
    win = 0.5 - 0.5 * np.cos(2.0 * np.pi * np.arange(n) / n)
    nfft = 1 << int(math.ceil(math.log2(max(n, 2048))))
    mag = np.abs(np.fft.rfft(x * win, nfft))
    freqs = np.fft.rfftfreq(nfft, 1.0 / sr)
    return freqs, mag, sr / nfft


def top_peaks(x: np.ndarray, sr: int, top: int = 3, exclude_hz: float = 25.0) -> tuple[list[tuple[float, float]], float]:
    """能量最强的 top 个频率峰；每选中一个就把它 ±exclude_hz 的邻域清零，避免只报同一个峰。"""
    freqs, mag, df = _spectrum(x, sr)
    work = mag.copy()
    out: list[tuple[float, float]] = []
    for _ in range(top):
        i = int(np.argmax(work))
        if work[i] <= 0.0:
            break
        f = float(freqs[i])
        # 抛物线插值细化峰位（比 bin 中心准，误差通常 < 1Hz）
        if 0 < i < len(work) - 1 and work[i - 1] > 0 and work[i + 1] > 0:
            a, b, c = np.log(work[i - 1]), np.log(work[i]), np.log(work[i + 1])
            den = a - 2.0 * b + c
            if den != 0.0:
                f = float(i + np.clip(0.5 * (a - c) / den, -0.5, 0.5)) * df
        out.append((f, float(work[i])))
        work[(freqs >= f - exclude_hz) & (freqs <= f + exclude_hz)] = 0.0
    return out, df


def band_ratio(x: np.ndarray, sr: int, lo: float, hi: float) -> tuple[float, float]:
    """返回 (50-80Hz 能量占比 %, 该频带 RMS 的 dBFS)。用 FFT 掩码取带内成分再反变换求 RMS。"""
    freqs, mag, _ = _spectrum(x, sr)
    power = mag ** 2
    mask = (freqs >= lo) & (freqs <= hi)
    total = float(power[1:].sum())
    ratio = 100.0 * float(power[mask].sum()) / total if total > 0 else 0.0

    n = len(x)
    X = np.fft.rfft(x)
    fx = np.fft.rfftfreq(n, 1.0 / sr)
    X[~((fx >= lo) & (fx <= hi))] = 0.0
    y = np.fft.irfft(X, n)
    rms = float(np.sqrt(np.mean(y ** 2))) if n else 0.0
    dbfs = 20.0 * math.log10(rms) if rms > 0 else -999.0
    return ratio, dbfs


def _label(f: float, d: dict) -> str:
    """把实测峰标注成 规格 / 二次谐波 / 噪声·旁瓣。"""
    if d.get("is_sweep"):
        lo, hi = min(d["expect"]), max(d["expect"])
        return "规格(带内)" if lo - 20.0 <= f <= hi + 20.0 else "带外"
    for e in d["expect"]:
        if abs(f - e) <= max(8.0, e * 0.012):
            return "规格"
    for e in d["expect"]:
        if abs(f - 2.0 * e) <= max(12.0, e * 0.024):
            return "二次谐波"
    return "噪声/旁瓣"


def analyze() -> int:
    """把磁盘上的 wav 读回来逐条验收；返回退出码（0 = 全部通过）。"""
    print("=" * 118)
    print("验收 2/3：时长 · 峰值 · FFT 前三主频（信号从磁盘读回，非内存副本）")
    print("=" * 118)
    print(f"{'文件':34s}{'规格(s)':>8s}{'实测(s)':>9s}{'偏差':>8s}{'峰值':>7s}   前三频率峰（标注：规格/二次谐波）")
    print("-" * 118)

    files: list[tuple[dict, str, Path]] = []
    for d in DESIGNS:
        for suffix in ("-dry", "-hum"):
            files.append((d, suffix, OUT_DIR / f"{d['prefix']}{suffix}.wav"))

    missing = [p.name for _, _, p in files if not p.exists()]
    if missing:
        print("缺文件：" + ", ".join(missing))
        return 2

    global_peak = 0.0
    rows = []
    worst_dev = 0.0
    for d, suffix, path in files:
        x, sr = read_wav(path)
        dur = len(x) / sr
        spec = d["ms"] / 1000.0
        dev = 100.0 * (dur - spec) / spec            # 与规格时长的偏差（%）
        worst_dev = max(worst_dev, abs(dev))
        pk = float(np.max(np.abs(x)))
        global_peak = max(global_peak, pk)
        peaks, df = top_peaks(x, sr, top=3)
        cells = []
        for f, _m in peaks:
            cells.append(f"{f:7.1f}Hz[{_label(f, d)}]")
        print(f"{path.name:34s}{spec:8.3f}{dur:9.3f}{dev:+7.1f}%{pk:7.3f}   " + "  ".join(cells))
        rows.append((d, suffix, dur, pk, peaks, df, x, sr))

    print(f"\n时长最大偏差 = {worst_dev:.1f}%（要求 <= 20%）")
    print(f"FFT 分辨率（bin 宽）：{min(r[5] for r in rows):.2f} ~ {max(r[5] for r in rows):.2f} Hz（细于要求的 10~20Hz，峰位另做抛物线插值）")

    # 扫频这类「频谱本来就是平」的设计，主峰不如带内能量占比有说服力 —— 单独补一行
    for d, suffix, _dur, _pk, _peaks, _df, x, sr in rows:
        if d.get("is_sweep"):
            lo, hi = min(d["expect"]), max(d["expect"])
            ratio, _db = band_ratio(x, sr, lo, hi)
            print(f"  {d['prefix']}{suffix}: {lo:.0f}-{hi:.0f}Hz 带内能量占比 = {ratio:.2f}%（扫频频谱天然平坦，故前三峰均为带内任意 bin）")

    print("\n" + "=" * 118)
    print("验收 4：dry vs hum 的 50-80Hz 低频能量（同一设计对比）")
    print("=" * 118)
    print(f"{'设计':30s}{'dry 占比%':>12s}{'hum 占比%':>12s}{'hum/dry':>12s}{'dry 带内dBFS':>14s}{'hum 带内dBFS':>14s}")
    print("-" * 118)
    table: dict[tuple[str, str], tuple[float, float]] = {}
    for d, suffix, _dur, _pk, _peaks, _df, x, sr in rows:
        table[(d["idx"], suffix)] = band_ratio(x, sr, 50.0, 80.0)
    ok4 = True
    for d in DESIGNS:
        rd, dd = table[(d["idx"], "-dry")]
        rh, dh = table[(d["idx"], "-hum")]
        if rd <= 0.0:
            times = "dry 为 0"
        elif rh / rd >= 1e5:
            times = f"{rh / rd:.2e}x"
        else:
            times = f"{rh / rd:.1f}x"
        flag = "" if rh > rd * 3.0 else "  <-- 不达标!"
        if flag:
            ok4 = False
        print(f"{d['idx'] + ' ' + d['prefix']:30s}{rd:12.5f}{rh:12.5f}{times:>12s}{dd:14.1f}{dh:14.1f}{flag}")

    print("\n" + "=" * 118)
    print("验收 5：削顶检查（全部 16 个文件）")
    print("=" * 118)
    print(f"全局峰值 = {global_peak:.4f}   ->  {'通过（<=1.0，无削顶）' if global_peak <= 1.0 else '不通过：存在削顶!'}")
    print(f"文件数 = {len(rows)}   ->  {'通过（8 设计 x 2 版本）' if len(rows) == 16 else '不通过：数量不对!'}")

    # 格式核验：每个文件都必须是 22050Hz / 16-bit / 单声道
    bad_fmt = []
    for _d, _s, path in files:
        with wave.open(str(path), "rb") as w:
            if (w.getframerate(), w.getsampwidth(), w.getnchannels()) != (SR, 2, 1):
                bad_fmt.append(f"{path.name}:{w.getframerate()}Hz/{w.getsampwidth() * 8}bit/{w.getnchannels()}ch")
    print(f"格式核验 = 22050Hz / 16-bit / 单声道   ->  {'通过（16/16）' if not bad_fmt else '不通过：' + ', '.join(bad_fmt)}")

    ok = global_peak <= 1.0 and len(rows) == 16 and ok4 and worst_dev <= 20.0 and not bad_fmt
    print("\n结论：" + ("全部验收项通过。" if ok else "存在未通过项，见上方标记。"))
    return 0 if ok else 1


def main() -> int:
    ap = argparse.ArgumentParser(description="BSH 工业提示音生成 / 验收分析")
    ap.add_argument("--analyze", action="store_true", help="只做验收分析，不重新生成")
    args = ap.parse_args()
    if args.analyze:
        return analyze()
    generate()
    return 0


if __name__ == "__main__":
    sys.exit(main())
