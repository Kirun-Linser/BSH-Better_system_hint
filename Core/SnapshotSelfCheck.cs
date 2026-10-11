namespace BSH.Core;

/// <summary>
/// 快照类型保真的**只读**自检入口（命令行 <c>--snapshot-check</c>），供 <c>tools\verify-snapshot-types.py</c> 取证。
/// <para>三个模式，**都不写注册表**（<see cref="SchemeSnapshot.Capture"/> 本身只读，<see cref="SchemeSnapshot.Load"/> 更是完全不碰注册表）：</para>
/// <list type="number">
///   <item><c>capture &lt;输出路径&gt;</c> —— 采集当前全部事件槽的原样读数（未展开字面值 + 类型），写出**新格式**快照；</item>
///   <item><c>normalize &lt;输入路径&gt; &lt;输出路径&gt;</c> —— 读一个快照文件（新格式或旧格式都认），归一化后写出新格式快照。
///         旧快照的"类型推断"结果就是靠它落到文件上，脚本才能核对 ExpandString / String / 空壳 各多少；</item>
///   <item><c>plan &lt;输入路径&gt;</c> —— 打印"还原**会**做什么"的计数：写值的槽（<see cref="SchemeSnapshot.Writes"/>）、
///         以及三类还原动作各自的条数（<see cref="SchemeSnapshot.Actions"/>：SetValue / DeleteValue / DeleteSubKeyTree）。
///         这是**不写注册表**的前提下对还原行为的直接证据。</item>
/// </list>
/// <para>
/// ⚠️ 本命令<b>绝不</b>调用 <see cref="SchemeSnapshot.Apply"/> —— 还原会写注册表，不在只读自检的范围内。
/// </para>
/// </summary>
public static class SnapshotSelfCheck
{
    /// <summary>摘要行的固定前缀（脚本按 <c>key=value</c> 取字段）。</summary>
    public const string ResultPrefix = "snapshot-check";

    /// <summary>参数不对或读写失败时的退出码。</summary>
    public const int ExitFailure = 6;

    /// <summary>
    /// 跑一次自检。
    /// </summary>
    /// <param name="mode"><c>capture</c> 或 <c>normalize</c>（忽略大小写与首尾空白）。</param>
    /// <param name="first">capture = 输出路径；normalize = 输入路径。</param>
    /// <param name="second">normalize 的输出路径；capture 用不到。</param>
    /// <returns>退出码（0 成功 / <see cref="ExitFailure"/> 失败）与一行可解析的中文摘要。</returns>
    public static (int ExitCode, string Summary) Run(string mode, string first, string second)
    {
        try
        {
            switch ((mode ?? "").Trim().ToLowerInvariant())
            {
                case "capture":
                {
                    if (string.IsNullOrWhiteSpace(first))
                        return (ExitFailure, $"{ResultPrefix} failed: 缺少输出路径（用法：capture <输出路径>）");

                    // 只读注册表；写盘只写这一个文件
                    var snap = SchemeSnapshot.Capture("--snapshot-check capture");
                    string full = WriteOut(snap, first);
                    return (0, $"{ResultPrefix} capture {Stats(snap)} path={full}");
                }

                case "normalize":
                {
                    if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
                        return (ExitFailure, $"{ResultPrefix} failed: 需要两个路径（用法：normalize <输入路径> <输出路径>）");

                    var snap = SchemeSnapshot.Load(first);
                    string full = WriteOut(snap, second);
                    return (0, $"{ResultPrefix} normalize {Stats(snap)} path={full}");
                }

                case "plan":
                {
                    if (string.IsNullOrWhiteSpace(first))
                        return (ExitFailure, $"{ResultPrefix} failed: 缺少输入路径（用法：plan <快照路径>）");

                    // 只读：Load 不碰注册表，Writes 只是 Apply 的投影（Apply 本人才会写）
                    var snap = SchemeSnapshot.Load(first);
                    return (0, $"{ResultPrefix} plan {Stats(snap)} writes={snap.Writes().Count}");
                }

                default:
                    return (ExitFailure, $"{ResultPrefix} failed: 模式只能是 capture / normalize / plan（收到「{mode}」）");
            }
        }
        catch (Exception ex)
        {
            return (ExitFailure, $"{ResultPrefix} failed: {ex.Message}");
        }
    }

    /// <summary>写出快照文件（自动建目录），返回写成的绝对路径。</summary>
    private static string WriteOut(SchemeSnapshot snap, string path)
    {
        string full = Path.GetFullPath(path);
        string? dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        snap.Save(full);
        return full;
    }

    /// <summary>
    /// 一行统计，脚本直接按 <c>key=value</c> 解析：
    /// <c>slots</c> = 槽位数；<c>string</c> / <c>expand</c> / <c>unknown</c> = 三种类型各多少；
    /// <c>shell</c> = "读不出值"的空壳槽（Kind=Unknown 且值为空）；<c>blank</c> = 被显式写成空串的 REG_SZ 槽；
    /// <c>untouched</c> = 快照里没有默认值的槽数（还原时**按键结构**恢复：删值保键 / 删键，不是"不动"）；
    /// <c>setValue</c> / <c>deleteValue</c> / <c>deleteSubKey</c> = 还原动作三类各多少
    /// （<see cref="SchemeSnapshot.Actions"/> 的计数，三者之和恒等于 <c>slots</c>）；
    /// <c>inferred</c> = 类型是推断出来的槽数；<c>legacy</c> = 是否来自旧格式文件。
    /// <para>
    /// ⚠️ <c>untouched</c> 这个键名是沿用旧语义留下的（验收脚本 tools\verify-snapshot-types.py 按它取值，
    /// 不能改键名）；语义已从"还原时保持不动"改成"不写值、按键结构恢复"。
    /// </para>
    /// </summary>
    private static string Stats(SchemeSnapshot snap)
    {
        int str = snap.Slots.Count(s => s.Kind == Microsoft.Win32.RegistryValueKind.String);
        int exp = snap.Slots.Count(s => s.Kind == Microsoft.Win32.RegistryValueKind.ExpandString);
        int unk = snap.Slots.Count(s => s.Kind == Microsoft.Win32.RegistryValueKind.Unknown);

        // 空壳 = 注册表给不出类型且读不出值（键在但零个值，或键不存在）
        int shell = snap.Slots.Count(s => s.Kind == Microsoft.Win32.RegistryValueKind.Unknown && s.IsSilent);

        // 显式静音：值被写成了空串，但类型是 String —— 与空壳是两回事，还原时必须写回空串
        int blank = snap.Slots.Count(s => s.Kind == Microsoft.Win32.RegistryValueKind.String && s.IsSilent);

        // 还原动作计数：与 Apply 同源（都出自 Actions()），所以"预演"里的这三个数就是还原真正会做的事
        var actions = snap.Actions();
        int nSet = actions.Count(a => a.Action == SlotRestoreAction.SetValue);
        int nDelVal = actions.Count(a => a.Action == SlotRestoreAction.DeleteValue);
        int nDelKey = actions.Count(a => a.Action == SlotRestoreAction.DeleteSubKey);

        return $"slots={snap.Count} string={str} expand={exp} unknown={unk} "
             + $"shell={shell} blank={blank} untouched={snap.NoValueCount} "
             + $"setValue={nSet} deleteValue={nDelVal} deleteSubKey={nDelKey} "
             + $"inferred={snap.InferredCount} legacy={snap.LegacyInferred}";
    }
}
