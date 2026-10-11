using System.Runtime.InteropServices;

namespace BSH.Core.Monitoring;

/// <summary>
/// 设备到达 / 移除监听：隐藏顶层窗口接收 <c>WM_DEVICECHANGE</c>。
/// <para>
/// 两条路径同时用，取并集：
/// <list type="number">
/// <item><description>卷设备（U 盘、移动硬盘）的到达 / 移除会被系统<b>广播</b>给所有顶层窗口，无需注册 —— 这是主力路径。</description></item>
/// <item><description>再额外 <c>RegisterDeviceNotification</c> 注册一次设备接口通知，覆盖卷以外的热插拔；注册失败<b>不算致命</b>（只记提示），因为路径 1 仍然有效。</description></item>
/// </list>
/// </para>
/// <para>事件明细为 <c>Arrival</c> / <c>Remove</c>，能解析出盘符时带上（如 <c>Arrival D:\</c>）；<c>DBT_DEVNODES_CHANGED</c> 这类噪声一律丢弃。</para>
/// </summary>
public sealed class DeviceMonitor : MonitorSourceBase
{
    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const int DBT_DEVTYP_VOLUME = 0x00000002;
    private const int DBT_DEVTYP_DEVICEINTERFACE = 0x00000005;
    private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;

    private HiddenMessageWindow? _window;
    private IntPtr _notifyHandle;

    /// <summary>注册设备接口通知是否成功（失败不影响卷广播路径）。</summary>
    public bool InterfaceNotificationRegistered { get; private set; }

    /// <inheritdoc />
    public override MonitorEventKind Kind => MonitorEventKind.DeviceChange;

    /// <inheritdoc />
    public override string Mechanism => "隐藏顶层窗口接收 WM_DEVICECHANGE（+ RegisterDeviceNotification）";

    /// <inheritdoc />
    protected override void OnStart()
    {
        var window = new HiddenMessageWindow { OnMessage = HandleMessage };

        if (!window.Start())
        {
            throw new InvalidOperationException(window.LastError ?? "隐藏消息窗口创建失败");
        }

        _window = window;

        // 附加注册：只影响「卷以外」的设备接口通知，失败就退化成纯广播路径
        var filter = new DEV_BROADCAST_DEVICEINTERFACE
        {
            dbcc_size = Marshal.SizeOf<DEV_BROADCAST_DEVICEINTERFACE>(),
            dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE,
            dbcc_reserved = 0,
            dbcc_classguid = Guid.Empty,        // 空 GUID = 不限定接口类
            dbcc_name = '\0',
        };

        _notifyHandle = RegisterDeviceNotificationW(window.Handle, ref filter, DEVICE_NOTIFY_WINDOW_HANDLE);
        InterfaceNotificationRegistered = _notifyHandle != IntPtr.Zero;
        if (!InterfaceNotificationRegistered)
        {
            LastError = $"RegisterDeviceNotification 未成功（Win32 {Marshal.GetLastWin32Error()}）；卷设备的广播路径仍然有效";
        }
    }

    /// <inheritdoc />
    protected override void OnStop()
    {
        if (_notifyHandle != IntPtr.Zero)
        {
            UnregisterDeviceNotification(_notifyHandle);
            _notifyHandle = IntPtr.Zero;
        }

        _window?.Dispose();
        _window = null;
        InterfaceNotificationRegistered = false;
    }

    /// <summary>窗口过程（窗口线程）：只认到达 / 移除两种，其余（如 DBT_DEVNODES_CHANGED）直接放行给默认处理。</summary>
    private bool HandleMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != WM_DEVICECHANGE) return false;

        int kind = (int)wParam;
        if (kind != DBT_DEVICEARRIVAL && kind != DBT_DEVICEREMOVECOMPLETE) return false;

        string action = kind == DBT_DEVICEARRIVAL ? "Arrival" : "Remove";
        Publish(Describe(action, lParam));
        return true;
    }

    /// <summary>尽量从广播结构里取出盘符（卷设备才有；其它设备类型只报动作名）。</summary>
    private static string Describe(string action, IntPtr lParam)
    {
        if (lParam == IntPtr.Zero) return action;

        try
        {
            var header = Marshal.PtrToStructure<DEV_BROADCAST_HDR>(lParam);
            if (header.dbch_devicetype != DBT_DEVTYP_VOLUME) return action;

            var volume = Marshal.PtrToStructure<DEV_BROADCAST_VOLUME>(lParam);
            var drives = new List<string>();
            for (int i = 0; i < 26; i++)
            {
                if ((volume.dbcv_unitmask & (1u << i)) != 0) drives.Add($"{(char)('A' + i)}:");
            }

            return drives.Count > 0 ? $"{action} {string.Join(",", drives)}" : action;
        }
        catch
        {
            return action;
        }
    }

    // ── Win32 ────────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct DEV_BROADCAST_HDR
    {
        public int dbch_size;
        public int dbch_devicetype;
        public int dbch_reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEV_BROADCAST_VOLUME
    {
        public int dbcv_size;
        public int dbcv_devicetype;
        public int dbcv_reserved;
        public uint dbcv_unitmask;
        public short dbcv_flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEV_BROADCAST_DEVICEINTERFACE
    {
        public int dbcc_size;
        public int dbcc_devicetype;
        public int dbcc_reserved;
        public Guid dbcc_classguid;
        public char dbcc_name;      // 变长字段：只有首字符在结构体内
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr RegisterDeviceNotificationW(IntPtr hRecipient, ref DEV_BROADCAST_DEVICEINTERFACE notificationFilter, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterDeviceNotification(IntPtr handle);
}
