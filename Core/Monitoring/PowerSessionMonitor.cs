using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BSH.Core.Monitoring;

/// <summary>
/// 电源与登录会话监听：<c>WM_POWERBROADCAST</c>（接市电 / 电池、挂起 / 恢复、电量低）+ <c>SystemEvents.SessionSwitch</c>（锁屏 / 解锁 / 登录 / 注销）。
/// <para>
/// 两条路径都在<b>同一个隐藏窗口线程</b>上：窗口收广播消息；<c>SessionSwitch</c> 也在该线程订阅，
/// 因为 .NET 的 SystemEvents 需要订阅线程有消息泵（见 <see cref="HiddenMessageWindow.OnWindowReady"/>）。
/// </para>
/// <para>事件明细：<c>AC</c> / <c>DC</c> / <c>Suspend</c> / <c>Resume</c> / <c>BatteryLow</c> / <c>Lock</c> / <c>Unlock</c> / <c>Logon</c> / <c>Logoff</c>。</para>
/// </summary>
public sealed class PowerSessionMonitor : MonitorSourceBase
{
    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_APMPOWERSTATUSCHANGE = 0x000A;
    private const int PBT_APMSUSPEND = 0x0004;
    private const int PBT_APMRESUMESUSPEND = 0x0007;
    private const int PBT_APMRESUMEAUTOMATIC = 0x0012;
    private const int PBT_APMBATTERYLOW = 0x0009;

    private const byte AC_LINE_OFFLINE = 0x00;
    private const byte AC_LINE_ONLINE = 0x01;

    private HiddenMessageWindow? _window;
    private bool _sessionSubscribed;

    /// <summary>会话事件（<c>SystemEvents.SessionSwitch</c>）是否订阅成功。</summary>
    public bool SessionSwitchSubscribed => _sessionSubscribed;

    /// <inheritdoc />
    public override MonitorEventKind Kind => MonitorEventKind.PowerSession;

    /// <inheritdoc />
    public override string Mechanism => "WM_POWERBROADCAST + SystemEvents.SessionSwitch";

    /// <inheritdoc />
    protected override void OnStart()
    {
        var window = new HiddenMessageWindow
        {
            OnMessage = HandleMessage,
            OnWindowReady = SubscribeSession,
            OnTeardown = UnsubscribeSession,
        };

        if (!window.Start())
        {
            throw new InvalidOperationException(window.LastError ?? "隐藏消息窗口创建失败");
        }

        _window = window;
    }

    /// <inheritdoc />
    protected override void OnStop()
    {
        _window?.Dispose();
        _window = null;
    }

    /// <summary>订阅会话切换（必须在窗口线程上做）。</summary>
    private void SubscribeSession()
    {
        SystemEvents.SessionSwitch += OnSessionSwitch;
        _sessionSubscribed = true;
    }

    /// <summary>退订（窗口线程退出前；可重复调用）。</summary>
    private void UnsubscribeSession()
    {
        if (!_sessionSubscribed) return;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _sessionSubscribed = false;
    }

    /// <summary>会话切换回调：映射成短事件名后投递。</summary>
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        var name = e.Reason switch
        {
            SessionSwitchReason.SessionLock => "Lock",
            SessionSwitchReason.SessionUnlock => "Unlock",
            SessionSwitchReason.SessionLogon => "Logon",
            SessionSwitchReason.SessionLogoff => "Logoff",
            SessionSwitchReason.ConsoleConnect => "ConsoleConnect",
            SessionSwitchReason.ConsoleDisconnect => "ConsoleDisconnect",
            SessionSwitchReason.RemoteConnect => "RemoteConnect",
            SessionSwitchReason.RemoteDisconnect => "RemoteDisconnect",
            _ => e.Reason.ToString(),
        };

        Publish(name);
    }

    /// <summary>窗口过程（窗口线程）：只认电源广播。</summary>
    private bool HandleMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != WM_POWERBROADCAST) return false;

        switch ((int)wParam)
        {
            case PBT_APMPOWERSTATUSCHANGE:
                Publish(ReadPowerLine());
                return true;

            case PBT_APMSUSPEND:
                Publish("Suspend");
                return true;

            case PBT_APMRESUMESUSPEND:
            case PBT_APMRESUMEAUTOMATIC:
                Publish("Resume");
                return true;

            case PBT_APMBATTERYLOW:
                Publish("BatteryLow");
                return true;

            default:
                // PBT_APMQUERYSUSPEND 等需要窗口返回 TRUE/FALSE 的消息：交回 DefWindowProc，绝不吞掉
                return false;
        }
    }

    /// <summary>读当前供电状态：接市电 <c>AC</c> / 用电池 <c>DC</c>。</summary>
    private static string ReadPowerLine()
    {
        if (!GetSystemPowerStatus(out var status)) return "PowerStatus";

        return status.ACLineStatus switch
        {
            AC_LINE_ONLINE => "AC",
            AC_LINE_OFFLINE => "DC",
            _ => "PowerStatus",
        };
    }

    // ── Win32 ────────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);
}
