using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BSH.Core.Monitoring;

/// <summary>
/// 前台窗口切换监听：<c>SetWinEventHook(EVENT_SYSTEM_FOREGROUND, …, WINEVENT_OUTOFCONTEXT)</c>。
/// <para>
/// <c>WINEVENT_OUTOFCONTEXT</c> 的回调在<b>安装钩子的线程</b>上、由消息泵派发，
/// 所以同样跑在 <see cref="MonitorMessageLoop"/> 的专用线程上。
/// </para>
/// <para>
/// 事件明细格式为 <c>进程名|窗口标题</c>（如 <c>notepad.exe|无标题 - 记事本</c>），
/// 规则既可以写进程名，也可以写标题关键词（见 <see cref="MonitorMatcher"/>）。
/// </para>
/// </summary>
public sealed class WindowSwitchMonitor : MonitorSourceBase
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    private MonitorMessageLoop? _loop;
    private WinEventProc? _callback;
    private IntPtr _hook;

    /// <inheritdoc />
    public override MonitorEventKind Kind => MonitorEventKind.WindowSwitch;

    /// <inheritdoc />
    public override string Mechanism => "SetWinEventHook(EVENT_SYSTEM_FOREGROUND, WINEVENT_OUTOFCONTEXT)";

    /// <inheritdoc />
    protected override void OnStart()
    {
        _callback = WinEventCallback;
        var loop = new MonitorMessageLoop();

        if (!loop.Start(
                () =>
                {
                    _hook = SetWinEventHook(
                        EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                        IntPtr.Zero, _callback, 0, 0,
                        WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

                    if (_hook == IntPtr.Zero)
                    {
                        return $"SetWinEventHook 失败（Win32 {Marshal.GetLastWin32Error()}）";
                    }
                    return null;
                },
                () =>
                {
                    if (_hook != IntPtr.Zero)
                    {
                        UnhookWinEvent(_hook);
                        _hook = IntPtr.Zero;
                    }
                    _callback = null;
                }))
        {
            throw new InvalidOperationException(loop.LastError ?? "前台窗口钩子线程启动失败");
        }

        _loop = loop;
    }

    /// <inheritdoc />
    protected override void OnStop()
    {
        _loop?.Stop();
        _loop = null;
        _hook = IntPtr.Zero;
    }

    /// <summary>WinEvent 回调：收集「进程名 + 标题」后投递。</summary>
    private void WinEventCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (eventType != EVENT_SYSTEM_FOREGROUND || hwnd == IntPtr.Zero || idObject != 0) return;

        Publish(Describe(hwnd));
    }

    /// <summary>
    /// 取「进程名|窗口标题」。<c>GetWindowText</c> 对跨进程窗口读的是缓存文本，不会因为对方无响应而卡住。
    /// </summary>
    private static string Describe(IntPtr hwnd)
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
            // 进程可能刚好退出、或不允许查询：保留占位名，不影响事件上报
        }

        var buffer = new char[260];
        int len = GetWindowTextW(hwnd, buffer, buffer.Length);
        string title = len > 0 ? new string(buffer, 0, len) : "";

        return $"{exe}|{title}";
    }

    // ── Win32 ────────────────────────────────────────────────────────────────

    private delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(IntPtr hWnd, [Out] char[] lpString, int nMaxCount);
}
