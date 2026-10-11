# BSH 增量扩展方案：应用维度规则 + 预设体系

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 BSH 支持「为特定应用设计独特提示音」，并把现有的「一套语音方案」升级为可切换、可导入导出的「预设」。

**Architecture:** 不重构。现有四层（注册表读写 / 方案登记 / 监听引擎 / 界面）职责已清晰，本方案只在两处生长：① 监听引擎补一个「当前前台应用」状态并把它接进匹配器；② 在方案层之上加一层「预设」= 一套声音映射 + 一套规则集的打包。

**Tech Stack:** C# / net10.0-windows / WinForms；规则表 JSON（`RuleStore`）；语音资产 JSON（`voice-scheme.json`）。

**Spec:** 本文件即规格（用户 2026-10-11 口述定位）：
> 「BSH：一款含预设的可自定义系统提示音的软件，可为特定应用设计独特提示音。作用方法：将自身预设或用户设定一键替换到 Windows 系统，同时支持监听用户操作从而为特定应用设计独特提示音。」

## Global Constraints

- 只写 `HKCU\AppEvents\Schemes\Apps\.Default\<事件>\.Current`；不碰 `HKLM`、不改系统文件、不需要管理员。
- 还原必须**类型保真**（`REG_EXPAND_SZ` 不得降级为 `REG_SZ`）—— 由 `SchemeSnapshot` 与 `SoundScheme.RestoreSlot` 保证，本次不改其口径。
- 规则表与预设里的**个人配置不入公开仓库**（`.gitignore` 已排除 `profiles/**/rules.json`；预设若含个人音频同理）。
- **向后兼容**：现有 `profiles/personal/rules.json` 与 `profile.json` 必须继续可用，不需要用户手工迁移。
- 界面文案全中文；颜色一律取自 `AppTheme.Palette`，不在界面代码里写死 RGB。
- 每条规则命中后**只兑现第一条**（现有语义，`MonitorEngine` 保证），本次不改。

## Review Focus

以下五类是本方案最可能被踩到、但任何单任务测试都不覆盖的边界：

1. **前台应用状态陈旧** —— 用户在 Chrome 里操作，但 BSH 是在 Chrome 之后启动的，或用户从未切换过窗口；期望 `app: chrome.exe` 的规则仍能命中。
2. **规则写了不存在的应用名** —— 例如 `app: "chrome"`（漏了 `.exe`）；期望是「永不命中」而不是「命中所有应用」。
3. **一个事件同时被两条规则命中，其中一条带 `app`** —— 期望带 `app` 的那条优先于无 `app` 的通用规则。
4. **预设内含不存在的音频文件** —— 期望应用预设时报出是哪个文件缺失并**跳过该槽**，而不是整批失败。
5. **`app` 与现有 `match` 同时给出且互相矛盾** —— 例如 `app: notepad.exe` + `match: "stop explorer.exe"`；期望两者都满足才命中（AND 语义），不是任一满足。

---

## 一、现状与缺口（有代码证据）

| 定位里的能力 | 现状 | 证据 |
|---|---|---|
| 一键替换到 Windows | ✅ 已有 | `Core/SoundScheme.cs`（类型保真）、`Core/ProfileStore.cs`（`PlanDetailed` / `Apply`） |
| 监听用户操作 | ✅ 已有 8 类 | `Core/Monitoring/*`（键盘/鼠标/窗口/进程/设备/电源/剪贴板/文件） |
| 可自定义 | ✅ 已有 | `rules.json`（规则）+ `voice/lines.json`（台词真源）+ `install-voice.ps1`（一键重装） |
| **含「预设」** | ❌ 缺 | `SoundProfile` 只有 `RestoreBaseline` / `Monitor` / `VoiceScheme` 三类，没有「多套可切换预设」 |
| **为特定应用设计提示音** | ⚠️ 部分 | 只有 `ProcessStartStop` 白名单（某程序**启动/关闭**时播某音）；规则的匹配维度里没有「当前应用」 |

**改动面已被收敛到一个点**：全工程只有 `MonitorEngine.cs:263` 调用 `MonitorMatcher.IsMatch(rule, evt.Detail)`。
补一个「当前前台应用」状态并加进这个调用，即可让**所有 8 类监听器**都获得「限定应用」的能力。

---

## 二、目标一：规则的「应用维度」

### 2.1 规则表新增字段

```json
{
  "id": "chrome-save",
  "name": "在 Chrome 里按 Ctrl+S",
  "enabled": true,
  "kind": "Keyboard",
  "app": "chrome.exe",
  "match": "Ctrl+S",
  "speakKey": "MenuCommand",
  "cooldownMs": 300
}
```

| 字段 | 语义 |
|---|---|
| `app` | **可选**。非空时，只有**当前前台应用**匹配才继续判 `match`；空或缺省 = 不限应用（**向后兼容**，老规则不受影响） |
| 匹配方式 | 与 `match` 里的进程名同款：**整词、忽略大小写**，支持用 `,` / `;` / `|` 列多个（如 `chrome.exe,msedge.exe`） |
| 组合语义 | `app` 与 `match` 是 **AND**：两者都满足才命中 |

### 2.2 「当前前台应用」从哪来

新增 `Core/Monitoring/ForegroundTracker.cs`（约 40 行）：

- **状态**：`static string Current`（当前前台进程名，小写）
- **更新时机**：
  1. 引擎启动时主动查一次 `GetForegroundWindow` + `GetWindowThreadProcessId`（**解决 Review Focus 1 的「状态陈旧」**）
  2. 每次 `WindowSwitch` 事件到达时更新（事件明细是 `进程名|标题`，取竖线前那段）
- **失败时**：拿不到就置 `""`，此时**带 `app` 的规则一律不命中**（安全侧），并写进 `--monitor-status` 供排查

> ⚠️ **诚实的边界**：这是「**前台窗口**」语义，不是「事件来源进程」。例如「Chrome 在后台下载完一个文件」时前台可能是别的程序，写 `app: chrome.exe` 的文件规则**不会命中**。要支持「按事件来源进程归因」需要 ETW / 进程级文件审计，**本方案不做**，README 里写明。

### 2.3 改动清单

| 文件 | 改动 |
|---|---|
| `Core/Monitoring/MonitoringRule.cs` | 加 `public string App { get; set; } = "";` |
| `Core/Monitoring/ForegroundTracker.cs` | **新建**（上述状态类） |
| `Core/Monitoring/MonitorMatcher.cs` | `IsMatch(rule, detail, app)` 加第三参；`app` 非空时先比 `app` 再比 `match` |
| `Core/Monitoring/MonitorEngine.cs:263` | 调 `IsMatch(rule, evt.Detail, ForegroundTracker.Current)` |
| `Core/Monitoring/MonitorEngine.cs`（事件入口） | `WindowSwitch` 事件到达时 `ForegroundTracker.Update(detail)` |
| `profiles/personal/rules.example.json` | 加一条带 `app` 的示例（默认 `enabled: false`） |
| `README.md` | 规则表字段表加 `app` 行 + 上面那条边界说明 |

**不新增监听器、不改钩子、不动注册表**——这是本方案最重要的性质。

---

## 三、目标二：预设体系

### 3.1 预设是什么

**一个预设 = 一套「事件 → 音频」映射 + 一套规则集**，可整体应用到系统。

```
profiles/presets/<预设id>/
├─ preset.json     预设定义（名字 / 说明 / 是否内置 / 声音映射）
└─ sounds/         该预设自带的音频（可为空 = 复用 %LOCALAPPDATA%\BSH\sounds）
```

```json
{
  "id": "eva-mech",
  "name": "EVA 机械女声",
  "description": "Edge TTS 合成 + EQ 塑形，59 槽全配",
  "builtin": true,
  "sounds": {
    "SystemAsterisk": "sounds/信息（星号）,Information.wav",
    "Maximize": "sounds/最大化窗口,Readiness.wav"
  },
  "rules": [
    { "id": "app-open", "kind": "ProcessStartStop", "app": "", "match": "start chrome.exe", "speakKey": "Open" }
  ]
}
```

### 3.2 与现有「方案」的关系

- `windows-default`（Windows 原样）**保持不变** —— 它永远是「还原到基线」的唯一出口。
- `personal`（个性化设置）**保留**，语义不变。
- **新增** `preset:<id>`：应用它 = ① 按 `sounds` 写槽（**先自动快照**）② 按 `rules` 装监听器。
- 预设**叠加**在基线上：应用预设前不必先还原（`SoundScheme` 的写入是逐槽覆盖，未覆盖的槽保持原样）。

### 3.3 内置预设的取材

**不新造音频**。内置预设直接复用已有的资产与链路：

| 预设 | 声音来源 | 规则来源 |
|---|---|---|
| `eva-mech`（当前这套） | `%LOCALAPPDATA%\BSH\sounds`（现有 39 条） | 现有 `rules.json` |
| 后续可加 | `tools/make-edge-batch.py` 换个音色参数即可批量产出 | 复用同一套规则 |

⇒ **预设体系的第一个可交付版本只需要「打包 + 切换」**，不需要新音频。

---

## 四、界面（草图，阶段二才动）

```
┌─ BSH · 更好的系统提示 ─────────────────────────────┐
│ [预设 ▾]  [应用预设]   [Windows 原样]   [个性化设置] │   ← 新增下拉：内置/我的预设
├────────────────────────────────────────────────────┤
│ 事件                        说明      分组  当前音效  状态 │
│ SystemAsterisk              信息（星号） 提示  …       正常 │
│ …（39 行）                                          │
├────────────────────────────────────────────────────┤
│ 方案：EVA 机械女声 · 引擎：运行中：1 个监听器 · 写 36 槽 │
└────────────────────────────────────────────────────┘

规则编辑（阶段三，可选）：面板加「应用」列，单元格里可选
  ┌─────────────────────────┐
  │ 给哪个应用配？（可留空） │
  │  [ chrome.exe      ▾ ]  │  ← 下拉列出当前正在运行的进程
  └─────────────────────────┘
```

---

## 五、迁移与兼容

| 现有资产 | 处理 |
|---|---|
| `profiles/personal/rules.json` | **不改**。没有 `app` 字段 ⇒ 解析为 `""` ⇒ 行为与现在完全一致 |
| `profiles/personal/profile.json` | **不改**。`personal` 仍是 `Monitor` 类型 |
| `%LOCALAPPDATA%\BSH\sounds` + `voice-scheme.json` | **不动**。预设 `eva-mech` 直接引用它 |
| `baseline-appevents.json` | **不动**（还原真源） |
| 旧的 `voice-scheme.json`（39 条） | 仍可用；预设是**更上层**的概念，不替换它 |

**兼容性验收**：把现有 `rules.json` 原样喂给新版，`--monitor-selftest` 与 `--profile-plan personal` 的输出必须与升级前**逐字一致**。

---

## 六、任务分解

### Task 1: `app` 字段进规则模型（无行为变化）

**Files:**
- Modify: `Core/Monitoring/MonitoringRule.cs`
- Modify: `profiles/personal/rules.example.json`（加一条带 `app` 的示例）
- Modify: `README.md`（字段表加 `app` 行）

**Interfaces:**
- Produces: `MonitorRule.App`（`string`，默认 `""`）

- [ ] **Step 1: 加字段**

`MonitorRule` 里加 `public string App { get; set; } = "";`，XML 注释写明「可选；非空时限定前台应用；与 match 是 AND」。

- [ ] **Step 2: 验证老规则仍解析**

Run: `dotnet "bin/Release/net10.0-windows/BSH.dll" --monitor-status`
Expected: 输出的规则条数与字段与改动前一致（`RuleStore` 用 `CamelCase` + 忽略大小写，多一个未知字段不影响反序列化；这里验的是「老 JSON 仍能读」）。

- [ ] **Step 3: 验证新字段能读**

把 `app` 加进 `rules.json` 的一条规则，重跑 `--monitor-status`，确认该规则被列出且无解析错误。

- [ ] **Step 4: Commit**

```bash
git add Core/Monitoring/MonitoringRule.cs profiles/personal/rules.example.json README.md
git commit -m "规则模型：新增可选的 app 字段（限定前台应用）"
```

### Task 2: `ForegroundTracker`

**Files:**
- Create: `Core/Monitoring/ForegroundTracker.cs`

**Interfaces:**
- Produces: `ForegroundTracker.Current`（`string`，小写进程名或 `""`）、`ForegroundTracker.Refresh()`（主动查一次前台窗口）、`ForegroundTracker.Update(string detail)`（从 `进程名|标题` 更新）

- [ ] **Step 1: 实现三个成员**

P/Invoke `GetForegroundWindow` + `GetWindowThreadProcessId`；`Update(detail)` 取 `detail` 里第一个 `|` 之前的部分并 `ToLowerInvariant()`。任何异常 ⇒ `Current = ""`。

- [ ] **Step 2: 写自检并运行**

在 `--monitor-status` 里加一行输出当前 `ForegroundTracker.Current`。
Run: `dotnet "bin/Release/net10.0-windows/BSH.dll" --monitor-status`
Expected: 打印出**你此刻前台窗口的进程名**（例如 `devenv.exe` 或 `explorer.exe`）—— 拿任务管理器核对一致。

- [ ] **Step 3: Commit**

```bash
git add Core/Monitoring/ForegroundTracker.cs
git commit -m "新增 ForegroundTracker：跟踪当前前台应用"
```

### Task 3: 接进匹配器与引擎

**Files:**
- Modify: `Core/Monitoring/MonitorMatcher.cs`（`IsMatch` 加第三参）
- Modify: `Core/Monitoring/MonitorEngine.cs:263`（传参）、事件入口（`Update`）

**Interfaces:**
- Consumes: `MonitorRule.App`（Task 1）、`ForegroundTracker`（Task 2）
- Produces: `MonitorMatcher.IsMatch(MonitorRule rule, string detail, string foregroundApp)`

- [ ] **Step 1: 写失败测试**

在 `--monitor-selftest` 里加一段：造一条 `app: "绝不存在的程序.exe"` 的键盘规则，注入 `F24`，**期望 0 次命中**；再造一条 `app` 为空的同样规则，**期望命中**。

Run: `dotnet "bin/Release/net10.0-windows/BSH.dll" --monitor-selftest 5`
Expected: FAIL（现在还不看 `app`，两条都会命中）

- [ ] **Step 2: 实现 AND 语义**

`IsMatch(rule, detail, app)`：`rule.App` 非空且与 `app` 不匹配（整词、忽略大小写、支持 `,` / `;` / `|` 多值）⇒ 直接 `false`；否则照原逻辑判 `match`。

- [ ] **Step 3: 接进引擎**

`MonitorEngine.cs:263` 改传 `ForegroundTracker.Current`；`WindowSwitch` 事件到达时先 `ForegroundTracker.Update(evt.Detail)`；引擎 `Start()` 时调一次 `Refresh()`。

- [ ] **Step 4: 重跑自检**

Run: `dotnet "bin/Release/net10.0-windows/BSH.dll" --monitor-selftest 5`
Expected: PASS（不存在的 app ⇒ 0 命中；空 app ⇒ 命中）

- [ ] **Step 5: 兼容性回归**

Run: `dotnet "bin/Release/net10.0-windows/BSH.dll" --profile-plan personal`
Expected: 输出与升级前逐字一致

- [ ] **Step 6: Commit**

```bash
git add Core/Monitoring/MonitorMatcher.cs Core/Monitoring/MonitorEngine.cs Core/Monitoring/MonitorSelfTest.cs
git commit -m "匹配器接入前台应用维度：app 与 match 是 AND"
```

### Task 4: 预设加载器（只读）

**Files:**
- Create: `Core/PresetStore.cs`

**Interfaces:**
- Produces: `PresetStore.List()` → `IReadOnlyList<PresetInfo>`；`PresetStore.Load(id)` → `Preset?`
- `PresetInfo`：`{ Id, Name, Description, Builtin, SoundCount, RuleCount }`
- `Preset`：`{ Info, Sounds: Dictionary<string,string>, Rules: List<MonitorRule> }`

- [ ] **Step 1: 定义与实现**

扫 `profiles/presets/*/preset.json`。文件缺失 / JSON 坏 ⇒ **跳过该预设并记原因，不抛异常**（与 `RuleStore.Load` 同款容错口径）。

- [ ] **Step 2: 验证**

Run: `dotnet "bin/Release/net10.0-windows/BSH.dll" --preset-list`
Expected: 列出发现的预设；没有预设时输出「未发现预设」并 exit 0

- [ ] **Step 3: Commit**

```bash
git add Core/PresetStore.cs Program.cs
git commit -m "新增 PresetStore：只读加载 profiles/presets/*/preset.json"
```
### Task 5: 应用预设

**Files:**
- Modify: `Core/ProfileStore.cs`（`preset:<id>` 分支）、`Program.cs`（新增 CLI）
- Create: `profiles/presets/eva-mech/preset.json`（把现有 39 条映射与规则打包成第一个内置预设）

**Interfaces:**
- Consumes: `PresetStore.Load`（Task 4）
- Produces: CLI `--apply-preset <id>`（先自动快照再写槽）

- [ ] **Step 1: 实现应用路径**

① `SchemeSnapshot.Capture` 快照 → ② 逐槽 `SoundScheme.SetSound` → ③ 按预设的 `rules` 起引擎。
**音频文件缺失的槽跳过并汇总报告**（对应 Review Focus 4），不整批失败。

- [ ] **Step 2: 端到端验证**

Run: `dotnet "bin/Release/net10.0-windows/BSH.dll" --apply-preset eva-mech`
Expected: 输出 `changed=<n> snapshot=<路径>`

Run: `dotnet "bin/Release/net10.0-windows/BSH.dll" --report`
Expected: 39 槽仍指向语音、`missingAssets` 为空

Run: `dotnet "bin/Release/net10.0-windows/BSH.dll" --apply-profile windows-default`
Expected: 还原后四元哈希回到原样基线值

- [ ] **Step 3: Commit**

```bash
git add Core/ProfileStore.cs Program.cs profiles/presets/eva-mech/preset.json
git commit -m "新增内置预设 eva-mech 与 --apply-preset"
```

### Task 6: 预设进界面

**Files:**
- Modify: `MainForm.cs`（按钮行加预设下拉）、`TrayContext.cs`（托盘菜单加子菜单）

- [ ] **Step 1: 加下拉与「应用预设」按钮**

复用现有 `MakeButton` 与 `ApplyProfile` 的确认框流程；下拉项来自 `PresetStore.List()`；浅色/深色档颜色一律取自 `AppTheme.Palette`。

- [ ] **Step 2: 界面自检（沿用现有探针）**

用 `/c/Users/11/.dsh/tmp/bsh-probe` 的探针模式创建 `MainForm`，断言下拉项数 == `PresetStore.List().Count` 并打印各项文本。

- [ ] **Step 3: Commit**

### Task 7: 文档

- [ ] README 加「预设」一节、`app` 字段行，以及**边界说明**：`app` 判定的是**前台窗口**，不是事件来源进程
- [ ] 本方案留在 `docs/plans/` 作为规格

---

## 七、分阶段交付与验收

| 阶段 | 交付 | 可独立验收的标准 |
|---|---|---|
| **一** | Task 1-3：`app` 维度 | 能写「在 chrome 里按 Ctrl+S 播某音」，且**老规则行为零变化** |
| **二** | Task 4-6：预设体系 | 能列出 / 切换 / 应用预设；应用后可一键还原；音频缺失不炸 |
| **三** | 可视化规则编辑（本方案不含，另行规划） | — |

---

## 八、风险与取舍（先说清）

| 项 | 说明 |
|---|---|
| **前台窗口 ≠ 事件来源** | `app` 只能按前台窗口判定。后台进程引发的事件（下载完成、索引器写文件）无法归因到来源应用。这是**设计边界**，不是缺陷 |
| **`GetForegroundWindow` 有竞态** | 前台切换与事件几乎同时发生时可能读到旧值。缓解：`WindowSwitch` 事件里同步更新；无法彻底消除 |
| **预设与 `personal` 会互相打架** | 两者都能写槽。为遵守既有约定，应用预设时**先停监听引擎再写**（沿用 `windows-default` 的顺序） |
| **内置预设的音频分发** | 音频体积大且可能涉及音源授权，**不入公开仓库**；预设只存**映射**，音频缺失时明确报告 |

---

## 九、Self-Review 记录

- **Spec 覆盖**：「含预设」→ Task 4-6；「可自定义」→ 已有，本方案不动；「为特定应用设计独特提示音」→ Task 1-3；「一键替换到 Windows」→ 已有 + Task 5；「监听用户操作」→ 已有。**无遗漏**。
- **步骤可执行性**：每步都给了命令与期望输出（`--monitor-status` / `--monitor-selftest` / `--profile-plan` / `--apply-preset` / `--preset-list`）。
- **类型一致性**：`MonitorMatcher.IsMatch(rule, detail, app)` 三参形式在 Task 3 各步一致；`PresetStore.Load` 的返回类型在 Task 4/5 一致。
- **Review Focus 落位**：五条边界分别落到 Task 3 Step 1（状态陈旧 / 不存在的应用名）、Task 3 Step 2（AND 语义）、Task 4 Step 1（坏文件容错）、Task 5 Step 1（音频缺失跳过）。
- **比例**：本方案 300 余行，对应的是两个子系统（匹配维度 + 预设体系），不是代码转录。

---

## 十、实施顺序修订（2026-10-11 21:1x，用户指定）

用户明确：**先写预设，再写可编程提示音，且后者先只做「对应应用打开与关闭」的适配。**

这带来两处调整：

### 10.1 任务顺序调整为

| 新顺序 | 内容 | 原编号 |
|---|---|---|
| **阶段一** | 预设体系（`PresetStore` → `--preset-list` → 内置预设 → `--apply-preset` → 界面） | 原 Task 4-6 |
| **阶段二** | 「应用打开/关闭」的适配（`ProcessStartStop` 上的 `app` 字段 + 界面化） | 原 Task 1-3 的**子集** |
| 阶段三 | 其它监听器的应用维度（需 `ForegroundTracker`，暂时不做） | 原 Task 1-3 余下部分 |

### 10.2 `app` 字段的语义按 Kind 区分（修正原设计）

原方案让 `app` 一律匹配「前台窗口」，**这对阶段二是多余的**：对 `ProcessStartStop` 而言，
事件明细里本来就带着进程名（`start chrome.exe` / `stop chrome.exe`），直接匹配它即可 ——
**精确、无竞态、不需要 `ForegroundTracker`**。

修正后的语义：

| `Kind` | `app` 匹配对象 | 何时可用 |
|---|---|---|
| `ProcessStartStop` | **事件里的那个进程名**（启动/退出的目标） | ✅ 阶段二即可用 |
| 其它 7 类 | 前台窗口进程 | 阶段三（需 `ForegroundTracker`，有竞态，方案见 §2.2） |

这条修正让阶段二变得**又小又准**：不需要新监听器、不需要前台跟踪、连新匹配分支都可以复用现有的
`MatchProcess`。预计改动量：`MonitorMatcher` 一个分支 + `MonitorRule` 一个字段 + 规则表示例 + README。

### 10.3 阶段二的最小可用形态

现有 `rules.json` 写成这样（冗长，且每加一个程序要改两处）：

```json
{ "kind": "ProcessStartStop", "match": "start WINWORD.EXE,start EXCEL.EXE,start POWERPNT.EXE,start msedge.exe,…" }
```

阶段二之后可写成（一个程序一条，增删互不影响）：

```json
{ "id": "open-chrome", "name": "Chrome 启动", "kind": "ProcessStartStop", "app": "chrome.exe", "match": "start", "speakKey": "Open" }
```

即 `app` 负责「哪个程序」，`match` 只负责「启动还是退出」。
