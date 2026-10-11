"""生成"机械女声"版全套提示音（排除循环铃声 / 循环来电）。

用户 2026-10-11 要求：
  · 除「循环电话」与「循环来电」之外的全部事件都要
  · 音色用**机械女声**
  · 只输出到 OUT_DIR（**不碰系统目录、不碰注册表**）

机械音配方沿用 tools/robotize.py（也是 Close 那条用的那套）：
  biquad 高通 300Hz → 低通 3500Hz（电话频带，掐掉低频厚度与高频齿音）
  → 40Hz 环形调制（产生金属质感的振铃边带）
  → 11bit 量化（加入量化噪声，这是"数字味"的来源）
参数比 Close 略柔（ring_depth 0.35→0.28、bits 11→12）：Close 只要一句，整套 39 条若同样重，
长时间听会刺耳。位深 12 仍明显"数码"，但对元音清晰度伤害更小。

单音色（Michelle）+ 机械处理：机械音不需要两个音色叠加 —— 那套 EQ 移植是给"轻柔女声"用的。
"""

from __future__ import annotations

import asyncio
import importlib.util
import subprocess
import sys
import os
from pathlib import Path

# 输出目录：可移植写法，可用环境变量覆盖（默认桌面 BSH-yushe）
OUT_DIR = Path(os.environ.get("BSH_MECH_OUT", str(Path.home() / "Desktop" / "BSH-yushe")))

import numpy as np

TOOLS = Path(r"D:\O级项目管理\BSH\tools")
EB = Path(r"D:\O级项目管理\BSH\voice\edge-batch")
OUT = OUT_DIR
FFMPEG = str(Path.home() / ".dsh" / "plugins" / "dsh-music-code-mv" / "node_modules" / "@ffmpeg-installer" / "win32-x64" / "ffmpeg.exe")
TMP = Path.home() / ".dsh" / "tmp" / "bsh-mech"

# 排除：循环铃声 / 循环来电（用户明确点名不要）
EXCLUDE_PREFIXES = ("Notification.Looping.",)

spec = importlib.util.spec_from_file_location("meb", TOOLS / "make-edge-batch.py")
meb = importlib.util.module_from_spec(spec)
spec.loader.exec_module(meb)


async def synth(text: str, voice: str, pitch: str, rate: str, mp3: Path) -> bool:
    """带退避重试的合成（网络不稳，实测经常要重试 1~4 次）。"""
    import edge_tts
    for i in range(8):
        try:
            await edge_tts.Communicate(text, voice, rate=rate, pitch=pitch).save(str(mp3))
            if mp3.exists() and mp3.stat().st_size > 800:
                return True
        except Exception as e:
            print(f"      第 {i + 1} 次失败：{type(e).__name__}")
        await asyncio.sleep(1.2 * (i + 1))
    return False


def to_wav(mp3: Path, wav: Path) -> None:
    subprocess.run([FFMPEG, "-y", "-loglevel", "error", "-i", str(mp3),
                    "-ar", str(meb.SR), "-ac", "1", str(wav)], check=True, capture_output=True)


def mech(y: np.ndarray) -> np.ndarray:
    """机械女声：电话带通 + 环形调制 + 量化。参数比 Close 略柔（见文件头说明）。"""
    y = meb.robotize_effect(y, ring_hz=40.0, ring_depth=0.28, bits=12, hp_hz=300.0, lp_hz=3800.0)
    y = meb.compress_pauses(y, keep_ms=100.0)
    return y / (np.max(np.abs(y)) or 1.0) * meb.PEAK


async def one(item: dict) -> tuple[str, bool, str]:
    event, text = item["event"], item["en"]
    safe = "".join(c if (c.isalnum() or c in "_-") else "_" for c in event)
    mp3, wav = TMP / f"{safe}.mp3", TMP / f"{safe}.wav"
    for p in (mp3, wav):
        if p.exists():
            p.unlink()

    if not await synth(text, meb.VOICE_MICH[0], meb.VOICE_MICH[1], meb.RATE, mp3):
        return event, False, "合成失败（网络）"
    to_wav(mp3, wav)
    y = mech(meb.trim(meb.read_wav(wav)))

    pretty = f"{item['zh']},{text.rstrip('.!?')}.wav"
    meb.write_wav(OUT / pretty, y)
    return event, True, f"{len(y) / meb.SR:.2f}s  RMS {np.sqrt(np.mean(y ** 2)):.4f}  {pretty}"


async def main() -> None:
    TMP.mkdir(parents=True, exist_ok=True)
    OUT.mkdir(parents=True, exist_ok=True)

    lines = meb.json.loads((TOOLS.parent / "voice" / "lines.json").read_text(encoding="utf-8"))["lines"]
    targets = [x for x in lines if not x["event"].startswith(EXCLUDE_PREFIXES)]

    print(f"台词表共 {len(lines)} 条，排除循环铃声/来电 {len(lines) - len(targets)} 条 → 生成 {len(targets)} 条")
    print(f"音色：{meb.VOICE_MICH[0]}（{meb.VOICE_MICH[1]}，速率 {meb.RATE}）+ 机械处理")
    print(f"输出：{OUT}\n")

    ok = fail = 0
    failures = []
    for i, item in enumerate(targets, 1):
        print(f"[{i}/{len(targets)}] {item['event']:<28} {item['en']!r}")
        event, good, msg = await one(item)
        if good:
            ok += 1
            print(f"      ✅ {msg}")
        else:
            fail += 1
            failures.append(event)
            print(f"      ❌ {msg}")

    print(f"\n完成：成功 {ok} / 失败 {fail}")
    if failures:
        print(f"失败的（重跑本脚本即可续做）：{', '.join(failures)}")
        sys.exit(1)


if __name__ == "__main__":
    asyncio.run(main())
