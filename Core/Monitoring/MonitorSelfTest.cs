using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BSH.Core.Monitoring;

/// <summary>
/// 监听引擎自检（<c>--monitor-selftest &lt;秒数&gt;</c>）：<b>不谈理论，只出证据</b> ——
/// 真装钩子、真发输入、真写文件，最后把每个监听器<b>实际收到</b>的事件条数与明细打出来。
/// <para>
/// 覆盖深度分两档（输出里会如实标注）：
/// <list type="number">
/// <item><description><b>真实注入</b>：键盘（<c>SendInput</c>）、文件（真写文件）、剪贴板（真写剪贴板再恢复）、
/// 窗口切换（真建自检窗口并切到前台）、鼠标（真注入左键与滚轮，注入前把光标移到自检窗口上）。</description></item>
/// <item><description><b>处理器级</b>：设备（向隐藏窗口自投递 <c>WM_DEVICECHANGE</c>）、电源（自投递
/// <c>WM_POWERBROADCAST</c>）—— 真实硬件广播 / 睡眠恢复无法自动化，这里只证明
/// 「消息进来后能正确解析并投递成事件」。</description></item>
/// </list>
/// </para>
/// <para>
/// 三条纪律：<b>不写注册表</b>；<b>不读也不写</b>用户的个人规则表（用内存规则）；结束时<b>一定</b>卸载钩子并销毁窗口。
/// 副作用（可接受、开始时会打印告知）：短暂显示一个自检窗口、移动光标、覆盖剪贴板内容（用后立即恢复）。
/// </para>
/// <para>
/// 判定按「事件明细与注入内容一致」计数，而不是「监听器收到了几条」——
/// 后者会把用户此刻真实的操作算进来，让自检在没收到注入事件时也"看起来达标"（2026-10-10 踩过这个坑）。
/// </para>
/// </summary>
public static class MonitorSelfTest
{
    /// <summary>键盘监听的预期最少事件数（自检模拟 3 次按键）。</summary>
    public const int ExpectedKeyboardEvents = 3;

    /// <summary>文件监听的预期最少事件数（自检写 2 个文件）。</summary>
    public const int ExpectedFileEvents = 2;

    /// <summary>自检用的模拟键：F24 在多数键盘上并不存在，前台程序基本不响应，避免打扰用户。</summary>
    private const string SimulatedKey = "F24";

    /// <summary>自检窗口标题（窗口切换事件按它筛选，避免把用户真实的切窗算进来）。</summary>
    private const string SelfTestFormTitle = "BSH 监听自检（自动关闭）";

    /// <summary>Alt 键虚拟键码：Windows 的前台锁规则允许「按下 Alt 后」由后台进程切前台。</summary>
    private const ushort VkMenu = 0x12;

    /// <summary>自检结果。</summary>
    /// <param name="ExitCode">
    /// 进程退出码（位掩码）：<c>0</c> 全通过 / <c>1</c> 键盘不足 / <c>2</c> 文件不足 / <c>4</c> 扩展项（鼠标·窗口·剪贴板·设备·电源）有未达标；
    /// 多项失败时按位或（如 <c>3</c> = 键盘 + 文件都不足，<c>7</c> = 全都不足）。
    /// </param>
    /// <param name="KeyboardEvents">键盘监听收到的「与注入键名一致」的事件条数（判定依据）。</param>
    /// <param name="FileEvents">文件监听收到的、落在监视目录里的事件条数。</param>
    /// <param name="Triggered">引擎规则命中次数。</param>
    /// <param name="TempDir">自检用的临时目录。</param>
    /// <param name="MouseLeftDown">鼠标监听收到的 <c>LeftDown</c> 条数（自检注入 1 次点击 + 1 次滚轮）。</param>
    /// <param name="MouseLeftUp">鼠标监听收到的 <c>LeftUp</c> 条数。</param>
    /// <param name="MouseWheel">鼠标监听收到的 <c>WheelUp</c> 条数。</param>
    /// <param name="WindowSwitchEvents">窗口切换监听收到的、切回原前台窗口后的跨进程切换事件条数。</param>
    /// <param name="ClipboardEvents">剪贴板监听收到的、含 <c>Text</c> 的事件条数。</param>
    /// <param name="DeviceEvents">设备监听收到的 <c>Arrival</c> 事件条数（处理器级）。</param>
    /// <param name="PowerEvents">电源监听收到的 <c>Resume</c> 事件条数（处理器级）。</param>
    /// <param name="ExtendedNote">扩展项里的非致命说明（如「系统拒绝前台切换」「剪贴板被占用」）。</param>
    public sealed record Outcome(
        int ExitCode,
        int KeyboardEvents,
        int FileEvents,
        long Triggered,
        string TempDir,
        int MouseLeftDown = 0,
        int MouseLeftUp = 0,
        int MouseWheel = 0,
        int WindowSwitchEvents = 0,
        int ClipboardEvents = 0,
        int DeviceEvents = 0,
        int PowerEvents = 0,
        string? ExtendedNote = null)
    {
        /// <summary>是否全部达标（键盘 / 文件 / 扩展项都通过）。</summary>
        public bool Passed => ExitCode == 0;

        /// <summary>扩展五项（鼠标·窗口·剪贴板·设备·电源）是否全部达标。</summary>
        public bool ExtendedPassed =>
            MouseLeftDown >= 1 && MouseLeftUp >= 1 && MouseWheel >= 1
            && WindowSwitchEvents >= 1 && ClipboardEvents >= 1 && DeviceEvents >= 1 && PowerEvents >= 1;
    }

    /// <summary>
    /// 跑一次自检。<paramref name="seconds"/> 是键盘 / 文件两项的<b>等待预算</b>（秒），等待期间一旦达标就提前结束。
    /// </summary>
    /// <param name="seconds">等待预算（秒），最小 1。</param>
    /// <param name="log">输出回调（逐行）。</param>
    public static Outcome Run(double seconds, Action<string> log)
    {
        int budget = (int)Math.Max(1, Math.Round(seconds));
        string tempDir = Path.Combine(
            Path.GetTempPath(),
            $"bsh-selftest-{DateTime.Now:yyyyMMdd-HHmmss}-{Random.Shared.Next(1000, 9999)}");

        log("=== BSH 监听引擎自检（--monitor-selftest）===");
        log($"预算 {budget} 秒 | 只匹配不发声（SuppressPlayback=true）| 不写注册表、不动个人规则表（改用内存规则）");
        log("副作用告知：本自检会短暂显示一个自检窗口、移动光标、覆盖剪贴板内容（三项都会立即恢复）");
        log("");

        log($"[1/9] 建临时监视目录：{tempDir}");
        Directory.CreateDirectory(tempDir);

        // 先把键名解析出来：解析不了就没必要往下跑（SendInput 会静默什么都不发）
        if (!VirtualKeyNames.TryParse(SimulatedKey, out int simulateVk))
        {
            log($"      ✗ 键名 {SimulatedKey} 无法解析成虚拟键码（VirtualKeyNames 表缺项），自检无法继续");
            return new Outcome(3, 0, 0, 0, tempDir, ExtendedNote: "键名解析失败，扩展项未测");
        }
        log($"      模拟键：{SimulatedKey} → 虚拟键码 0x{simulateVk:X2}");

        // 7 类监听器全开（ProcessStartStop 需要真起停进程，另由人工实测覆盖）
        var rules = new List<MonitorRule>
        {
            Rule("selftest-keyboard", "自检：模拟按键", MonitorEventKind.Keyboard, SimulatedKey),
            Rule("selftest-filesystem", "自检：临时目录变化", MonitorEventKind.FileSystem, tempDir),
            Rule("selftest-mouse", "自检：注入鼠标点击/滚轮", MonitorEventKind.Mouse, "*"),
            Rule("selftest-window", "自检：前台窗口切换", MonitorEventKind.WindowSwitch, "*"),
            Rule("selftest-device", "自检：自投递设备到达", MonitorEventKind.DeviceChange, "*"),
            Rule("selftest-power", "自检：自投递电源恢复", MonitorEventKind.PowerSession, "*"),
            Rule("selftest-clipboard", "自检：写入剪贴板", MonitorEventKind.Clipboard, "*"),
        };

        var engine = new MonitorEngine { SuppressPlayback = true };
        MonitorStatus status;
        long triggered;
        int keyboardCount;
        int fileCount;
        int injected;
        int mouseDown = 0, mouseUp = 0, mouseWheel = 0;
        int windowCount = 0, clipboardCount = 0, deviceCount = 0, powerCount = 0;
        var notes = new List<string>();

        try
        {
            log($"[2/9] 启动引擎（内存规则 {rules.Count} 条 → {rules.Select(r => r.Kind).Distinct().Count()} 类监听器）……");
            engine.Start(rules);

            status = engine.Status();
            foreach (var s in status.Sources)
            {
                log($"      {s.Kind,-16} IsRunning={s.IsRunning,-5} 机制={s.Mechanism}");
                if (!string.IsNullOrWhiteSpace(s.LastError)) log($"      └ 提示/失败原因：{s.LastError}");
            }

            if (status.Sources.Count == 0)
            {
                log("      ✗ 没有任何监听器被启动，自检无法继续");
                return new Outcome(3, 0, 0, 0, tempDir, ExtendedNote: "监听器未启动，扩展项未测");
            }

            // 给钩子/窗口线程一点余量：Start 虽已同步等到位，注入前留白更接近真实使用
            Thread.Sleep(300);

            log($"[3/9] SendInput 模拟 {ExpectedKeyboardEvents} 次按键 {SimulatedKey}（F24 多数键盘没有这个键，前台程序一般不响应）……");
            injected = 0;
            for (int i = 0; i < ExpectedKeyboardEvents; i++)
            {
                if (InputSimulator.KeyPress((ushort)simulateVk, 30)) injected++;
                else log($"      ! 第 {i + 1} 次 SendInput 未成功注入（Win32 错误码 {InputSimulator.LastErrorCode}；"
                         + $"INPUT 结构体 {InputSimulator.InputStructSize} 字节）");
                Thread.Sleep(80);
            }
            log($"      注入成功 {injected}/{ExpectedKeyboardEvents} 次");

            log($"[4/9] 向监视目录写入 {ExpectedFileEvents} 个文件……");
            for (int i = 1; i <= ExpectedFileEvents; i++)
            {
                string file = Path.Combine(tempDir, $"selftest-{i}.txt");
                File.WriteAllText(file, $"BSH monitor selftest {DateTime.Now:O}");
                log($"      写入 {file}");
                Thread.Sleep(150);
            }

            // ── 扩展 1：剪贴板（真实写入，随后恢复原内容）──
            log("[5/9] 剪贴板监听：写入一段文本 → 等 Text 事件 → 恢复原剪贴板……");
            var clipMark = DateTime.UtcNow;
            IDataObject? oldClip = null;
            bool clipSaved = false;
            try
            {
                oldClip = Clipboard.GetDataObject();
                clipSaved = true;
            }
            catch (Exception ex)
            {
                notes.Add($"读取原剪贴板失败（{ex.GetType().Name}），本次不做剪贴板实测");
            }

            if (clipSaved)
            {
                try
                {
                    Clipboard.SetText($"BSH 监听自检 {DateTime.UtcNow:O}");
                    WaitFor(() => CountSince(engine, clipMark, MonitorEventKind.Clipboard,
                                             d => d.Contains("Text", StringComparison.OrdinalIgnoreCase)) >= 1, 2000);
                }
                catch (Exception ex)
                {
                    notes.Add($"写剪贴板失败（{ex.GetType().Name}），本次剪贴板项未达标");
                }
                finally
                {
                    try
                    {
                        if (oldClip is null) Clipboard.Clear();
                        else Clipboard.SetDataObject(oldClip, true);
                    }
                    catch (Exception ex)
                    {
                        notes.Add($"恢复原剪贴板失败（{ex.GetType().Name}）—— 原内容可能已丢失");
                    }
                }
            }

            clipboardCount = CountSince(engine, clipMark, MonitorEventKind.Clipboard,
                                        d => d.Contains("Text", StringComparison.OrdinalIgnoreCase));
            log($"      剪贴板监听收到 {clipboardCount} 条含 Text 的事件（预期 ≥1）");

            // ── 扩展 2：鼠标 + 窗口切换 ──
            // 窗口切换项不能拿自检窗口验证：WindowSwitchMonitor 装了 WINEVENT_SKIPOWNPROCESS，
            // 本进程自己的窗口切前台按设计不会上报（避免 BSH 自己的界面触发提示音）。
            // 所以先用自检窗口承载鼠标注入，再切回「原前台窗口」（跨进程）来验证窗口切换事件。
            log("[6/9] 鼠标 + 窗口切换：建自检窗口注入左键/滚轮 → 关窗后切回原前台窗口（验证跨进程切换事件）……");
            var inputMark = DateTime.UtcNow;
            IntPtr previousForeground = GetForegroundWindow();
            string previousLabel = WindowLabel(previousForeground);
            string previousExe = previousLabel.Split('|')[0];
            // 窗口切换的计时从「建自检窗口之前」开始：关闭自检窗口时 Windows 会把前台自动还给原窗口，
            // 那条 EVENT_SYSTEM_FOREGROUND 就发生在这之后（若等关窗后再取时间点会把它漏掉 —— 2026-10-18 实测踩过）。
            var windowMark = DateTime.UtcNow;
            Point previousCursor = Cursor.Position;
            try
            {
                using var form = new Form
                {
                    Text = SelfTestFormTitle,
                    Width = 260,
                    Height = 150,
                    StartPosition = FormStartPosition.CenterScreen,
                    FormBorderStyle = FormBorderStyle.FixedToolWindow,
                    ShowInTaskbar = false,
                    TopMost = true,
                };
                form.Show();
                PumpFor(150);

                // 后台进程切前台会被前台锁拦；标准解法是先按一下 Alt（Windows 允许此后切前台）
                bool foreground = SetForegroundWindow(form.Handle);
                if (!foreground)
                {
                    InputSimulator.KeyDown(VkMenu);
                    PumpFor(60);
                    foreground = SetForegroundWindow(form.Handle);
                    InputSimulator.KeyUp(VkMenu);
                }
                PumpFor(200);
                log($"      自检窗口前台：SetForegroundWindow={foreground}，确在前台={GetForegroundWindow() == form.Handle}"
                    + "（本进程窗口的切换按设计被 SKIPOWNPROCESS 跳过，不用于判定）");

                var center = form.PointToScreen(new Point(form.ClientSize.Width / 2, form.ClientSize.Height / 2));
                if (!InputSimulator.MouseMoveTo(center.X, center.Y))
                    notes.Add($"SetCursorPos 失败（Win32 {InputSimulator.LastErrorCode}）");
                PumpFor(80);

                if (!InputSimulator.MouseLeftClick())
                    notes.Add($"鼠标左键注入失败（Win32 {InputSimulator.LastErrorCode}）");
                PumpFor(150);

                if (!InputSimulator.MouseWheel(120))
                    notes.Add($"滚轮注入失败（Win32 {InputSimulator.LastErrorCode}）");
                PumpFor(200);
            }
            catch (Exception ex)
            {
                notes.Add($"自检窗口 / 鼠标注入异常（{ex.GetType().Name}: {ex.Message}）");
            }
            finally
            {
                try { Cursor.Position = previousCursor; } catch { /* 光标恢复失败不影响判定 */ }
            }

            // 鼠标：等 LeftDown / LeftUp / WheelUp 三类都到
            WaitFor(() => CountSince(engine, inputMark, MonitorEventKind.Mouse,
                                     d => d.Equals("LeftDown", StringComparison.OrdinalIgnoreCase)) >= 1
                      && CountSince(engine, inputMark, MonitorEventKind.Mouse,
                                     d => d.Equals("LeftUp", StringComparison.OrdinalIgnoreCase)) >= 1
                      && CountSince(engine, inputMark, MonitorEventKind.Mouse,
                                     d => d.Equals("WheelUp", StringComparison.OrdinalIgnoreCase)) >= 1, 2000);
            mouseDown = CountSince(engine, inputMark, MonitorEventKind.Mouse, d => d.Equals("LeftDown", StringComparison.OrdinalIgnoreCase));
            mouseUp = CountSince(engine, inputMark, MonitorEventKind.Mouse, d => d.Equals("LeftUp", StringComparison.OrdinalIgnoreCase));
            mouseWheel = CountSince(engine, inputMark, MonitorEventKind.Mouse, d => d.Equals("WheelUp", StringComparison.OrdinalIgnoreCase));
            log($"      鼠标 LeftDown={mouseDown} LeftUp={mouseUp} WheelUp={mouseWheel}（各预期 ≥1）");

            // 窗口切换：关闭自检窗口后前台会回到原窗口；再显式切一次做兜底（可能已是前台，属正常）
            if (previousForeground != IntPtr.Zero)
            {
                SetForegroundWindow(previousForeground);
                PumpFor(300);
                WaitFor(() => CountSince(engine, windowMark, MonitorEventKind.WindowSwitch,
                                         d => d.StartsWith(previousExe + "|", StringComparison.OrdinalIgnoreCase)) >= 1, 2500);
                windowCount = CountSince(engine, windowMark, MonitorEventKind.WindowSwitch,
                                         d => d.StartsWith(previousExe + "|", StringComparison.OrdinalIgnoreCase));
                log($"      切回原前台「{previousLabel}」→ 窗口切换监听收到 {windowCount} 条（预期 ≥1）");
            }
            else
            {
                notes.Add("没有可切回的原前台窗口，窗口切换项未测");
                log("      没有可切回的原前台窗口，窗口切换项未测");
            }

            // ── 扩展 3：设备（处理器级：自投递消息）──
            log("[7/9] 设备监听：向隐藏窗口自投递「卷到达 Z:」（处理器级实测；真实硬件插拔无法自动化）……");
            var deviceMark = DateTime.UtcNow;
            bool devicePosted = engine.FindSource(MonitorEventKind.DeviceChange) is DeviceMonitor deviceMonitor
                                && deviceMonitor.SimulateVolumeArrival('Z');
            if (!devicePosted) notes.Add("设备监听器未启动或 WM_DEVICECHANGE 未投递");
            else
            {
                WaitFor(() => CountSince(engine, deviceMark, MonitorEventKind.DeviceChange,
                                         d => d.StartsWith("Arrival", StringComparison.OrdinalIgnoreCase)) >= 1, 2000);
            }
            deviceCount = CountSince(engine, deviceMark, MonitorEventKind.DeviceChange,
                                     d => d.StartsWith("Arrival", StringComparison.OrdinalIgnoreCase));
            log($"      设备监听收到 {deviceCount} 条 Arrival 事件（预期 ≥1）");

            // ── 扩展 4：电源（处理器级：自投递消息）──
            log("[8/9] 电源监听：向隐藏窗口自投递「恢复」消息（处理器级实测；真实睡眠/恢复无法自动化）……");
            var powerMark = DateTime.UtcNow;
            bool powerPosted = engine.FindSource(MonitorEventKind.PowerSession) is PowerSessionMonitor powerMonitor
                               && powerMonitor.SimulateResume();
            if (!powerPosted) notes.Add("电源监听器未启动或 WM_POWERBROADCAST 未投递");
            else
            {
                WaitFor(() => CountSince(engine, powerMark, MonitorEventKind.PowerSession,
                                         d => d.Equals("Resume", StringComparison.OrdinalIgnoreCase)) >= 1, 2000);
            }
            powerCount = CountSince(engine, powerMark, MonitorEventKind.PowerSession,
                                    d => d.Equals("Resume", StringComparison.OrdinalIgnoreCase));
            log($"      电源监听收到 {powerCount} 条 Resume 事件（预期 ≥1）");

            log($"[9/9] 等键盘 / 文件事件到齐（最多 {budget} 秒，达标即提前结束）……");
            var keyName = VirtualKeyNames.Name(simulateVk);
            var deadline = DateTime.UtcNow.AddSeconds(budget);
            while (DateTime.UtcNow < deadline)
            {
                if (CountKey(engine, keyName) >= ExpectedKeyboardEvents
                    && CountFiles(engine, tempDir) >= ExpectedFileEvents)
                {
                    break;
                }
                PumpFor(50);
            }

            status = engine.Status();
            keyboardCount = CountKey(engine, keyName);
            fileCount = CountFiles(engine, tempDir);
            triggered = engine.TriggeredCount;

            log("停止引擎并卸载监听器……");
            engine.Stop();

            var after = engine.LastStoppedSources;
            log($"      停止后 IsRunning：{(after.Count == 0 ? "（无）" : string.Join("  ", after.Select(s => $"{s.Kind}={s.IsRunning}")))}");
            log($"      引擎运行中={engine.IsRunning}（IsRunning 全为 False 才算卸载干净）");
            log("");

            Report(log, engine, tempDir, keyName, keyboardCount, fileCount, triggered, injected, status, after,
                   windowCount, mouseDown, mouseUp, mouseWheel, clipboardCount, deviceCount, powerCount, notes);
        }
        finally
        {
            engine.Dispose();

            try
            {
                Directory.Delete(tempDir, true);
            }
            catch (Exception ex)
            {
                log($"（临时目录清理失败，可手动删除：{tempDir} —— {ex.Message}）");
            }
        }

        var extendedPassed = mouseDown >= 1 && mouseUp >= 1 && mouseWheel >= 1
                             && windowCount >= 1 && clipboardCount >= 1 && deviceCount >= 1 && powerCount >= 1;

        int exit = 0;
        if (keyboardCount < ExpectedKeyboardEvents) exit |= 1;
        if (fileCount < ExpectedFileEvents) exit |= 2;
        if (!extendedPassed) exit |= 4;

        log("");
        log(exit == 0
            ? "结论：通过（键盘 / 文件 / 扩展五项全部收到预期事件）"
            : $"结论：失败（键盘 {keyboardCount}/{ExpectedKeyboardEvents}，文件 {fileCount}/{ExpectedFileEvents}，"
              + $"扩展五项{(extendedPassed ? "通过" : "有未达标")}）");

        return new Outcome(exit, keyboardCount, fileCount, triggered, tempDir,
                           mouseDown, mouseUp, mouseWheel, windowCount, clipboardCount, deviceCount, powerCount,
                           notes.Count == 0 ? null : string.Join("；", notes));
    }

    // ───────────────────────────── 内部工具 ─────────────────────────────

    /// <summary>造一条自检常用规则（CooldownMs=0，避免防抖把连续注入吃掉）。</summary>
    private static MonitorRule Rule(string id, string name, MonitorEventKind kind, string match) => new()
    {
        Id = id,
        Name = name,
        Kind = kind,
        Match = match,
        CooldownMs = 0,
    };

    /// <summary>一边泵一次窗口消息一边等条件成立（自检里创建的 Form 需要消息泵才有反应）。</summary>
    /// <param name="condition">判定条件。</param>
    /// <param name="timeoutMs">超时（毫秒）。</param>
    private static bool WaitFor(Func<bool> condition, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            Application.DoEvents();
            Thread.Sleep(20);
        }
        return condition();
    }

    /// <summary>泵消息并等待指定毫秒（自检窗口显示 / 前台切换后给它一点时间）。</summary>
    private static void PumpFor(int ms)
    {
        var deadline = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }

    /// <summary>统计「指定类别 + 时间在 <paramref name="since"/> 之后 + 明细满足条件」的事件条数。</summary>
    private static int CountSince(MonitorEngine engine, DateTime since, MonitorEventKind kind, Func<string, bool> predicate) =>
        engine.RecentEvents(500).Count(e =>
            e.Kind == kind && e.TimestampUtc >= since && predicate(e.Detail));

    /// <summary>
    /// 把窗口句柄描述成与 <see cref="WindowSwitchMonitor"/> 相同的 <c>进程名.exe|窗口标题</c> 格式。
    /// <para>自检用它核对「切回原前台窗口」后监听器收到的事件是否对得上，避免把用户真实的切窗噪声算进来。</para>
    /// </summary>
    /// <param name="hwnd">窗口句柄；<see cref="IntPtr.Zero"/> 时返回 <c>unknown.exe|</c>。</param>
    private static string WindowLabel(IntPtr hwnd)
    {
        string exe = "unknown.exe";
        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != 0)
            {
                using var p = Process.GetProcessById((int)pid);
                exe = p.ProcessName + ".exe";
            }
        }
        catch
        {
            // 进程可能刚好退出、或不允许查询：与监听器一样退回占位名
        }

        var buffer = new char[260];
        int len = GetWindowTextW(hwnd, buffer, buffer.Length);
        string title = len > 0 ? new string(buffer, 0, len) : "";
        return $"{exe}|{title}";
    }

    /// <summary>打印实测明细：条数 + 每条事件的时间与内容。</summary>
    private static void Report(
        Action<string> log,
        MonitorEngine engine,
        string tempDir,
        string keyName,
        int keyboardCount,
        int fileCount,
        long triggered,
        int injected,
        MonitorStatus during,
        IReadOnlyList<MonitorSourceStatus> after,
        int windowCount,
        int mouseDown,
        int mouseUp,
        int mouseWheel,
        int clipboardCount,
        int deviceCount,
        int powerCount,
        IReadOnlyList<string> notes)
    {
        var events = engine.RecentEvents(500);

        long keyTotal = during.Sources.FirstOrDefault(s => s.Kind == MonitorEventKind.Keyboard)?.PublishedCount ?? 0;
        long fileTotal = during.Sources.FirstOrDefault(s => s.Kind == MonitorEventKind.FileSystem)?.PublishedCount ?? 0;

        log("── 实测结果（真实注入）────────────────────────");
        log($"键盘监听收到 {keyboardCount} 条「按键={keyName}」（预期 ≥{ExpectedKeyboardEvents}）：{(keyboardCount >= ExpectedKeyboardEvents ? "达标" : "未达标")}");
        log($"  （SendInput 注入成功 {injected}/{ExpectedKeyboardEvents} 次；该监听器累计收到 {keyTotal} 条，差额来自此刻外部真实按键）");
        foreach (var (e, i) in events.Where(e => e.Kind == MonitorEventKind.Keyboard).Select((e, i) => (e, i + 1)))
        {
            log($"  #{i} {e.TimestampUtc.ToLocalTime():HH:mm:ss.fff}  按键={e.Detail}");
        }

        log($"文件监听收到 {fileCount} 条落在监视目录里的事件（预期 ≥{ExpectedFileEvents}）：{(fileCount >= ExpectedFileEvents ? "达标" : "未达标")}");
        log($"  （该监听器累计收到 {fileTotal} 条）");
        foreach (var (e, i) in events.Where(e => e.Kind == MonitorEventKind.FileSystem).Select((e, i) => (e, i + 1)))
        {
            log($"  #{i} {e.TimestampUtc.ToLocalTime():HH:mm:ss.fff}  {e.Detail}");
        }

        log("── 实测结果（扩展项）────────────────────────────");
        log($"鼠标：LeftDown={mouseDown} LeftUp={mouseUp} WheelUp={mouseWheel}（各预期 ≥1）"
            + $"：{(mouseDown >= 1 && mouseUp >= 1 && mouseWheel >= 1 ? "达标" : "未达标")}");
        log($"窗口切换：切回原前台窗口后收到 {windowCount} 条（预期 ≥1）：{(windowCount >= 1 ? "达标" : "未达标")}");
        foreach (var (e, i) in events.Where(e => e.Kind == MonitorEventKind.WindowSwitch).Select((e, i) => (e, i + 1)))
        {
            log($"  #{i} {e.TimestampUtc.ToLocalTime():HH:mm:ss.fff}  {e.Detail}");
        }
        log($"剪贴板：含 Text 事件 {clipboardCount} 条（预期 ≥1）：{(clipboardCount >= 1 ? "达标" : "未达标")}");
        log($"设备（处理器级，自投递 Arrival）：{deviceCount} 条（预期 ≥1）：{(deviceCount >= 1 ? "达标" : "未达标")}");
        log($"电源（处理器级，自投递 Resume）：{powerCount} 条（预期 ≥1）：{(powerCount >= 1 ? "达标" : "未达标")}");
        if (notes.Count > 0)
        {
            log("  说明：");
            foreach (var note in notes) log($"   · {note}");
        }

        log($"监视目录：{tempDir}");
        log($"引擎规则命中：{triggered} 次（自检模式只记账不发声）");
        log($"停止后监听器状态：{string.Join("  ", after.Select(s => $"{s.Kind}={s.IsRunning}"))}｜引擎运行中=False");
    }

    /// <summary>统计「与注入键名完全一致」的键盘事件条数。</summary>
    private static int CountKey(MonitorEngine engine, string keyName) =>
        engine.RecentEvents(500).Count(e =>
            e.Kind == MonitorEventKind.Keyboard && e.Detail.Equals(keyName, StringComparison.OrdinalIgnoreCase));

    /// <summary>统计落在监视目录里的文件事件条数（按路径前缀判断，避免把外部目录的噪声算进来）。</summary>
    private static int CountFiles(MonitorEngine engine, string dir) =>
        engine.RecentEvents(500).Count(e =>
            e.Kind == MonitorEventKind.FileSystem
            && e.Detail.Contains(dir, StringComparison.OrdinalIgnoreCase));

    // ── Win32 ────────────────────────────────────────────────────────────────

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, [Out] char[] lpString, int nMaxCount);
}
