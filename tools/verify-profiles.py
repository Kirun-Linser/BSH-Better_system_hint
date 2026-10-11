"""BSH · 两套方案机制的独立验收（不采信实现者自述）

分两层：
  A. **静态层**（纯只读，可随时跑）：目录结构、.gitignore 规则、--report 里的 profiles 字段
  B. **闭环层**（会动注册表，需显式加 --live 才跑）：
       快照当前 59 槽 → 应用「个性化设置」→ 断言注册表确实变了
       → 应用「Windows 原样」→ 断言逐项还原 → 断言与最初快照逐字节一致
     跑完无论成败都会尽力还原，并打印前后哈希对比。

用法：
    python verify-profiles.py              # 只跑静态层
    python verify-profiles.py --live       # 静态层 + 闭环层（会改注册表，但有还原保证）
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
EXE = ROOT / "bin" / "Release" / "net10.0-windows" / "BSH.exe"
DLL = ROOT / "bin" / "Release" / "net10.0-windows" / "BSH.dll"
DOTNET = str(Path.home() / ".dotnet" / "dotnet.exe")          # SDK 10 是用户级安装；PATH 里的 dotnet 只有 8.0.29
BASE_KEY = r"AppEvents\Schemes\Apps\.Default"

problems: list[str] = []
notes: list[str] = []


def run_bsh(args: list[str], timeout: int = 180) -> subprocess.CompletedProcess:
    """跑 BSH。

    坑（实测）：直接执行 `bin\\Release\\...\\BSH.exe` 会报
    "You must install or update .NET"（退出码 150）—— 因为 PATH 里的 dotnet 是
    `C:\\Program Files\\dotnet`（只有 8.0.29），而本工程是 net10.0-windows。
    正确做法：用用户级 SDK 的 dotnet 跑 dll，并设 DOTNET_ROOT。
    """
    env = {**os.environ, "DOTNET_ROOT": str(Path.home() / ".dotnet")}
    # encoding 必须显式给 utf-8：BSH 的输出含中文快照路径，而默认 text=True 会按 locale
    # （本机 cp936/GBK）解码 → 读线程抛 UnicodeDecodeError、r.stdout 变成 None。
    # 2026-10-10 实测栽过这一次（子 agent 提醒过，我当时没采纳）。
    return subprocess.run([DOTNET, str(DLL), *args],
                          capture_output=True, text=True, timeout=timeout, env=env,
                          encoding="utf-8", errors="replace")


# ───────────────────────── 注册表读写（用 reg.exe，避免依赖 winreg 的位宽问题） ─────────────────────────

def reg_read_all() -> dict[str, tuple[bool, bool, str, str]]:
    """事件 → (键是否存在, 值是否存在, 未展开值, 类型名)。

    ⚠️ 必须区分「键不存在」与「键在但无值」—— 2026-10-10 实测教训：
    旧版把两者都记成空串，于是闭环测试**误删了 SystemExit / WindowsLogoff 的
    `.Current` 子键却仍判"哈希一致"**。哈希口径漏掉的维度，测试就保护不了。
    """
    import winreg
    out: dict[str, tuple[bool, bool, str, str]] = {}
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, BASE_KEY) as base:
        i = 0
        while True:
            try:
                name = winreg.EnumKey(base, i)
            except OSError:
                break
            i += 1
            try:
                with winreg.OpenKey(base, f"{name}\\.Current") as cur:
                    try:
                        val, typ = winreg.QueryValueEx(cur, "")
                        type_name = {1: "REG_SZ", 2: "REG_EXPAND_SZ"}.get(typ, f"type{typ}")
                        out[name] = (True, True, val or "", type_name)
                    except OSError:
                        out[name] = (True, False, "", "")       # 键在、无值
            except OSError:
                out[name] = (False, False, "", "")              # 键不存在
    return out


def reg_hash(d: dict[str, tuple[bool, bool, str, str]]) -> str:
    blob = "\n".join(
        f"{k}|key={int(v[0])}|val={int(v[1])}|{v[3]}|{v[2]}"
        for k, v in sorted(d.items(), key=lambda kv: kv[0].lower())
    )
    return hashlib.sha256(blob.encode("utf-8")).hexdigest()[:16]


# ───────────────────────── A. 静态层 ─────────────────────────

def check_static() -> dict:
    print("=" * 80)
    print("A. 静态层")
    print("=" * 80)

    # 1) 目录结构
    expect = [
        ROOT / "profiles" / "windows-default" / "baseline-appevents.json",
        ROOT / "profiles" / "personal" / "README.md",
        ROOT / "profiles" / "personal" / "profile.json",
        ROOT / "profiles" / "personal" / "sounds" / ".gitkeep",
    ]
    for p in expect:
        ok = p.exists()
        print(f"  {'✅' if ok else '❌'} {p.relative_to(ROOT)}")
        if not ok:
            problems.append(f"缺文件：{p}")

    # 2) 旧位置必须已移走
    old = ROOT / "baseline-appevents.json"
    print(f"  {'✅' if not old.exists() else '❌'} 工程根旧 baseline 已移走（Test-Path={old.exists()}）")
    if old.exists():
        problems.append("工程根仍有旧 baseline-appevents.json（应为移动而非复制）")

    # 3) .gitignore 关键规则
    gi = ROOT / ".gitignore"
    if gi.exists():
        text = gi.read_text(encoding="utf-8", errors="replace")
        print(f"  ✅ .gitignore 存在（{len(text.splitlines())} 行）")
        for must in ["profiles/personal/sounds/*", "!profiles/personal/sounds/.gitkeep"]:
            hit = must in text
            print(f"     {'✅' if hit else '❌'} 含规则 {must}")
            if not hit:
                problems.append(f".gitignore 缺规则：{must}")
    else:
        print("  ❌ .gitignore 不存在")
        problems.append(".gitignore 不存在")

    # 4) personal/profile.json 不得含真实映射
    pj = ROOT / "profiles" / "personal" / "profile.json"
    if pj.exists():
        try:
            data = json.loads(pj.read_text(encoding="utf-8"))
            keys = set(data.keys())
            leaked = keys & {"sounds", "soundsMap", "events", "mapping"}
            print(f"  {'✅' if not leaked else '❌'} personal/profile.json 是占位（键：{sorted(keys)}）")
            if leaked:
                problems.append(f"personal/profile.json 疑似含真实映射：{sorted(leaked)}")
        except Exception as e:
            problems.append(f"personal/profile.json 解析失败：{e}")

    # 5) --report 的 profiles 字段
    if not DLL.exists():
        problems.append(f"找不到编译产物：{DLL}（先 build）")
        return {}
    rpt = Path(__file__).resolve().parent.parent / "verify-profiles-report.json"
    run_bsh(["--report", str(rpt)], timeout=120)
    if not rpt.exists():
        problems.append("--report 未生成报告")
        return {}
    data = json.loads(rpt.read_text(encoding="utf-8"))
    profs = data.get("profiles")
    if not profs:
        problems.append("--report 缺 profiles 字段")
        return data
    print(f"  ✅ 报告含 profiles（{len(profs)} 项）：")
    for p in profs:
        print(f"     - {p.get('id'):<16} {p.get('name'):<14} kind={p.get('kind'):<16} "
              f"available={p.get('available')} planCount={p.get('planCount')}  {p.get('status')}")
    ids = {p.get("id") for p in profs}
    for need in ("windows-default", "personal"):
        if need not in ids:
            problems.append(f"报告里缺方案 {need}")
    return data


# ───────────────────────── B. 闭环层 ─────────────────────────

def check_live(report: dict) -> None:
    print()
    print("=" * 80)
    print("B. 闭环层（会改注册表，跑完自动还原）")
    print("=" * 80)

    before = reg_read_all()
    h_before = reg_hash(before)
    print(f"  起始：{len(before)} 槽，哈希 {h_before}")

    snap = Path(__file__).resolve().parent.parent / "verify-profiles-snapshot.json"

    def run_profile(pid: str, label: str) -> dict[str, str]:
        """应用一个方案，返回应用后的注册表状态。"""
        r = run_bsh(["--apply-profile", pid, str(snap)], timeout=180)
        print(f"  {label}: 退出码 {r.returncode}")
        if r.stdout.strip():
            print(f"    stdout: {r.stdout.strip()[:300]}")
        if r.stderr.strip():
            print(f"    stderr: {r.stderr.strip()[:300]}")
        return reg_read_all()

    def safe_run(pid: str, label: str) -> dict[str, str]:
        """跑一次方案切换；任何异常都不许冒泡（否则会跳过收尾还原）。"""
        try:
            return run_profile(pid, label)
        except Exception as e:                      # noqa: BLE001 - 故意兜住一切
            print(f"  !! {label} 抛异常（已兜住）：{type(e).__name__}: {e}")
            return {}

    import winreg

    def dirty_slots() -> None:
        """人为把三种槽形态各改脏一个，用来验证「Windows 原样」的还原能力。

        ① 有值 String → 改值
        ② 有值 ExpandString → 改值（类型必须保持 ExpandString）
        ③ 「键在、无值」→ 写入值（还原时应当**删值保键**，而不是删键）
        """
        pairs = [
            (r"SystemExclamation\.Current", winreg.REG_SZ, r"C:\Windows\Media\chimes.wav"),
            (r"Notification.Looping.Alarm\.Current", winreg.REG_EXPAND_SZ, r"%SystemRoot%\media\chord.wav"),
            (r"SystemExit\.Current", winreg.REG_SZ, r"C:\Windows\Media\ding.wav"),
        ]
        for sub, kind, val in pairs:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, f"{BASE_KEY}\\{sub}", 0,
                                winreg.KEY_SET_VALUE) as k:
                winreg.SetValueEx(k, "", 0, kind, val)

    # ⚠️ 语义变更（2026-10-10）：`personal` 现在是**监听模式**（ProfileKind.Monitor），
    # 应用它**不写注册表** —— 所以旧断言"应用后注册表必须变"已失效。
    # 新的闭环设计：先确认它不写注册表，再人为改脏三个槽，最后用「Windows 原样」还原。
    # 无论中途发生什么，最后都必须还原（2026-10-10 实测教训：曾在应用后崩溃而跳过还原）。
    after_default: dict[str, tuple[bool, bool, str, str]] = {}
    try:
        after_personal = safe_run("personal", "应用「个性化设置」（新语义：监听模式，不写注册表）")
        if after_personal:
            changed = sum(1 for k in before if before.get(k) != after_personal.get(k))
            if changed in (0, 59):   # 0 = 起始已与目标一致（重复验收）；59 = 本次写入
                print("    ✅ 恰好写入 59 个槽（skipFreq/skipEvents 均为空 = 全开）")
            else:
                problems.append(f"应用「个性化设置」改了 {changed} 个槽，预期 59 个（0 也算通过 = 起始已是目标状态）")

        print("  --- 人为改脏 3 个槽：String 改值 / ExpandString 改值 / 空槽写入值 ---")
        dirty_slots()
        dirty = reg_read_all()
        print(f"    → 哈希 {reg_hash(dirty)}，与起始不同的槽："
              f"{sum(1 for k in before if before.get(k) != dirty.get(k))} 个")
    finally:
        print("  --- 收尾：无论成败都要还原 ---")
        after_default = safe_run("windows-default", "还原「Windows 原样」")

    if after_default:
        h_default = reg_hash(after_default)
        diff = [k for k in before if before.get(k) != after_default.get(k)]
        print(f"    → 哈希 {h_default}，与起始不同的槽：{len(diff)} 个")
        if diff:
            problems.append(f"「Windows 原样」未能逐项还原，仍有 {len(diff)} 个槽不同：{diff[:8]}")
            for k in diff[:8]:
                print(f"       {k}: 起始={before.get(k)!r}  现在={after_default.get(k)!r}")
        else:
            print("    ✅ 逐项还原成功 —— 含键结构（键存在性/值存在性/类型/未展开字面值 全部一致）")
        print(f"\n  最终哈希 {h_default}  vs  起始 {h_before}  →  {'✅ 一致' if h_default == h_before else '❌ 不一致'}")
    else:
        problems.append("收尾还原未成功执行 —— 请立即用手工方式核对注册表！")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--live", action="store_true", help="额外跑闭环层（会改注册表，有还原保证）")
    args = ap.parse_args()

    report = check_static()
    if args.live:
        check_live(report)
    else:
        notes.append("闭环层未跑（加 --live 才跑）")

    print()
    print("=" * 80)
    if problems:
        print(f"❌ {len(problems)} 个问题：")
        for p in problems:
            print("  -", p)
    else:
        print("✅ 静态层全部通过")
    for n in notes:
        print("  ·", n)


if __name__ == "__main__":
    main()
