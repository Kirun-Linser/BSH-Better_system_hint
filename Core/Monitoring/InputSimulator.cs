using System.Runtime.InteropServices;

namespace BSH.Core.Monitoring;

/// <summary>
/// 输入模拟（<c>SendInput</c>）：自检时用它「自己按几下键」，无需真实用户操作。
/// <para>
/// 注入的按键会走完整的系统输入链，因此低层键盘钩子（<see cref="KeyboardMonitor"/>）能看到它们 ——
/// 这正是自检能证明「钩子真的在工作」的原理。
/// </para>
/// <para>
/// ⚠️ 注入的按键会同时被<b>当前前台窗口</b>收到。自检默认用 <c>F24</c>（多数键盘上没有、绝大多数程序不响应）来避免打扰用户。
/// </para>
/// </summary>
public static class InputSimulator
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

    /// <summary>按下（不抬起）。返回是否注入成功。</summary>
    public static bool KeyDown(ushort virtualKey) => Send(virtualKey, false);

    /// <summary>抬起。</summary>
    public static bool KeyUp(ushort virtualKey) => Send(virtualKey, true);

    /// <summary>按指定虚拟键码完成一次「按下 → 抬起」。</summary>
    /// <param name="virtualKey">虚拟键码。</param>
    /// <param name="holdMs">按下与抬起之间的间隔（毫秒）。</param>
    public static bool KeyPress(ushort virtualKey, int holdMs = 30)
    {
        bool ok = KeyDown(virtualKey);
        if (holdMs > 0) Thread.Sleep(holdMs);
        return KeyUp(virtualKey) && ok;
    }

    /// <summary>按名字完成一次「按下 → 抬起」（名字取 <see cref="VirtualKeyNames"/> 表，如 <c>F24</c> / <c>A</c>）。</summary>
    public static bool KeyPress(string keyName, int holdMs = 30) =>
        VirtualKeyNames.TryParse(keyName, out int vk) && KeyPress((ushort)vk, holdMs);

    /// <summary>最近一次 <c>SendInput</c> 的 Win32 错误码（成功时为 0），用于诊断「注入被拒」。</summary>
    public static int LastErrorCode { get; private set; }

    /// <summary><c>INPUT</c> 结构体在本进程位数下的字节数（x64 应为 40）；大小不对会让 <c>SendInput</c> 直接报参数错误。</summary>
    public static int InputStructSize => Marshal.SizeOf<INPUT>();

    private static bool Send(ushort virtualKey, bool keyUp)
    {
        // 方向键 / 小键盘之外的功能键（F13-F24 等）属于扩展键，带上标志更接近真实键盘行为
        uint flags = keyUp ? KEYEVENTF_KEYUP : 0;
        if (IsExtended(virtualKey)) flags |= KEYEVENTF_EXTENDEDKEY;

        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = virtualKey,
                    wScan = 0,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                },
            },
        };

        uint sent = SendInput(1, [input], Marshal.SizeOf<INPUT>());
        LastErrorCode = sent == 1 ? 0 : Marshal.GetLastWin32Error();
        return sent == 1;
    }

    /// <summary>扩展键判断（与 <c>MapVirtualKey</c> 的扩展位一致的最小集合）。</summary>
    private static bool IsExtended(ushort vk) => vk switch
    {
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 => true,  // PageUp/Down/End/Home/方向键
        0x2D or 0x2E => true,                                                   // Insert / Delete
        0x5B or 0x5C => true,                                                   // Win 键
        0x6F => true,                                                           // 小键盘 /
        0x90 => true,                                                           // NumLock
        0xA3 or 0xA5 => true,                                                   // 右 Ctrl / 右 Alt
        _ => false,
    };

    // ── Win32 ────────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
