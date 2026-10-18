# BSH · 更好的系统提示（Better System Hints）

一个接管 Windows 系统提示音的 C# WinForms 托盘工具（`net10.0-windows`）。

它解决的是这个问题：**系统自带的提示音不够全面** —— Windows 的声音设置只覆盖「登录 / 出错 / 设备连接」这类少数场景，而你真正想被提醒的时刻（按下 CapsLock、插上 U 盘、Steam 启动、下载目录有变化、锁屏解锁……）系统一声不吭。

BSH 给两条互不冲突的路：

- **「Windows 原样」**：把 **59 项基线**里的系统声音事件槽**逐字节还原**成改造前的原始值（含注册表类型与「键在无值」形态），并停止监听 —— 一键回到出厂状态。⚠️ 基线是 59 项，其中 20 个 `Notification.Looping.*`（循环铃声 / 来电）已于 2026-10-11 按用户要求**从系统删除**，所以现在系统里是 **39 个事件槽**；还原会把这 20 个一并建回来（"原样"就该是 59 个，删除可逆）。
- **「个性化设置」**：**两件事一起做** —— ① 把未被跳过的系统声音事件槽指向自制语音；② 按 `profiles\personal\rules.json` 里启用的规则安装钩子，命中时补播你配的提示音。

> 一切写入都只在 `HKCU`（当前用户），**不需要管理员权限**，不改任何系统文件。

---

## 1. 功能一览

### 1.1 三套方案 + 预设

| 方案 id | 界面名 | 类型 | 真实动作 | 写注册表？ |
|---|---|---|---|---|
| `windows-default` | Windows 原样（还原 + 停止监听） | `RestoreBaseline` | **先停监听引擎**，再把 59 槽逐项还原成基线值 | ✅ 写 `HKCU` |
| `personal` | 个性化设置（换提示音 + 启用监听） | `Monitor` | ① 把未被 `skipFreq`/`skipEvents` 跳过的槽指向语音（**本机当前配置 = 36 槽**：39 个现存事件中跳过 3 个）② 按规则表启动监听引擎 | ✅ 写（跳过的槽**不碰**） |
| `takeover` | BSH 接管（改由规则判断） | `Takeover` | 清空 **18 个**槽（10 个交给 BSH 规则判断 + 8 个界面音关掉），其余 21 槽保持系统原样；然后启动监听引擎 | ✅ 写（只清这 18 个） |

> 三套方案之外还有**预设**（`profiles/presets/<id>/`）—— 一套「事件 → 音频」映射（+ 可选规则集），见 1.6。

- 内置方案登记见 `Core/ProfileStore.cs:52`，`personal` 的 `Kind` 是 `Monitor`（`Core/ProfileStore.cs:61`）。
- 还原是**类型保真**的：基线里 22 个槽原本是 `REG_EXPAND_SZ`（字面值 `%SystemRoot%\media\X.wav`），还原时按原类型写回，绝不降级成 `REG_SZ`（`Core/SoundScheme.cs:186`、`Core/ProfileStore.cs:244`）。
- **写槽同样保类型（2026-10-18 起）**：「个性化设置」、预设应用、一键重排、单个替换 / 静音都走 `ProfileStore.VoiceWriteKind` —— 基线里原本是 `REG_EXPAND_SZ` 的槽继续按 `REG_EXPAND_SZ` 写，不再因为"换过一次音"就把类型降级（本机现存 2 个：`WindowsLogon` / `WindowsUnlock`；原始 59 槽里是 22 个）。
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
| `ProcessStartStop` | 进程启停 | 轮询 `Process.GetProcesses()` 快照对比（每 300 ms，刻意不用 WMI） | `start steam.exe` / `stop steam.exe` |
| `DeviceChange` | 设备插拔 | 隐藏顶层窗口收 `WM_DEVICECHANGE`（+ `RegisterDeviceNotification`） | `Arrival D:` / `Remove` |
| `PowerSession` | 电源会话 | `WM_POWERBROADCAST` + `SystemEvents.SessionSwitch` | `AC` / `DC` / `Lock` / `Unlock` / … |
| `Clipboard` | 剪贴板 | `AddClipboardFormatListener` + `WM_CLIPBOARDUPDATE` | `Text` / `Image` / `Files`（多类型用 `+` 连接） |
| `FileSystem` | 文件变化 | `FileSystemWatcher`（每个目录一个，单层不递归） | `Created C:\dir\a.txt` |

机制描述与工厂集中在 `Core/Monitoring/MonitorSources.cs:72`（`Describe`）与 `:49`（`Create`），中文短名在 `Core/Monitoring/MonitorHost.cs:72`（`KindLabel`）。

### 1.4 语音 / 音效素材

监听到命中时播的 wav 有两种来源：

1. 规则里 `sound` 直接写 **wav 绝对路径**；
2. 规则里 `speakKey` 写**事件名**，从语音方案（`%LOCALAPPDATA%\BSH\sounds\voice-scheme.json`）里取（`Core/Monitoring/MonitorEngine.cs:308`、`Core/VoiceScheme.cs:19`）。

素材是本地自制的，生成脚本在 `tools/`（**当前实装的是 Edge 神经语音**：`tools/make-edge-batch.py` 合成 Aria×Michelle 混合、字幕真源 `voice/lines.json` rev3，经 `tools/install-voice.ps1` 装到 `%LOCALAPPDATA%\BSH\sounds\`）。判定依据（2026-10-11 实测）：装机那 39 个 wav 与 `voice/edge-batch/out/` 的产物 **39/39 逐字节相同**（2026-10-11 删除 20 个循环事件后从 59 条缩减到 39 条）。另有两条早期试过、**未采用**的路线：**SAPI `Microsoft Zira Desktop` + `tools/robotize.py` 机械化后处理**，与 **EVA 风格后处理**（`tools/make-eva-voice.py`）。产物一律不入库（见第 8 节）。

### 1.5 「BSH 接管」与开机自启

「BSH 接管」是**不改音、改由 BSH 判断响不响**的一套方案（`ProfileKind.Takeover`）：

- **清空 18 个槽**（`Core/ProfileStore.cs` 的 `TakeoverSilenceSlots`）：
  - **10 个交给 BSH 规则判断**：`Open` / `Close`（程序启停）、`DeviceConnect` / `DeviceDisconnect` / `DeviceFail`（设备插拔）、`WindowsLogon` / `WindowsLogoff` / `WindowsUnlock`（会话）、`LowBatteryAlarm` / `CriticalBatteryAlarm`（电池）；
  - **8 个界面音关掉**：`Maximize` / `Minimize` / `RestoreUp` / `RestoreDown` / `CCSelect` / `MenuCommand` / `MenuPopup` / `ShowBand`。
- 其余 21 个槽**保持系统原样**（不像「个性化设置」那样整批换音）。
- 清空的槽**必须由规则表补上声音**，所以这条方案要和 `profiles/personal/rules.json` 配合用（本机现有 `app-open` / `app-close` 两条进程启停规则，白名单 10 个程序）。
- **退出即还原**（`TrayContext.RestoreIfTakeover`）：若当前活动方案是 `takeover`，退出 BSH 时按基线逐项还原 —— 那 18 个槽的语义是"改由 BSH 判断"，BSH 不在就没人判断，提示音会静默消失；所以「退出还原」和「开机自启」是**一对**，闭环才是「开机 → BSH 起来接管 → 退出 → 系统提示音还回去」。
- **开机自启**：托盘右键「开机自启动」勾选，写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 的 `BSH` 值（用户级、不需要管理员；`Core/AutoStart.cs`）。自包含 exe 直接登记自身路径；`dotnet BSH.dll` 形态会把 dll 路径一并写上。
- 界面入口：面板按钮 **「BSH 接管」**；命令行 `--apply-profile takeover`。

### 1.6 预设体系（PresetStore）

**一个预设 = 一套「事件 → 音频」映射 + 一套（可选的）监听规则集**，可整体应用到系统。与上面的"方案"并列：方案管"怎么响"，预设管"用哪套音"。

```
profiles/presets/<id>/
├─ preset.json     预设定义（名字 / 说明 / builtin / 声音映射 / 规则）
└─ sounds/         该预设自带的音频（可选；也可以复用 %LOCALAPPDATA%\BSH\sounds 里的装机音频）
```

```json
{
  "id": "eva-mech",
  "name": "BSH EVA",
  "description": "Edge TTS 合成 + EQ 塑形，39 槽全配",
  "builtin": true,
  "sounds": {
    "SystemAsterisk": "sounds/信息（星号）,Information. A new message has arrived.wav",
    "DeviceConnect": "sounds/设备已连接,Device detected.wav"
  },
  "rules": []
}
```

- **内置预设**：`mech-voice`（界面名 **「BSH」**）与 `eva-mech`（**「BSH EVA」**）；`builtin: true` 的只读。
- **`sounds` 的值怎么解析**（`PresetStore.ResolveAudio`）：
  1. `sounds/xxx.wav` → 先找 `%LOCALAPPDATA%\BSH\presets\<id>\sounds\`，**找不到回退**到 `profiles\presets\<id>\sounds\`（随 exe 分发的那份，绿色版「解压即用」靠它）；
  2. 绝对路径 → 直接用；
  3. 纯文件名 → 在 `%LOCALAPPDATA%\BSH\sounds\` 里找（复用装机音频）。
- **应用预设**：界面「预设下拉 + 应用预设…」（先把"会写多少 / 按偏好跳过多少 / 音频缺失多少"摆给你看 → 确认 → 自动快照 → 写入），或命令行 `--apply-preset <id> [快照路径]`。
  - 顺序与「Windows 原样」一致：**先停监听引擎 → 自动快照 → 再逐槽写**；
  - **尊重 `personal/profile.json` 的跳过偏好**（本机 = 不碰 `CCSelect` / `Open` / `Close` 这 3 个槽）；
  - **音频缺失的槽跳过并汇总**，不会整批失败；
  - 写槽同样按基线保类型（见 1.1）。
- **用户自建**：界面「新建预设…」（默认名 **「个性化设置」**，id 自动 `my-N`，**空壳**，`sounds` 为空 = 应用它不改任何槽）、「预设改名…」（内置也允许改，改的是本地 `preset.json` 的 `name`）；删除只允许用户自建预设（会连 `%LOCALAPPDATA%\BSH\presets\<id>\sounds\` 一起删）。`--capture-preset`（把当前系统槽反捕成预设）**尚未实现**。
- **只读列出**：`--preset-list` 会打印每个预设的 `builtin / sounds / rules / missing` 与坏预设的跳过原因。

### 1.7 图形界面（主面板 / 监听规则编辑器）

**主面板**（托盘单击图标或右键「打开面板…」）分四组按钮：

1. **方案**：Windows 原样 / 个性化设置 / BSH 接管；
2. **开机自启动**：复选框（2026-10-18 新增）。勾选即写 `HKCU\...\Run`，取消即删；**托盘右键的同一个开关与它双向同步**，状态栏也常显「自启：开/关」。
   - **默认开启**：首次运行时（还没有 `%LOCALAPPDATA%\BSH\autostart.json` 标记）自动登记自启并写标记；**之后用户取消，就不会在下次启动时被重新打开**。
   - 自启命令写的是"当时正在运行的这份程序"：发布版 exe 写 exe 路径，`dotnet BSH.dll` 开发形态写 dotnet+dll 路径（换形态/换目录后再启动一次会自动刷新）。
3. **预设**：预设下拉 + 应用预设… / 新建预设… / 预设改名… / **删除预设…**（2026-10-18 新增；内置预设只读，用户预设会连自带音频目录一起删）;
4. **单事件与维护**：▶ 试听 / 替换音效… / 设为静音 / 一键重排… / 保存快照… / 还原快照… / 刷新 / 主题。

**监听规则编辑器**（主面板「监听规则…」或托盘「监听规则（可视化编辑）…」，2026-10-18 新增）：

- 直接读写 `profiles\personal\rules.json`（不再需要手改 JSON）；表格列：启用 / 名称 / 类别 / 条件 / 播放 / 冷却。
- 「启用」列可以直接勾；其余字段走「编辑…」对话框（名称、类别、条件、限定应用、播放方式、冷却，附各类别 `match` 写法提示）。
- **顺序有意义**：引擎对同一事件只兑现第一条命中的规则，所以提供「上移 / 下移」。
- 保存前**自动备份**现有规则表到 `%APPDATA%\BSH\snapshots\rules-backup-<时间戳>.json`；保存后若监听引擎正在运行，会询问是否**立即按新规则重装钩子**。
- 另有「重新载入」「用示例作起点…」（8 类各一条、全部禁用）「打开规则文件…」；有未保存修改时关闭会先问。

> 两个窗口都跟随深/浅色主题（颜色全部取自 `AppTheme.Palette`）。`--ui-smoke` 会真建这两个窗口并截图，供自动化验收界面渲染（见第 3 节）。

---

## 2. 快速开始

### 2.1 构建

```powershell
cd <你 clone 下来的仓库根目录>
& "$env:USERPROFILE\.dotnet\dotnet.exe" build BSH.csproj -c Release
```

### 2.2 运行（⚠️ 必须带 `DOTNET_ROOT`）

本机 PATH 里的 `dotnet` 只有 8.x，而本工程是 `net10.0-windows`；用户级 SDK 10 装在 `$env:USERPROFILE\.dotnet`。所以命令行跑 dll 时必须显式给 `DOTNET_ROOT`：

```powershell
$env:DOTNET_ROOT="$env:USERPROFILE\.dotnet"
& "$env:USERPROFILE\.dotnet\dotnet.exe" bin\Release\net10.0-windows\BSH.dll <参数>
```

- **不带参数** = 托盘常驻：单击托盘图标打开面板，关闭按钮只隐藏窗口，真正退出走托盘菜单「退出（会停止监听）」（`Program.cs:107`、`TrayContext.cs:49`）。
- **单实例**：重复启动会弹「BSH 已在运行（见系统托盘）」并直接退出（`Program.cs:99`）。
- 工程是 `WinExe`：双击运行没有控制台，看不到输出是正常的；但被脚本重定向时 stdout 有效（`Program.cs:192`）。

### 2.3 切换方案

```powershell
# ① 个性化设置：改写未被跳过的槽（本机当前 36 个）＋ 启动监听引擎（0 条规则 = 零钩子）
& "$env:USERPROFILE\.dotnet\dotnet.exe" bin\Release\net10.0-windows\BSH.dll --apply-profile personal

# ② Windows 原样（还原 + 停止监听）：会写 HKCU，写之前自动快照
& "$env:USERPROFILE\.dotnet\dotnet.exe" bin\Release\net10.0-windows\BSH.dll --apply-profile windows-default
#    输出形如：applied windows-default changed=59 snapshot=C:\Users\<你>\AppData\Roaming\BSH\snapshots\auto-before-profile-<时间戳>.json
```

> ⚠️ `--apply-profile` 的还原路径**真的会写注册表**，代码注释里写明「只给自动化验收用」；日常操作走界面按钮（`Program.cs:192` 的 ⚠️ 段）。
> 快照默认落在 `%APPDATA%\BSH\snapshots\`（`Program.cs:226`）；面板的「保存快照…/还原快照…」与「一键重排」共用同一个目录（`MainForm.cs:34`）。

### 2.4 发布（自包含单文件）

```powershell
& "$env:USERPROFILE\.dotnet\dotnet.exe" publish BSH.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o dist
```

（`dist/`、`dist-new/` 已在 `.gitignore` 里排除；仓库不发布二进制。）

### 2.5 便携部署（绿色版，解压即用）

**当前 Release**（v0.1.0）提供 `BSH-portable.zip`（约 58 MB / 87 条目），内容：

| 条目 | 说明 |
|---|---|
| `BSH.exe` | 自包含单文件（约 51.8 MB），**不需要装 .NET、不需要 `DOTNET_ROOT`、不需要管理员** |
| `profiles/` | 基线 + 两个预设（含它们自己的 78 个 wav，因此预设**开箱可用**、不用先跑安装脚本） |
| `使用说明.txt` | 零依赖、不需要管理员、shell 音效需重启 explorer、「BSH 接管」行为与退出还原、卸载步骤 |

可移植性的两个前提（2026-10-11 审计 + 实测）：

1. **主程序不依赖任何自定义环境变量**，也没有硬编码本机路径；只用标准的 `%LOCALAPPDATA%` / `%APPDATA%` / `%WINDIR%`，依赖目录会自动创建。
2. **预设音频有两处查找**（见 1.6）：先从 `%LOCALAPPDATA%\BSH\presets\<id>\sounds\`（本机装机位），找不到回退到 `profiles\presets\<id>\sounds\`（随包分发的那份）。实测把 `%LOCALAPPDATA%` 那份临时改名后，两个预设仍 `missing=0`。

⚠️ 局限：绿色版只带**主程序 + 预设音频**；`%LOCALAPPDATA%\BSH\sounds\` 那套 39 条装机语音与个人 `rules.json` 属于个人数据，**不随包分发**，需要时从本机复制或重新生成（见 1.4）。

---

## 3. CLI 参数表

参数名常量集中定义在 `Program.cs` 顶部（`--report` … `--ui-smoke`），入口分派在 `Program.cs` 的 `Main` 起。

| 参数 | 形态 | 作用 | 写注册表？ | 退出码（源码位置） |
|---|---|---|---|---|
| （无参数） | — | 托盘常驻（单实例） | 仅界面动作时 | — |
| `--report` | `[输出路径]` | 自检 JSON 报告：事件总数 / 空槽 / 撞车 / 重排预演 / 缺失素材 / 两套方案可用性与预演条数（监听模式附规则表路径、启用条数、将启动哪几类监听器） | ❌ 只读 | 恒 0（`Program.cs:115`，从不设置退出码） |
| `--monitor-report` | `[输出路径]` | 监听引擎体检：对 8 类监听器各做一次「探针启动 → 读状态 → 立刻停止」，报告里的「可启动」是实测值 | ❌ 只读 | 0 成功 / 5 写盘失败（`Program.cs:442`） |
| `--monitor-status` | `[--live]` | 监听接线状态：规则表路径与存在性、启用条数、8 类监听器的装机前提、只读预演。**默认不启引擎** | ❌ 只读 | 0 / 8（`--live` 时监听器没起齐）（`Program.cs:266`） |
| `--monitor-selftest` | `<秒数>` | 监听引擎自检：真装钩子并做**事件级实测** —— `SendInput` 注入 3 次 `F24`、真写 2 个文件、真写剪贴板再恢复、真建自检窗口做跨进程前台切换、注入鼠标左键与滚轮；设备 / 电源两项用「向隐藏窗口自投递消息」做处理器级实测；缺省 4 秒 | ❌ 只读（会短暂显示自检窗口、移动光标、覆盖剪贴板，三项均立即恢复） | 位掩码：0 全通过 / 1 键盘不足 / 2 文件不足 / **4 扩展项（鼠标·窗口·剪贴板·设备·电源）不足**；多项失败按位或（如 3 / 7）（`Core/Monitoring/MonitorSelfTest.cs`） |
| `--apply-profile` | `<方案id> [快照路径]` | 应用三套方案之一：`windows-default`（还原）/ `personal`（换音 + 监听）/ **`takeover`（清 18 槽 + 监听；退出时自动还原）**。**还原类写前自动快照**，个人化与接管不写快照（`Program.cs` 固定输出 `snapshot=none`，回滚走「Windows 原样」+ 基线） | ✅ 会 | 0 / 3 方案 id 不存在 / 4 应用失败 |
| `--preset-list` | — | 列出全部可用预设（`builtin / sounds / rules / missing`），坏预设列在末尾「⚠️ 跳过」里，**一个都没有不算错误** | ❌ 只读 | 恒 0 |
| `--apply-preset` | `<预设id> [快照路径]` | 应用预设：**先停监听引擎 → 自动快照 → 逐槽写**；尊重 `personal` 的跳过偏好；音频缺失的槽跳过并汇总 | ✅ 会 | 0 / 3 预设不存在 / 4 全部音频缺失或写入失败 |
| `--autostart` | `status`｜`on`｜`off` | 开机自启动开关（缺省 = status 只读）。只写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 的 `BSH` 值，用户级、不需要管理员 | `on` / `off` 会 | 0 成功 / 1 写注册表失败 / 2 动作名不认识 |
| `--ui-smoke` | `[输出目录] [light\|dark]` | GUI 冒烟自测：真建主面板 / 规则窗口 / 规则编辑对话框并各截一张 PNG 后退出；`light\|dark` 时临时改 `ui.json`（退出前恢复）。**不写注册表、不保存规则** | ❌ 只读 | 0 成功 / 1 构造或截图失败 |
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
├─ Program.cs                      CLI 参数与入口分派（848 行）
├─ MainForm.cs                     主面板：事件总览（当前系统 39 槽；基线 59 项）/ 试听 / 替换 / 静音 / 一键重排 / 快照 / 三套方案 + 预设
├─ TrayContext.cs                  托盘常驻 + 菜单 + 引擎生命周期兜底
├─ RulesForm.cs                    监听规则可视化编辑器 + 规则编辑对话框（2026-10-18）
├─ Core/
│  ├─ SoundScheme.cs               注册表读写 + 类型保真还原 + SchemeSnapshot（589 行）
│  ├─ ProfileStore.cs              三套方案的登记 / 只读预演 / 应用（含 VoiceWriteKind 类型保真，1082 行）
│  ├─ PresetStore.cs               预设仓库：列表 / 加载 / 应用 / 新建 / 改名 / 删除 + 音频路径回退
│  ├─ AutoStart.cs                 开机自启（只写 HKCU 的 Run 键）
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
│  ├─ presets/<id>/preset.json                  预设定义：内置 mech-voice（界面名「BSH」）/ eva-mech（「BSH EVA」）；自带音频不入库
│  └─ personal/
│     ├─ profile.json              方案定义（kind / applyVoiceScheme / skipFreq / skipEvents）
│     ├─ README.md                 个人配置目录的约定说明
│     ├─ rules.example.json        ★ 公开示例：8 类各一条、全部 enabled=false
│     ├─ rules.json                ❌ 真实规则表（个人配置，不入库，需自己从示例复制）
│     └─ sounds/.gitkeep           ❌ 真实音频（个人素材，仅留目录骨架）
├─ tools/                          验证脚本（verify-*.py）与语音生成脚本（make-*.py / make-voice.ps1 / install-voice.ps1）
├─ voice/                          ❌ 自制音频工作目录（原始素材 + 中间产物，整体不入库）
├─ dist/  dist-new/                ❌ 发布产物（自包含单文件 BSH.exe，不入库；绿色版 zip 见 Release）
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
| `app` | string | **限定哪个应用**（可选，空 = 不限）—— 2026-10-11 新增。⚠️ **语义按 `kind` 而定，目前只有 `ProcessStartStop` 实现了它**：那时匹配的是**事件里的那个进程名**（启动/退出的目标），**不是前台窗口**。于是可以写成 `app: "chrome.exe"` + `match: "start"`：`app` 管哪个程序、`match` 只管方向。写法与 `match` 一致（整词、忽略大小写、多值用 `,` `;` `\|`）；⚠️ 漏写 `.exe` 不会模糊匹配，该规则将**永不命中**。其它 7 类填了 `app` 同样永不命中（刻意选的安全侧） |
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
| `Keyboard` | **事件级实测** | `--monitor-selftest` 真发 `SendInput`（模拟键 `F24`）→ 按「事件明细与注入键名完全一致」计数（`Core/Monitoring/MonitorSelfTest.cs`） |
| `FileSystem` | **事件级实测** | 自检真建临时目录并写 2 个文件 → 按路径前缀计数 |
| `Mouse` | **事件级实测** | 自检建自检窗口 → 把光标移进去 → 真注入左键（LeftDown/LeftUp）与滚轮（WheelUp），按明细计数 |
| `WindowSwitch` | **事件级实测** | 自检关掉自检窗口后切回原前台窗口（跨进程）→ 按「进程名\|标题」匹配计数。⚠️ 监听器带 `WINEVENT_SKIPOWNPROCESS`：**本进程自己的窗口切前台不会上报**（刻意设计，避免 BSH 自己的界面触发提示音） |
| `Clipboard` | **事件级实测** | 自检真写一段文本到剪贴板 → 等含 `Text` 的事件 → 立即恢复原剪贴板内容 |
| `DeviceChange` | **处理器级实测** | 自检向隐藏窗口**自投递** `WM_DEVICECHANGE`（卷到达 Z:）→ 收到 `Arrival` 事件。**真实硬件广播的送达不可自动化**（无虚拟设备可用），只证明「消息进来后能正确解析、投递成事件」 |
| `PowerSession` | **处理器级实测** | 自检向隐藏窗口**自投递** `WM_POWERBROADCAST`（恢复）→ 收到 `Resume` 事件。**真实睡眠 / 恢复不可自动化**，且可能根本不广播给普通顶层窗口（见 6.2） |
| `ProcessStartStop` | **事件级实测（未自动化）** | 代码未接入 `--monitor-selftest`；其"实数"来自人工用宿主进程起停的实测，**无法用一条命令复现** |

换句话说：**八类监听器里，键盘 / 文件 / 鼠标 / 窗口切换 / 剪贴板五项是真实注入的事件级实测；设备 / 电源两项是处理器级实测；只有进程启停仍是人工实测。**

> `--monitor-selftest` 的副作用（开始时会打印告知，均自动恢复）：短暂显示一个自检窗口、移动光标、覆盖剪贴板内容。

`--monitor-status --live` 会额外证明「零规则 = 零钩子」与「有规则时真装钩子」互为对照（`Program.cs:317`、`Program.cs:332`）。

### 6.2 代码里明确标注的限制

- **`DeviceMonitor` 的设备接口通知用了空 GUID**（`DeviceMonitor.cs:55`），即不限定接口类。本机探针实测 `RegisterDeviceNotification` **注册成功**（`--monitor-status` 输出「设备接口通知注册=成功」）；2026-10-18 起 `--monitor-selftest` 还会做**处理器级**实测（自投递卷到达消息 → 收到 `Arrival`），但**「系统真实广播 / 卷以外设备通知能否到达」仍未验证** —— 卷设备（U 盘 / 移动硬盘）靠的是系统广播给所有顶层窗口的主路径，与这次注册无关（`DeviceMonitor.cs:8`）。
- **`PowerSessionMonitor` 的 Suspend / Resume 可能收不到**：它依赖 `WM_POWERBROADCAST`，而现代 Windows 上的挂起 / 恢复**未必广播给普通顶层窗口**。2026-10-18 起 `--monitor-selftest` 会做**处理器级**实测（自投递 `PBT_APMRESUMEAUTOMATIC` → 收到 `Resume`），但**真实挂起 / 恢复参数下是否真的收到广播仍未实测**。锁屏 / 解锁（`SystemEvents.SessionSwitch`）走的是另一条路径，探针里订阅成功。
- **`ProcessMonitor` 会漏事件**：1.5 秒轮询 + 按**进程名集合**比对（`ProcessMonitor.cs:19`、`ProcessMonitor.cs:71`）——
  - 启动后立刻退出的短命进程可能被整个漏掉；
  - 已经有一个同名进程在跑时，**再开一个不会报事件**（集合不变）；
  - 事件明细只有进程名，没有 PID / 命令行。
- **鼠标监听不报移动**（有意为之，`MouseMonitor.cs:8`）；**剪贴板只报类型、不读内容**（有意为之，避免把密码等敏感数据带进规则表与报告，`ClipboardMonitor.cs:11`）。
- **文件监听不递归**（`IncludeSubdirectories = false`，`FileSystemMonitor.cs:73`）；缓冲 64 KB，繁忙目录上仍可能溢出（溢出会记 `LastError`，不抛异常）。
- **`Clipboard` 明细可能是 `Unknown`**：剪贴板同一时刻只允许一个进程打开，被占用是常态、不是故障（`ClipboardMonitor.cs:70`）。
- 面板的「一键重排」只处理**空槽与撞车**，不是自动挑音效。
- ⚠️ **它的计划里没有 `skipEvents` 的例外**（2026-10-11 读代码 + `--report` 实测）：`Core/ReorderPlan.cs` 的补空表
  把 `CCSelect` / `Open` / `Close` 也列在里面，而 `MainForm.RunReorder` 完全不读跳过键。当前状态下点这个按钮，
  会把这三个槽填成系统自带音效（`Windows Pop-up Blocked` / `Windows Recycle` / `Windows Navigation Start`）——
  而按设计它们应当留空：`CCSelect` 永久留空（BSH 自己选行 / 选下拉时不该被自己的提示音吵到，且触发极频繁），
  `Open` / `Close` 交给监听引擎（这两个事件**任何程序**都能触发，后台同步、索引器、自动更新都会碰，
  直接配在槽里会「没操作也响」）。**当前处置：保留功能、不改代码**（用户 2026-10-11 决定，自己注意不点）；
  点之前可以用 `--report` 先看完整的 `reorderPlan`。
- **「BSH 接管」的还原只在正常退出时触发**（`TrayContext.RestoreIfTakeover` 挂在 `ApplicationExit`）：进程被任务管理器强杀 / 崩溃时不会执行，那 18 个槽会保持清空 —— 重启 BSH 会重新接管，或手动点「Windows 原样」。
- **「BSH 接管」清掉的 10 个槽必须靠 `rules.json` 补音**：规则表缺失 / 全部禁用时，这 10 个提示音会静默消失（是配置问题，不是崩溃）。本机 `rules.json` 目前只有 `app-open` / `app-close` 两条进程启停规则。
- **用户新建的预设是空壳**（`sounds` 为空 = 应用它不改任何槽）；`--capture-preset`（把当前系统槽反捕成预设）**尚未实现**，目前只能手写 `sounds` 映射。
- **预设里的 `rules` 字段会被读入，但应用预设目前只写声音槽、不安装预设自带规则** —— 监听规则仍以 `profiles/personal/rules.json` 为准。
- **绿色版便携包不含个人数据**：`%LOCALAPPDATA%\BSH\sounds\` 的 39 条装机语音、个人 `rules.json` 不随包分发（体积与音源授权原因）。
- **GUI 的验证深度（2026-10-18）**：`--ui-smoke` 能证明「主面板 / 监听规则窗口 / 规则编辑对话框」**能构造、能渲染、浅深两档配色正确**（真实屏幕采样截图）；但**按钮点击流程没有自动化** —— 规则保存（备份 → 写盘 → 询问重装钩子）、删除预设确认、自启复选框的点击行为，需要人工点一次验收。自启动的「默认开启 / 取消后不再自动开」已用真实进程启动 + 注册表取证做过闭环实测。
- **`--ui-smoke` 有副作用**：会短暂显示真实窗口（每个约 1 秒）；指定 `light|dark` 时会临时改 `%LOCALAPPDATA%\BSH\ui.json`，退出前恢复。
- **删除用户预设不可撤销**：界面会提示先复制目录备份；删除会连 `%LOCALAPPDATA%\BSH\presets\<id>\sounds\` 一起删。

### 6.3 注册表安全性

- **只动 `HKCU\AppEvents\Schemes\Apps\.Default\<事件>\.Current` 的默认值**；不写 `HKLM`、不需要管理员、不改任何系统文件（`Core/SoundScheme.cs:94`）。
- 应用还原类方案前**自动快照**（界面与 `--apply-profile` 都是），快照记的是**未展开字面值 + 注册表类型 + 子键是否存在**（`SchemeSnapshot.Capture` → `ReadAllRaw`，`Core/SoundScheme.cs:383`）。
- 还原**逐槽都有明确动作**，没有「保持不动」这个选项：`SetValue(原类型)` / `DeleteValue`（删值保键）/ `DeleteSubKeyTree`（删键）（`Core/SoundScheme.cs:241`、`Core/SlotPlanSelfCheck.cs:78`）。
- 还原是**幂等**的：现场本来就是目标结构时返回 `false`，不计入改动条数（`Core/SoundScheme.cs:273`、`Core/SoundScheme.cs:305`）。
- 想在不写注册表的前提下看「还原会做什么」，用 `--slot-plan <快照文件>`（纯只读投影，连文件都不写）。

### 6.4 项目状态

**已发布 v0.1.0（2026-10-11），仍在开发中。** 公开仓库 <https://github.com/Kirun-Linser/BSH-Better_system_hint>，Release 提供绿色版 `BSH-portable.zip`（免安装、自包含单文件 exe + 预设音频 + 使用说明）；尚无 CI 与安装程序。`tools/` 里的 `verify-*.py` 是作者本机的验收脚本（`verify-profiles.py` 会调用 `--apply-profile`，**属于会写注册表的脚本，别顺手跑**）。接口与数据格式（`rules.json` 字段、快照格式）仍可能变，历史快照的兼容只做到「旧格式按值推断类型 + 明确告知用户类型是推断的」（`Core/SoundScheme.cs`）。

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

- **状态**：**v0.1.0 已发布（2026-10-11）**，Release 为绿色版 `BSH-portable.zip`；功能仍在演进（预设体系 / BSH 接管方案已实现，文档见代码与提交记录）。
- **许可**：仓库里目前**没有 LICENSE 文件**，作者尚未声明许可条款。在补上之前，请按「保留所有权利」处理 —— 想要复用请先来找作者。
- 个人素材（`profiles/personal/sounds/`、`voice/`）与自制语音不随代码分发。
