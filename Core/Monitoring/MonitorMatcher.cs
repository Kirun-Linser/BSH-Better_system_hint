namespace BSH.Core.Monitoring;

/// <summary>
/// 规则匹配：<b>唯一</b>的「事件明细 ↔ 规则 Match」判定入口，引擎与自检都走这里，避免两套语义。
/// <para>Match 里的多个条件用 <c>,</c> / <c>;</c> / <c>|</c> 分隔，忽略大小写；Match 为空或 <c>*</c> 表示「全部命中」。</para>
/// <para>
/// 注意这里<b>不做子串模糊匹配</b>（除了窗口标题与文件路径两处有意为之的场景）：
/// 键名 / 设备 / 电源 / 剪贴板都是「枚举名」语义，写成子串匹配会让 <c>Match="A"</c> 误命中 <c>CapsLock</c>。
/// </para>
/// </summary>
public static class MonitorMatcher
{
    /// <summary>判断一个事件明细是否满足某条规则的条件。</summary>
    public static bool IsMatch(MonitorRule rule, string detail) => IsMatch(rule.Kind, rule.Match, detail);

    /// <summary>判断一个事件明细是否满足指定类别与条件。</summary>
    public static bool IsMatch(MonitorEventKind kind, string? match, string detail)
    {
        var items = Split(match);
        if (items.Length == 0) return true;                 // 未写条件 = 全部命中

        foreach (var item in items)
        {
            if (item == "*") return true;
            if (MatchItem(kind, item, detail)) return true;
        }
        return false;
    }

    /// <summary>把 Match 拆成条件项（去空白、丢空项）。</summary>
    public static string[] Split(string? match)
    {
        if (string.IsNullOrWhiteSpace(match)) return [];

        var parts = match.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return [.. parts.Where(p => p.Length > 0)];
    }

    private static bool MatchItem(MonitorEventKind kind, string item, string detail) => kind switch
    {
        // 键名语义：整词相等，另允许用通用名（Shift / Ctrl / Alt / Win）命中左右键（LShift / RShift …）
        MonitorEventKind.Keyboard => MatchKeyName(item, detail),

        // 枚举名语义：Detail 形如 "Arrival D:\"，条件 "Arrival" 要能命中第一段
        MonitorEventKind.Mouse or MonitorEventKind.DeviceChange
            or MonitorEventKind.PowerSession or MonitorEventKind.Clipboard
            => EqualsToken(detail, item),

        // 进程语义：条件可带 start/stop 前缀限定方向
        MonitorEventKind.ProcessStartStop => MatchProcess(item, detail),

        // 目录语义：Detail 是完整文件路径，条件是它所在的目录
        MonitorEventKind.FileSystem => MatchPath(item, detail),

        // 窗口语义：Detail 形如 "进程名|窗口标题"；条件命中进程名，或作为关键词出现在标题里
        MonitorEventKind.WindowSwitch => MatchWindow(item, detail),

        _ => detail.Contains(item, StringComparison.OrdinalIgnoreCase),
    };

    /// <summary>整词相等，或「以该词开头 + 一个空格」（Detail 带附加信息时仍能被短条件命中）。</summary>
    private static bool EqualsToken(string detail, string item) =>
        detail.Equals(item, StringComparison.OrdinalIgnoreCase)
        || detail.StartsWith(item + " ", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 键名匹配。低层钩子对修饰键报的是左右具体码（<c>LShift</c> / <c>RCtrl</c>），
    /// 这里额外允许用通用名（<c>Shift</c> / <c>Ctrl</c> / <c>Alt</c>）命中其中之一。
    /// </summary>
    private static bool MatchKeyName(string item, string detail)
    {
        if (EqualsToken(detail, item)) return true;

        return detail.Length > 1
            && (detail[0] == 'L' || detail[0] == 'R')
            && detail[1..].Equals(item, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchProcess(string item, string detail)
    {
        // detail 固定为 "start 名字" / "stop 名字"
        int sp = detail.IndexOf(' ');
        string action = sp > 0 ? detail[..sp] : "";
        string name = sp > 0 ? detail[(sp + 1)..] : detail;

        if (item.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;   // 不限定方向

        if (item.StartsWith("start ", StringComparison.OrdinalIgnoreCase))
            return action.Equals("start", StringComparison.OrdinalIgnoreCase)
                && item[6..].Trim().Equals(name, StringComparison.OrdinalIgnoreCase);

        if (item.StartsWith("stop ", StringComparison.OrdinalIgnoreCase))
            return action.Equals("stop", StringComparison.OrdinalIgnoreCase)
                && item[5..].Trim().Equals(name, StringComparison.OrdinalIgnoreCase);

        return false;
    }

    private static bool MatchPath(string item, string detail)
    {
        var dir = item.TrimEnd('\\', '/');
        if (dir.Length == 0) return false;

        // 文件监听的事件明细前面带变更类型（形如 "Created C:\dir\a.txt"，见 FileSystemMonitor），
        // 而规则写的是**目录** —— 所以先剥掉那个可选前缀再比。
        // ⚠️ 2026-10-10 实测教训：不剥前缀时 MatchPath 永远不成立（detail 以 "Created " 开头，
        //    不可能 StartsWith 目录），表现为"文件规则一条都不触发"：自检里 4 条文件事件
        //    全部落空、引擎规则命中只有键盘那 3 次（triggered=3）。
        var path = StripAction(detail);

        // 命中的是「该目录下的文件」（用分隔符挡住 C:\Data 误命中 C:\Database 这类前缀重合）
        return path.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase)
            || path.Equals(dir, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>文件明细的变更类型前缀（<see cref="FileSystemMonitor"/> 投递的四种）。</summary>
    private static readonly string[] FileActions = ["Created", "Changed", "Deleted", "Renamed"];

    /// <summary>
    /// 剥掉文件明细开头的变更类型；没有前缀（明细本身就是路径）时原样返回。
    /// </summary>
    private static string StripAction(string detail)
    {
        int sp = detail.IndexOf(' ');
        if (sp <= 0) return detail;

        var head = detail[..sp];
        foreach (var action in FileActions)
        {
            if (head.Equals(action, StringComparison.OrdinalIgnoreCase)) return detail[(sp + 1)..];
        }
        return detail;
    }

    private static bool MatchWindow(string item, string detail)
    {
        int bar = detail.IndexOf('|');
        string exe = bar >= 0 ? detail[..bar] : detail;
        string title = bar >= 0 ? detail[(bar + 1)..] : "";

        return exe.Equals(item, StringComparison.OrdinalIgnoreCase)
            || (title.Length > 0 && title.Contains(item, StringComparison.OrdinalIgnoreCase));
    }
}
