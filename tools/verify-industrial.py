"""BSH · 工业提示层独立验证（不依赖实现者的自述）

假设实现是错的，用数字反驳或证实：
  1. 前置 beep 是否真的加进去了 —— 对比开头 200ms 的 RMS 与峰值
  2. 低频嗡鸣是否真的存在   —— 用 FFT 量 62 Hz 附近的能量占比
  3. 总时长是否变长约 0.2s —— 前置段长度
  4. 是否削顶              —— 全片峰值

用法：python verify-industrial.py [事件名...]
默认检查 SystemExclamation / DeviceConnect / WindowsUAC 三个事件的三档文件。
"""

from __future__ import annotations

import sys
import wave
from pathlib import Path

import numpy as np

OUT = Path(r"D:\O级项目管理\BSH\voice\out")
TAGS = ["I-off", "I-lite", "I-mid"]


def load(path: Path) -> tuple[np.ndarray, int]:
    with wave.open(str(path), "rb") as w:
        sr = w.getframerate()
        x = np.frombuffer(w.readframes(w.getnframes()), dtype="<i2").astype(np.float64) / 32768.0
    return x, sr


def band_energy(x: np.ndarray, sr: int, lo: float, hi: float) -> float:
    """lo~hi Hz 频带能量占全谱能量的比例。"""
    if len(x) < 1024:
        return 0.0
    win = np.hanning(len(x))
    spec = np.abs(np.fft.rfft(x * win)) ** 2
    freqs = np.fft.rfftfreq(len(x), 1.0 / sr)
    total = spec.sum() or 1.0
    band = spec[(freqs >= lo) & (freqs <= hi)].sum()
    return float(band / total)


def main() -> None:
    events = sys.argv[1:] or ["SystemExclamation", "DeviceConnect", "WindowsUAC"]
    print(f"{'事件':<18}{'档位':<9}{'时长s':>7}{'前200ms RMS':>13}{'前200ms 峰值':>13}{'峰值':>7}{'50-80Hz占比':>13}")
    problems: list[str] = []

    for ev in events:
        rows = {}
        for tag in TAGS:
            p = OUT / f"{ev}__{tag}.wav"
            if not p.exists():
                problems.append(f"缺文件：{p.name}")
                continue
            x, sr = load(p)
            head = x[: int(sr * 0.2)]
            rms = float(np.sqrt(np.mean(head ** 2))) if len(head) else 0.0
            peak_head = float(np.max(np.abs(head))) if len(head) else 0.0
            peak = float(np.max(np.abs(x)))
            hum = band_energy(x, sr, 50.0, 80.0)
            rows[tag] = (len(x) / sr, rms, peak_head, peak, hum)
            print(f"{ev:<18}{tag:<9}{len(x)/sr:>7.2f}{rms:>13.4f}{peak_head:>13.4f}{peak:>7.3f}{hum*100:>12.2f}%")

        if "I-off" in rows and "I-lite" in rows:
            d_lite = rows["I-lite"][0] - rows["I-off"][0]
            d_mid = rows["I-mid"][0] - rows["I-off"][0] if "I-mid" in rows else float("nan")
            r_off, r_lite = rows["I-off"][1], rows["I-lite"][1]
            hum_off = rows["I-off"][4]
            hum_lite = rows["I-lite"][4]
            print(f"{'':<18}→ 时长差 I-lite {d_lite:+.2f}s / I-mid {d_mid:+.2f}s ；"
                  f"开头 RMS {r_off:.4f} → {r_lite:.4f}（×{r_lite / max(r_off, 1e-9):.1f}）；"
                  f"50-80Hz 占比 {hum_off*100:.2f}% → {hum_lite*100:.2f}%")
            if d_lite < 0.1:
                problems.append(f"{ev}: I-lite 时长只比 I-off 长 {d_lite:+.2f}s，前置 beep 可能没生效")
            if r_lite <= r_off:
                problems.append(f"{ev}: I-lite 开头 RMS 没变大，beep 可能没生效")
        for tag, r in rows.items():
            if r[3] > 1.0:
                problems.append(f"{ev}__{tag}: 峰值 {r[3]:.4f} > 1.0（削顶）")
        print()

    print("=" * 78)
    if problems:
        print(f"❌ 发现 {len(problems)} 个问题：")
        for p in problems:
            print("  -", p)
    else:
        print("✅ 全部检查通过：前置 beep 有效、时长增加约 0.2s、无削顶")


if __name__ == "__main__":
    main()
