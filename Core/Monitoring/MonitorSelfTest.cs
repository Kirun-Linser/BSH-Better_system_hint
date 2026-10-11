namespace BSH.Core.Monitoring;

/// <summary>
/// 监听引擎自检（<c>--monitor-selftest &lt;秒数&gt;</c>）：<b>不谈理论，只出证据</b> ——
/// 真装钩子、真发按键、真写文件，最后把每个监听器<b>实际收到</b>的事件条数与明细打出来。
/// <para>
/// 三条纪律：<b>不写注册表</b>；<b>不读也不写</b>用户的个人规则表（用内存规则）；结束时<b>一定</b>卸载钩子并销毁窗口。
/// </para>
/// <para>
/// 判定按「事件明细与注入的键名完全一致」计数，而不是「监听器收到了几条」——
/// 后者会把用户此刻真实的敲键算进来，让自检在没收到注入事件时也“看起来达标”（2026-10-10 踩过这个坑）。
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

    /// <summary>自检结果。</summary>
    /// <param name="ExitCode">进程退出码：0 通过 / 1 键盘不足 / 2 文件不足 / 3 两者都不足。</param>
    /// <param name="KeyboardEvents">键盘监听收到的「与注入键名一致」的事件条数（判定依据）。</param>
    /// <param name="FileEvents">文件监听收到的、落在监视目录里的事件条数。</param>
    /// <param name="Triggered">引擎规则命中次数。</param>
    /// <param name="TempDir">自检用的临时目录。</param>
    public sealed record Outcome(int ExitCode, int KeyboardEvents, int FileEvents, long Triggered, string TempDir)
    {
        /// <summary>是否达到预期（键盘 ≥3 且 文件 ≥2）。</summary>
        public bool Passed => ExitCode == 0;
    }

    /// <summary>
    /// 跑一次自检。<paramref name="seconds"/> 是<b>总等待预算</b>（秒），等待期间一旦达标就提前结束。
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
        log("");

        log($"[1/6] 建临时监视目录：{tempDir}");
        Directory.CreateDirectory(tempDir);

        // 先把键名解析出来：解析不了就没必要往下跑（SendInput 会静默什么都不发）
        if (!VirtualKeyNames.TryParse(SimulatedKey, out int simulateVk))
        {
            log($"      ✗ 键名 {SimulatedKey} 无法解析成虚拟键码（VirtualKeyNames 表缺项），自检无法继续");
            return new Outcome(3, 0, 0, 0, tempDir);
        }
        log($"      模拟键：{SimulatedKey} → 虚拟键码 0x{simulateVk:X2}");

        var rules = new List<MonitorRule>
        {
            new()
            {
                Id = "selftest-keyboard",
                Name = "自检：模拟按键",
                Kind = MonitorEventKind.Keyboard,
                Match = SimulatedKey,
                CooldownMs = 0,
            },
            new()
            {
                Id = "selftest-filesystem",
                Name = "自检：临时目录变化",
                Kind = MonitorEventKind.FileSystem,
                Match = tempDir,
                CooldownMs = 0,
            },
        };

        var engine = new MonitorEngine { SuppressPlayback = true };
        MonitorStatus status;
        long triggered;
        int keyboardCount;
        int fileCount;
        int injected;

        try
        {
            log($"[2/6] 启动引擎（内存规则 {rules.Count} 条：Keyboard/{SimulatedKey} + FileSystem/临时目录）……");
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
                return new Outcome(3, 0, 0, 0, tempDir);
            }

            // 给钩子/窗口线程一点余量：Start 虽已同步等到位，注入前留白更接近真实使用
            Thread.Sleep(300);

            log($"[3/6] SendInput 模拟 {ExpectedKeyboardEvents} 次按键 {SimulatedKey}（F24 多数键盘没有这个键，前台程序一般不响应）……");
            injected = 0;
            for (int i = 0; i < ExpectedKeyboardEvents; i++)
            {
                if (InputSimulator.KeyPress((ushort)simulateVk, 30)) injected++;
                else log($"      ! 第 {i + 1} 次 SendInput 未成功注入（Win32 错误码 {InputSimulator.LastErrorCode}；"
                         + $"INPUT 结构体 {InputSimulator.InputStructSize} 字节）");
                Thread.Sleep(80);
            }
            log($"      注入成功 {injected}/{ExpectedKeyboardEvents} 次");

            log($"[4/6] 向监视目录写入 {ExpectedFileEvents} 个文件……");
            for (int i = 1; i <= ExpectedFileEvents; i++)
            {
                string file = Path.Combine(tempDir, $"selftest-{i}.txt");
                File.WriteAllText(file, $"BSH monitor selftest {DateTime.Now:O}");
                log($"      写入 {file}");
                Thread.Sleep(150);
            }

            log($"[5/6] 等待事件到达（最多 {budget} 秒，达标即提前结束）……");
            var keyName = VirtualKeyNames.Name(simulateVk);
            var deadline = DateTime.UtcNow.AddSeconds(budget);
            while (DateTime.UtcNow < deadline)
            {
                if (CountKey(engine, keyName) >= ExpectedKeyboardEvents
                    && CountFiles(engine, tempDir) >= ExpectedFileEvents)
                {
                    break;
                }
                Thread.Sleep(50);
            }

            status = engine.Status();
            keyboardCount = CountKey(engine, keyName);
            fileCount = CountFiles(engine, tempDir);
            triggered = engine.TriggeredCount;

            log("[6/6] 停止引擎并卸载监听器……");
            engine.Stop();

            var after = engine.LastStoppedSources;
            log($"      停止后 IsRunning：{(after.Count == 0 ? "（无）" : string.Join("  ", after.Select(s => $"{s.Kind}={s.IsRunning}")))}");
            log($"      引擎运行中={engine.IsRunning}（IsRunning 全为 False 才算卸载干净）");
            log("");

            Report(log, engine, tempDir, keyName, keyboardCount, fileCount, triggered, injected, status, after);
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

        int exit = keyboardCount >= ExpectedKeyboardEvents
            ? (fileCount >= ExpectedFileEvents ? 0 : 2)
            : (fileCount >= ExpectedFileEvents ? 1 : 3);

        log("");
        log(exit == 0
            ? "结论：通过（键盘与文件监听都收到了预期数量的事件）"
            : $"结论：失败（键盘 {keyboardCount}/{ExpectedKeyboardEvents}，文件 {fileCount}/{ExpectedFileEvents}）");

        return new Outcome(exit, keyboardCount, fileCount, triggered, tempDir);
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
        IReadOnlyList<MonitorSourceStatus> after)
    {
        var events = engine.RecentEvents(500);

        long keyTotal = during.Sources.FirstOrDefault(s => s.Kind == MonitorEventKind.Keyboard)?.PublishedCount ?? 0;
        long fileTotal = during.Sources.FirstOrDefault(s => s.Kind == MonitorEventKind.FileSystem)?.PublishedCount ?? 0;

        log("── 实测结果 ─────────────────────────────────");
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
}
