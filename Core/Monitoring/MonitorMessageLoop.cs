using System.Runtime.InteropServices;

namespace BSH.Core.Monitoring;

/// <summary>
/// 专用消息循环线程。三处地方需要它，缺一不可：
/// <list type="number">
/// <item><description>低层键鼠钩子：<c>WH_KEYBOARD_LL</c> / <c>WH_MOUSE_LL</c> 的回调<b>只会在安装钩子的线程处理消息时被调用</b>，
/// 没有消息泵就一个事件都收不到（本轮自检的第一版就踩过这个坑）。</description></item>
/// <item><description>隐藏窗口（设备 / 电源 / 剪贴板）：窗口过程必须在有消息循环的线程上被 <c>DispatchMessage</c> 调用。</description></item>
/// <item><description><c>SystemEvents.SessionSwitch</c>：.NET 的实现依赖订阅线程有消息泵。</description></item>
/// </list>
/// <para>
/// 线程是后台线程（<c>IsBackground = true</c>），即使极端情况下没来得及退出也不会吊住进程；
/// 但正常路径一定会走完 <c>teardown</c>（卸载钩子 / 销毁窗口），这是「不泄漏钩子」的保证。
/// </para>
/// </summary>
internal sealed class MonitorMessageLoop : IDisposable
{
    private const uint WM_QUIT = 0x0012;
    private const uint PM_NOREMOVE = 0x0000;

    private readonly ManualResetEventSlim _ready = new(false);
    private Thread? _thread;
    private volatile uint _threadId;
    private volatile bool _running;
    private Func<string?>? _setup;
    private Action? _teardown;

    /// <summary>启动失败的原因（超时 / setup 返回的文本 / setup 抛异常）；成功时为 <c>null</c>。</summary>
    public string? LastError { get; private set; }

    /// <summary>消息循环线程是否在跑。</summary>
    public bool IsRunning => _running;

    /// <summary>
    /// 起线程并同步等待初始化完成。
    /// </summary>
    /// <param name="setup">
    /// 在线程内执行（已建好消息队列）；返回 <c>null</c> 表示成功，返回文本表示失败原因。
    /// </param>
    /// <param name="teardown">线程退出前执行（卸载钩子 / 销毁窗口）；不允许抛异常。</param>
    /// <returns>初始化是否成功。</returns>
    public bool Start(Func<string?> setup, Action? teardown = null)
    {
        if (_running) return true;

        _setup = setup;
        _teardown = teardown;
        LastError = null;
        _ready.Reset();

        var thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "BSH-MonitorLoop",
        };
        // STA：剪贴板（OLE）相关调用要求单线程单元
        thread.SetApartmentState(ApartmentState.STA);

        _thread = thread;
        thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
        {
            LastError = "消息循环线程初始化超时（5 秒）";
            Stop();                     // 超时也要把线程收回来，绝不留下孤儿线程
            return false;
        }

        if (LastError is not null)
        {
            Stop();                     // 初始化失败：线程已跑完 teardown 即将退出，这里把它 join 干净
            return false;
        }

        _running = true;
        return true;
    }

    /// <summary>结束消息循环并等待线程退出（teardown 一定跑完）。</summary>
    public void Stop()
    {
        var thread = _thread;
        if (thread is null) return;

        if (thread.IsAlive && _threadId != 0)
        {
            PostThreadMessageW(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        if (!thread.Join(TimeSpan.FromSeconds(3)))
        {
            // 不要覆盖已有的失败原因（初始化失败时的原因更有诊断价值）
            LastError ??= "消息循环线程未能在 3 秒内退出";
        }

        _thread = null;
        _setup = null;
        _teardown = null;
        _threadId = 0;
        _running = false;
    }

    /// <inheritdoc />
    public void Dispose() => Stop();

    private void Loop()
    {
        _threadId = GetCurrentThreadId();

        // 先碰一下消息队列：线程消息队列是「首次调用 User32 消息函数时」才创建的，
        // 不提前创建的话 PostThreadMessage(WM_QUIT) 会丢，Stop() 就只能干等超时。
        PeekMessageW(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);

        var ok = false;
        try
        {
            ok = _setup is null || _setup() is null;
        }
        catch (Exception ex)
        {
            LastError = Describe(ex);
        }

        if (!ok && LastError is null) LastError = "消息循环初始化失败";

        // ⚠️ 必须先放行 Start() 再进消息循环：GetMessage 会一直阻塞到 WM_QUIT，
        // 把 _ready.Set() 放在循环之后会让 Start() 每次都等满超时（2026-10-10 踩过）。
        _ready.Set();

        if (ok && LastError is null)
        {
            try
            {
                while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessageW(ref msg);
                }
            }
            catch (Exception ex)
            {
                LastError = Describe(ex);
            }
        }

        try
        {
            _teardown?.Invoke();
        }
        catch (Exception ex)
        {
            LastError ??= $"清理失败：{Describe(ex)}";
        }

        _threadId = 0;
    }

    /// <summary>把异常压成一行可读文本。</summary>
    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    // ── Win32 ────────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessageW(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
