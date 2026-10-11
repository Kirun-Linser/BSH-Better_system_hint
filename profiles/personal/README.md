# profiles/personal —— 个人配置（不进公开仓库）

这个目录放**本机个人化**的东西，属于「只对我有意义、对别人无意义」的一层，**不进入公开仓库**。

## 目录约定

| 路径 | 入库？ | 说明 |
|---|---|---|
| `profile.json` | ✅ 入库 | **开关文件**：`id` / `name` / `kind` / `applyVoiceScheme` / `skipFreq` / `skipEvents` / `note`（不含任何真实音频映射） |
| `README.md` | ✅ 入库 | 本文件 |
| `rules.example.json` | ✅ 入库 | **公开示例规则表**：8 类监听各一条、全部 `enabled: false`，每条带 `$comment` 说明 `match` 怎么写 |
| `rules.json` | ❌ 排除 | **真实规则表**（个人配置）：哪些操作要出声，就写在这里 |
| `sounds/` | ❌ 排除 | 真实音频（自制语音 wav）；仅 `.gitkeep` 例外入库 |

## 「个性化设置」现在的含义：换提示音 + 启用监听（两件事一起做）

2026-10-11 起，「个性化设置」**两个都做**：

```
① 改写系统提示音：按 voice-scheme.json 把事件槽指向自制语音（跳掉哪些槽由下面两个跳过键决定）
② 启用监听：监听器盯住 键盘 / 鼠标 / 窗口 / 进程 / 设备 / 电源 / 剪贴板 / 文件
            → 命中 rules.json 里启用的规则时，再补播一声你配的提示音
```

**开关在 `profile.json`：**

| 键 | 默认 | 含义 |
|---|---|---|
| `applyVoiceScheme` | `true` | `false` = 只启监听、注册表一个字节都不动（回到旧语义） |
| `skipFreq` | `["F5"]`（键缺失时的内置默认） | 要跳过的**频率档**数组。`F5` = 极高频界面音（最大化/最小化/打开/关闭/菜单/切换主题…共 11 个），Windows 11 默认这些槽本来就是空的，全写会让"每次点窗口都播报"。**改成 `[]` 就是一个档都不跳**；键缺失 = 默认 `["F5"]` |
| `skipEvents` | `[]`（键缺失时的内置默认） | 要跳过的**事件名**数组（黑名单），粒度到**单个事件**。名字按事件名写（`MenuCommand` / `MenuPopup` / `CCSelect` / `Maximize`…），大小写不敏感。**改成 `[]` / 不写 = 一个事件都不跳** —— 它没有 `skipFreq` 那样的历史默认值，不写就是不点名 |

**两个跳过键的区别（`skipEvents` 于 2026-10-11 新增）：**

| 键 | 粒度 | 判定依据 | 典型用法 |
|---|---|---|---|
| `skipFreq` | **整档**（粗刀） | 事件在 `voice\lines.json` 里的 `freq` 命中就跳，一档十几个一起走 | "F5 整档都不要" |
| `skipEvents` | **单个事件**（细刀） | 按事件名点名跳，**与该事件的 freq 无关** | "F5 里除了菜单那几个，其它我都要" |

**两者叠加生效（并集）**，判定顺序固定为「**先按 `skipFreq` 跳档、再按 `skipEvents` 跳事件**」：

- 同一事件两处都命中 → **只算一次**，归到"按档"（所以状态串里的「按档 N 个 + 按事件 M 个」恒等于跳过总数，不重复计数）；
- 「**整档都要、只掐掉其中几个**」→ 把 `skipFreq` 写成 `[]`，只给 `skipEvents`；
- 「整档跳、再按名字补几个」→ 两个键都给，后者接着前者判。

**本机当前就是这么配的**（`skipFreq: []` + 9 个 `skipEvents`）：F5 整档跳会连 `Open`/`Close` 一起掐掉，
而**打开/关闭程序是要有声的**；真正吵的是菜单/下拉/选择那三个（会让 Word 打开文档时连响多次）。
⇒ 改成"不整档跳、只点名跳 9 个"，最终写 **50** 槽（59 − 9），比 F5 整档跳的 48 槽**多回 `Open`/`Close` 两个**。
名单：`MenuCommand`、`MenuPopup`、`CCSelect`、`Maximize`、`Minimize`、`RestoreUp`、`RestoreDown`、`ChangeTheme`、`ShowBand`。

频率档来自仓库内文件 `voice\lines.json`（每个事件带 `freq`：F5 11 / F4 8 / F3 8 / F2 6 / F1 6 / F0 20）——
`voice-scheme.json` 本身**没有** freq 信息。**该文件读不到时自动降级为按档"全部写入"**（只影响 `skipFreq` 的整档判定，
`skipEvents` 是按名字点名的，照跳），并在 `--report` / `--monitor-status` / `--profile-plan` 的状态里说明，绝不因此让功能失败。

**执行顺序（刻意定的）**：**先写注册表、成功后再启监听引擎**。
中途失败最坏留下「音换了但没监听」—— 零常驻资源，而且「Windows 原样」能完整复原；
反过来最坏会留下「钩子在跑但音没换」，排查时噪音大得多。任何一半失败都抛
`PartialApplyException`，界面与命令行会把"做到哪一步"如实报出来。

**复原**：切回「Windows 原样」= 停引擎 + 按 `baseline-appevents.json` 逐槽还原 59 项
（含 22 个 `REG_EXPAND_SZ` 的类型保真与 2 个空壳槽的"删值保键"）。

- 没写规则 = **零钩子**（引擎按规则惰性装监听器）：不会白占系统资源。
- 切回「Windows 原样」或托盘「退出」会**卸载全部钩子 / 隐藏窗口 / 定时器**。

## 怎么开始

1. 把 `rules.example.json` 复制成同目录的 `rules.json`；
2. 按每条规则里的 `$comment` 改 `match`，把 `enabled` 改成 `true`；
3. 声音二选一：`sound` 填 wav 绝对路径，或 `speakKey` 填语音方案（`voice-scheme.json`）里的事件名；
4. 界面点「个性化设置（换提示音 + 启用监听）」；
5. 命令行核对：`BSH.exe --report`（看 `personal` 段的 `slotWriteCount` / `slotSkipCount`，
   以及拆开的 `skipFreq` / `skipEvents` / `slotSkipByFreqCount` / `slotSkipByEventCount`）、
   `BSH.exe --profile-plan personal`（逐条列出会写哪些槽、**被跳过的事件名单与各自的原因**）、
   `BSH.exe --monitor-status`（接线与监听器）。

`--report` / `--monitor-report` / `--monitor-status`（不加 `--live`）/ `--profile-plan` 都是**只读**的：
不写注册表、不启动引擎、也**不会**替你创建 `rules.json`。
⚠️ `--apply-profile personal` **会真写注册表**（与界面按钮同一条路径），验证完请用
`--apply-profile windows-default` 还原。

## 为什么 `sounds/` 被排除

自制音频体积大、且是个人素材（可能涉及第三方音源授权），一律不入库：
`.gitignore` 里用 `profiles/personal/sounds/*` 排除，再用 `!profiles/personal/sounds/.gitkeep` 把占位文件捞回来，
这样目录结构能保留、音频不进仓库。

## 历史：`kind: VoiceScheme` 那条路

`ProfileKind.VoiceScheme`（整表把 59 个事件改成指向自制 wav）**保留在代码里**，只是不再单独挂在
「个性化设置」上 —— 它的逻辑现在被「个性化设置」复用（见 `ProfileStore.WriteVoiceSchemeSlots`，
`Apply(VoiceScheme)` 那条老路径仍在，只是没有任何方案在用它）；语音素材流程
（`tools\make-voice.ps1` → `robotize.py` → `install-voice.ps1`）产出的
`%LOCALAPPDATA%\BSH\sounds\voice-scheme.json`，既当"改事件槽"的数据源，也当监听规则 `speakKey` 的音源库。
