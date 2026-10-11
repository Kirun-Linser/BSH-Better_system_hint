"""只读四元哈希取证：AppEvents\\Schemes\\Apps\\.Default 下每个事件槽的
「未展开值 + 类型 + 键是否存在 + 值是否存在」四元组的 sha256。

用途：证明某条命令（--report / --monitor-status）前后注册表**逐槽一模一样** —— 一个字节都没动。
只读：只用 winreg 的 KEY_READ / KEY_WOW64_64KEY，不 SetValue、不 CreateKey、不 DeleteKey。
"""

import hashlib
import json
import sys
import winreg

BASE = r"AppEvents\Schemes\Apps\.Default"
FLAGS = winreg.KEY_READ | winreg.KEY_WOW64_64KEY


def read_all() -> dict[str, list]:
    """枚举全部事件槽 → [字面值, 类型名, 键是否存在, 值是否存在]。"""
    out: dict[str, list] = {}
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, BASE, 0, FLAGS) as base:
        idx = 0
        while True:
            try:
                name = winreg.EnumKey(base, idx)
            except OSError:
                break
            idx += 1
            try:
                with winreg.OpenKey(base, rf"{name}\.Current", 0, FLAGS) as cur:
                    names = [v[0] for v in _values(cur)]
                    has = "" in names
                    if has:
                        val, kind = winreg.QueryValueEx(cur, "")
                        val = "" if val is None else str(val)
                    else:
                        val, kind = None, None
                    out[name] = [val, _kind(kind), True, has]
            except FileNotFoundError:
                out[name] = [None, None, False, False]
            except OSError as e:
                out[name] = [f"<ERR {e.winerror}>", None, False, False]
    return out


def _values(key):
    idx = 0
    while True:
        try:
            yield winreg.EnumValue(key, idx)
        except OSError:
            return
        idx += 1


def _kind(kind):
    if kind is None:
        return None
    return {
        winreg.REG_SZ: "String",
        winreg.REG_EXPAND_SZ: "ExpandString",
        winreg.REG_MULTI_SZ: "MultiString",
        winreg.REG_DWORD: "DWord",
        winreg.REG_BINARY: "Binary",
        winreg.REG_QWORD: "QWord",
        winreg.REG_NONE: "None",
    }.get(kind, f"kind={kind}")


def digest(data: dict[str, list]) -> str:
    """按槽名排序后序列化再哈希（顺序无关，内容敏感）。"""
    blob = json.dumps({k: data[k] for k in sorted(data)}, ensure_ascii=False, separators=(",", ":"))
    return hashlib.sha256(blob.encode("utf-8")).hexdigest()


if __name__ == "__main__":
    label = sys.argv[1] if len(sys.argv) > 1 else ""
    data = read_all()
    print(f"{label} slots={len(data)} hash={digest(data)}")
    print(f"{label} detail=" + json.dumps({k: data[k] for k in sorted(data)}, ensure_ascii=False)[:1600])
