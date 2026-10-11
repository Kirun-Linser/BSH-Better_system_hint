using System.Runtime.InteropServices;

namespace BSH.Core.Monitoring;

/// <summary>
/// 隐藏消息窗口：一个<b>不可见的顶层窗口</b> + 专用消息循环线程，用来收「只能发给窗口」的系统通知。
/// <para>
/// ⚠️ 必须是顶层窗口，不能用 message-only 窗口（<c>HWND_MESSAGE</c>）：
/// <c>WM_POWERBROADCAST</c> 与 <c>WM_DEVICECHANGE</c> 都是<b>广播</b>给顶层窗口的，
/// message-only 窗口收不到广播。这里用 <c>WS_POPUP</c> 创建后<b>不调用 ShowWindow</b>，所以屏幕上不可见、也不进任务栏。
/// </para>
/// <para>
/// 设备 / 电源 / 剪贴板三个监听器<b>各持一个</b>窗口实例（而不是共用一个）：
/// 生命周期与监听器一一对应，某个窗口过程出问题不会连累其它监听器，销毁语义也更简单。
/// </para>
/// </summary>
internal sealed class HiddenMessageWindow : IDisposable
{
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOACTIVATE = 0x08000000;

    private static int _seq;

    private readonly MonitorMessageLoop _loop = new();
    private readonly string _className = $"BSH.Monitor.Wnd.{Environment.ProcessId}.{Interlocked.Increment(ref _seq)}";
    private WndProcDelegate? _wndProc;      // 必须保活：委托被 GC 回收后窗口过程指针就悬空了
    private IntPtr _hInstance;
    private volatile IntPtr _handle;
    private bool _classRegistered;

    /// <summary>窗口过程回调：返回 <c>true</c> 表示消息已处理（不再交给 <c>DefWindowProc</c>）。<b>在窗口线程上执行</b>。</summary>
    public Func<uint, IntPtr, IntPtr, bool>? OnMessage { get; set; }

    /// <summary>
    /// 窗口创建完成后、消息循环开始前，<b>在窗口线程上</b>执行的一步初始化。
    /// <para>电源监听用它在本线程订阅 <c>SystemEvents.SessionSwitch</c>：.NET 的实现要求订阅线程有消息泵，在别的线程订阅收不到事件。</para>
    /// <para>抛异常 = 启动失败（原因会记进 <see cref="LastError"/>）。</para>
    /// </summary>
    public Action? OnWindowReady { get; set; }

    /// <summary>窗口线程退出前执行的清理（与 <see cref="OnWindowReady"/> 对称）。</summary>
    public Action? OnTeardown { get; set; }

    /// <summary>窗口句柄；未启动时为 <see cref="IntPtr.Zero"/>。</summary>
    public IntPtr Handle => _handle;

    /// <summary>窗口是否已就绪。</summary>
    public bool IsRunning => _loop.IsRunning && _handle != IntPtr.Zero;

    /// <summary>启动 / 运行期失败原因。</summary>
    public string? LastError => _loop.LastError;

    /// <summary>创建窗口并开始泵消息。</summary>
    public bool Start()
    {
        if (_loop.IsRunning) return true;

        _wndProc = WndProc;

        return _loop.Start(Setup, Teardown);
    }

    /// <summary>销毁窗口并结束线程。</summary>
    public void Stop()
    {
        _loop.Stop();
        _handle = IntPtr.Zero;
    }

    /// <inheritdoc />
    public void Dispose() => Stop();

    /// <summary>在窗口线程上执行：注册窗口类 + 创建（不显示的）顶层窗口。</summary>
    private string? Setup()
    {
        _hInstance = GetModuleHandleW(null);

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc!),
            cbClsExtra = 0,
            cbWndExtra = 0,
            hInstance = _hInstance,
            hIcon = IntPtr.Zero,
            hCursor = IntPtr.Zero,
            hbrBackground = IntPtr.Zero,
            lpszMenuName = null,
            lpszClassName = _className,
            hIconSm = IntPtr.Zero,
        };

        if (RegisterClassExW(ref wc) == 0)
        {
            return $"RegisterClassEx 失败（Win32 {Marshal.GetLastWin32Error()}）";
        }
        _classRegistered = true;

        var hwnd = CreateWindowExW(
            WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            _className,
            "BSH Monitor",
            WS_POPUP,
            0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
        {
            return $"CreateWindowEx 失败（Win32 {Marshal.GetLastWin32Error()}）";
        }

        _handle = hwnd;

        // 线程内的一步附加初始化（如 SystemEvents 订阅）；抛异常即启动失败
        OnWindowReady?.Invoke();

        return null;
    }

    /// <summary>在窗口线程上执行：销毁窗口 + 注销窗口类 + 附加清理。绝不抛异常。</summary>
    private void Teardown()
    {
        try
        {
            OnTeardown?.Invoke();
        }
        catch
        {
            // 清理阶段不允许把异常抛回消息循环
        }

        try
        {
            if (_handle != IntPtr.Zero)
            {
                DestroyWindow(_handle);
                _handle = IntPtr.Zero;
            }

            if (_classRegistered)
            {
                UnregisterClassW(_className, _hInstance);
                _classRegistered = false;
            }
        }
        catch
        {
            // 清理阶段不允许把异常抛回消息循环
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            var handler = OnMessage;
            if (handler is not null && handler(msg, wParam, lParam)) return IntPtr.Zero;
        }
        catch
        {
            // 单个消息处理失败不得打断消息循环
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    // ── Win32 ────────────────────────────────────────────────────────────────

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
}
