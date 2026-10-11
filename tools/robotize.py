"""BSH · 机械女声合成（第二步：机器人化后处理）

把 voice/raw/*.wav（SAPI 生成的干净英文女声）处理成「红警 EVA / 星际副官」那种
电子机械音。纯手写 DSP，不依赖 scipy —— 只用 numpy。

处理链（顺序可调，参数集中在 CONFIG）：
  1. 电话带通      —— 300 Hz 高通 + 3400 Hz 低通（二阶 RBJ biquad），去掉齿音与低频轰鸣
  2. 环形调制      —— 乘以 (1 + depth·sin(2π·fm·t))，产生金属/电子泛音（机械感的主要来源）
  3. 比特压缩      —— 量化到 N bit，模拟老式数字合成器
  4. 轻微降采样    —— 可选，模拟 8 kHz 采样时代的音色
  5. 峰值归一化 + 短淡入淡出，避免爆音
  6. 工业提示层      —— 末尾叠加前置两声电子 beep + 贯穿低频嗡鸣（可用 indu_enable 关闭）

用法：
    python robotize.py            # 处理 voice/raw 全部 → voice/out
    python robotize.py --test     # 只处理前 3 个，便于试听对比
"""

from __future__ import annotations

import argparse
import json
import math
import wave
from pathlib import Path

import numpy as np

ROOT = Path(r"D:\O级项目管理\BSH\voice")
RAW = ROOT / "raw"
OUT = ROOT / "out"

CONFIG = {
    "hp_hz": 200.0,        # 高通截止（A 轻档：200，几乎不削低频）
    "lp_hz": 4200.0,       # 低通截止
    "ring_hz": 34.0,       # 环形调制频率（Hz）
    "ring_depth": 0.12,    # 调制深度 0~1（A 轻档：只留一点金属味）
    "bits": 15,            # 量化位数（A 轻档：15，基本听不出量化）
    "downsample_to": 0,    # >0 时先降到该采样率再升回来（0 = 关闭）
    "gain": 0.92,          # 输出峰值
    "fade_ms": 6.0,        # 首尾淡入淡出
    # —— 工业提示层（在最终归一化与淡入淡出之后叠加；关闭时输出与原来完全一致）——
    "indu_enable": True,      # 是否叠加工业提示层
    "indu_beep_gain": 0.20,   # 前置两声电子 beep 的幅度
    "indu_hum_gain": 0.025,   # 贯穿全片的低频嗡鸣幅度（极低，只做底噪）
    "indu_hum_hz": 62.0,      # 嗡鸣基频（电机 / 机柜底噪感）
}


def read_wav(path: Path) -> tuple[np.ndarray, int]:
    with wave.open(str(path), "rb") as w:
        if w.getsampwidth() != 2:
            raise ValueError(f"{path.name}: 只支持 16-bit PCM，实际 {w.getsampwidth() * 8}-bit")
        ch, sr, n = w.getnchannels(), w.getframerate(), w.getnframes()
        data = np.frombuffer(w.readframes(n), dtype="<i2").astype(np.float64) / 32768.0
    if ch > 1:                                    # 多声道取平均
        data = data.reshape(-1, ch).mean(axis=1)
    return data, sr


def write_wav(path: Path, data: np.ndarray, sr: int) -> None:
    clipped = np.clip(data, -1.0, 1.0)
    pcm = (clipped * 32767.0).astype("<i2")
    path.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(sr)
        w.writeframes(pcm.tobytes())


def biquad(x: np.ndarray, sr: int, freq: float, kind: str, q: float = 0.707) -> np.ndarray:
    """RBJ cookbook 二阶滤波器（手写，不需要 scipy）。"""
    w0 = 2.0 * math.pi * freq / sr
    cos_w0, sin_w0 = math.cos(w0), math.sin(w0)
    alpha = sin_w0 / (2.0 * q)

    if kind == "hp":
        b0, b1, b2 = (1 + cos_w0) / 2, -(1 + cos_w0), (1 + cos_w0) / 2
    elif kind == "lp":
        b0, b1, b2 = (1 - cos_w0) / 2, 1 - cos_w0, (1 - cos_w0) / 2
    else:
        raise ValueError(kind)
    a0, a1, a2 = 1 + alpha, -2 * cos_w0, 1 - alpha

    b0, b1, b2, a1, a2 = b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0

    y = np.empty_like(x)
    x1 = x2 = y1 = y2 = 0.0
    for i, xi in enumerate(x):
        yi = b0 * xi + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2
        x2, x1 = x1, xi
        y2, y1 = y1, yi
        y[i] = yi
    return y


def resample_roundtrip(x: np.ndarray, sr: int, target: int) -> np.ndarray:
    """线性插值降到 target 再升回来 —— 模拟低采样率时代的粗糙感。"""
    n_out = int(len(x) * target / sr)
    idx = np.linspace(0, len(x) - 1, n_out)
    low = np.interp(idx, np.arange(len(x)), x)
    back_idx = np.linspace(0, n_out - 1, len(x))
    return np.interp(back_idx, np.arange(n_out), low)


def industrial_layer(n: int, sr: int) -> np.ndarray:
    """工业提示层：前置两声电子 beep + 贯穿全片的低频嗡鸣。

    返回长度 n + lead 的浮点数组，lead 就是前置段采样数：
      [0, lead)   前置段（工业控制台提示音）：1046.5 Hz 75ms → 约 30ms 静默 → 1568 Hz 95ms
      [lead, ...) 贯穿段（电机 / 机柜底噪）：indu_hum_hz 基频 + 0.4 倍二次谐波
    调用方把语音混进 layer[:len(y)]，前置段便自然落在语音之前（总时长随之变长）。
    """
    cfg = CONFIG
    n = max(int(n), 0)

    # 关闭时没有前置段，lead = 0，返回与语音等长的全零层（与纯语音输出叠加后无任何变化）
    if not cfg["indu_enable"] or n == 0:
        return np.zeros(n)

    def _beep(freq: float, ms: float) -> np.ndarray:
        """单个电子 beep：基频 + 一点二次谐波，两端各 4ms 淡入淡出防爆音。"""
        k = max(1, int(sr * ms / 1000.0))
        t = np.arange(k) / sr
        w = np.sin(2.0 * math.pi * freq * t) + 0.25 * np.sin(2.0 * math.pi * 2.0 * freq * t)
        f = max(1, int(sr * 0.004))                     # 4ms
        if k > 2 * f:
            w[:f] *= np.linspace(0.0, 1.0, f)
            w[-f:] *= np.linspace(1.0, 0.0, f)
        return w * cfg["indu_beep_gain"]

    gap = np.zeros(max(1, int(sr * 0.030)))              # 两声之间约 30ms 静默
    head = np.concatenate([_beep(1046.5, 75.0), gap, _beep(1568.0, 95.0)])
    lead = len(head)

    # 贯穿段：低频嗡鸣，整段 100ms 淡入 / 150ms 淡出
    t = np.arange(n) / sr
    f0 = float(cfg["indu_hum_hz"])
    hum = np.sin(2.0 * math.pi * f0 * t) + 0.4 * np.sin(2.0 * math.pi * 2.0 * f0 * t)
    env = np.ones(n)
    fi, fo = min(int(sr * 0.100), n), min(int(sr * 0.150), n)
    if fi > 0:
        env[:fi] *= np.linspace(0.0, 1.0, fi)
    if fo > 0:
        env[-fo:] *= np.linspace(1.0, 0.0, fo)
    hum = hum * env * cfg["indu_hum_gain"]

    return np.concatenate([head, hum])


def robotize(x: np.ndarray, sr: int) -> np.ndarray:
    cfg = CONFIG

    if len(x) == 0:
        return x

    # 1) 电话带通
    y = biquad(x, sr, cfg["hp_hz"], "hp")
    y = biquad(y, sr, cfg["lp_hz"], "lp")

    # 2) 环形调制（机械味的主来源）
    t = np.arange(len(y)) / sr
    y = y * (1.0 + cfg["ring_depth"] * np.sin(2.0 * math.pi * cfg["ring_hz"] * t))

    # 3) 可选降采样
    if cfg["downsample_to"] > 0:
        y = resample_roundtrip(y, sr, cfg["downsample_to"])

    # 4) 比特压缩
    levels = 2 ** cfg["bits"]
    y = np.round(y * levels) / levels

    # 5) 归一化 + 淡入淡出
    peak = float(np.max(np.abs(y))) or 1.0
    y = y / peak * cfg["gain"]
    fade = max(1, int(sr * cfg["fade_ms"] / 1000.0))
    if len(y) > 2 * fade:
        y[:fade] *= np.linspace(0.0, 1.0, fade)
        y[-fade:] *= np.linspace(1.0, 0.0, fade)

    # 6) 叠加工业提示层（在归一化与淡入淡出之后；前置 beep 会让总时长变长约 0.2s，这是预期行为）
    if cfg["indu_enable"] and len(y) > 0:
        layer = industrial_layer(len(y), sr)
        layer[:len(y)] += y                     # 语音混入工业层的后半段，前半段（beep）保持干净
        peak = float(np.max(np.abs(layer))) or 1.0
        if peak > 1.0:                          # 叠加后超过满量程就重新限幅，避免削顶
            layer = layer / peak * cfg["gain"]
        y = layer
    return y


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--test", action="store_true", help="只处理前 3 个文件")
    ap.add_argument("--suffix", default="", help="输出文件名后缀，便于对比不同参数")
    args = ap.parse_args()

    files = sorted(RAW.glob("*.wav"))
    if not files:
        raise SystemExit(f"没有找到原始 wav，请先跑 tools/make-voice.ps1（目录：{RAW}）")
    if args.test:
        files = files[:3]

    done = 0
    for f in files:
        audio, sr = read_wav(f)
        out = OUT / (f.stem + args.suffix + ".wav")
        write_wav(out, robotize(audio, sr), sr)
        done += 1
        print(f"[{done}/{len(files)}] {f.name}  {len(audio)/sr:.2f}s @ {sr} Hz -> {out.name}")

    print(f"完成：{done} 个 → {OUT}")
    print("参数：" + json.dumps(CONFIG, ensure_ascii=False))


if __name__ == "__main__":
    main()
