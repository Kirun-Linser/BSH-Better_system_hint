"""BSH · 一次生成多档机械感参数，供 A/B 对比挑选。

复用 robotize.py 的 DSP 链，只覆盖 CONFIG 里的几个参数，输出到同一目录、
文件名带档位后缀（__A-light / __B-mid / __C-heavy），互不覆盖。

用法：python robotize-variants.py
"""

from __future__ import annotations

import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import robotize as R  # noqa: E402

VARIANTS: dict[str, dict] = {
    # 轻：几乎只是"电话味"，耳朵不太察觉电子感
    "A-light": dict(hp_hz=200.0, lp_hz=4200.0, ring_hz=34.0, ring_depth=0.12, bits=15),
    # 中：能听出机器味，但不刺耳
    "B-mid":   dict(hp_hz=260.0, lp_hz=3900.0, ring_hz=38.0, ring_depth=0.22, bits=13),
    # 重：rev2 用的那套（用户反馈"太明显"）
    "C-heavy": dict(hp_hz=320.0, lp_hz=3400.0, ring_hz=42.0, ring_depth=0.35, bits=11),
}


def main() -> None:
    files = sorted(R.RAW.glob("*.wav"))
    if not files:
        raise SystemExit(f"没有原始 wav，请先跑 make-voice.ps1（目录：{R.RAW}）")

    for tag, over in VARIANTS.items():
        R.CONFIG.update(over)
        for f in files:
            audio, sr = R.read_wav(f)
            R.write_wav(R.OUT / f"{f.stem}__{tag}.wav", R.robotize(audio, sr), sr)
        print(f"[{tag}] {len(files)} 个 → {R.OUT}")
        print(f"        参数 {over}")


if __name__ == "__main__":
    main()
