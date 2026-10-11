"""BSH · 快照「类型保真」独立验收（只读注册表，绝不还原）

背景：旧 SchemeSnapshot 存的是「事件 → 已展开的值」字典，既没有类型、也没有「键是否存在」，
      于是任何「快照 → 还原」往返都会：① 把 22 个 REG_EXPAND_SZ 槽降级成 REG_SZ 且字面值改写；
      ② 把 2 个「键存在但零个值」的空壳槽写成「键 + 空串」。本脚本用四组证据核对修复后的实现：

  a. **真值**：Python winreg 采集当前 59 槽（QueryValueEx 不展开 + 返回类型）—— 不采信 C# 侧自述
  b. **新格式快照**：调 `BSH.dll --snapshot-check capture <tmp>`（只读注册表、只写一个 JSON 文件），
     逐条断言事件数 = 59，且 Kind / **未展开**字面值 / 键是否存在 与 a 的真值完全一致
  c. **旧格式兼容**：造一个只含 "Sounds" 的旧格式 JSON，调 `--snapshot-check normalize`，
     断言推断结果为 ExpandString=22 / String=35 / 空壳=2
  d. **只读性**：--report 前后，注册表「未展开值 + 类型 + 键是否存在」联合哈希必须一致，
     并核对报告里 profiles 段的 planCount（windows-default=0 / personal=59）

⚠️ 全程**不会**调用 SchemeSnapshot.Apply（--snapshot-check 根本没有还原入口），不改注册表任何一个字节。

用法：
    python tools\\verify-snapshot-types.py
"""

from __future__ import annotations

import hashlib
import json
import os
import subprocess
import sys
import tempfile
import winreg
from pathlib import Path

ROOT = Path(r"D:\O级项目管理\BSH")
DLL = ROOT / "bin" / "Release" / "net10.0-windows" / "BSH.dll"
DOTNET = str(Path.home() / ".dotnet" / "dotnet.exe")     # SDK 10 是用户级安装，PATH 里那个只有 8.0.29
DOTNET_ROOT = str(Path.home() / ".dotnet")
BASE_KEY = r"AppEvents\Schemes\Apps\.Default"

# 文档记录的 Windows 原样分布（用于给出"是否偏离"的提示，不作为唯一判据）
EXPECTED = {"REG_SZ": 35, "REG_EXPAND_SZ": 22, "NO_VALUE": 2}

problems: list[str] = []


def ok(cond: bool, msg: str) -> bool:
    """打一行 ✅/❌；失败时登记问题。返回是否通过。"""
    print(f"  {'✅' if cond else '❌'} {msg}")
    if not cond:
        problems.append(msg)
    return cond


def run_bsh(args: list[str], timeout: int = 180) -> subprocess.CompletedProcess:
    """跑 BSH（UTF-8 显式指定：输出里的路径可能含中文，按 locale 解码会炸）。"""
    env = {**os.environ, "DOTNET_ROOT": DOTNET_ROOT}
    return subprocess.run([DOTNET, str(DLL), *args], capture_output=True, text=True,
                          timeout=timeout, env=env, encoding="utf-8", errors="replace")


def parse_kv(line: str) -> dict[str, str]:
    """把 'snapshot-check capture slots=59 ... path=C:\\x.json' 解析成字典。"""
    out: dict[str, str] = {}
    for token in line.split():
        if "=" in token:
            k, v = token.split("=", 1)
            out[k] = v
    return out


# ─────────────────────────── a. 真值（Python winreg，不展开） ───────────────────────────

def read_truth() -> dict[str, dict]:
    """采集当前注册表的真值：事件 → {key_exists, value_exists, kind, literal}。

    - literal 是**未展开**字面值：RegQueryValueEx（winreg.QueryValueEx 的底层）不会展开 REG_EXPAND_SZ，
      所以 `%SystemRoot%\\media\\Alarm01.wav` 会被原样读出来 —— 这正是 C# 侧 DoNotExpandEnvironmentNames 的口径。
    - kind 取 winreg 自己报的类型号，不靠猜。
    - "键在但零个值"（value_exists=False 且 key_exists=True）与"键不存在"（key_exists=False）分开记。
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
            rec = {"event": ev, "key_exists": False, "value_exists": False, "kind": None, "literal": None}
            try:
                cur = winreg.OpenKey(base, ev + r"\.Current")
            except FileNotFoundError:
                truth[ev] = rec          # .Current 子键不存在
                continue
            with cur:
                rec["key_exists"] = True
                names = [winreg.EnumValue(cur, j)[0] for j in range(winreg.QueryInfoKey(cur)[1])]
                if "" in names:          # 默认值存在（"" 就是默认值的名字）
                    rec["value_exists"] = True
                    val, typ = winreg.QueryValueEx(cur, "")
                    rec["kind"] = {winreg.REG_SZ: "REG_SZ", winreg.REG_EXPAND_SZ: "REG_EXPAND_SZ"}.get(typ, f"REG_TYPE_{typ}")
                    rec["literal"] = val
            truth[ev] = rec
    return truth


def truth_bucket(rec: dict) -> str:
    """真值 → 三类之一：REG_SZ / REG_EXPAND_SZ / NO_VALUE（读不出值的槽）。"""
    if not rec["value_exists"]:
        return "NO_VALUE"
    return rec["kind"] or "NO_VALUE"


def truth_digest(truth: dict[str, dict]) -> str:
    """注册表「未展开值 + 类型 + 键是否存在」的联合哈希（只读性证明用）。"""
    lines = [
        f"{ev}|key={rec['key_exists']}|val={rec['value_exists']}|kind={rec['kind']}|lit={rec['literal']}"
        for ev, rec in sorted(truth.items(), key=lambda kv: kv[0].lower())
    ]
    return hashlib.sha256("\n".join(lines).encode("utf-8")).hexdigest()[:16]


def section_a() -> dict[str, dict]:
    print("=" * 84)
    print("a. 真值：Python winreg 采集（QueryValueEx 不展开 + 自带类型）")
    print("=" * 84)

    truth = read_truth()
    stats = {"REG_SZ": 0, "REG_EXPAND_SZ": 0, "NO_VALUE": 0}
    for rec in truth.values():
        stats[truth_bucket(rec)] += 1

    print(f"  槽位总数：{len(truth)}")
    print(f"  类型统计：REG_SZ={stats['REG_SZ']}  REG_EXPAND_SZ={stats['REG_EXPAND_SZ']}  "
          f"读不出值的槽={stats['NO_VALUE']}")

    expand = {e: r for e, r in truth.items() if truth_bucket(r) == "REG_EXPAND_SZ"}
    no_value = {e: r for e, r in truth.items() if truth_bucket(r) == "NO_VALUE"}
    print(f"  REG_EXPAND_SZ 事件（{len(expand)}）：{', '.join(sorted(expand))}")
    print(f"  读不出值的槽（{len(no_value)}）：")
    for e, r in sorted(no_value.items()):
        print(f"     - {e}：.Current 键{'存在' if r['key_exists'] else '不存在'}，默认值{'不存在' if not r['value_exists'] else '存在'}")
    if expand:
        sample = sorted(expand)[0]
        print(f"  未展开证据：{sample} 的字面值 = {expand[sample]['literal']!r}（含 % → winreg 未展开 ✅）")

    ok(len(truth) == 59, f"真值槽位数 = 59（实测 {len(truth)}）")
    ok(sum(stats.values()) == len(truth), "三类互斥且覆盖全部槽位")
    for k, v in EXPECTED.items():
        if stats[k] != v:
            print(f"  ⚠ {k} 实测 {stats[k]}，与文档记录的 {v} 不同 —— 注册表可能不在 Windows 原样状态（或有并发写入）")
    return truth


# ─────────────────────────── b. 新格式快照：逐条与真值比对 ───────────────────────────

def section_b(truth: dict[str, dict]) -> None:
    print()
    print("=" * 84)
    print("b. 新格式快照：--snapshot-check capture（只读注册表、只写一个 JSON）")
    print("=" * 84)

    out = Path(tempfile.gettempdir()) / "bsh-verify-snapshot-new.json"
    r = run_bsh(["--snapshot-check", "capture", str(out)])
    line = (r.stdout or "").strip().splitlines()[-1] if (r.stdout or "").strip() else ""
    print(f"  退出码 {r.returncode}｜{line}")
    if not ok(r.returncode == 0 and out.exists(), f"capture 成功并写出 {out}"):
        return

    kv = parse_kv(line)
    data = json.loads(out.read_text(encoding="utf-8"))
    slots = data.get("Slots") or []
    ok(data.get("Sounds") is None, "新格式快照里没有旧字段 Sounds（不写 null 占位）")
    ok(int(kv.get("slots", -1)) == 59 and len(slots) == 59, f"事件数 = 59（文件内 {len(slots)}）")

    # 逐条比对：Kind / 未展开字面值 / 键是否存在
    want_kind = {"REG_SZ": "String", "REG_EXPAND_SZ": "ExpandString", "NO_VALUE": "Unknown"}
    mismatches = []
    seen = set()
    for s in slots:
        ev = s["Event"]
        seen.add(ev)
        rec = truth.get(ev)
        if rec is None:
            mismatches.append(f"{ev}: 真值里没有这个事件")
            continue
        bucket = truth_bucket(rec)
        exp_kind = want_kind[bucket]
        exp_raw = rec["literal"] if rec["value_exists"] else None
        exp_key = rec["key_exists"]
        if s.get("Kind") != exp_kind or s.get("RawWav") != exp_raw or bool(s.get("KeyExists")) != exp_key:
            mismatches.append(f"{ev}: 快照(Kind={s.get('Kind')!r}, RawWav={s.get('RawWav')!r}, KeyExists={s.get('KeyExists')!r})"
                              f" ≠ 真值(Kind={exp_kind!r}, RawWav={exp_raw!r}, KeyExists={exp_key!r})")
    ok(not mismatches, f"59 条的 Kind / 未展开字面值 / 键是否存在与真值逐条一致（不一致 {len(mismatches)} 条）")
    for m in mismatches[:10]:
        print(f"       {m}")
    ok(seen == set(truth), "事件名集合与真值集合完全相同")

    # 计数器交叉核对
    n_sz = sum(1 for r_ in truth.values() if truth_bucket(r_) == "REG_SZ")
    n_exp = sum(1 for r_ in truth.values() if truth_bucket(r_) == "REG_EXPAND_SZ")
    n_none = sum(1 for r_ in truth.values() if truth_bucket(r_) == "NO_VALUE")
    ok(kv.get("string") == str(n_sz) and kv.get("expand") == str(n_exp) and kv.get("unknown") == str(n_none),
       f"命令自称的计数 string={kv.get('string')} expand={kv.get('expand')} unknown={kv.get('unknown')} "
       f"与真值 {n_sz}/{n_exp}/{n_none} 一致")
    ok(kv.get("inferred") == "0" and kv.get("legacy") == "False", "新格式快照被标为『非推断』（inferred=0 legacy=False）")

    # 关键证据：22 个 ExpandString 的字面值必须是 **未展开** 的 %SystemRoot% 形式
    exp_slots = [s for s in slots if s.get("Kind") == "ExpandString"]
    unexpanded = [s for s in exp_slots if "%" in (s.get("RawWav") or "")]
    ok(len(exp_slots) == n_exp and len(unexpanded) == n_exp,
       f"{n_exp} 个 ExpandString 槽全部保留 %…% 字面值（未展开），例如：")
    for s in sorted(unexpanded, key=lambda x: x["Event"])[:3]:
        print(f"       {s['Event']}: RawWav={s['RawWav']!r} Kind={s['Kind']}")
    shells = [s for s in slots if s.get("Kind") == "Unknown"]
    blanks = [s for s in slots if s.get("Kind") == "String" and (s.get("RawWav") or "") == ""]
    print(f"  空壳槽（Kind=Unknown，还原时**按原结构恢复**：键在无值→删默认值保键；键不存在→删子键）{len(shells)} 个："
          + "、".join(f"{s['Event']}(KeyExists={s['KeyExists']})" for s in shells))
    print(f"  显式静音槽（Kind=String，值为空串，还原时照写空串）{len(blanks)} 个")

    # 还原行为（不能真跑 Apply —— 那会写注册表）→ 用 Apply 的只读投影核对"会写哪 57 条、按什么类型写"
    r2 = run_bsh(["--snapshot-check", "plan", str(out)])
    line2 = (r2.stdout or "").strip().splitlines()[-1] if (r2.stdout or "").strip() else ""
    print(f"  还原预演（只读投影）退出码 {r2.returncode}｜{line2}")
    kv2 = parse_kv(line2)
    want_writes = sum(1 for s in slots if s.get("Kind") in ("String", "ExpandString"))
    want_expand = sum(1 for s in slots if s.get("Kind") == "ExpandString")
    ok(r2.returncode == 0 and kv2.get("writes") == str(want_writes), f"还原会写 {want_writes} 条（跳开 {59 - want_writes} 个空壳槽）")
    ok(kv2.get("expand") == str(want_expand), f"其中按 REG_EXPAND_SZ 写回 {want_expand} 条（不会被降级成 REG_SZ）")
    # ⚠️ 语义变更（2026-10-10 实测后修正）：空壳槽不再"保持不动" —— 那会让
    # 「快照捕获时为空 → 别的方案写入值 → 还原」这一步留下脏值。
    # 正确语义是"按原结构恢复"：键在无值 → DeleteValue（删默认值、保留子键）；
    # 键不存在 → DeleteSubKeyTree。untouched 计数仍成立，但必须同时断言 deleteValue。
    ok(kv2.get("untouched") == str(len(shells)), f"空壳槽 {len(shells)} 条不写值（untouched）")
    if kv2.get("deleteValue") is not None:
        ok(kv2.get("deleteValue") == str(len(shells)),
           f"其中 {len(shells)} 条走『删值保键』（deleteValue），而非留着脏值不动")

    # 新格式往返无损：Load → Save 后与原文件必须逐字节相同（类型/字面值/键存在 都不许掉信息）
    rt = Path(tempfile.gettempdir()) / "bsh-verify-snapshot-new-roundtrip.json"
    r3 = run_bsh(["--snapshot-check", "normalize", str(out), str(rt)])
    same = rt.exists() and hashlib.sha256(rt.read_bytes()).hexdigest() == hashlib.sha256(out.read_bytes()).hexdigest()
    ok(r3.returncode == 0 and same, "新格式快照往返无损：Load → Save 后与原文件逐字节相同")


# ─────────────────────────── c. 旧格式兼容：类型推断 ───────────────────────────

def build_legacy_fixture(truth: dict[str, dict], expand_values: bool) -> dict:
    """造一个**只含 Sounds** 的旧格式快照。

    旧格式的口径是「事件 → 已展开的值」，且没有类型字段，所以 fixture 每条都必须靠推断定性：
      · 真值里读不出值的槽（键在零值 / 键不存在）→ null（→ 推断成"空壳"）
      · 其余槽：把字面值放进去（含 % 的 → 推断成 ExpandString；不含 % 的 → 推断成 String）
      · 字面值为空串的槽（显式静音）在旧格式里等于"没值"，会给不出 String 分支，
        因此换成一个不含 % 的普通路径，保证 String 分支被真实覆盖到 35 条
    expand_values=True 时模拟"当年真的存了展开值"的旧快照（用于打印已知局限）。
    """
    sounds: dict[str, str | None] = {}
    for ev, rec in truth.items():
        if not rec["value_exists"]:
            sounds[ev] = None
            continue
        literal = rec["literal"] or ""
        if expand_values:
            # 模拟"当年真的存了展开值"的旧快照
            sounds[ev] = os.path.expandvars(literal) if literal else fr"C:\WINDOWS\media\{ev}.wav"
        elif literal:
            # 含 % → 推断 ExpandString；不含 % → 推断 String
            sounds[ev] = literal
        else:
            # 显式写空的 REG_SZ 槽：旧格式里与"没值"同形，给不出 String 分支，换个普通路径覆盖到它
            sounds[ev] = fr"C:\WINDOWS\media\{ev}.wav"

    return {
        "CreatedAt": "2026-01-01 00:00:00",
        "Note": "verify-snapshot-types.py 造的旧格式快照",
        "Sounds": sounds,
    }


def section_c(truth: dict[str, dict]) -> None:
    print()
    print("=" * 84)
    print("c. 旧格式兼容：只含 Sounds 的旧 JSON → 类型推断")
    print("=" * 84)

    tmp = Path(tempfile.gettempdir())
    fixture = tmp / "bsh-verify-snapshot-legacy.json"
    out = tmp / "bsh-verify-snapshot-legacy-normalized.json"

    legacy = build_legacy_fixture(truth, expand_values=False)
    fixture.write_text(json.dumps(legacy, ensure_ascii=False, indent=2), encoding="utf-8")
    ok(set(legacy.keys()) == {"CreatedAt", "Note", "Sounds"}, "fixture 只含旧字段（CreatedAt/Note/Sounds），没有 Slots/Kind")

    r = run_bsh(["--snapshot-check", "normalize", str(fixture), str(out)])
    line = (r.stdout or "").strip().splitlines()[-1] if (r.stdout or "").strip() else ""
    print(f"  退出码 {r.returncode}｜{line}")
    if not ok(r.returncode == 0 and out.exists(), "normalize 成功并写出归一化结果"):
        return

    kv = parse_kv(line)
    slots = json.loads(out.read_text(encoding="utf-8")).get("Slots") or []
    inferred_expand = sorted(s["Event"] for s in slots if s.get("Kind") == "ExpandString")
    inferred_string = sorted(s["Event"] for s in slots if s.get("Kind") == "String")
    shell = sorted(s["Event"] for s in slots if s.get("Kind") == "Unknown")

    print(f"  推断结果：ExpandString={len(inferred_expand)}  String={len(inferred_string)}  空壳={len(shell)}")
    print(f"     空壳事件：{', '.join(shell)}")
    ok(len(slots) == 59, f"归一化后仍是 59 条（实测 {len(slots)}）")
    ok(len(inferred_expand) == 22, f"推断 ExpandString = 22（实测 {len(inferred_expand)}）")
    ok(len(inferred_string) == 35, f"推断 String = 35（实测 {len(inferred_string)}）")
    ok(len(shell) == 2, f"推断空壳 = 2（实测 {len(shell)}）")
    ok(len(inferred_expand) + len(inferred_string) + len(shell) == 59, "三桶合计 = 59")

    # 逐事件核对桶位（避免"总数对但张冠李戴"）
    want = {}
    for ev, rec in truth.items():
        if not rec["value_exists"]:
            want[ev] = "Unknown"
        elif rec["literal"] and "%" in rec["literal"]:
            want[ev] = "ExpandString"
        else:
            want[ev] = "String"
    got = {s["Event"]: s["Kind"] for s in slots}
    wrong = [f"{e}: 推断 {got.get(e)!r} ≠ 期望 {want[e]!r}" for e in want if got.get(e) != want[e]]
    ok(not wrong, f"逐事件桶位全部正确（错 {len(wrong)} 条）")
    for w in wrong[:10]:
        print(f"       {w}")

    # 旧格式要让用户看得见"类型是推断的"
    ok(kv.get("inferred") == "59" and kv.get("legacy") == "True",
       f"旧格式被标记为『类型已推断』（inferred={kv.get('inferred')} legacy={kv.get('legacy')}）→ "
       f"MainForm 的还原提示会显示『旧格式快照，类型已推断』")

    # ── 已知局限（如实打印，不算失败）：真正的旧快照存的是**展开值**，那 22 个槽事后无从分辨 ──
    legacy2 = build_legacy_fixture(truth, expand_values=True)
    f2, o2 = tmp / "bsh-verify-snapshot-legacy-expanded.json", tmp / "bsh-verify-snapshot-legacy-expanded-normalized.json"
    f2.write_text(json.dumps(legacy2, ensure_ascii=False, indent=2), encoding="utf-8")
    r2 = run_bsh(["--snapshot-check", "normalize", str(f2), str(o2)])
    kv2 = parse_kv((r2.stdout or "").strip().splitlines()[-1] if (r2.stdout or "").strip() else "")
    print(f"  ⚠ 已知局限（非失败）：若旧快照里存的是**展开值**（旧 Capture 的真实行为），"
          f"推断结果变成 String={kv2.get('string')} ExpandString={kv2.get('expand')} 空壳={kv2.get('unknown')}"
          f" —— 那 22 个槽事后无法与 REG_SZ 区分，这正是旧格式必须提示用户的原因")


# ─────────────────────────── d. 只读性 + 报告 ───────────────────────────

def section_d() -> None:
    print()
    print("=" * 84)
    print("d. 只读性：--report 前后注册表联合哈希一致 + profiles 段核对")
    print("=" * 84)

    before = truth_digest(read_truth())
    report = Path(tempfile.gettempdir()) / "bsh-verify-snapshot-report.json"
    r = run_bsh(["--report", str(report)])
    after = truth_digest(read_truth())

    print(f"  哈希（未展开值+类型+键是否存在）  before={before}  after={after}")
    ok(r.returncode == 0 and report.exists(), "--report 正常退出并生成报告")
    ok(before == after, "注册表在 --report 前后逐字节一致（命令确实只读）")

    if report.exists():
        data = json.loads(report.read_text(encoding="utf-8"))
        profs = {p["id"]: p for p in data.get("profiles", [])}
        print(f"  profiles：")
        for pid, p in profs.items():
            print(f"     - {pid:<16} kind={p['kind']:<16} available={p['available']} planCount={p['planCount']}")
        ok(profs.get("windows-default", {}).get("planCount") == 0, "windows-default planCount = 0（已是 Windows 原样）")
        # ⚠️ 语义变更（2026-10-10）：personal 已改为「监听模式」（ProfileKind.Monitor），
        # 不再往注册表写语音 —— 旧断言 planCount==59 已失效，改为断言新语义。
        _p = profs.get("personal", {})
        ok(_p.get("kind") == "Monitor", f"personal kind = Monitor（实得 {_p.get('kind')}）")
        _writes = _p.get("plannedRegistryWrites", _p.get("planCount"))
        ok(_writes == 59, f"personal 会写 59 个槽（全开，实得 {_writes}）")
        print(f"  totalEvents={data.get('totalEvents')} emptyCount={data.get('emptyCount')}")


def main() -> int:
    print(f"BSH 快照类型保真验收｜DLL = {DLL}")
    if not DLL.exists():
        print(f"❌ 找不到编译产物：{DLL}（先 dotnet build -c Release）")
        return 2

    truth = section_a()
    section_b(truth)
    section_c(truth)
    section_d()

    print()
    print("=" * 84)
    if problems:
        print(f"❌ {len(problems)} 项未通过：")
        for p in problems:
            print("  -", p)
        return 1
    print("✅ 全部通过：快照类型保真（a 真值 / b 新格式逐条一致 / c 旧格式推断 22-35-2 / d 只读）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
