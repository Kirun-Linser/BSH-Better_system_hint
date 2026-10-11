namespace BSH.Core;

/// <summary>
/// 槽位还原动作的**只读**投影自检（命令行 <c>--slot-plan &lt;快照文件&gt;</c>）。
/// <para>
/// 它回答的是那个最要命的问题：**"快照里的空槽，还原时到底做什么？"**
/// 三种形态各有各的动作，且**没有"保持不动"这一项**：
/// </para>
/// <list type="bullet">
///   <item><c>KeyExists=True</c> 且无值（本机 SystemExit / WindowsLogoff 的原始形态）→ <c>DeleteValue</c>（删默认值、**保留**子键）；</item>
///   <item><c>KeyExists=False</c> → <c>DeleteSubKeyTree</c>（删键）；</item>
///   <item>有值（<c>String</c> / <c>ExpandString</c>，空串也算）→ <c>SetValue(原类型)</c>。</item>
/// </list>
/// <para>
/// ⚠️ <b>绝不碰注册表</b>：输入只有命令行给的那个 JSON 文件，输出只有 stdout ——
/// 它只调用 <see cref="SchemeSnapshot.Load"/> 与 <see cref="SchemeSnapshot.Actions"/>（都是纯只读投影），
/// <b>不</b>调用 <see cref="SchemeSnapshot.Apply"/>。所以可以拿**造出来的 fixture** 证明分派，
/// 而不必真去写一次注册表。
/// </para>
/// </summary>
public static class SlotPlanSelfCheck
{
    /// <summary>摘要行的固定前缀（脚本按 <c>key=value</c> 取字段）。</summary>
    public const string ResultPrefix = "slot-plan";

    /// <summary>参数不对或读写失败时的退出码。</summary>
    public const int ExitFailure = 7;

    /// <summary>
    /// 跑一次自检：读一个快照文件，逐槽打印"还原会做什么"。
    /// </summary>
    /// <param name="fixturePath">快照文件路径（新格式 / 旧格式都认，走的是与还原完全相同的 <see cref="SchemeSnapshot.Load"/>）。</param>
    /// <returns>
    /// 退出码（0 成功 / <see cref="ExitFailure"/> 失败）与要打印的行；
    /// **最后一行**是固定格式的摘要：<c>slot-plan slots=N setValue=… deleteValue=… deleteSubKey=… path=…</c>。
    /// </returns>
    public static (int ExitCode, List<string> Lines) Run(string fixturePath)
    {
        var lines = new List<string>();

        if (string.IsNullOrWhiteSpace(fixturePath))
            return (ExitFailure, [$"{ResultPrefix} failed: 缺少快照文件路径（用法：--slot-plan <快照文件路径>）"]);

        try
        {
            // Load 只读文件；Actions 是 Apply 的只读投影 —— 到这里为止一个注册表字节都没碰
            var snap = SchemeSnapshot.Load(fixturePath);
            var actions = snap.Actions();

            int nSet = actions.Count(a => a.Action == SlotRestoreAction.SetValue);
            int nDelVal = actions.Count(a => a.Action == SlotRestoreAction.DeleteValue);
            int nDelKey = actions.Count(a => a.Action == SlotRestoreAction.DeleteSubKey);

            lines.Add($"快照：{Path.GetFullPath(fixturePath)}（{snap.Count} 槽 · 备注：{snap.Note}）");
            lines.Add("逐槽还原动作（只读投影，未写注册表）：");

            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                lines.Add(
                    $"  slot {i + 1} event={a.Event} keyExists={a.KeyExists} "
                    + $"valueExists={a.RawWav is not null} kind={(a.Kind?.ToString() ?? "(none)")} "
                    + $"value={(string.IsNullOrEmpty(a.RawWav) ? "(空或空串)" : a.RawWav)} "
                    + $"action={a.ActionText} 说明={Explain(a.Action)}");
            }

            lines.Add($"{ResultPrefix} slots={snap.Count} setValue={nSet} deleteValue={nDelVal} "
                    + $"deleteSubKey={nDelKey} path={Path.GetFullPath(fixturePath)}");
            return (0, lines);
        }
        catch (Exception ex)
        {
            return (ExitFailure, [$"{ResultPrefix} failed: {ex.Message}"]);
        }
    }

    /// <summary>把动作翻成一句人话（打印在每行末尾，便于肉眼核对）。</summary>
    private static string Explain(SlotRestoreAction action) => action switch
    {
        SlotRestoreAction.SetValue => "按原类型写回默认值（含空串=显式静音）",
        SlotRestoreAction.DeleteValue => "删默认值、保留 .Current 子键（Windows 原样的键在无值）",
        SlotRestoreAction.DeleteSubKey => "删掉整个 .Current 子键（快照记的是键不存在）",
        _ => "（无动作）",
    };
}
