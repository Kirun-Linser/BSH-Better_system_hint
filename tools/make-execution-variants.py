"""BSH · "execution" 一词两义的语气方案

用户定稿：Open 与 Close **共用同一个词 `Execution`**（"单次一样"），
靠语气区分两种含义：
  · Open  = 程序开始执行（上扬、明亮）
  · Close = 执行死刑 / 程序被终结（下沉、阴冷）

四种可落地的"语气"手段：
  flat  纯 TTS 读 `Execution.`（句号 → 陈述语调）
  excl  纯 TTS 读 `Execution!`（感叹号 → 上扬）
  up    `Execution.` + 后处理升调 ~6Hz + 高频提亮（明亮、启动感）
  down  `Execution.` + 后处理降调 ~6Hz + 低通 4kHz（沉、终结感）

用法：python make-execution-variants.py
输出：D:\\O级项目管理\\BSH\\voice\\execution\\
"""

from __future__ import annotations

import asyncio
import importlib.util
import subprocess
import sys
import wave
from pathlib import Path

import numpy as np

TOOLS = Path(r"D:\O级项目管理\BSH\tools")
OUT = Path(r"D:\O级项目管理\BSH\voice\execution")
FFMPEG = r"C:\Users\11\.dsh\plugins\dsh-music-code-mv\node_modules\@ffmpeg-installer\win32-x64\ffmpeg.exe"

spec = importlib.util.spec_from_file_location("meb", TOOLS / "make-edge-batch.py")
meb = importlib.util.module_from_spec(spec)
spec.loader.exec_module(meb)


async def synth(text: str, mp3: Path) -> bool:
    import edge_tts
    for i in range(6):
        try:
            c = edge_tts.Communicate(text, "en-US-MichelleNeural", rate="-10%", pitch="-10Hz")
            await c.save(str(mp3))
            if mp3.exists() and mp3.stat().st_size > 1000:
                return True
        except Exception:
            pass
        await asyncio.sleep(1.5 * (i + 1))
    return False


def to_wav(mp3: Path, wav: Path) -> bool:
    try:
        subprocess.run([FFMPEG, "-y", "-loglevel", "error", "-i", str(mp3),
                        "-ar", str(meb.SR), "-ac", "1", str(wav)],
                       check=True, capture_output=True, timeout=60)
    except Exception as e:
        print(f"    ffmpeg 失败：{e}")
        return False
    return wav.exists()


def shift_pitch(x: np.ndarray, hz_at: float = 200.0, delta_hz: float = 6.0) -> np.ndarray:
    factor = 1.0 + delta_hz / hz_at
    n = int(len(x) / factor)
    idx = np.linspace(0, len(x) - 1, n)
    return np.interp(idx, np.arange(len(x)), x)


def tilt(x: np.ndarray, low_db: float, high_db: float) -> np.ndarray:
    S = meb.stft(x)
    f = np.fft.rfftfreq(meb.FFT, 1.0 / meb.SR)
    g = np.ones_like(f)
    g[f <= 250] = 10 ** (low_db / 20)
    g[f >= 5000] = 10 ** (high_db / 20)
    mid = (f > 250) & (f < 5000)
    t = (np.log(f[mid] / 250)) / np.log(5000 / 250)
    g[mid] = 10 ** ((low_db + (high_db - low_db) * t) / 20)
    y = meb.istft(S * g[None, :], len(x))
    return y / (np.max(np.abs(y)) or 1.0) * meb.PEAK


def norm(x: np.ndarray) -> np.ndarray:
    """统一归一化 —— 否则经过后处理的版本（被 tilt 归一到 0.85）会显得更响，对比不公平。"""
    return x / (np.max(np.abs(x)) or 1.0) * meb.PEAK


def save(name: str, x: np.ndarray) -> None:
    x = norm(x)
    OUT.mkdir(parents=True, exist_ok=True)
    with wave.open(str(OUT / f"{name}.wav"), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(meb.SR)
        w.writeframes((np.clip(x, -1, 1) * 32767).astype("<i2").tobytes())
    print(f"  {name:<34} {len(x)/meb.SR:5.2f}s  RMS {np.sqrt(np.mean(x**2)):.4f}")


async def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    for f in OUT.glob("*.wav"):
        f.unlink()

    src = OUT / "src"
    src.mkdir(exist_ok=True)

    variants = {"flat": "Execution.", "excl": "Execution!"}
    base: dict[str, np.ndarray] = {}
    for tag, text in variants.items():
        mp3, wav = src / f"{tag}.mp3", src / f"{tag}.wav"
        if not wav.exists():
            if not await synth(text, mp3):
                print(f"  {tag}: 合成失败（网络）")
                continue
            if not to_wav(mp3, wav):
                continue
        base[tag] = meb.compress_pauses(meb.trim(meb.read_wav(wav)))
        save(f"Exec-{tag}-{'句号(陈述)' if tag == 'flat' else '感叹号(上扬)'}", base[tag])

    if "flat" in base:
        x = base["flat"]
        save("Exec-open-升调+提亮(启动感)", tilt(shift_pitch(x, delta_hz=6.0), -1.0, 2.5))
        save("Exec-close-降调+低通(终结感)", tilt(shift_pitch(x, delta_hz=-6.0), -3.0, -6.0))

    print(f"\n→ {OUT}")


if __name__ == "__main__":
    asyncio.run(main())
