"""BSH · 用 Edge 神经语音批量生成 59 条播报（Aria×Michelle 混合 35/65）

用户最终选定：
  · 音色 = **Mix-35A-65D**：Aria(35%) 与 Michelle(65%) 的 STFT 幅度谱混合，
    保留 Aria 的相位（音高/语调/节奏不发生双音）
  · 参数 = Aria `pitch=+0Hz`、Michelle `pitch=-10Hz`，两者都 `rate=-15%`

每条的处理链：
  1. edge-tts 分别合成 Aria 与 Michelle（**最多重试 6 次，指数退避** —— 本机到
     speech.platform.bing.com 的连接时通时不通）
  2. ffmpeg 把 mp3 转成 22050 Hz / 16-bit / 单声道 wav
  3. trim 首尾静音（Edge 输出尾部常带约 1 秒静音）
  4. STFT(1024/256) 混合：幅度谱 0.35*Aria + 0.65*Michelle，相位沿用 Aria
  5. ISTFT 重建 → 峰值归一到 0.85 → 输出

**断点续做**：最终的 out/<事件>.wav 已存在就跳过；中间文件（mp3 / 分轨 wav）保留复用。
网络中断后重跑本脚本即可接着做，不会重复劳动。

用法：
    python make-edge-batch.py              # 全量 59 条
    python make-edge-batch.py --limit 3    # 只做前 3 条（链路自测）
"""

from __future__ import annotations

import argparse
import asyncio
import json
import subprocess
import sys
import wave
from pathlib import Path

import math

import numpy as np

ROOT = Path(r"D:\O级项目管理\BSH")
LINES = ROOT / "voice" / "lines.json"
WORK = ROOT / "voice" / "edge-batch"
ARIA_DIR = WORK / "aria"
MICH_DIR = WORK / "michelle"
OUT_DIR = WORK / "out"
FFMPEG = str(Path.home() / ".dsh" / "plugins" / "dsh-music-code-mv" / "node_modules"
              / "@ffmpeg-installer" / "win32-x64" / "ffmpeg.exe")

# ⚠️ 必须与 Windows 音频引擎对齐：系统自带音效实测为 48000Hz/16bit/2ch。
# 之前用 22050Hz/1ch，Windows 每次播放都要重采样+上混 ⇒ 用户实测「打开和关闭时延迟很高」。
SR = 48000
FFT = 1024
HOP = 256
W_ARIA = 0.35
PEAK = 0.85
VOICE_ARIA = ("en-US-AriaNeural", "+0Hz")
VOICE_MICH = ("en-US-MichelleNeural", "-10Hz")
RATE = "-10%"          # 语速：用户 2026-10-11 要求"略微提升"（原 -15%）
TRIES = 6


# ───────────────────── Edge TTS 合成（带重试） ─────────────────────

async def synth_once(text: str, voice: str, pitch: str, out_mp3: Path) -> bool:
    import edge_tts
    try:
        c = edge_tts.Communicate(text, voice, rate=RATE, pitch=pitch)
        await c.save(str(out_mp3))
    except Exception:
        return False
    return out_mp3.exists() and out_mp3.stat().st_size > 1000


async def synth_retry(text: str, voice: str, pitch: str, out_mp3: Path, tag: str) -> bool:
    if out_mp3.exists() and out_mp3.stat().st_size > 1000:
        return True                                     # 断点：已有就复用
    for i in range(TRIES):
        if await synth_once(text, voice, pitch, out_mp3):
            return True
        await asyncio.sleep(1.5 * (i + 1))               # 指数退避
    print(f"    !! {tag} 重试 {TRIES} 次仍失败", flush=True)
    return False


def mp3_to_wav(mp3: Path, wav: Path) -> bool:
    if wav.exists() and wav.stat().st_size > 1000:
        return True
    try:
        subprocess.run([FFMPEG, "-y", "-loglevel", "error", "-i", str(mp3),
                        "-ar", str(SR), "-ac", "1", str(wav)],
                       check=True, capture_output=True, timeout=60)
    except Exception as e:
        print(f"    !! ffmpeg 失败：{e}", flush=True)
        return False
    return wav.exists()


# ───────────────────── 音频工具 ─────────────────────

def read_wav(p: Path) -> np.ndarray:
    with wave.open(str(p), "rb") as w:
        x = np.frombuffer(w.readframes(w.getnframes()), dtype="<i2").astype(np.float64) / 32768.0
        if w.getnchannels() > 1:
            x = x.reshape(-1, w.getnchannels()).mean(axis=1)
    return x


def write_wav(p: Path, x: np.ndarray) -> None:
    """输出 2 声道 —— 与 Windows 系统音效格式一致，避免播放时的上混开销。"""
    p.parent.mkdir(parents=True, exist_ok=True)
    data = (np.clip(x, -1, 1) * 32767).astype("<i2")
    stereo = np.repeat(data[:, None], 2, axis=1).ravel()
    with wave.open(str(p), "wb") as w:
        w.setnchannels(2)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(stereo.tobytes())


def trim(x: np.ndarray, thr: float = 0.02) -> np.ndarray:
    env = np.abs(x)
    idx = np.where(env > thr * env.max())[0]
    return x[idx[0]:idx[-1] + 1] if len(idx) > 100 else x


def stft(x: np.ndarray) -> np.ndarray:
    win = np.hanning(FFT)
    n = 1 + max(0, (len(x) - FFT) // HOP)
    return np.stack([np.fft.rfft(x[i * HOP:i * HOP + FFT] * win) for i in range(n)])


def istft(spec: np.ndarray, length: int) -> np.ndarray:
    win = np.hanning(FFT)
    y = np.zeros(length + FFT)
    wsum = np.zeros(length + FFT)
    for i in range(spec.shape[0]):
        seg = np.fft.irfft(spec[i], n=FFT) * win
        y[i * HOP:i * HOP + FFT] += seg
        wsum[i * HOP:i * HOP + FFT] += win ** 2
    # ⚠️ 边缘绝不能用 1e-6 兜底做除法：那里窗和趋近 0，y 的浮点残差除以极小数会炸出
    # 巨大尖峰（唯一实测：混音 y 的 min=-31.64，而语音本体只有 ±1.75）—— 随后
    # mix_35_65 里"按峰值归一化"会把整段语音压成 1/37（缩放系数 0.0269），
    # 听起来近乎静音，而峰值却显示 0.85，极具迷惑性。
    # 正确做法：只在窗和足够大的位置做归一化；无覆盖的边缘直接输出 0。
    out = np.zeros(length)
    w = wsum[:length]
    valid = w > 0.05 * (w.max() or 1.0)
    out[valid] = y[:length][valid] / w[valid]
    return out


def resample_to(x: np.ndarray, n: int) -> np.ndarray:
    """线性重采样到 n 个样本（把两个音色的时长对齐用）。"""
    if len(x) == n:
        return x
    idx = np.linspace(0, len(x) - 1, n)
    return np.interp(idx, np.arange(len(x)), x)


def mix_35_65(a: np.ndarray, d: np.ndarray) -> np.ndarray:
    # ⚠️ 必须先做**长度对齐**：Aria 与 Michelle 读同一句话的语速不同（实测 trim 后
    # 2.73s vs 2.66s，差 70ms —— 已远超人耳感知回声的 ~30ms 阈值）。直接按帧混合时，
    # 错位会随句子推进逐渐累积，句尾差出一整段，听感就是"两个声音叠在一起"的回声。
    # （2026-10-11 用户实测反馈："有回音"。）
    # 线性重采样到同一长度：2.7% 的缩放 ≈ 0.5 个半音，人耳几乎察觉不到。
    n_len = min(len(a), len(d))
    a, d = resample_to(a, n_len), resample_to(d, n_len)

    sa, sd = stft(a), stft(d)
    n = min(sa.shape[0], sd.shape[0])
    mag = W_ARIA * np.abs(sa[:n]) + (1 - W_ARIA) * np.abs(sd[:n])
    y = istft(mag * np.exp(1j * np.angle(sa[:n])), n_len)
    return y / (np.max(np.abs(y)) or 1.0) * PEAK


# ── 以下为 2026-10-11 起的**生产路径**（用户定稿：D 版 EQ 塑形 + 压缩停顿）──
# 上面的 mix_35_65（帧级混合 + 长度对齐）保留**仅供对照**（tools/compare-mix.py 用），
# 不再用于生产。原因（实测）：两个录音读同一句话的语速差 67–210ms —— 不对齐会有回声，
# 对齐后相位干涉又会变成梳状滤波，两条路都有结构性缺陷。

def lta(x: np.ndarray) -> np.ndarray:
    """长时平均幅度谱（只统计有声帧，避免静音帧把谱拉平）。"""
    mag = np.abs(stft(x))
    energy = mag.sum(axis=1)
    if energy.max() <= 0:
        return mag.mean(axis=0)
    voiced = mag[energy > energy.max() * 0.05]
    return voiced.mean(axis=0) if len(voiced) else mag.mean(axis=0)


def smooth_eq(v: np.ndarray, k: int = 9) -> np.ndarray:
    """移动平均平滑，避免 EQ 逐 bin 抖动带来金属味。"""
    return v if k <= 1 else np.convolve(v, np.ones(k) / k, mode="same")


def make_voice(a: np.ndarray, d: np.ndarray) -> np.ndarray:
    """把 Aria 的音色按 W_ARIA 强度塑形到 Michelle 上 —— **单一音源，不可能有回声**。

    做法：只借 Aria 的**长时平均谱形**，乘到 Michelle 的 STFT 幅度上（相位保持 Michelle
    不变）后重建。时间结构只有一份 ⇒ 既拿到"混合音色"，又不引入错位/干涉。
    """
    eps = 1e-9
    ratio = (lta(a) + eps) / (lta(d) + eps)
    eq = smooth_eq(np.clip(ratio ** W_ARIA, 0.25, 4.0))
    S = stft(d)
    y = istft(S * eq[None, :], len(d))          # 复数乘 = 只改幅度，相位仍是 Michelle
    return y / (np.max(np.abs(y)) or 1.0) * PEAK


def compress_pauses(x: np.ndarray, min_ms: float = 90.0, keep_ms: float = 100.0,
                    thresh_ratio: float = 0.02) -> np.ndarray:
    """把**内部**的长静音段压缩到 keep_ms。

    用户 2026-10-11 要求「减少空格的停顿」：播报文本里的句号会让 TTS 停 200–400ms。
    这里不改文本（不改语调），只把过长的停顿裁短，保留一点自然呼吸。
    首尾静音不在此处理（交给 trim）。
    """
    win = max(1, int(SR * 0.005))                       # 5ms 分辨率
    n_win = (len(x) + win - 1) // win
    env = np.array([np.max(np.abs(x[i * win:(i + 1) * win])) if i * win < len(x) else 0.0
                    for i in range(n_win)])
    if env.max() <= 0:
        return x
    quiet = env < thresh_ratio * env.max()
    keep = max(1, int(keep_ms / 5.0))
    min_len = int(min_ms / 5.0)

    parts: list[np.ndarray] = []
    i = 0
    while i < n_win:
        j = i
        while j < n_win and quiet[j] == quiet[i]:
            j += 1
        if quiet[i] and i > 0 and j < n_win and (j - i) > min_len:
            parts.append(x[i * win:(i + keep) * win])   # 长停顿 → 只留 keep_ms
        else:
            parts.append(x[i * win:min(j * win, len(x))])
        i = j
    return np.concatenate(parts) if parts else x


# ── 2026-10-11 定稿：V3 轻柔 + 事件专属语气 ──
# 用户选定 V3「减降调 + 频谱倾斜」作为全局轻柔手法；
# 并要求 Open / Close **共用同一单词 Execution**，靠**语气**区分两种含义。

V3_SHIFT_HZ = 6.0        # V3：整段升 6Hz（降调幅度收敛，声音更轻盈）
V3_LOW_DB = -3.0         # V3：低频 -3dB（削"胸声"厚重感）
V3_HIGH_DB = 2.0         # V3：高频 +2dB（加空气感）

# 事件专属处理（2026-10-11 用户定稿的 Open/Close 区分方式）：
#   Open  = **音色更轻**（V3 轻柔的加强版：升 10Hz、低频 −4dB、高频 +3dB）
#   Close = **机械音更重**（电话带通 + 环形调制 + 比特压缩 —— 像机器被关停）
# 两者共用同一个词 Execution.，靠**音色**区分含义，不靠语调。
TONE_OVERRIDE: dict[str, dict[str, float]] = {
    "Open": dict(shift_hz=10.0, low_db=-4.0, high_db=3.0),
}


def shift_pitch(x: np.ndarray, hz_at: float = 200.0, delta_hz: float = 0.0) -> np.ndarray:
    """重采样升降调（时长同比例变化，几 % 听不出来）。"""
    if abs(delta_hz) < 1e-6:
        return x
    factor = 1.0 + delta_hz / hz_at
    n = int(len(x) / factor)
    idx = np.linspace(0, len(x) - 1, n)
    return np.interp(idx, np.arange(len(x)), x)


def spectral_tilt(x: np.ndarray, low_db: float, high_db: float,
                  f_low: float = 250.0, f_high: float = 5000.0) -> np.ndarray:
    """频谱倾斜：低频与高频各自加权，中频按对数频率线性过渡。"""
    S = stft(x)
    f = np.fft.rfftfreq(FFT, 1.0 / SR)
    g = np.ones_like(f)
    lo, hi = f <= f_low, f >= f_high
    g[lo] = 10 ** (low_db / 20.0)
    g[hi] = 10 ** (high_db / 20.0)
    mid = (~lo) & (~hi)
    if mid.any():
        t = np.log(f[mid] / f_low) / np.log(f_high / f_low)
        g[mid] = 10 ** ((low_db + (high_db - low_db) * t) / 20.0)
    y = istft(S * g[None, :], len(x))
    return y


def biquad(x: np.ndarray, freq: float, kind: str, q: float = 0.707) -> np.ndarray:
    """二阶 RBJ 滤波器（机械音处理要用）。"""
    w0 = 2.0 * math.pi * freq / SR
    cw, sw = math.cos(w0), math.sin(w0)
    alpha = sw / (2.0 * q)
    if kind == "lp":
        b0, b1, b2 = (1 - cw) / 2, 1 - cw, (1 - cw) / 2
    else:
        b0, b1, b2 = (1 + cw) / 2, -(1 + cw), (1 + cw) / 2
    a0, a1, a2 = 1 + alpha, -2 * cw, 1 - alpha
    b0, b1, b2, a1, a2 = b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0
    y = np.empty_like(x)
    x1 = x2 = y1 = y2 = 0.0
    for i, xi in enumerate(x):
        yi = b0 * xi + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2
        x2, x1 = x1, xi
        y2, y1 = y1, yi
        y[i] = yi
    return y


def robotize_effect(x: np.ndarray, ring_hz: float = 40.0, ring_depth: float = 0.35,
                    bits: int = 11, hp_hz: float = 300.0, lp_hz: float = 3500.0) -> np.ndarray:
    """机械音处理（沿用 tools/robotize.py 的配方：电话带通 + 环形调制 + 比特压缩）。"""
    y = biquad(x, hp_hz, "hp")
    y = biquad(y, lp_hz, "lp")
    t = np.arange(len(y)) / SR
    y = y * (1.0 + ring_depth * np.sin(2.0 * math.pi * ring_hz * t))
    levels = 2 ** bits
    return np.round(y * levels) / levels


def finalize(y: np.ndarray, event: str) -> np.ndarray:
    """收尾：音色塑形 → 压缩停顿 → 归一化。

    · Close → **机械音更重**（带通 + 环形调制 + 量化）—— 与 Open 同词不同音色
    · Open  → **音色更轻**（V3 轻柔的加强版）
    · 其余  → V3 全局轻柔（升 6Hz + 低 −3dB / 高 +2dB）
    """
    if event == "Close":
        y = robotize_effect(y)
    else:
        t = TONE_OVERRIDE.get(event, dict(shift_hz=V3_SHIFT_HZ, low_db=V3_LOW_DB, high_db=V3_HIGH_DB))
        y = shift_pitch(y, delta_hz=t["shift_hz"])
        y = spectral_tilt(y, t["low_db"], t["high_db"])
    y = compress_pauses(y, keep_ms=100.0)
    return y / (np.max(np.abs(y)) or 1.0) * PEAK


# ───────────────────── 主流程 ─────────────────────

async def run(limit: int, reprocess: bool = False) -> None:
    if not LINES.exists():
        sys.exit(f"找不到 {LINES}")
    if not Path(FFMPEG).exists():
        sys.exit(f"找不到 ffmpeg：{FFMPEG}")

    lines = json.loads(LINES.read_text(encoding="utf-8"))["lines"]
    if limit:
        lines = lines[:limit]

    for d in (ARIA_DIR, MICH_DIR, OUT_DIR):
        d.mkdir(parents=True, exist_ok=True)

    ok, skip, fail = 0, 0, []
    for i, line in enumerate(lines, 1):
        ev = line["event"]
        safe = "".join(ch if (ch.isalnum() or ch in "_-") else "_" for ch in ev)
        final = OUT_DIR / f"{safe}.wav"
        if final.exists() and final.stat().st_size > 1000 and not reprocess:
            skip += 1
            print(f"[{i}/{len(lines)}] {ev:<32} 已存在，跳过", flush=True)
            continue

        text = line["en"]
        aria_mp3, mich_mp3 = ARIA_DIR / f"{safe}.mp3", MICH_DIR / f"{safe}.mp3"
        aria_wav, mich_wav = ARIA_DIR / f"{safe}.wav", MICH_DIR / f"{safe}.wav"

        a_ok = await synth_retry(text, VOICE_ARIA[0], VOICE_ARIA[1], aria_mp3, f"{ev}/aria")
        m_ok = await synth_retry(text, VOICE_MICH[0], VOICE_MICH[1], mich_mp3, f"{ev}/michelle")
        if not (a_ok and m_ok):
            fail.append(ev)
            print(f"[{i}/{len(lines)}] {ev:<32} 合成失败（网络）", flush=True)
            continue
        if not (mp3_to_wav(aria_mp3, aria_wav) and mp3_to_wav(mich_mp3, mich_wav)):
            fail.append(ev)
            print(f"[{i}/{len(lines)}] {ev:<32} 转码失败", flush=True)
            continue

        y = make_voice(trim(read_wav(aria_wav)), trim(read_wav(mich_wav)))
        y = finalize(y, ev)                         # V3 轻柔 / 事件专属语气 + 压缩停顿 + 归一化
        write_wav(final, y)
        ok += 1
        print(f"[{i}/{len(lines)}] {ev:<32} {len(y)/SR:.2f}s  ✓", flush=True)

    print(f"\n完成：新做 {ok} / 跳过 {skip} / 失败 {len(fail)}")
    if fail:
        print("失败清单（重跑本脚本可续做）：" + ", ".join(fail))
    print(f"输出：{OUT_DIR}")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--limit", type=int, default=0, help="只做前 N 条（自测用）")
    ap.add_argument("--reprocess", action="store_true",
                    help="忽略已存在的最终产物，只用现成的 aria/michelle 源重做后处理（不联网）")
    args = ap.parse_args()
    asyncio.run(run(args.limit, args.reprocess))


if __name__ == "__main__":
    main()
