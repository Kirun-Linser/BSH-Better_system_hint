"""BSH · 语音素材质检

对 voice/out/*.wav 逐个体检：时长 / 峰值 / RMS / 削顶样本数 / 静音占比。
用来在交付前发现"空文件、爆音、几乎没声音"这类问题 —— 不用耳朵也能拦下大部分废品。
"""

import glob
import os
import wave

import numpy as np

OUT = r"D:\O级项目管理\BSH\voice\out"


def stat(path: str):
    with wave.open(path, "rb") as w:
        sr, n, ch = w.getframerate(), w.getnframes(), w.getnchannels()
        x = np.frombuffer(w.readframes(n), dtype="<i2").astype(np.float64) / 32768.0
    if ch > 1:
        x = x.reshape(-1, ch).mean(axis=1)
    dur = len(x) / sr if sr else 0.0
    peak = float(np.max(np.abs(x))) if len(x) else 0.0
    rms = float(np.sqrt(np.mean(x ** 2))) if len(x) else 0.0
    clip = int(np.sum(np.abs(x) >= 0.999))
    silence = float(np.mean(np.abs(x) < 0.001)) if len(x) else 1.0
    return sr, dur, peak, rms, clip, silence


def main() -> None:
    files = sorted(glob.glob(os.path.join(OUT, "*.wav")))
    if not files:
        raise SystemExit(f"没有可质检的文件：{OUT}")

    print(f"{'文件':<34}{'采样率':>8}{'时长s':>7}{'峰值':>8}{'RMS':>8}{'削顶':>6}{'静音%':>7}")
    bad: list[str] = []
    total = 0.0
    for f in files:
        sr, dur, peak, rms, clip, sil = stat(f)
        total += dur
        print(f"{os.path.basename(f):<34}{sr:>8}{dur:>7.2f}{peak:>8.3f}{rms:>8.3f}{clip:>6}{sil * 100:>7.1f}")
        reasons = []
        if dur < 0.15:
            reasons.append("过短")
        if rms < 0.01:
            reasons.append("近静音")
        if clip > 0:
            reasons.append("削顶")
        if sil > 0.85:
            reasons.append("静音占比过高")
        if reasons:
            bad.append(f"{os.path.basename(f)}（{'/'.join(reasons)}）")

    print(f"\n共 {len(files)} 个，总时长 {total:.1f}s，平均 {total / len(files):.2f}s")
    print(f"异常 {len(bad)} 个：")
    for b in bad[:15]:
        print("  !", b)


if __name__ == "__main__":
    main()
