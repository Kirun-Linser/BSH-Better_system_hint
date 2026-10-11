namespace BSH.Core.Monitoring;

/// <summary>
/// 监听的事件类别。每一类对应一个 <see cref="IMonitorSource"/> 实现，
/// 也对应规则表里 <see cref="MonitorRule.Kind"/> 的取值。
/// <para>
/// 枚举名字符串会原样写进 <c>profiles\personal\rules.json</c>（如 <c>"kind": "Keyboard"</c>），
/// 所以<b>不要随意改名</b>，否则用户已有的规则会解析失败。
/// </para>
/// </summary>
public enum MonitorEventKind
{
    /// <summary>键盘：低层全局钩子 <c>SetWindowsHookEx(WH_KEYBOARD_LL)</c>（含 SendInput 注入的按键）。</summary>
    Keyboard,

    /// <summary>鼠标：低层全局钩子 <c>SetWindowsHookEx(WH_MOUSE_LL)</c>（只报按键与滚轮，不报移动）。</summary>
    Mouse,

    /// <summary>前台窗口切换：<c>SetWinEventHook(EVENT_SYSTEM_FOREGROUND)</c>。</summary>
    WindowSwitch,

    /// <summary>进程启动 / 退出：轮询 <c>Process.GetProcesses()</c> 快照对比（不依赖 WMI）。</summary>
    ProcessStartStop,

    /// <summary>设备到达 / 移除：隐藏顶层窗口接收 <c>WM_DEVICECHANGE</c>。</summary>
    DeviceChange,

    /// <summary>电源与登录会话：<c>WM_POWERBROADCAST</c> + <c>SystemEvents.SessionSwitch</c>。</summary>
    PowerSession,

    /// <summary>剪贴板变化：<c>AddClipboardFormatListener</c> + 隐藏顶层窗口。</summary>
    Clipboard,

    /// <summary>文件系统变化：<c>FileSystemWatcher</c>。</summary>
    FileSystem,
}

/// <summary>
/// 一条监听规则：<i>什么条件下（<see cref="Kind"/> + <see cref="Match"/>）播什么（<see cref="Sound"/> / <see cref="SpeakKey"/>）</i>。
/// <para>
/// 规则表存在个人配置 <c>profiles\personal\rules.json</c>（见 <see cref="RuleStore"/>），
/// 该文件不入公开仓库。
/// </para>
/// </summary>
public sealed class MonitorRule
{
    /// <summary>规则唯一标识（用于冷却记账）；留空时由 <see cref="RuleStore.Load"/> 自动补 <c>rule-N</c>。</summary>
    public string Id { get; set; } = "";

    /// <summary>人读名字，仅用于展示与日志。</summary>
    public string Name { get; set; } = "";

    /// <summary>是否启用；<c>false</c> 的规则不参与匹配，也不会让引擎去装对应的监听器。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>这条规则关心的事件类别（决定用哪个监听器）。</summary>
    public MonitorEventKind Kind { get; set; }

    /// <summary>
    /// 条件表达式，<b>语义由各类监听器定义</b>（多个条件用 <c>,</c> / <c>;</c> / <c>|</c> 分隔，忽略大小写）：
    /// <list type="bullet">
    /// <item><description><see cref="MonitorEventKind.Keyboard"/>：虚拟键名，如 <c>CapsLock,VolumeUp</c>；<c>*</c> 或留空 = 任意键。</description></item>
    /// <item><description><see cref="MonitorEventKind.Mouse"/>：事件名，如 <c>LeftDown,MiddleUp,Wheel</c>。</description></item>
    /// <item><description><see cref="MonitorEventKind.WindowSwitch"/>：进程名（<c>notepad.exe</c>）或窗口标题关键词。</description></item>
    /// <item><description><see cref="MonitorEventKind.ProcessStartStop"/>：进程名，可加 <c>start </c> / <c>stop </c> 前缀限定方向，如 <c>start steam.exe</c>。</description></item>
    /// <item><description><see cref="MonitorEventKind.DeviceChange"/>：<c>Arrival</c> / <c>Remove</c>。</description></item>
    /// <item><description><see cref="MonitorEventKind.PowerSession"/>：<c>AC,DC,Lock,Unlock,Suspend,Resume</c>（另有 <c>BatteryLow</c> / <c>Logon</c> / <c>Logoff</c>）。</description></item>
    /// <item><description><see cref="MonitorEventKind.Clipboard"/>：<c>Text,Image,Files</c>。</description></item>
    /// <item><description><see cref="MonitorEventKind.FileSystem"/>：目录路径，如 <c>C:\Users\<用户名>\Downloads</c>。</description></item>
    /// </list>
    /// </summary>
    public string? Match { get; set; }

    /// <summary>要播放的 wav 绝对路径；为空则改用 <see cref="SpeakKey"/> 从语音方案里取。</summary>
    public string? Sound { get; set; }

    /// <summary>系统事件名（如 <c>DeviceConnect</c>），用于复用语音方案（<see cref="VoiceScheme"/>）里已有的 wav。</summary>
    public string? SpeakKey { get; set; }

    /// <summary>同一规则的最小触发间隔（毫秒），用于防抖；<c>0</c> = 不限制。</summary>
    public int CooldownMs { get; set; } = 800;
}
