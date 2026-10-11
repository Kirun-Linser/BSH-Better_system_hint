using System.Runtime.InteropServices;

namespace BSH.Core.Monitoring;

/// <summary>
/// 键盘监听：低层全局钩子 <c>SetWindowsHookEx(WH_KEYBOARD_LL)</c>。
/// <para>
/// 三条硬约束（都体现在实现里）：
/// <list type="number">
/// <item><description>钩子只能装在<b>有消息循环</b>的线程上，否则回调永远不被调用 → 用 <see cref="MonitorMessageLoop"/> 起专用线程。</description></item>
/// <item><description>回调必须<b>极快返回</b>（系统有 <c>LowLevelHooksTimeout</c>，超时会被摘钩子）→ 回调里只解析键码 + <see cref="MonitorSourceBase.Publish"/>。</description></item>
/// <item><description>委托实例必须保活，且 <c>Stop</c>/<c>Dispose</c> 必须 <c>UnhookWindowsHookEx</c> → 卸载放在消息循环线程的 teardown 里，<c>Stop()</c> 会等它跑完。</description></item>
/// </list>
/// </para>
/// <para>只报按下（<c>WM_KEYDOWN</c> / <c>WM_SYSKEYDOWN</c>），不报抬起，否则一次按键会出两条事件。</para>
/// </summary>
public sealed class KeyboardMonitor : MonitorSourceBase
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private MonitorMessageLoop? _loop;
    private LowLevelKeyboardProc? _callback;
    private IntPtr _hook;

    /// <inheritdoc />
    public override MonitorEventKind Kind => MonitorEventKind.Keyboard;

    /// <inheritdoc />
    public override string Mechanism => "低层全局键盘钩子 SetWindowsHookEx(WH_KEYBOARD_LL)";

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
            throw new InvalidOperationException(loop.LastError ?? "键盘钩子线程启动失败");
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

    /// <summary>钩子回调（跑在钩子线程上）：只取键码、只投递。</summary>
    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN))
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            Publish(VirtualKeyNames.Name((int)info.vkCode));
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>
    /// 安装钩子。低层钩子的 <c>hMod</c> 在不同宿主下要求不一：
    /// 先用 <c>GetModuleHandle(NULL)</c>，失败再退到本模块句柄，两条都失败才报错（把两次 Win32 错误码都带上）。
    /// </summary>
    private static IntPtr InstallHook(LowLevelKeyboardProc callback, out string? reason)
    {
        reason = null;

        var hook = SetWindowsHookExW(WH_KEYBOARD_LL, callback, GetModuleHandleW(null), 0);
        if (hook != IntPtr.Zero) return hook;

        int first = Marshal.GetLastWin32Error();
        hook = SetWindowsHookExW(WH_KEYBOARD_LL, callback, Marshal.GetHINSTANCE(typeof(KeyboardMonitor).Module), 0);
        if (hook != IntPtr.Zero) return hook;

        int second = Marshal.GetLastWin32Error();
        reason = $"SetWindowsHookEx(WH_KEYBOARD_LL) 失败：主模块句柄路径 Win32={first}，当前模块句柄路径 Win32={second}";
        return IntPtr.Zero;
    }

    // ── Win32 ────────────────────────────────────────────────────────────────

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
}

/// <summary>
/// 虚拟键码 ↔ 键名的对照表（规则里写的键名就是这里的名字，大小写不敏感）。
/// <para>覆盖：字母、数字、<c>F1</c>–<c>F24</c>、编辑键、修饰键、小键盘、媒体键与浏览器键。表外的键报成 <c>VK_0x5B</c> 形式。</para>
/// </summary>
public static class VirtualKeyNames
{
    private static readonly Dictionary<int, string> ByCode = new()
    {
        [0x08] = "Back", [0x09] = "Tab", [0x0D] = "Enter", [0x13] = "Pause", [0x14] = "CapsLock",
        [0x1B] = "Esc", [0x20] = "Space", [0x21] = "PageUp", [0x22] = "PageDown", [0x23] = "End",
        [0x24] = "Home", [0x25] = "Left", [0x26] = "Up", [0x27] = "Right", [0x28] = "Down",
        [0x2C] = "PrintScreen", [0x2D] = "Insert", [0x2E] = "Delete",
        [0x5B] = "LWin", [0x5C] = "RWin", [0x5D] = "Apps",
        [0x60] = "Num0", [0x61] = "Num1", [0x62] = "Num2", [0x63] = "Num3", [0x64] = "Num4",
        [0x65] = "Num5", [0x66] = "Num6", [0x67] = "Num7", [0x68] = "Num8", [0x69] = "Num9",
        [0x6A] = "Multiply", [0x6B] = "Add", [0x6D] = "Subtract", [0x6E] = "Decimal", [0x6F] = "Divide",
        [0x90] = "NumLock", [0x91] = "ScrollLock",
        [0xA0] = "LShift", [0xA1] = "RShift", [0xA2] = "LCtrl", [0xA3] = "RCtrl", [0xA4] = "LAlt", [0xA5] = "RAlt",
        [0xAD] = "VolumeMute", [0xAE] = "VolumeDown", [0xAF] = "VolumeUp",
        [0xB0] = "MediaNextTrack", [0xB1] = "MediaPrevTrack", [0xB2] = "MediaStop", [0xB3] = "MediaPlayPause",
        [0xA6] = "BrowserBack", [0xA7] = "BrowserForward", [0xA8] = "BrowserRefresh", [0xA9] = "BrowserStop",
        [0xAA] = "BrowserSearch", [0xAB] = "BrowserFavorites", [0xAC] = "BrowserHome",
        [0xBA] = "Oem1", [0xBB] = "OemPlus", [0xBC] = "OemComma", [0xBD] = "OemMinus", [0xBE] = "OemPeriod",
        [0xBF] = "Oem2", [0xC0] = "Oem3", [0xDB] = "Oem4", [0xDC] = "Oem5", [0xDD] = "Oem6", [0xDE] = "Oem7",
    };

    // 低层钩子给的是「通用」修饰键码（不区分左右），规则里两种写法都收
    private static readonly Dictionary<string, int> ByName;

    /// <summary>静态构造：先补齐 F1–F24（0x70–0x87），再生成反查表。</summary>
    static VirtualKeyNames()
    {
        for (int i = 0; i < 24; i++) ByCode[0x70 + i] = $"F{i + 1}";
        ByName = BuildNames();
    }

    /// <summary>把虚拟键码转成键名；表外的键返回 <c>VK_0x5B</c> 形式。</summary>
    public static string Name(int vkCode) =>
        ByCode.TryGetValue(vkCode, out var name) ? name : $"VK_0x{vkCode:X2}";

    /// <summary>把键名解析回虚拟键码（自检里用它决定发哪个键）。</summary>
    public static bool TryParse(string? name, out int vkCode)
    {
        vkCode = 0;
        if (string.IsNullOrWhiteSpace(name)) return false;
        return ByName.TryGetValue(name.Trim(), out vkCode);
    }

    private static Dictionary<string, int> BuildNames()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int vk = 0x30; vk <= 0x39; vk++) map[((char)vk).ToString()] = vk;   // 0-9
        for (int vk = 0x41; vk <= 0x5A; vk++) map[((char)vk).ToString()] = vk;   // A-Z
        for (int i = 0; i < 24; i++) map[$"F{i + 1}"] = 0x70 + i;                 // F1-F24

        foreach (var (code, name) in ByCode) map[name] = code;

        // 常用别名
        map["Escape"] = 0x1B;
        map["Return"] = 0x0D;
        map["Shift"] = 0x10;
        map["Ctrl"] = 0x11;
        map["Control"] = 0x11;
        map["Alt"] = 0x12;
        map["Menu"] = 0x12;
        map["Del"] = 0x2E;
        map["Ins"] = 0x2D;
        map["PageDownKey"] = 0x22;

        return map;
    }
}
