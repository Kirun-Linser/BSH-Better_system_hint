"""BSH · 工业提示层三档对比生成

复用 robotize.py 的 DSP 链，只覆盖工业提示层那 4 个参数，一次性产出三档到 voice/out：
    __I-off   对照组（indu_enable=False，纯语音，与正式产物内容一致）
    __I-lite  轻工业层（beep 0.14 / hum 0.015）
    __I-mid   中工业层（beep 0.24 / hum 0.035）

不触碰 robotize.py 里的既有 DSP 默认值，也不覆盖 voice/out 下无后缀的正式产物。

用法（在 tools 目录下）：
    python robotize-industrial.py                     # 处理 voice/raw 全部 59 个
    python robotize-industrial.py SystemExclamation   # 只处理指定事件，便于快速试听
"""

from __future__ import annotations

import json
import sys

import robotize as R

# 每档要覆盖的参数（其余键沿用 robotize.CONFIG 的「轻机械」基线）
VARIANTS: list[tuple[str, dict]] = [
    ("__I-off", {"indu_enable": False}),
    ("__I-lite", {"indu_enable": True, "indu_beep_gain": 0.14, "indu_hum_gain": 0.015}),
    ("__I-mid", {"indu_enable": True, "indu_beep_gain": 0.24, "indu_hum_gain": 0.035}),
]


def main() -> None:
    # 基线快照：每档都从干净的基线开始覆盖，避免上一档的参数残留
    base = dict(R.CONFIG)

    only = set(sys.argv[1:])                                     # 可选：只处理指定事件名
    files = sorted(R.RAW.glob("*.wav"))
    if only:
        files = [f for f in files if f.stem in only]
    if not files:
        raise SystemExit(f"没有找到原始 wav：{R.RAW}")

    total = 0
    for suffix, overrides in VARIANTS:
        R.CONFIG.clear()
        R.CONFIG.update(base)
        R.CONFIG.update(overrides)

        for f in files:
            audio, sr = R.read_wav(f)
            R.write_wav(R.OUT / (f.stem + suffix + ".wav"), R.robotize(audio, sr), sr)
            total += 1
        print(f"[{suffix.lstrip('_')}] {len(files)} 个 → {R.OUT}"
              f"   （{json.dumps(overrides, ensure_ascii=False)}）")

    # 收尾：把 CONFIG 还原成基线，避免影响同进程内的其他调用
    R.CONFIG.clear()
    R.CONFIG.update(base)

    print(f"完成：{len(VARIANTS)} 档 × {len(files)} 个 = {total} 个文件 → {R.OUT}")


if __name__ == "__main__":
    main()
