using System.Diagnostics;

namespace BSH.Core.Monitoring;

/// <summary>一次监听器「探针启动」的结果（<c>--monitor-report</c> 用它给出<b>实测</b>的可启动状态，而不是靠猜）。</summary>
/// <param name="Kind">事件类别名。</param>
/// <param name="Mechanism">底层机制。</param>
/// <param name="Available">静态前提是否成立（Windows 平台 + 该机制需要的条件，如文件监听要有目录）。</param>
/// <param name="UnavailableReason">前提不成立的原因。</param>
/// <param name="Startable">探针启动后 <c>IsRunning</c> 是否为真。</param>
/// <param name="ProbeMs">「启动 + 立即停止」耗时（毫秒）。</param>
/// <param name="Note">非致命提示或失败原因（如设备通知注册失败、目录不存在被跳过）。</param>
/// <param name="Stopped">探针结束后是否已确认停止（<c>false</c> 说明有残留，必须排查）。</param>
/// <param name="Detail">该机制在本次探针里的专属证据（如「设备接口通知注册=成功」「基线快照 231 个进程」）。</param>
public sealed record MonitorProbeResult(
    string Kind,
    string Mechanism,
    bool Available,
    string? UnavailableReason,
    bool Startable,
    long ProbeMs,
    string? Note,
    bool Stopped,
    string? Detail);

/// <summary>
/// 监听器工厂与探针：引擎与 <c>--monitor-report</c> 都从这里拿实例，保证「报告里看到的机制」与「跑起来的机制」是同一份代码。
/// </summary>
public static class MonitorSources
{
    /// <summary>全部 8 类监听器（顺序固定，报告里照这个顺序列）。</summary>
    public static IReadOnlyList<MonitorEventKind> All { get; } =
    [
        MonitorEventKind.Keyboard,
        MonitorEventKind.Mouse,
        MonitorEventKind.WindowSwitch,
        MonitorEventKind.ProcessStartStop,
        MonitorEventKind.DeviceChange,
        MonitorEventKind.PowerSession,
        MonitorEventKind.Clipboard,
        MonitorEventKind.FileSystem,
    ];

    /// <summary>
    /// 新建一个监听器。<b>只新建，不启动</b>（文件监听会顺带把 <paramref name="fileSystemRoot"/> 灌进监视目录）。
    /// </summary>
    /// <param name="kind">要哪种监听器。</param>
    /// <param name="fileSystemRoot">文件监听的监视目录（其它类别忽略此参数）。</param>
    public static IMonitorSource Create(MonitorEventKind kind, string? fileSystemRoot = null)
    {
        switch (kind)
        {
            case MonitorEventKind.Keyboard: return new KeyboardMonitor();
            case MonitorEventKind.Mouse: return new MouseMonitor();
            case MonitorEventKind.WindowSwitch: return new WindowSwitchMonitor();
            case MonitorEventKind.ProcessStartStop: return new ProcessMonitor();
            case MonitorEventKind.DeviceChange: return new DeviceMonitor();
            case MonitorEventKind.PowerSession: return new PowerSessionMonitor();
            case MonitorEventKind.Clipboard: return new ClipboardMonitor();
            case MonitorEventKind.FileSystem:
                {
                    var monitor = new FileSystemMonitor();
                    if (!string.IsNullOrWhiteSpace(fileSystemRoot)) monitor.SetRoots([fileSystemRoot]);
                    return monitor;
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知的监听类别");
        }
    }

    /// <summary>取某类监听器的机制描述。</summary>
    public static string Describe(MonitorEventKind kind) => kind switch
    {
        MonitorEventKind.Keyboard => "低层全局键盘钩子 SetWindowsHookEx(WH_KEYBOARD_LL)",
        MonitorEventKind.Mouse => "低层全局鼠标钩子 SetWindowsHookEx(WH_MOUSE_LL)",
        MonitorEventKind.WindowSwitch => "SetWinEventHook(EVENT_SYSTEM_FOREGROUND, WINEVENT_OUTOFCONTEXT)",
        MonitorEventKind.ProcessStartStop => $"轮询 Process.GetProcesses() 快照对比（每 {ProcessMonitor.DefaultPollMs} ms）",
        MonitorEventKind.DeviceChange => "隐藏顶层窗口接收 WM_DEVICECHANGE（+ RegisterDeviceNotification）",
        MonitorEventKind.PowerSession => "WM_POWERBROADCAST + SystemEvents.SessionSwitch",
        MonitorEventKind.Clipboard => "AddClipboardFormatListener + WM_CLIPBOARDUPDATE",
        MonitorEventKind.FileSystem => "FileSystemWatcher（每个目录一个，单层不递归）",
        _ => kind.ToString(),
    };

    /// <summary>
    /// 探针：真启动一次、读状态、立刻停掉。
    /// <para>这是 <c>--monitor-report</c> 里「可启动」这一列的证据来源；探针<b>结束后不会留下任何钩子 / 窗口 / 定时器</b>。</para>
    /// </summary>
    /// <param name="kind">要探测的类别。</param>
    /// <param name="fileSystemRoot">文件监听的探针目录（默认用 <c>%TEMP%</c>）。</param>
    public static MonitorProbeResult Probe(MonitorEventKind kind, string? fileSystemRoot = null)
    {
        var sw = Stopwatch.StartNew();
        string? unavailable = null;
        string? note = null;
        string? detail = null;
        bool startable = false;
        bool stopped = false;

        if (!OperatingSystem.IsWindows())
        {
            unavailable = "非 Windows 平台";
        }
        else if (kind == MonitorEventKind.FileSystem)
        {
            fileSystemRoot ??= Path.GetTempPath();
            if (!Directory.Exists(fileSystemRoot)) unavailable = $"探针目录不存在：{fileSystemRoot}";
        }

        if (unavailable is null)
        {
            IMonitorSource? source = null;
            try
            {
                source = Create(kind, fileSystemRoot);
                source.Start();
                startable = source.IsRunning;
                note = source.LastError;
                detail = DescribeDetail(source);
            }
            catch (Exception ex)
            {
                note = $"{ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                try
                {
                    source?.Stop();
                    source?.Dispose();
                    stopped = source is null || !source.IsRunning;
                }
                catch (Exception ex)
                {
                    note ??= $"停止失败：{ex.Message}";
                }
            }
        }

        sw.Stop();
        return new MonitorProbeResult(
            kind.ToString(),
            Describe(kind),
            unavailable is null,
            unavailable,
            startable,
            sw.ElapsedMilliseconds,
            note,
            stopped,
            detail);
    }

    /// <summary>各机制在探针里的「专属证据」：能拿到什么就报什么，拿不到返回 <c>null</c>。</summary>
    private static string? DescribeDetail(IMonitorSource source) => source switch
    {
        DeviceMonitor d => $"设备接口通知注册={(d.InterfaceNotificationRegistered ? "成功" : "失败，退化为卷广播路径")}",
        PowerSessionMonitor p => $"SystemEvents.SessionSwitch 订阅={(p.SessionSwitchSubscribed ? "成功" : "失败")}",
        ProcessMonitor p => $"基线快照 {p.LastSnapshotSize} 个进程",
        FileSystemMonitor f => $"监视目录 {f.Roots.Count} 个",
        KeyboardMonitor k => $"累计事件 {k.PublishedCount} 条",
        MouseMonitor m => $"累计事件 {m.PublishedCount} 条",
        WindowSwitchMonitor w => $"累计事件 {w.PublishedCount} 条",
        ClipboardMonitor c => $"累计事件 {c.PublishedCount} 条",
        _ => null,
    };
}
