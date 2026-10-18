namespace BSH.Core;

/// <summary>一条重排建议：把 <see cref="Event"/> 换成 <see cref="NewWav"/>，并说明理由。</summary>
public sealed record ReorderItem(string Event, string NewWav, string Reason);

/// <summary>
/// 「一键重排」的规则层 —— 本期只用 C:\Windows\Media 自带素材，不引入外部音频。
/// 两件事：① 给空槽补音 ② 拆开多个事件共用同一个音的撞车。
/// 映射表是纯数据，改这里即可调整方案。
/// </summary>
public static class ReorderPlan
{
    /// <summary>16 个空槽 → 语义最贴合的 Windows 自带音效（文件名都经实测存在）。</summary>
    private static readonly Dictionary<string, string> FillEmpty = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AppGPFault"]      = "Windows Error.wav",
        ["SystemHand"]      = "Windows Critical Stop.wav",
        ["SystemQuestion"]  = "Windows Ding.wav",
        ["SystemExit"]      = "Windows Shutdown.wav",
        ["WindowsLogoff"]   = "Windows Logoff Sound.wav",
        ["Maximize"]        = "Windows Restore.wav",
        ["Minimize"]        = "Windows Minimize.wav",
        ["RestoreUp"]       = "chord.wav",
        ["RestoreDown"]     = "ding.wav",
        ["Open"]            = "Windows Navigation Start.wav",
        ["Close"]           = "Windows Recycle.wav",
        ["MenuCommand"]     = "Windows Menu Command.wav",
        ["MenuPopup"]       = "Windows Information Bar.wav",
        ["CCSelect"]        = "Windows Pop-up Blocked.wav",
        ["ChangeTheme"]     = "chimes.wav",
        ["ShowBand"]        = "recycle.wav",
        ["PrintComplete"]   = "Windows Print complete.wav",
    };

    /// <summary>撞车拆解：同一 wav 被多个事件占用时，只留"语义最贴合"的那个，其余改到这些音上。</summary>
    private static readonly Dictionary<string, string> SplitCollision = new(StringComparer.OrdinalIgnoreCase)
    {
        // Windows Background.wav ×5 → 只留 .Default
        ["SystemAsterisk"]     = "Windows Notify.wav",
        ["SystemExclamation"]  = "Windows Exclamation.wav",
        ["SystemNotification"] = "Windows Balloon.wav",
        ["LowBatteryAlarm"]    = "Windows Battery Low.wav",
        // Windows Notify Email.wav ×3 → 只留 Notification.Mail
        ["MailBeep"]           = "notify.wav",
        ["FaxBeep"]            = "Windows Ringin.wav",
        // Windows Foreground.wav ×2 → 只留 SystemHand（已在上面改成 Critical Stop，故此组全部改写）
        ["CriticalBatteryAlarm"] = "Windows Battery Critical.wav",
        // Windows Notify Messaging.wav ×2 → 只留 Notification.IM
        ["Notification.SMS"]   = "Windows Feed Discovered.wav",
    };

    /// <summary>
    /// 基于当前状态生成重排建议（不写注册表）。
    /// 已手工改过、且不空、又不撞车的事件<b>不会被触碰</b>。
    /// </summary>
    public static List<ReorderItem> Build(IReadOnlyList<EventSound> current)
    {
        var plan = new List<ReorderItem>();
        var used = current
            .Where(s => !s.IsEmpty)
            .GroupBy(s => Path.GetFileName(s.WavPath!)!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Event).ToList(), StringComparer.OrdinalIgnoreCase);

        // ① 空槽补音
        foreach (var s in current.Where(x => x.IsEmpty))
            if (FillEmpty.TryGetValue(s.Event, out var wav))
                plan.Add(new ReorderItem(s.Event, wav, "空槽补音"));

        // ② 拆撞车
        foreach (var s in current.Where(x => !x.IsEmpty))
        {
            var file = Path.GetFileName(s.WavPath!)!;
            if (!used.TryGetValue(file, out var owners) || owners.Count <= 1) continue;
            if (!SplitCollision.TryGetValue(s.Event, out var wav)) continue;

            // 同一事件若已同时在"补空槽"里排过队，就不重复处理
            if (plan.Any(p => string.Equals(p.Event, s.Event, StringComparison.OrdinalIgnoreCase))) continue;
            plan.Add(new ReorderItem(s.Event, wav, $"拆分撞车（{file} 被 {owners.Count} 个事件共用）"));
        }

        return plan;
    }

    /// <summary>执行重排：写注册表 + 返回逐条结果。调用方负责先做快照。</summary>
    public static List<string> Apply(IReadOnlyList<ReorderItem> plan)
    {
        var log = new List<string>();
        foreach (var item in plan)
        {
            var full = Path.Combine(SoundScheme.MediaDir, item.NewWav);
            if (!File.Exists(full))
            {
                log.Add($"跳过 {item.Event} —— 素材不存在：{item.NewWav}");
                continue;
            }
            // 值类型按基线保真：不把原本 REG_EXPAND_SZ 的槽降级成 REG_SZ（2026-10-18）
            SoundScheme.SetSound(item.Event, full, ProfileStore.VoiceWriteKind(item.Event));
            log.Add($"{item.Event} → {item.NewWav}（{item.Reason}）");
        }
        return log;
    }
}
