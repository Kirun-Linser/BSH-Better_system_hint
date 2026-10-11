using System.Runtime.InteropServices;

namespace BSH.Core.Monitoring;

/// <summary>
/// 鼠标监听：低层全局钩子 <c>SetWindowsHookEx(WH_MOUSE_LL)</c>。
/// <para>
/// 只报「按键按下 / 抬起」与「滚轮」，<b>刻意不报移动</b>（<c>WM_MOUSEMOVE</c>）——
/// 移动事件的频率是每秒数百条，既没有规则价值，也会把钩子线程拖到超时被摘钩子。
/// </para>
/// <para>结构与 <see cref="KeyboardMonitor"/> 完全一致：专用消息循环线程 + 回调只投递 + teardown 里 <c>UnhookWindowsHookEx</c>。</para>
/// </summary>
public sealed class MouseMonitor : MonitorSourceBase
{
    private const int WH_MOUSE_LL = 14;

    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_MBUTTONUP = 0x0208;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_XBUTTONDOWN = 0x020B;
    private const int WM_XBUTTONUP = 0x020C;
    private const int WM_MOUSEHWHEEL = 0x020E;

    private MonitorMessageLoop? _loop;
    private LowLevelMouseProc? _callback;
    private IntPtr _hook;

    /// <inheritdoc />
    public override MonitorEventKind Kind => MonitorEventKind.Mouse;

    /// <inheritdoc />
    public override string Mechanism => "低层全局鼠标钩子 SetWindowsHookEx(WH_MOUSE_LL)";

    /// <inheritdoc />
    protected override void OnStart()
    {
        _callback = HookCallback;
        var loop = new MonitorMessageLoop();

        if (!loop.Start(
                () =>
                {
                    _hook = InstallHook(_callback, out var reason);
                    return _hook == IntPtr.Zero ? reason : null;
                },
                () =>
                {
                    if (_hook != IntPtr.Zero)
                    {
                        UnhookWindowsHookEx(_hook);
                        _hook = IntPtr.Zero;
                    }
                    _callback = null;
                }))
        {
            throw new InvalidOperationException(loop.LastError ?? "鼠标钩子线程启动失败");
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

    /// <summary>钩子回调（跑在钩子线程上）：只判消息号、只投递。</summary>
    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            switch (msg)
            {
                case WM_LBUTTONDOWN: Publish("LeftDown"); break;
                case WM_LBUTTONUP: Publish("LeftUp"); break;
                case WM_RBUTTONDOWN: Publish("RightDown"); break;
                case WM_RBUTTONUP: Publish("RightUp"); break;
                case WM_MBUTTONDOWN: Publish("MiddleDown"); break;
                case WM_MBUTTONUP: Publish("MiddleUp"); break;
                case WM_XBUTTONDOWN:
                case WM_XBUTTONUP:
                    {
                        var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                        // mouseData 高 16 位是 XBUTTON1 / XBUTTON2
                        int button = (int)(info.mouseData >> 16);
                        Publish($"{(msg == WM_XBUTTONDOWN ? "XButtonDown" : "XButtonUp")}{button}");
                        break;
                    }
                case WM_MOUSEWHEEL:
                    {
                        var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                        short delta = unchecked((short)(info.mouseData >> 16));
                        Publish(delta > 0 ? "WheelUp" : "WheelDown");
                        break;
                    }
                case WM_MOUSEHWHEEL:
                    {
                        var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                        short delta = unchecked((short)(info.mouseData >> 16));
                        Publish(delta > 0 ? "WheelRight" : "WheelLeft");
                        break;
                    }
            }
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>安装钩子（与键盘监听同策略：先主模块句柄，再退到当前模块句柄）。</summary>
    private static IntPtr InstallHook(LowLevelMouseProc callback, out string? reason)
    {
        reason = null;

        var hook = SetWindowsHookExW(WH_MOUSE_LL, callback, GetModuleHandleW(null), 0);
        if (hook != IntPtr.Zero) return hook;

        int first = Marshal.GetLastWin32Error();
        hook = SetWindowsHookExW(WH_MOUSE_LL, callback, Marshal.GetHINSTANCE(typeof(MouseMonitor).Module), 0);
        if (hook != IntPtr.Zero) return hook;

        int second = Marshal.GetLastWin32Error();
        reason = $"SetWindowsHookEx(WH_MOUSE_LL) 失败：主模块句柄路径 Win32={first}，当前模块句柄路径 Win32={second}";
        return IntPtr.Zero;
    }

    // ── Win32 ────────────────────────────────────────────────────────────────

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
}
