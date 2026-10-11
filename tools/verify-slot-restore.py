"""BSH · 「空槽还原不精确」修复的独立只读验收（绝不写注册表）

背景（我方实测确认的两个缺陷）：
  A. ProfileStore.RestoreBaseline 把基线里 Kind=Missing 的槽走成了 DeleteSound → **删掉整个 .Current 子键**。
     本机 SystemExit / WindowsLogoff 的 Windows 原样是「键存在但零个值」，被这样一跑就**结构变了**。
  B. SchemeSnapshot.Apply 对 Kind=Unknown 的槽「保持不动」→ 快照捕获时是空的、之后被别的方案写进了值，
     还原时那个值**留在注册表里**，根本没还原。

本脚本用**只读投影**（--slot-plan，纯造出来的 fixture，不碰注册表）证明修复后的分派：
     键在无值 → DeleteValue（删默认值、保留子键）
     键不存在 → DeleteSubKeyTree
     有值 ExpandString → SetValue(ExpandString)

四节：
  a. fixture 分派：--slot-plan 逐槽动作（三组必测 + 一组"键+空串"的显式静音）
  b. 报告：--report 的 profiles 段 planCount（windows-default=0 / personal=59）
  c. 只读性：--report 前后注册表四元联合哈希（键是否存在+值是否存在+类型+未展开字面值）一致
  d. 现场形态：SystemExit / WindowsLogoff 到底是「键在无值」还是「键不存在」

⚠️ 全程**只调用** --slot-plan 与 --report 两个只读命令；脚本自己也不写注册表。
   本脚本不会、也不能触发任何 Apply / 还原路径。

用法：
    python tools\\verify-slot-restore.py
"""

from __future__ import annotations

import hashlib
import json
import os
import re
import subprocess
import sys
import tempfile
import winreg
from pathlib import Path

# 控制台编码：本机默认 cp936，✅/❌ 这类字符会直接抛 UnicodeEncodeError —— 显式切成 UTF-8
# （BSH 侧同理，它的 stdout 固定 UTF-8 无 BOM，所以两边都按 UTF-8 收发）
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:                                   # noqa: BLE001 - 老环境没有 reconfigure 就算了
    pass

ROOT = Path(__file__).resolve().parent.parent
DLL = ROOT / "bin" / "Release" / "net10.0-windows" / "BSH.dll"
DOTNET = str(Path.home() / ".dotnet" / "dotnet.exe")      # SDK 10 是用户级安装，PATH 里那个只有 8.0.29
DOTNET_ROOT = str(Path.home() / ".dotnet")
BASE_KEY = r"AppEvents\Schemes\Apps\.Default"

# ★ 只读命令白名单：脚本只允许调这两个参数，任何写路径都不在射程内
READ_ONLY_ARGS = ("--slot-plan", "--report")

problems: list[str] = []


def ok(cond: bool, msg: str) -> bool:
    """打一行 ✅/❌；失败时登记问题。返回是否通过。"""
    print(f"  {'✅' if cond else '❌'} {msg}")
    if not cond:
        problems.append(msg)
    return cond


def run_bsh(args: list[str], timeout: int = 180) -> subprocess.CompletedProcess:
    """跑 BSH（UTF-8 显式指定：输出里可能有中文路径，按 locale 解码会炸）。"""
    assert args and args[0] in READ_ONLY_ARGS, f"本脚本只允许只读命令，收到：{args}"
    env = {**os.environ, "DOTNET_ROOT": DOTNET_ROOT}
    return subprocess.run([DOTNET, str(DLL), *args], capture_output=True, text=True,
                          timeout=timeout, env=env, encoding="utf-8", errors="replace")


def parse_kv(line: str) -> dict[str, str]:
    """把 'slot-plan slots=4 setValue=2 … path=C:\\x.json' 解析成字典。"""
    out: dict[str, str] = {}
    for token in line.split():
        if "=" in token:
            k, v = token.split("=", 1)
            out[k] = v
    return out


# ─────────────────────── a. fixture：只读投影证明还原分派 ───────────────────────

def build_fixture() -> Path:
    """造一个 4 槽的 fixture（**新格式快照**，与还原走同一个 Load）。

    前三条就是验收要求的三组：
      ① SystemExit      键在、无值（本机 Windows 原样）        → DeleteValue
      ② Ghost.NoSuchKey 键不存在                              → DeleteSubKeyTree
      ③ Alarm           REG_EXPAND_SZ + %SystemRoot% 字面值    → SetValue(ExpandString)
    第四条是补的边界：值是空串的 REG_SZ（"显式静音"，**有**默认值）→ 必须 SetValue(String)，
    不能与"键在、零个值"混为一谈。
    """
    fixture = {
        "CreatedAt": "2026-10-10 00:00:00",
        "Note": "tools/verify-slot-restore.py 造的分派 fixture（只读投影用，不写注册表）",
        "Slots": [
            {"Event": "SystemExit", "RawWav": None, "Kind": "Unknown", "KeyExists": True},
            {"Event": "Ghost.NoSuchKey", "RawWav": None, "Kind": "Unknown", "KeyExists": False},
            {"Event": "Notification.Looping.Alarm", "RawWav": r"%SystemRoot%\media\Alarm01.wav",
             "Kind": "ExpandString", "KeyExists": True},
            {"Event": "AppGPFault", "RawWav": "", "Kind": "String", "KeyExists": True},
        ],
    }
    path = Path(tempfile.gettempdir()) / "bsh-verify-slot-restore-fixture.json"
    path.write_text(json.dumps(fixture, ensure_ascii=False, indent=2), encoding="utf-8")
    return path


def section_a() -> None:
    print("=" * 88)
    print("a. fixture 分派：--slot-plan（只读投影，未写注册表）")
    print("=" * 88)

    fixture = build_fixture()
    r = run_bsh(["--slot-plan", str(fixture)])
    out = (r.stdout or "").strip()
    print(f"  退出码 {r.returncode}")
    for line in out.splitlines():
        print(f"  {line}")

    if not ok(r.returncode == 0 and out, "--slot-plan 正常退出并打印投影"):
        return

    # 逐槽解析（value= 里可能含空格与反斜杠，所以按字段正则取，别按空格切）
    # 组序：1=序号 2=事件 3=keyExists 4=valueExists 5=kind 6=value 7=action
    steps = []
    for line in out.splitlines():
        m = re.search(r"slot\s+(\d+)\s+event=(\S+)\s+keyExists=(\w+)\s+valueExists=(\w+)\s+"
                      r"kind=(\S+)\s+value=(.*?)\s+action=(\S+)", line)
        if m:
            steps.append({"idx": int(m.group(1)), "event": m.group(2), "keyExists": m.group(3),
                          "valueExists": m.group(4), "kind": m.group(5), "value": m.group(6),
                          "action": m.group(7)})
    if not ok(len(steps) == 4, f"投影逐槽可解析（{len(steps)} 条）"):
        return

    summary = parse_kv(out.splitlines()[-1])
    print(f"  末行摘要：{out.splitlines()[-1]}")

    # ① 键在无值 → DeleteValue（**不是** DeleteSubKeyTree，也不是"不动"）
    got = steps[0]
    ok(got["event"] == "SystemExit" and got["keyExists"] == "True" and got["valueExists"] == "False"
       and got["action"] == "DeleteValue",
       f"① 键在无值 → 动作 = {got['action']}（期望 DeleteValue：删默认值、保留子键）")

    # ② 键不存在 → DeleteSubKeyTree
    got = steps[1]
    ok(got["keyExists"] == "False" and got["action"] == "DeleteSubKeyTree",
       f"② 键不存在 → 动作 = {got['action']}（期望 DeleteSubKeyTree）")

    # ③ 有值 ExpandString → SetValue(ExpandString)，且字面值未被展开
    got = steps[2]
    ok(got["kind"] == "ExpandString" and got["action"] == "SetValue(ExpandString)",
       f"③ 有值 ExpandString → 动作 = {got['action']}（期望 SetValue(ExpandString)）")
    ok("%SystemRoot%" in out, "③ 投影里保留的是 %SystemRoot% 字面值（未被展开成 C:\\WINDOWS）")

    # ④ 边界：空串 REG_SZ（显式静音）是"有值"，必须 SetValue(String)，不能当空壳
    got = steps[3]
    ok(got["event"] == "AppGPFault" and got["valueExists"] == "True"
       and got["action"] == "SetValue(String)",
       f"④ 键+空串（显式静音）→ 动作 = {got['action']}（期望 SetValue(String)，别当空壳删值）")

    ok(summary.get("slots") == "4" and summary.get("setValue") == "2"
       and summary.get("deleteValue") == "1" and summary.get("deleteSubKey") == "1",
       f"摘要计数 slots=4 setValue=2 deleteValue=1 deleteSubKey=1（实得 "
       f"slots={summary.get('slots')} setValue={summary.get('setValue')} "
       f"deleteValue={summary.get('deleteValue')} deleteSubKey={summary.get('deleteSubKey')}）")
    ok(summary.get("slots") is not None
       and int(summary["setValue"]) + int(summary["deleteValue"]) + int(summary["deleteSubKey"])
       == int(summary["slots"]),
       "三桶之和 = 槽位数（每个槽都有明确动作，**没有『保持不动』这一档**）")


# ─────────────────────── b. 报告：profiles 段 planCount ───────────────────────

def section_b() -> None:
    print()
    print("=" * 88)
    print("b. --report 的 profiles 段（与改动前一致：windows-default=0 / personal=59）")
    print("=" * 88)

    rpt = Path(tempfile.gettempdir()) / "bsh-verify-slot-restore-report.json"
    r = run_bsh(["--report", str(rpt)])
    ok(r.returncode == 0 and rpt.exists(), "--report 正常退出并生成报告")
    if not rpt.exists():
        return

    data = json.loads(rpt.read_text(encoding="utf-8"))
    for p in data.get("profiles", []):
        # 字段一律用 .get()：Monitor 模式没有 planCount，只有 plannedRegistryWrites
        print(f"     - {p.get('id'):<16} kind={p.get('kind'):<16} available={p.get('available')} "
              f"planCount={p.get('planCount')} writes={p.get('plannedRegistryWrites')}  {p.get('status')}")

    profs = {p["id"]: p for p in data.get("profiles", [])}
    ok(profs.get("windows-default", {}).get("planCount") == 0,
       f"windows-default planCount = 0（实得 {profs.get('windows-default', {}).get('planCount')}）")
    # ⚠️ 语义变更（2026-10-10）：personal 已改为「监听模式」（ProfileKind.Monitor），
    # 不再写注册表 —— 旧断言 planCount==59 已失效。
    _p = profs.get("personal", {})
    ok(_p.get("kind") == "Monitor", f"personal kind = Monitor（实得 {_p.get('kind')}）")
    _writes = _p.get("plannedRegistryWrites", _p.get("planCount"))
    ok(_writes == 59, f"personal 会写 59 个槽（全开，实得 {_writes}）")


# ─────────────── c. 只读性：四元联合哈希（能区分"键在无值"与"键不存在"） ───────────────

def read_truth() -> dict[str, dict]:
    """事件 → {key_exists, value_count, value_exists, kind, literal}。

    ⚠️ 口径必须四元：**键是否存在** + **值是否存在** + 类型 + 未展开字面值。
    旧脚本只记"读出来的值"，于是"键在无值"与"键不存在"都成了空串 —— 误删了子键却仍判"哈希一致"
    （2026-10-10 实测教训）。
    """
    truth: dict[str, dict] = {}
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, BASE_KEY) as base:
        events, i = [], 0
        while True:
            try:
                events.append(winreg.EnumKey(base, i))
            except OSError:
                break
            i += 1

        for ev in events:
            rec = {"event": ev, "key_exists": False, "value_count": 0,
                   "value_exists": False, "kind": None, "literal": None}
            try:
                cur = winreg.OpenKey(base, ev + r"\.Current")
            except FileNotFoundError:
                truth[ev] = rec               # .Current 子键不存在
                continue
            with cur:
                rec["key_exists"] = True
                rec["value_count"] = winreg.QueryInfoKey(cur)[1]
                names = [winreg.EnumValue(cur, j)[0] for j in range(rec["value_count"])]
                if "" in names:               # 默认值的名字就是空串
                    rec["value_exists"] = True
                    val, typ = winreg.QueryValueEx(cur, "")
                    rec["kind"] = {winreg.REG_SZ: "REG_SZ",
                                   winreg.REG_EXPAND_SZ: "REG_EXPAND_SZ"}.get(typ, f"REG_TYPE_{typ}")
                    rec["literal"] = val      # QueryValueEx 不展开 REG_EXPAND_SZ
            truth[ev] = rec
    return truth


def truth_digest(truth: dict[str, dict]) -> str:
    """「键是否存在 + 值是否存在 + 类型 + 未展开字面值」的联合哈希。"""
    lines = [
        f"{ev}|key={int(r['key_exists'])}|val={int(r['value_exists'])}|kind={r['kind']}|lit={r['literal']}"
        for ev, r in sorted(truth.items(), key=lambda kv: kv[0].lower())
    ]
    return hashlib.sha256("\n".join(lines).encode("utf-8")).hexdigest()[:16]


def section_c() -> dict[str, dict]:
    print()
    print("=" * 88)
    print("c. 只读性：--report 前后四元联合哈希（键存在+值存在+类型+未展开字面值）")
    print("=" * 88)

    before = read_truth()
    h_before = truth_digest(before)

    rpt = Path(tempfile.gettempdir()) / "bsh-verify-slot-restore-report2.json"
    r = run_bsh(["--report", str(rpt)])

    after = read_truth()
    h_after = truth_digest(after)

    print(f"  before = {h_before}")
    print(f"  after  = {h_after}")
    ok(r.returncode == 0, "--report 退出码 0")
    ok(h_before == h_after, "注册表在 --report 前后完全一致（命令确实只读）")
    ok(before == after, "四元逐槽比对一致（哈希之外再逐项核一遍）")

    # 口径自证：哈希输入里必须同时含 key= 与 val=，否则区分不出"键在无值/键不存在"
    sample = next((r_ for r_ in before.values() if r_["key_exists"] and not r_["value_exists"]), None)
    if sample is not None:
        print(f"  口径自证：{sample['event']} key_exists={sample['key_exists']} "
              f"value_count={sample['value_count']} value_exists={sample['value_exists']} "
              f"→ 该槽在哈希里是 key=1|val=0，与「键不存在」的 key=0|val=0 **可区分**")
    return before


# ─────────────────────── d. 现场形态：两个空槽到底长什么样 ───────────────────────

def section_d(truth: dict[str, dict]) -> None:
    print()
    print("=" * 88)
    print("d. 现场形态：SystemExit / WindowsLogoff（应为「键在、无值」，值个数 0）")
    print("=" * 88)

    for ev in ("SystemExit", "WindowsLogoff"):
        rec = truth.get(ev)
        if rec is None:
            ok(False, f"{ev} 在注册表里根本不存在")
            continue
        print(f"     - {ev}：.Current 键={'存在' if rec['key_exists'] else '不存在'}，"
              f"值个数={rec['value_count']}，默认值={'存在' if rec['value_exists'] else '不存在'}"
              f"，类型={rec['kind']}")
        ok(rec["key_exists"] and not rec["value_exists"] and rec["value_count"] == 0,
           f"{ev} = 键在、无值（value_count=0）")


def main() -> int:
    print(f"BSH 空槽还原语义验收（全程只读）｜DLL = {DLL}")
    if not DLL.exists():
        print(f"❌ 找不到编译产物：{DLL}（先 dotnet build -c Release）")
        return 2

    section_a()
    section_b()
    truth = section_c()
    section_d(truth)

    print()
    print("=" * 88)
    if problems:
        print(f"❌ {len(problems)} 项未通过：")
        for p in problems:
            print("  -", p)
        return 1
    print("✅ 全部通过：fixture 分派正确（DeleteValue / DeleteSubKeyTree / SetValue(ExpandString)）、"
          "profiles 未变、--report 纯只读、两个空槽仍是「键在、无值」")
    return 0


if __name__ == "__main__":
    sys.exit(main())
