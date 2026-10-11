# BSH · 更好的系统提示（Better System Hints）

一个接管 Windows 系统提示音的 C# WinForms 托盘工具（`net10.0-windows`）。

它解决的是这个问题：**系统自带的提示音不够全面** —— Windows 的声音设置只覆盖「登录 / 出错 / 设备连接」这类少数场景，而你真正想被提醒的时刻（按下 CapsLock、插上 U 盘、Steam 启动、下载目录有变化、锁屏解锁……）系统一声不吭。

BSH 给两条互不冲突的路：

- **「Windows 原样」**：把 59 个系统声音事件槽**逐字节还原**成改造前的原始值（含注册表类型与「键在无值」形态），并停止监听 —— 一键回到出厂状态。
- **「个性化设置」**：**不写注册表**，改为启动监听引擎，按 `profiles\personal\rules.json` 里启用的规则安装钩子，命中时补播你配的提示音。

> 一切写入都只在 `HKCU`（当前用户），**不需要管理员权限**，不改任何系统文件。

---

## 1. 功能一览

### 1.1 两套方案

| 方案 id | 界面名 | 类型 | 真实动作 | 写注册表？ |
|---|---|---|---|---|
| `windows-default` | Windows 原样（还原 + 停止监听） | `RestoreBaseline` | **先停监听引擎**，再把 59 槽逐项还原成基线值 | ✅ 写 `HKCU` |
| `personal` | 个性化设置（启用监听） | `Monitor` | 按规则表启动监听引擎；启用 0 条规则 = 零钩子 | ❌ 一个注册表 API 都不调 |

- 内置方案登记见 `Core/ProfileStore.cs:52`，`personal` 的 `Kind` 是 `Monitor`（`Core/ProfileStore.cs:61`）。
- 还原是**类型保真**的：基线里 22 个槽原本是 `REG_EXPAND_SZ`（字面值 `%SystemRoot%\media\X.wav`），还原时按原类型写回，绝不降级成 `REG_SZ`（`Core/SoundScheme.cs:186`、`Core/ProfileStore.cs:244`）。
- 「键在、无值」的空壳槽（本机 `SystemExit` / `WindowsLogoff`）还原成**删默认值、保留 `.Current` 子键**，不会凭空把键建出来，也不会写成「键 + 空串」（`Core/SoundScheme.cs:284`、`Core/ProfileStore.cs:232`）。
- 顺序是刻意的：`windows-default` **先停监听再写注册表**，避免还原中途抛异常后钩子还挂在系统里（`Core/ProfileStore.cs:217`）。
- 通过界面（托盘菜单 / 面板按钮）走的所有还原动作，**写之前自动快照**；`--apply-profile` 这条命令行入口同样先快照后写（`Program.cs:235`）。

### 1.2 监听引擎（`Core/Monitoring/`，20 个 `.cs`）

```
规则表 → 需要哪些监听器 → 事件 → 匹配 → 冷却 → 播放
```

- **惰性启动**：只按规则表里**实际用到的** `Kind` 装监听器；一条启用规则都没有 → 一个钩子都不装（`Core/Monitoring/MonitorEngine.cs:136`）。
- **默认不常驻**：引擎不会自己启动，只有显式 `Start()`（「个性化设置」经 `ProfileStore.Apply`，或 `--monitor-selftest`）才会有钩子存在（`Core/Monitoring/MonitorHost.cs:12`）。
- **订阅方不抛**：事件从线程池进来，播放与匹配的异常绝不反噬监听器（`Core/Monitoring/MonitorSourceBase.cs:89`）。
- **先停后起**：重复点「个性化设置」会先卸再装，保证改完 `rules.json` 后按最新规则重装钩子（`Core/Monitoring/MonitorHost.cs:56`）。
- **退出必干净**：切「Windows 原样」或托盘「退出（会停止监听）」都会卸载全部钩子 / 隐藏窗口 / 定时器（`Core/ProfileStore.cs:220`、`TrayContext.cs:67`）。

### 1.3 八类监听器

| Kind | 中文名 | 底层机制 | 事件明细格式 |
|---|---|---|---|
| `Keyboard` | 键盘 | `SetWindowsHookEx(WH_KEYBOARD_LL)` 低层全局钩子，只报按下 | `CapsLock`、`F24`、`VolumeUp` |
| `Mouse` | 鼠标 | `SetWindowsHookEx(WH_MOUSE_LL)`，只报按键与滚轮，**不报移动** | `LeftDown`、`XButtonDown1`、`WheelUp` |
| `WindowSwitch` | 窗口切换 | `SetWinEventHook(EVENT_SYSTEM_FOREGROUND, WINEVENT_OUTOFCONTEXT)` | `notepad.exe\|无标题 - 记事本` |
| `ProcessStartStop` | 进程启停 | 轮询 `Process.GetProcesses()` 快照对比（每 1500 ms，刻意不用 WMI） | `start steam.exe` / `stop steam.exe` |
| `DeviceChange` | 设备插拔 | 隐藏顶层窗口收 `WM_DEVICECHANGE`（+ `RegisterDeviceNotification`） | `Arrival D:` / `Remove` |
| `PowerSession` | 电源会话 | `WM_POWERBROADCAST` + `SystemEvents.SessionSwitch` | `AC` / `DC` / `Lock` / `Unlock` / … |
| `Clipboard` | 剪贴板 | `AddClipboardFormatListener` + `WM_CLIPBOARDUPDATE` | `Text` / `Image` / `Files`（多类型用 `+` 连接） |
| `FileSystem` | 文件变化 | `FileSystemWatcher`（每个目录一个，单层不递归） | `Created C:\dir\a.txt` |

机制描述与工厂集中在 `Core/Monitoring/MonitorSources.cs:72`（`Describe`）与 `:49`（`Create`），中文短名在 `Core/Monitoring/MonitorHost.cs:72`（`KindLabel`）。

### 1.4 语音 / 音效素材

监听到命中时播的 wav 有两种来源：

1. 规则里 `sound` 直接写 **wav 绝对路径**；
2. 规则里 `speakKey` 写**事件名**，从语音方案（`%LOCALAPPDATA%\BSH\sounds\voice-scheme.json`）里取（`Core/Monitoring/MonitorEngine.cs:308`、`Core/VoiceScheme.cs:19`）。

素材是本地自制的，生成脚本在 `tools/`（本机实装的那套：**SAPI `Microsoft Zira Desktop` 底音 + `tools/robotize.py` 机械化后处理**，经 `tools/install-voice.ps1` 装到 `%LOCALAPPDATA%\BSH\sounds\`）。另有两条可选的实验路线：**Edge 神经语音 Aria×Michelle 混合**（`tools/make-edge-batch.py`，幅度谱 0.35×Aria + 0.65×Michelle）与 **EVA 风格后处理**（`tools/make-eva-voice.py`）。产物一律不入库（见第 8 节）。

---

## 2. 快速开始

### 2.1 构建

```powershell
cd D:\O级项目管理\BSH
& 'C:\Users\11\.dotnet\dotnet.exe' build BSH.csproj -c Release
```

### 2.2 运行（⚠️ 必须带 `DOTNET_ROOT`）

本机 PATH 里的 `dotnet` 只有 8.x，而本工程是 `net10.0-windows`；用户级 SDK 10 装在 `C:\Users\11\.dotnet`。所以命令行跑 dll 时必须显式给 `DOTNET_ROOT`：

```powershell
$env:DOTNET_ROOT='C:\Users\11\.dotnet'
& 'C:\Users\11\.dotnet\dotnet.exe' bin\Release\net10.0-windows\BSH.dll <参数>
```

- **不带参数** = 托盘常驻：单击托盘图标打开面板，关闭按钮只隐藏窗口，真正退出走托盘菜单「退出（会停止监听）」（`Program.cs:107`、`TrayContext.cs:49`）。
- **单实例**：重复启动会弹「BSH 已在运行（见系统托盘）」并直接退出（`Program.cs:99`）。
- 工程是 `WinExe`：双击运行没有控制台，看不到输出是正常的；但被脚本重定向时 stdout 有效（`Program.cs:192`）。

### 2.3 切换方案

```powershell
# ① 个性化设置（启用监听）：不写注册表；启用 0 条规则就返回 monitors=0
& 'C:\Users\11\.dotnet\dotnet.exe' bin\Release\net10.0-windows\BSH.dll --apply-profile personal

# ② Windows 原样（还原 + 停止监听）：会写 HKCU，写之前自动快照
& 'C:\Users\11\.dotnet\dotnet.exe' bin\Release\net10.0-windows\BSH.dll --apply-profile windows-default
#    输出形如：applied windows-default changed=59 snapshot=C:\Users\<你>\AppData\Roaming\BSH\snapshots\auto-before-profile-<时间戳>.json
```

> ⚠️ `--apply-profile` 的还原路径**真的会写注册表**，代码注释里写明「只给自动化验收用」；日常操作走界面按钮（`Program.cs:192` 的 ⚠️ 段）。
> 快照默认落在 `%APPDATA%\BSH\snapshots\`（`Program.cs:226`）；面板的「保存快照…/还原快照…」与「一键重排」共用同一个目录（`MainForm.cs:34`）。

### 2.4 发布（自包含单文件）

```powershell
& 'C:\Users\11\.dotnet\dotnet.exe' publish BSH.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o dist
```

（`dist/`、`dist-new/` 已在 `.gitignore` 里排除；仓库不发布二进制。）

---

## 3. CLI 参数表

参数名常量集中定义在 `Program.cs:11`–`Program.cs:29`，入口分派在 `Program.cs:49`。

| 参数 | 形态 | 作用 | 写注册表？ | 退出码（源码位置） |
|---|---|---|---|---|
| （无参数） | — | 托盘常驻（单实例） | 仅界面动作时 | — |
| `--report` | `[输出路径]` | 自检 JSON 报告：事件总数 / 空槽 / 撞车 / 重排预演 / 缺失素材 / 两套方案可用性与预演条数（监听模式附规则表路径、启用条数、将启动哪几类监听器） | ❌ 只读 | 恒 0（`Program.cs:115`，从不设置退出码） |
| `--monitor-report` | `[输出路径]` | 监听引擎体检：对 8 类监听器各做一次「探针启动 → 读状态 → 立刻停止」，报告里的「可启动」是实测值 | ❌ 只读 | 0 成功 / 5 写盘失败（`Program.cs:442`） |
| `--monitor-status` | `[--live]` | 监听接线状态：规则表路径与存在性、启用条数、8 类监听器的装机前提、只读预演。**默认不启引擎** | ❌ 只读 | 0 / 8（`--live` 时监听器没起齐）（`Program.cs:266`） |
| `--monitor-selftest` | `<秒数>` | 监听引擎自检：真装钩子、`SendInput` 注入 3 次 `F24`、真写 2 个文件，按**事件级实测**计数；缺省 4 秒 | ❌ 只读 | 0 通过 / 1 键盘不足 / 2 文件不足 / 3 都不足（`Core/Monitoring/MonitorSelfTest.cs:174`） |
| `--apply-profile` | `<方案id> [快照路径]` | 应用方案。还原类**会写注册表**（先自动快照）；监听类只起引擎，输出 `registry=untouched` | ✅ 还原类会 | 0 / 3 方案 id 不存在 / 4 应用失败（`Program.cs:197`） |
| `--snapshot-check` | `capture <输出>`｜`normalize <输入> <输出>`｜`plan <输入>` | 快照类型保真的自检；**不调用** `SchemeSnapshot.Apply`，唯一的写盘是调用方指定的那个 JSON | ❌ 只读 | 0 / 6 参数不对或读写失败（`Core/SnapshotSelfCheck.cs:24`） |
| `--slot-plan` | `<快照文件>` | 槽位还原动作的**只读投影**：逐槽打印 `SetValue(ExpandString)` / `DeleteValue` / `DeleteSubKeyTree` | ❌ 只读，且**不写任何文件** | 0 / 7（`Core/SlotPlanSelfCheck.cs:27`） |

补充说明（都以代码为准）：

- **`--report` 优先级最高**：即使同时带了 `--apply-profile`，也**只出报告、绝不误写注册表**（`Program.cs:51`）。
- 参数名比较**忽略大小写**（`Program.cs:52` 等处统一用 `OrdinalIgnoreCase`）。
- 值参数若以 `--` 开头会被当成「没给」而回落到默认路径（`Program.cs:527` 的 `ReadArg`）。
- `--monitor-status --live` 才会真动手，且**每步结束都收尾**：先走 `ProfileStore.Apply(personal)`（与界面按钮同一条代码路径），再用公开示例规则（仅内存启用、不动文件、`SuppressPlayback=true` 不发声）起一次引擎，随后 `Stop()`（`Program.cs:317`、`Program.cs:332`）。
- 命令行输出固定 UTF-8 无 BOM，脚本侧请按 `utf-8` 读取（路径可能含中文）（`Program.cs:531`）。

---

## 4. 目录结构

```
BSH/
├─ BSH.csproj                      net10.0-windows / WinExe / UseWindowsForms
├─ Program.cs                      CLI 参数与入口分派（552 行）
├─ MainForm.cs                     主面板：59 事件总览 / 试听 / 替换 / 静音 / 一键重排 / 快照
├─ TrayContext.cs                  托盘常驻 + 菜单 + 引擎生命周期兜底
├─ Core/
│  ├─ SoundScheme.cs               注册表读写 + 类型保真还原 + SchemeSnapshot（587 行）
│  ├─ ProfileStore.cs              两套方案的登记 / 只读预演 / 应用
│  ├─ SoundProfile.cs              方案记录与 ProfileKind 枚举
│  ├─ AppEventCatalog.cs           事件的中文说明与分组（界面用）
│  ├─ ReorderPlan.cs               一键重排：空槽 / 撞车 → 目标 wav
│  ├─ AudioProbe.cs                试听（Win32 PlaySound）
│  ├─ VoiceScheme.cs               语音方案（事件名 → 自制 wav 绝对路径）
│  ├─ SnapshotSelfCheck.cs         快照类型保真自检（--snapshot-check）
│  ├─ SlotPlanSelfCheck.cs         槽位还原动作只读投影（--slot-plan）
│  └─ Monitoring/                  ★ 监听引擎，共 20 个 .cs
│     ├─ MonitorEngine.cs          规则 → 监听器 → 事件 → 匹配 → 冷却 → 播放
│     ├─ MonitorHost.cs            进程内唯一引擎实例 + 人话状态
│     ├─ MonitoringRule.cs         规则模型 + Match 语义说明（唯一真源）
│     ├─ RuleStore.cs              规则表读写（rules.json / rules.example.json）
│     ├─ MonitorMatcher.cs         事件明细 ↔ Match 判定（唯一匹配入口）
│     ├─ MonitorSources.cs         8 类监听器工厂 + 探针
│     ├─ MonitorSourceBase.cs      幂等启停 / 启动失败不崩 / 只投递不重活
│     ├─ IMonitorSource.cs         监听器接口与 MonitorEvent
│     ├─ KeyboardMonitor.cs        WH_KEYBOARD_LL（+ VirtualKeyNames 键名表）
│     ├─ MouseMonitor.cs           WH_MOUSE_LL
│     ├─ WindowSwitchMonitor.cs    SetWinEventHook(EVENT_SYSTEM_FOREGROUND)
│     ├─ ProcessMonitor.cs         轮询进程快照对比
│     ├─ DeviceMonitor.cs          WM_DEVICECHANGE
│     ├─ PowerSessionMonitor.cs    WM_POWERBROADCAST + SessionSwitch
│     ├─ ClipboardMonitor.cs       WM_CLIPBOARDUPDATE
│     ├─ FileSystemMonitor.cs      FileSystemWatcher
│     ├─ HiddenMessageWindow.cs    不可见顶层窗口 + 消息循环
│     ├─ MonitorMessageLoop.cs     专用消息泵线程
│     ├─ MonitorSelfTest.cs        --monitor-selftest 的实测逻辑
│     └─ InputSimulator.cs         SendInput 注入（自检用）
├─ profiles/
│  ├─ windows-default/baseline-appevents.json   59 槽基线（35 String + 22 ExpandString + 2 Missing）
│  └─ personal/
│     ├─ profile.json              占位（id / name / kind / note）
│     ├─ README.md                 个人配置目录的约定说明
│     ├─ rules.example.json        ★ 公开示例：8 类各一条、全部 enabled=false
│     ├─ rules.json                ❌ 真实规则表（个人配置，不入库，需自己从示例复制）
│     └─ sounds/.gitkeep           ❌ 真实音频（个人素材，仅留目录骨架）
├─ tools/                          验证脚本（verify-*.py）与语音生成脚本（make-*.py / make-voice.ps1 / install-voice.ps1）
├─ voice/                          ❌ 自制音频工作目录（原始素材 + 中间产物，整体不入库）
├─ dist/  dist-new/                ❌ 发布产物（自包含单文件 BSH.exe，不入库）
├─ report.json                     ❌ --report 的默认输出（不入库）
└─ verify-profiles*.json           本机验收脚本产出的快照 / 报告（未列入 .gitignore，提交前请自行确认）
```

---

## 5. 规则表怎么写

### 5.1 起手

把 `profiles/personal/rules.example.json` 复制成同目录的 `rules.json`，改 `match`、把要用的那条 `enabled` 改成 `true`。
规则表**不存在不是故障**：`RuleStore.Load` 只返回空表并说明原因，绝不替你创建文件（`Core/Monitoring/RuleStore.cs:84`）。

### 5.2 字段

JSON 用**小写驼峰**（`RuleStore` 的序列化策略是 `CamelCase` + 属性名不区分大小写 + 允许注释与尾逗号，`Core/Monitoring/RuleStore.cs:23`）：

| 字段 | 类型 | 含义 |
|---|---|---|
| `id` | string | 规则唯一标识，用于冷却记账；留空自动补 `rule-N`（`MonitoringRule.cs:48`、`RuleStore.cs:98`） |
| `name` | string | 人读名字，仅用于展示与日志 |
| `enabled` | bool | `false` 的规则不参与匹配，**也不会让引擎去装对应的监听器** |
| `kind` | string | 事件类别枚举名（`Keyboard` / `Mouse` / `WindowSwitch` / `ProcessStartStop` / `DeviceChange` / `PowerSession` / `Clipboard` / `FileSystem`）—— **不要改名**，改了用户已有规则会解析失败（`MonitoringRule.cs:7`） |
| `match` | string | 条件表达式，**语义由各类监听器定义**；多个条件用 `,` `;` `\|` 分隔，忽略大小写；空或 `*` = 全部命中（`MonitorMatcher.cs:20`、`MonitorMatcher.cs:31`） |
| `sound` | string | 要播的 wav **绝对路径**；非空时优先于 `speakKey`（`MonitorEngine.cs:310`） |
| `speakKey` | string | 语音方案（`voice-scheme.json`）里的事件名，如 `DeviceConnect`（`MonitorEngine.cs:315`） |
| `cooldownMs` | int | 同一规则的最小触发间隔，防抖；`0` = 不限制；默认 `800`（`MonitoringRule.cs:81`） |

`match` 的语义（照 `MonitoringRule.cs:59` 的注释，并对照 `MonitorMatcher.cs` 的实现）：

- 键盘：**虚拟键名**，整词相等；`Shift` / `Ctrl` / `Alt` / `Win` 这类通用名会同时命中左右键（`MonitorMatcher.cs:70`）。名字取自 `VirtualKeyNames` 表（`KeyboardMonitor.cs:134`）。
- 鼠标：事件名枚举，如 `LeftDown,MiddleUp,WheelUp`。
- 窗口切换：**进程名整词相等**，**或**标题关键词包含匹配（`MonitorMatcher.cs:136`）。
- 进程启停：进程名（两个方向都命中），可用 `start ` / `stop ` 前缀限定方向（`MonitorMatcher.cs:79`）。
- 设备插拔：只能写 `Arrival` / `Remove`。
- 电源会话：`AC` / `DC` / `Suspend` / `Resume` / `BatteryLow` / `Lock` / `Unlock` / `Logon` / `Logoff`（另有 `ConsoleConnect` 等）。
- 剪贴板：`Text` / `Image` / `Files`，**按整段相等判**（写 `Text` 只命中纯文本，图文混装要写 `Text+Image`）。
- 文件变化：**目录绝对路径**（不是文件），命中该目录下的新建 / 修改 / 删除 / 改名；判定前会剥掉 `Created ` 这类动作前缀（`MonitorMatcher.cs:99`）。
- 其它未列举的类别才走子串匹配（`MonitorMatcher.cs:58`）——**不会**出现 `Match="A"` 误命中 `CapsLock` 这种事。
- 一个事件只兑现**第一条命中的规则**，避免多重播放互相打断（`MonitorEngine.cs:274`）。
- `sound` 与 `speakKey` 都留空 = **只记账不出声**（示例里的鼠标 / 窗口 / 进程三条就是这样）。

### 5.3 三个真实可用的例子

**例 1 · 键盘：按 CapsLock / 音量键时出声**（用语音方案里的 `MenuCommand`）

```json
{
  "id": "kbd-capslock",
  "name": "按 CapsLock / 音量键时提示",
  "enabled": true,
  "kind": "Keyboard",
  "match": "CapsLock,VolumeUp,VolumeDown",
  "speakKey": "MenuCommand",
  "cooldownMs": 300
}
```

**例 2 · 文件：下载目录有变化时出声**（`sound` 直接写 wav 绝对路径）

```json
{
  "id": "fs-downloads",
  "name": "下载目录有变化时提示",
  "enabled": true,
  "kind": "FileSystem",
  "match": "C:\\Users\\<你的用户名>\\Downloads",
  "sound": "C:\\Windows\\Media\\Windows Notify System Generic.wav",
  "cooldownMs": 500
}
```

**例 3 · 进程：Steam 启动时出声**（`start ` 前缀限定方向）

```json
{
  "id": "proc-start-steam",
  "name": "Steam 启动时提示",
  "enabled": true,
  "kind": "ProcessStartStop",
  "match": "start steam.exe",
  "speakKey": "DeviceConnect",
  "cooldownMs": 1500
}
```

改完规则后**再点一次「个性化设置」**即可按最新规则重装钩子（先停后起，见 `MonitorHost.cs:56`）。
想不开界面先看接线，用 `--monitor-status`（只读）。

---

## 6. 已知限制与验证深度

这一节写的是**实测到什么程度、哪里没测**，请按这个口径理解上面的功能。

### 6.1 监听器的验证分两档

| 监听器 | 验证深度 | 证据 |
|---|---|---|
| `Keyboard` | **事件级实测** | `--monitor-selftest` 真发 `SendInput`（模拟键 `F24`）→ 按「事件明细与注入键名完全一致」计数（`Core/Monitoring/MonitorSelfTest.cs:112`） |
| `FileSystem` | **事件级实测** | 自检真建临时目录并写 2 个文件 → 按路径前缀计数（`MonitorSelfTest.cs:124`） |
| `ProcessStartStop` | **事件级实测（未自动化）** | 代码未接入 `--monitor-selftest`；仓库内没有对应的自动化脚本，其"实数"来自人工用宿主进程起停的实测，**无法用一条命令复现** |
| `Mouse` / `WindowSwitch` / `DeviceChange` / `PowerSession` / `Clipboard` | **仅探针级：能启动、能停止** | `--monitor-report` / `--monitor-status` 各做一次「启动 → 读状态 → 立刻停止」（`MonitorSources.cs:91`）。未做事件级实测 —— 真测会抢焦点、覆盖剪贴板、或需要真实硬件事件 |

换句话说：**八类监听器都能装能卸（探针有实测值），但只有键盘与文件两类有内建的事件级自检证据。**

`--monitor-status --live` 会额外证明「零规则 = 零钩子」与「有规则时真装钩子」互为对照（`Program.cs:317`、`Program.cs:332`）。

### 6.2 代码里明确标注的限制

- **`DeviceMonitor` 的设备接口通知用了空 GUID**（`DeviceMonitor.cs:55`），即不限定接口类。本机探针实测 `RegisterDeviceNotification` **注册成功**（`--monitor-status` 输出「设备接口通知注册=成功」），但**「卷以外设备的通知能否真的到达」未做验证**；卷设备（U 盘 / 移动硬盘）靠的是系统广播给所有顶层窗口的主路径，与这次注册无关（`DeviceMonitor.cs:8`）。
- **`PowerSessionMonitor` 的 Suspend / Resume 可能收不到**：它依赖 `WM_POWERBROADCAST`，而现代 Windows 上的挂起 / 恢复**未必广播给普通顶层窗口**。代码只保证消息一到就映射成 `Suspend` / `Resume`（`PowerSessionMonitor.cs:109`），没有在真挂起场景下实测过。锁屏 / 解锁（`SystemEvents.SessionSwitch`）走的是另一条路径，探针里订阅成功。
- **`ProcessMonitor` 会漏事件**：1.5 秒轮询 + 按**进程名集合**比对（`ProcessMonitor.cs:19`、`ProcessMonitor.cs:71`）——
  - 启动后立刻退出的短命进程可能被整个漏掉；
  - 已经有一个同名进程在跑时，**再开一个不会报事件**（集合不变）；
  - 事件明细只有进程名，没有 PID / 命令行。
- **鼠标监听不报移动**（有意为之，`MouseMonitor.cs:8`）；**剪贴板只报类型、不读内容**（有意为之，避免把密码等敏感数据带进规则表与报告，`ClipboardMonitor.cs:11`）。
- **文件监听不递归**（`IncludeSubdirectories = false`，`FileSystemMonitor.cs:73`）；缓冲 64 KB，繁忙目录上仍可能溢出（溢出会记 `LastError`，不抛异常）。
- **`Clipboard` 明细可能是 `Unknown`**：剪贴板同一时刻只允许一个进程打开，被占用是常态、不是故障（`ClipboardMonitor.cs:70`）。
- 面板的「一键重排」只处理**空槽与撞车**，不是自动挑音效。

### 6.3 注册表安全性

- **只动 `HKCU\AppEvents\Schemes\Apps\.Default\<事件>\.Current` 的默认值**；不写 `HKLM`、不需要管理员、不改任何系统文件（`Core/SoundScheme.cs:94`）。
- 应用还原类方案前**自动快照**（界面与 `--apply-profile` 都是），快照记的是**未展开字面值 + 注册表类型 + 子键是否存在**（`SchemeSnapshot.Capture` → `ReadAllRaw`，`Core/SoundScheme.cs:383`）。
- 还原**逐槽都有明确动作**，没有「保持不动」这个选项：`SetValue(原类型)` / `DeleteValue`（删值保键）/ `DeleteSubKeyTree`（删键）（`Core/SoundScheme.cs:241`、`Core/SlotPlanSelfCheck.cs:78`）。
- 还原是**幂等**的：现场本来就是目标结构时返回 `false`，不计入改动条数（`Core/SoundScheme.cs:273`、`Core/SoundScheme.cs:305`）。
- 想在不写注册表的前提下看「还原会做什么」，用 `--slot-plan <快照文件>`（纯只读投影，连文件都不写）。

### 6.4 项目状态

**开发中，未发布。** 没有版本号、没有安装包、没有 CI；`tools/` 里的 `verify-*.py` 是作者本机的验收脚本（`verify-profiles.py` 会调用 `--apply-profile`，**属于会写注册表的脚本，别顺手跑**）。接口与数据格式（`rules.json` 字段、快照格式）仍可能变，历史快照的兼容只做到「旧格式按值推断类型 + 明确告知用户类型是推断的」（`Core/SoundScheme.cs:459`）。

---

## 7. 隐私与仓库分层

`.gitignore`（15 行，含注释）把仓库切成三层：

| 层 | 内容 | 入库？ |
|---|---|---|
| 公开代码与示例 | `*.cs`、`BSH.csproj`、`profiles/windows-default/baseline-appevents.json`、`profiles/personal/{profile.json,README.md,rules.example.json}`、`tools/` | ✅ |
| **个人配置** | `profiles/personal/rules.json`（真实规则表：你什么时候想被提醒）、`profiles/personal/sounds/*`（自制音频） | ❌ 排除 |
| 构建 / 中间产物 | `bin/`、`obj/`、`dist/`、`dist-new/`、`voice/`（音频工作目录）、`report.json` | ❌ 排除 |

- `rules.json` 只保留 `rules.example.json` 作为**公开示例**（8 类各一条、全部 `enabled: false`，每条带 `$comment` 说明怎么写）——`enabled: false` 保证示例本身不会替用户出声。
- `profiles/personal/sounds/` 用 `sounds/*` 排除再 `!.gitkeep` 捞回占位，目录结构保留、音频不进仓库（`.gitignore:12`）。
- **音频一律不入库**（体积大，且可能涉及第三方音源授权）：`voice/` 整体排除，实装音频在 `%LOCALAPPDATA%\BSH\sounds\`（工作目录之外）。
- 剪贴板监听**只报内容类型、不读内容**；规则表与报告里不会出现你复制过的东西（`ClipboardMonitor.cs:11`）。

---

## 8. 许可与状态

- **状态**：开发中，未发布（无 release、无版本号、无安装程序）。
- **许可**：仓库里目前**没有 LICENSE 文件**，作者尚未声明许可条款。在补上之前，请按「保留所有权利」处理 —— 想要复用请先来找作者。
- 个人素材（`profiles/personal/sounds/`、`voice/`）与自制语音不随代码分发。
