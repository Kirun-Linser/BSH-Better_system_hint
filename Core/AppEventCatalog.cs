namespace BSH.Core;

/// <summary>
/// 系统声音事件的中文标签与分组。
/// 未收录的事件不报错，回退显示英文原名（见 <see cref="Label"/>）。
/// 事件清单不硬编码 —— 一律运行时从注册表枚举，这里只负责"人话翻译"。
/// </summary>
public static class AppEventCatalog
{
    private static readonly Dictionary<string, (string Label, string Group)> Map =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".Default"]                   = ("（默认应用通用事件）", "其它"),

            // ── 错误 / 警告 ──
            ["AppGPFault"]                 = ("程序崩溃（一般保护性错误）", "错误"),
            ["SystemHand"]                 = ("严重错误（停止）", "错误"),
            ["SystemExclamation"]          = ("警告（感叹号）", "错误"),
            ["SystemAsterisk"]             = ("信息（星号）", "错误"),
            ["SystemQuestion"]             = ("询问（问号）", "错误"),
            ["SystemExit"]                 = ("退出 Windows", "错误"),

            // ── 通知 ──
            ["SystemNotification"]         = ("系统通知", "通知"),
            ["Notification.Default"]       = ("通用通知", "通知"),
            ["Notification.IM"]            = ("即时消息", "通知"),
            ["Notification.Mail"]          = ("新邮件", "通知"),
            ["Notification.SMS"]           = ("短信", "通知"),
            ["Notification.Reminder"]      = ("日程提醒", "通知"),
            ["Notification.Proximity"]     = ("近场提示", "通知"),
            ["MessageNudge"]               = ("消息抖动提醒", "通知"),

            // ── 界面 ──
            ["Maximize"]                   = ("最大化窗口", "界面"),
            ["Minimize"]                   = ("最小化窗口", "界面"),
            ["RestoreUp"]                  = ("向上还原窗口", "界面"),
            ["RestoreDown"]                = ("向下还原窗口", "界面"),
            ["Open"]                       = ("打开", "界面"),
            ["Close"]                      = ("关闭", "界面"),
            ["MenuCommand"]                = ("执行菜单命令", "界面"),
            ["MenuPopup"]                  = ("弹出菜单", "界面"),
            ["CCSelect"]                   = ("选择控件", "界面"),
            ["ChangeTheme"]                = ("切换主题", "界面"),
            ["ShowBand"]                   = ("显示工具栏", "界面"),
            ["PrintComplete"]              = ("打印完成", "界面"),

            // ── 设备 / 电源 ──
            ["DeviceConnect"]              = ("设备已连接", "设备"),
            ["DeviceDisconnect"]           = ("设备已断开", "设备"),
            ["DeviceFail"]                 = ("设备连接失败", "设备"),
            ["ProximityConnection"]        = ("近场连接", "设备"),
            ["LowBatteryAlarm"]            = ("电池电量低", "电源"),
            ["CriticalBatteryAlarm"]       = ("电池电量严重不足", "电源"),

            // ── 通讯 / 登录 ──
            ["MailBeep"]                   = ("收到邮件（旧式）", "通讯"),
            ["FaxBeep"]                    = ("传真提示", "通讯"),
            ["WindowsLogon"]               = ("登录 Windows", "登录"),
            ["WindowsLogoff"]              = ("注销 Windows", "登录"),
            ["WindowsUnlock"]              = ("解锁 Windows", "登录"),
            ["WindowsUAC"]                 = ("用户账户控制（UAC）", "登录"),
        };

    /// <summary>把事件名翻成中文；未收录就原样返回英文名。</summary>
    public static string Label(string eventName) =>
        Map.TryGetValue(eventName, out var v) ? v.Label : eventName;

    /// <summary>事件分组（用于界面分组与筛选）。</summary>
    public static string Group(string eventName)
    {
        if (Map.TryGetValue(eventName, out var v)) return v.Group;

        // 循环类事件按规则识别：Notification.Looping.Alarm* / Call*
        if (eventName.StartsWith("Notification.Looping.Alarm", StringComparison.OrdinalIgnoreCase))
            return "循环铃声";
        if (eventName.StartsWith("Notification.Looping.Call", StringComparison.OrdinalIgnoreCase))
            return "循环来电";
        return "其它";
    }

    /// <summary>该事件是否"适合作为循环长音"（循环类事件不该被短提示音占用）。</summary>
    public static bool IsLooping(string eventName) =>
        eventName.StartsWith("Notification.Looping.", StringComparison.OrdinalIgnoreCase);

    /// <summary>排序用的权重 —— 让界面里的顺序符合直觉（错误在前、铃声在后）。</summary>
    public static int SortWeight(string eventName) => Group(eventName) switch
    {
        "错误" => 10,
        "通知" => 20,
        "界面" => 30,
        "设备" => 40,
        "电源" => 50,
        "通讯" => 60,
        "登录" => 70,
        "循环铃声" => 80,
        "循环来电" => 90,
        _ => 100,
    };
}
