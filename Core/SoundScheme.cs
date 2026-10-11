using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace BSH.Core;

/// <summary>一个系统声音事件及其当前指向的 wav 文件（WavPath 为 null / 空 = 该事件静音）。</summary>
public sealed record EventSound(string Event, string? WavPath)
{
    [JsonIgnore] public bool IsEmpty => string.IsNullOrWhiteSpace(WavPath);
    [JsonIgnore] public string FileName => IsEmpty ? "" : Path.GetFileName(WavPath!);
}

/// <summary>
/// 一个事件槽在注册表里的**原貌**：未展开字面值 + 值类型 + <c>.Current</c> 键是否存在。
/// <para>
/// 与 <see cref="EventSound"/> 的区别就是"保真"：<see cref="EventSound.WavPath"/> 是 C# 读出来的值
/// （REG_EXPAND_SZ 已被展开成 <c>C:\WINDOWS\media\X.wav</c>），拿它写回会把类型与字面值一起改掉；
/// 本类型保存 <c>%SystemRoot%\media\X.wav</c> 与类型，才能逐字节还原。
/// </para>
/// </summary>
/// <param name="Event">事件名（注册表子键名，如 <c>Notification.Looping.Alarm</c>）。</param>
/// <param name="RawWav">**未展开**的字面值；键不存在或键上没有默认值时是 <c>null</c>。</param>
/// <param name="Kind">值的注册表类型；没有默认值时是 <see cref="RegistryValueKind.Unknown"/>。</param>
/// <param name="KeyExists">该事件的 <c>.Current</c> 子键是否存在（"键在但没值"与"键不存在"是两回事）。</param>
public sealed record EventSlotRaw(string Event, string? RawWav, RegistryValueKind Kind, bool KeyExists)
{
    /// <summary>现在读不出任何 wav（键不存在，或键在但没有默认值）= 该槽是静音。</summary>
    [JsonIgnore] public bool IsSilent => string.IsNullOrEmpty(RawWav);

    /// <summary>
    /// 该槽现在**有没有默认值** —— 与 <see cref="IsSilent"/> 不是一回事，别混用：
    /// <list type="bullet">
    ///   <item>"键在、值 = 空串"是**有**默认值的（被人显式写成静音，类型多为 REG_SZ）；</item>
    ///   <item>"键在、零个值"（本机 SystemExit / WindowsLogoff 的原始形态）**没有**默认值；</item>
    ///   <item>"键不存在"同样**没有**默认值。</item>
    /// </list>
    /// <para>
    /// 判定走 <see cref="Kind"/>：<see cref="SoundScheme.ReadRaw(string)"/> 只在读不到默认值时才给出
    /// <see cref="RegistryValueKind.Unknown"/>，其余情况给的都是真实类型。
    /// 还原预演必须用它 —— 用 <see cref="IsSilent"/> 会把"键 + 空串"误判成"已经是空壳、不用动"，
    /// 而实际还原是会去删掉那个空串值的（预演与实际就会打架）。
    /// </para>
    /// </summary>
    [JsonIgnore] public bool HasDefaultValue => Kind != RegistryValueKind.Unknown;
}

/// <summary>
/// 一个槽的还原动作 —— <see cref="SoundScheme.RestoreSlot"/> 的真实分派结果（只读投影用）。
/// <para>三种形态各对应一种动作，这也是"逐字节还原 Windows 原样"的全部可能：写值 / 删值保键 / 删键。</para>
/// </summary>
public enum SlotRestoreAction
{
    /// <summary>不需要动作（分派永远给出下面三种之一，留这个值是给"未来新增类型"兜底）。</summary>
    None = 0,

    /// <summary>把默认值按**原类型**写回（含"显式静音"的空串 REG_SZ）。</summary>
    SetValue,

    /// <summary>删掉默认值、**保留** <c>.Current</c> 子键 —— Windows 原样的"键在、无值"形态。</summary>
    DeleteValue,

    /// <summary>删掉整个 <c>.Current</c> 子键 —— 快照/基线记的就是"键不存在"。</summary>
    DeleteSubKey,
}

/// <summary>
/// 只读投影的一行：某个事件槽"还原时会做什么"。
/// <para>
/// <see cref="SchemeSnapshot.Apply"/> 就是照这份清单逐条执行的，所以自检命令能在**不写注册表**的前提下
/// 打印出还原的真实分派（命令 <c>--slot-plan</c> 用的就是它）。
/// </para>
/// </summary>
/// <param name="Event">事件名。</param>
/// <param name="Action">动作类型。</param>
/// <param name="RawWav">**未展开**字面值；只有 <see cref="SlotRestoreAction.SetValue"/> 会用到它。</param>
/// <param name="Kind">要写回的类型；没有值时为 <c>null</c>。</param>
/// <param name="KeyExists">记录下来的"<c>.Current</c> 子键是否存在"。</param>
public sealed record SlotRestoreStep(string Event, SlotRestoreAction Action, string? RawWav,
                                    RegistryValueKind? Kind, bool KeyExists)
{
    /// <summary>动作的可读文本，与验收口径逐字对应：<c>SetValue(ExpandString)</c> / <c>SetValue(String)</c> / <c>DeleteValue</c> / <c>DeleteSubKeyTree</c>。</summary>
    [JsonIgnore] public string ActionText => Action switch
    {
        SlotRestoreAction.SetValue => $"SetValue({Kind})",
        SlotRestoreAction.DeleteValue => "DeleteValue",
        SlotRestoreAction.DeleteSubKey => "DeleteSubKeyTree",
        _ => "None",
    };
}

/// <summary>
/// 系统声音方案的读写层。
/// 只动 HKCU（用户级）：<c>HKCU\AppEvents\Schemes\Apps\.Default\&lt;事件&gt;\.Current</c> 的默认值。
/// 不写 HKLM、不需要管理员、不改任何系统文件。
/// </summary>
public static class SoundScheme
{
    public const string BaseKeyPath = @"AppEvents\Schemes\Apps\.Default";

    /// <summary>系统音效素材目录（C:\Windows\Media）。</summary>
    public static string MediaDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");

    /// <summary>枚举全部系统声音事件及其当前 wav（事件清单以注册表为准，不硬编码）。</summary>
    public static List<EventSound> ReadAll()
    {
        var list = new List<EventSound>();
        using var baseKey = Registry.CurrentUser.OpenSubKey(BaseKeyPath);
        if (baseKey is null) return list;

        foreach (var name in baseKey.GetSubKeyNames())
        {
            using var current = baseKey.OpenSubKey(Path.Combine(name, ".Current"));
            var value = current?.GetValue("") as string;
            list.Add(new EventSound(name, string.IsNullOrWhiteSpace(value) ? null : value));
        }

        list.Sort((a, b) =>
        {
            int w = AppEventCatalog.SortWeight(a.Event).CompareTo(AppEventCatalog.SortWeight(b.Event));
            return w != 0 ? w : string.Compare(a.Event, b.Event, StringComparison.OrdinalIgnoreCase);
        });
        return list;
    }

    /// <summary>
    /// 枚举全部事件槽的**原样**读数：未展开字面值 + 注册表值类型。
    /// <para>
    /// 与 <see cref="ReadAll"/> 只差两点：值**不展开**、并且**带类型**。
    /// 基线比对与"类型保真"的还原必须走这一个 —— 用展开值比对会把
    /// "类型被改成 REG_SZ 但读出来一样"这种破坏漏掉。
    /// </para>
    /// </summary>
    public static List<EventSlotRaw> ReadAllRaw()
    {
        var list = new List<EventSlotRaw>();
        using var baseKey = Registry.CurrentUser.OpenSubKey(BaseKeyPath);
        if (baseKey is null) return list;

        foreach (var name in baseKey.GetSubKeyNames()) list.Add(ReadRaw(baseKey, name));

        // 排序口径与 ReadAll 完全一致，方便两份结果逐项对照
        list.Sort((a, b) =>
        {
            int w = AppEventCatalog.SortWeight(a.Event).CompareTo(AppEventCatalog.SortWeight(b.Event));
            return w != 0 ? w : string.Compare(a.Event, b.Event, StringComparison.OrdinalIgnoreCase);
        });
        return list;
    }

    /// <summary>读单个事件槽的原样读数（未展开字面值 + 类型）。</summary>
    /// <param name="eventName">事件名（注册表子键名）。</param>
    public static EventSlotRaw ReadRaw(string eventName)
    {
        using var baseKey = Registry.CurrentUser.OpenSubKey(BaseKeyPath);
        return baseKey is null
            ? new EventSlotRaw(eventName, null, RegistryValueKind.Unknown, false)
            : ReadRaw(baseKey, eventName);
    }

    /// <summary>读单个槽的内部实现（复用已打开的父键句柄，避免反复 OpenSubKey）。</summary>
    private static EventSlotRaw ReadRaw(RegistryKey baseKey, string eventName)
    {
        using var current = baseKey.OpenSubKey(Path.Combine(eventName, ".Current"));
        if (current is null) return new EventSlotRaw(eventName, null, RegistryValueKind.Unknown, false);

        // 默认值的名字就是空串；"键在但一个值都没有"（本机 SystemExit / WindowsLogoff 实测如此）
        // 必须与"值是空串"区分开：前者没写过，后者是被显式写成静音的。
        bool hasDefault = Array.Exists(current.GetValueNames(), n => n.Length == 0);
        if (!hasDefault) return new EventSlotRaw(eventName, null, RegistryValueKind.Unknown, true);

        // ⚠️ DoNotExpandEnvironmentNames：要的是字面值 %SystemRoot%\media\X.wav，
        //    不是展开后的 C:\WINDOWS\media\X.wav —— 一旦展开过，原样就再也写不回去了。
        var raw = current.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        return new EventSlotRaw(eventName, raw, current.GetValueKind(""), true);
    }

    /// <summary>把某个事件指向指定 wav（用户级写入）。</summary>
    /// <param name="valueKind">
    /// 写入用的值类型。**还原基线时必须传 <see cref="RegistryValueKind.ExpandString"/>**：
    /// 实测本机 59 槽里有 22 个（Alarm/Ring/Logon/Unlock）原本存的是
    /// <c>%SystemRoot%\media\X.wav</c> 且类型为 ExpandString。若一律按 String 写回，
    /// 读出来的值虽然一样，但**类型与字面值都被改掉**，就不再是逐字节的"Windows 原样"。
    /// </param>
    public static void SetSound(string eventName, string wavPath,
                                RegistryValueKind valueKind = RegistryValueKind.String)
    {
        using var key = Registry.CurrentUser.CreateSubKey(
            Path.Combine(BaseKeyPath, eventName, ".Current"), writable: true)
            ?? throw new InvalidOperationException($"无法打开注册表键：{eventName}\\.Current");
        key.SetValue("", wavPath, valueKind);
    }

    /// <summary>把某个事件设为静音（写空值）—— 与系统"（无）"等价。</summary>
    public static void ClearSound(string eventName) => SetSound(eventName, "");

    /// <summary>
    /// 把某个事件的 <c>.Current</c> 子键整个删掉 —— 这是**快照里记着"键不存在"**的槽才该走的还原动作
    /// （见 <see cref="RestoreSlot"/> / <see cref="SlotRestoreAction.DeleteSubKey"/>）。
    /// <para>
    /// ⚠️ 别拿它来还原"键在、无值"的空壳槽（<see cref="EventSlotRaw.KeyExists"/> 为 true 的那些）：
    /// 那会把键结构改掉，本机 SystemExit / WindowsLogoff 正是这种形态。空壳槽要的是
    /// <see cref="RestoreSlot"/> 的 <see cref="SlotRestoreAction.DeleteValue"/> 分支（删值保键）。
    /// </para>
    /// <para>键本来就不存在时**静默返回 false**，不抛异常（幂等，重复还原安全）。</para>
    /// </summary>
    /// <param name="eventName">事件名（注册表子键名，如 <c>SystemExit</c>）。</param>
    /// <returns>确实删掉了一个存在的 <c>.Current</c> 键时为 <c>true</c>；键不存在时为 <c>false</c>。</returns>
    public static bool DeleteSound(string eventName)
    {
        var path = Path.Combine(BaseKeyPath, eventName, ".Current");

        // 先探一下在不在：DeleteSubKeyTree 不返回"删没删到"，而调用方（还原计数）想知道
        using (var probe = Registry.CurrentUser.OpenSubKey(path))
        {
            if (probe is null) return false;
        }

        Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        return true;
    }

    /// <summary>
    /// 还原动作的**纯分派**（不碰注册表）：按记录下来的原始结构决定该做什么。
    /// <para>
    /// 这是还原语义的**唯一真源** —— <see cref="RestoreSlot"/> 照它执行、<see cref="SlotRestoreStep"/> 投影照它生成，
    /// 所以"预演说会做什么"与"实际做什么"永远一致。规则与三种形态一一对应：
    /// </para>
    /// <list type="number">
    ///   <item>有值（String / ExpandString，值为空串也算——那是"显式静音"；只记了值没记类型也算）→
    ///         <see cref="SlotRestoreAction.SetValue"/>；</item>
    ///   <item>键在、无值 → <see cref="SlotRestoreAction.DeleteValue"/>（**删默认值、保留子键**）；</item>
    ///   <item>键不存在 → <see cref="SlotRestoreAction.DeleteSubKey"/>（删键）。</item>
    /// </list>
    /// </summary>
    /// <param name="keyExists">记录下来的"<c>.Current</c> 子键是否存在"（本机 SystemExit / WindowsLogoff 为 true）。</param>
    /// <param name="rawWav">记录下来的**未展开**字面值；没有值时为 <c>null</c>。</param>
    /// <param name="kind">记录下来的值类型；没有值时为 <c>null</c> 或 <see cref="RegistryValueKind.Unknown"/>。</param>
    /// <returns>该槽的还原动作。</returns>
    public static SlotRestoreAction DecideRestoreAction(bool keyExists, string? rawWav, RegistryValueKind? kind)
    {
        // 有值：类型说了算（String / ExpandString 都照写）。
        // rawWav 非 null 也算"有值"—— 兜住"记着值却没记类型"的畸形数据：宁可写回去，也不能当没值删掉。
        if (kind is RegistryValueKind.String or RegistryValueKind.ExpandString || rawWav is not null)
            return SlotRestoreAction.SetValue;

        // 没值：键在 → 删值保键；键不在 → 删键。这两种形态**结构不同**，必须分开还原
        return keyExists ? SlotRestoreAction.DeleteValue : SlotRestoreAction.DeleteSubKey;
    }

    /// <summary>
    /// 把一个事件槽还原成**记录下来的原始结构**（三种形态各走一条路，绝不"保持不动"）。
    /// <para>
    /// 为什么必须有这一个：快照捕获时槽是空的，之后别的方案写进了值，还原时若"保持不动"，
    /// 那个值就会**留在注册表里没被还原**（实测缺陷）。所以"读不出值"必须当作一条**明确的还原指令**，
    /// 而不是"没什么可做的"。
    /// </para>
    /// <list type="bullet">
    ///   <item><see cref="SlotRestoreAction.SetValue"/> → <see cref="SetSound"/>，按 <paramref name="kind"/> 原类型写回
    ///         （<paramref name="kind"/> 缺记时按 REG_SZ 写，但**不**把它当没值删掉）；</item>
    ///   <item><see cref="SlotRestoreAction.DeleteValue"/> → <c>DeleteValue("", throwOnMissingValue: false)</c>：
    ///         删默认值、**保留** <c>.Current</c> 子键。用 <c>OpenSubKey</c> 而**不是** <c>CreateSubKey</c> ——
    ///         子键已经不存在时不许凭空建出来，保持"键不存在"；</item>
    ///   <item><see cref="SlotRestoreAction.DeleteSubKey"/> → <see cref="DeleteSound"/>，对缺失键是空操作。</item>
    /// </list>
    /// </summary>
    /// <param name="eventName">事件名（注册表子键名）。</param>
    /// <param name="keyExists">记录下来的"<c>.Current</c> 子键是否存在"。</param>
    /// <param name="rawWav">记录下来的**未展开**字面值；没有值时为 <c>null</c>。</param>
    /// <param name="kind">记录下来的值类型；没有值时为 <c>null</c> 或 <see cref="RegistryValueKind.Unknown"/>。</param>
    /// <returns><c>true</c> = 确实改动了注册表；<c>false</c> = 现场本来就是目标结构、一个字节都没动（幂等）。</returns>
    /// <exception cref="InvalidOperationException"><see cref="SlotRestoreAction.SetValue"/> 分支打不开键时抛出。</exception>
    public static bool RestoreSlot(string eventName, bool keyExists, string? rawWav, RegistryValueKind? kind)
    {
        switch (DecideRestoreAction(keyExists, rawWav, kind))
        {
            case SlotRestoreAction.SetValue:
                // 有值：连类型一起写回（REG_EXPAND_SZ 绝不能被降级成 REG_SZ）
                // kind 缺记（畸形数据）时只能按 REG_SZ 写 —— 但**不**把它当没值删掉
                SetSound(eventName, rawWav ?? "", kind ?? RegistryValueKind.String);
                return true;

            case SlotRestoreAction.DeleteValue:
            {
                // 键在、无值：删掉默认值，但**保留** .Current 子键
                var path = Path.Combine(BaseKeyPath, eventName, ".Current");

                // 先只读探一次：键不存在就静默返回 false —— "子键本来就不存在"就让它继续不存在，
                // 这里**不用** CreateSubKey，绝不凭空把键建出来
                using (var probe = Registry.CurrentUser.OpenSubKey(path))
                {
                    if (probe is null) return false;
                }

                // 键确实在：必须可写。打不开就**抛异常**而不是静默跳过 ——
                // "什么都没做却装作还原了"正是这两个缺陷的根源，不能再留一条静默失败的路
                using var key = Registry.CurrentUser.OpenSubKey(path, writable: true)
                    ?? throw new InvalidOperationException($"无法以可写方式打开注册表键：{eventName}\\.Current");

                // 有没有默认值要自己数：DeleteValue 不告诉我们"删没删到"，
                // 而调用方（还原计数）想知道这次到底动没动
                bool hadDefault = Array.Exists(key.GetValueNames(), n => n.Length == 0);
                key.DeleteValue("", throwOnMissingValue: false);
                return hadDefault;
            }

            default:
                // 键不存在：删键（DeleteSound 对缺失键是空操作，返回 false）
                return DeleteSound(eventName);
        }
    }

    /// <summary>Media 目录里可用的 wav 素材。</summary>
    public static List<string> AvailableMediaFiles()
    {
        if (!Directory.Exists(MediaDir)) return [];
        return Directory.EnumerateFiles(MediaDir, "*.wav")
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

/// <summary>
/// 应用前的整机快照 —— 一键还原就靠它。
/// <para>
/// <b>类型保真</b>：快照存的是每个事件槽的**原样读数**（未展开字面值 + 注册表值类型 + <c>.Current</c> 子键是否存在），
/// 而不是旧版那种"读出来已经被展开过的路径"。这一条直接决定"快照 → 还原"往返是否安全：
/// </para>
/// <list type="bullet">
///   <item>22 个 <c>REG_EXPAND_SZ</c> 槽（Alarm / Call / Logon / Unlock）不会在往返后被降级成 <c>REG_SZ</c>；</item>
///   <item>"键在、零个值"的空壳槽不会被写成"键 + 空串"（写空串 = 把"从来没写过"变成"显式静音"）；</item>
///   <item><b>结构保真</b>：快照里读不出值的槽不再是"保持不动"，而是按记下的 <see cref="EventSlotRaw.KeyExists"/>
///         明确还原成"键在、无值"（删值保键）或"键不存在"（删键）—— 否则"捕获时是空的、之后被写进值"的槽
///         会**留在被改过的状态里**，根本没还原。</item>
/// </list>
/// </summary>
public sealed class SchemeSnapshot
{
    /// <summary>创建时间（本地时间，格式 <c>yyyy-MM-dd HH:mm:ss</c>）。</summary>
    public string CreatedAt { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>备注（自动快照会写明是"哪个操作之前"存的）。</summary>
    public string Note { get; set; } = "";

    /// <summary>
    /// 快照里的原始槽位列表：事件 + **未展开**字面值 + 注册表类型 + <c>.Current</c> 键是否存在。
    /// <para>顺序与 <see cref="SoundScheme.ReadAllRaw"/> 一致（先按事件权重、再按名字）。</para>
    /// </summary>
    public List<EventSlotRaw> Slots { get; set; } = [];

    /// <summary>槽位数（界面确认框与统计用；等价于 <see cref="Slots"/>.Count）。</summary>
    [JsonIgnore] public int Count => Slots.Count;

    /// <summary>
    /// true = 本快照来自**旧格式文件**（只有 <c>Sounds</c> 字典、没有类型字段），
    /// 各槽的类型是加载时按值推断的，不是当初采集下来的。界面必须把这件事告诉用户 ——
    /// 推断出的类型可能与真实类型不同（尤其那 22 个 ExpandString 槽）。
    /// </summary>
    [JsonIgnore] public bool LegacyInferred { get; private set; }

    /// <summary>类型是推断出来的槽数（新格式快照恒为 0）。</summary>
    [JsonIgnore] public int InferredCount { get; private set; }

    /// <summary>
    /// 快照里**没有默认值**的槽数（键在但零个值、或键不存在）。
    /// <para>
    /// 这些槽还原时**不写值**，但要按键结构恢复（键在 → 删值保键；键不存在 → 删键），
    /// 所以**不是**"保持不动"；调用方别把它当成"这次不用管的槽数"。
    /// </para>
    /// </summary>
    [JsonIgnore] public int NoValueCount =>
        Slots.Count(s => s.Kind != RegistryValueKind.String && s.Kind != RegistryValueKind.ExpandString);

    /// <summary>
    /// 只读采集：把当前全部事件槽的**原样读数**（未展开字面值 + 类型）存成快照。
    /// <para>本方法只读注册表、不写任何东西；真正写盘的只有 <see cref="Save"/>。</para>
    /// </summary>
    /// <param name="note">备注，写进 <see cref="Note"/>。</param>
    public static SchemeSnapshot Capture(string note = "")
    {
        var snap = new SchemeSnapshot { Note = note };

        // 必须走 ReadAllRaw：ReadAll 读出来的是已展开的值，展开过的原样再也写不回去
        snap.Slots.AddRange(SoundScheme.ReadAllRaw());
        return snap;
    }

    /// <summary>
    /// 还原时**会真正写入值**的槽（只读投影，不碰注册表）。
    /// <para>
    /// 分派与 <see cref="Apply"/> 完全同源（都出自 <see cref="SoundScheme.DecideRestoreAction"/>）：
    /// 就是 <see cref="RegistryValueKind.String"/> / <see cref="RegistryValueKind.ExpandString"/> 这两类有值的槽，
    /// 所以自检命令能在**不写注册表**的前提下打印"还原会写哪些槽、按什么类型写"。
    /// </para>
    /// <para>
    /// ⚠️ 它**不是**"Apply 会动的全部"：没有默认值的槽（<see cref="NoValueCount"/> 那些）不在清单里，
    /// 但它们并不是"保持不动"—— 它们走的是删值保键 / 删键。要看完整的还原分派请用 <see cref="Actions"/>。
    /// </para>
    /// </summary>
    public List<EventSlotRaw> Writes() =>
        Slots.Where(s => SoundScheme.DecideRestoreAction(s.KeyExists, s.RawWav, s.Kind)
                         == SlotRestoreAction.SetValue).ToList();

    /// <summary>
    /// 还原时**全部槽**的动作清单（只读投影，不碰注册表）—— <see cref="Apply"/> 就是照它逐条执行的。
    /// <para>
    /// 每个槽都会得到一条 <see cref="SlotRestoreStep"/>，**没有"什么都不做"这个选项**：
    /// 有值 → <see cref="SlotRestoreAction.SetValue"/>；键在无值 → <see cref="SlotRestoreAction.DeleteValue"/>；
    /// 键不存在 → <see cref="SlotRestoreAction.DeleteSubKey"/>。
    /// 自检命令 <c>--slot-plan</c> 就是拿它来证明分派的（见 <c>Core\SlotPlanSelfCheck.cs</c>）。
    /// </para>
    /// </summary>
    public List<SlotRestoreStep> Actions() =>
        Slots.Select(s => new SlotRestoreStep(
                s.Event,
                SoundScheme.DecideRestoreAction(s.KeyExists, s.RawWav, s.Kind),
                s.RawWav,
                s.HasDefaultValue ? s.Kind : null,
                s.KeyExists))
            .ToList();

    /// <summary>
    /// 还原：逐槽**按当初记下的结构与类型**恢复，返回实际改动的条数。
    /// <para>
    /// 分派规则见 <see cref="Actions"/>（与 <see cref="SoundScheme.RestoreSlot"/> 同源）：
    /// </para>
    /// <list type="bullet">
    ///   <item><see cref="RegistryValueKind.String"/> / <see cref="RegistryValueKind.ExpandString"/> →
    ///         按原类型写回（值为空串也照写 —— 那是"显式静音"，是真实的原样）；</item>
    ///   <item><see cref="RegistryValueKind.Unknown"/> 且快照记着键存在 → 删掉默认值、**保留**子键
    ///         （把"被写进过值"的空壳槽还原成 Windows 原样的"键在、无值"）；</item>
    ///   <item><see cref="RegistryValueKind.Unknown"/> 且快照记着键不存在 → 删掉整个子键。</item>
    /// </list>
    /// <para>快照里没有的事件当然不动（本方法只遍历 <see cref="Slots"/>）。</para>
    /// </summary>
    /// <returns>真正改动了注册表的槽数（现场本来就已经是目标结构、一个字节都没动的槽不计入）。</returns>
    public int Apply()
    {
        int n = 0;
        foreach (var step in Actions())
        {
            // 22 个 ExpandString 槽的"逐字节原样"就靠 kind 一起写回：
            // 只写值不写类型，读出来虽然一样，却已不是 Windows 原样
            if (SoundScheme.RestoreSlot(step.Event, step.KeyExists, step.RawWav, step.Kind)) n++;
        }
        return n;
    }

    /// <summary>写盘（UTF-8、缩进、中文不转义）。</summary>
    /// <param name="path">目标文件路径。</param>
    public void Save(string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(ToFile(), JsonOpts));

    /// <summary>
    /// 读盘。**兼容旧格式快照**：文件里只有 <c>Sounds</c>（事件 → 已展开的值、没有类型字段）时，
    /// 按「值为空 → 空壳；含 <c>%</c> → <see cref="RegistryValueKind.ExpandString"/>；
    /// 否则 <see cref="RegistryValueKind.String"/>」推断，并置
    /// <see cref="LegacyInferred"/> / <see cref="InferredCount"/>，让调用方如实告知用户"这次的类型是推断的"。
    /// </summary>
    /// <param name="path">快照文件路径。</param>
    /// <exception cref="InvalidDataException">文件内容解析不出快照（例如内容是字面量 <c>null</c>）。</exception>
    public static SchemeSnapshot Load(string path) =>
        FromFile(JsonSerializer.Deserialize<SnapshotFile>(File.ReadAllText(path), JsonOpts)
                 ?? throw new InvalidDataException("快照文件无法解析"));

    // ───────────────────────── 落盘格式与归一化（照 ProfileStore 的「按 Kind 分派」风格） ─────────────────────────

    /// <summary>
    /// 落盘结构。<c>CreatedAt</c> / <c>Note</c> / <c>Slots</c> 是新格式；
    /// <c>Sounds</c> 只为**读旧格式文件**而留 —— 旧快照是「事件 → 已展开的 wav」字典，没有类型信息。
    /// <para>新写出的快照里 <c>Slots</c> 一定非空、<c>Sounds</c> 一定是 null（不再写旧字段）。</para>
    /// </summary>
    private sealed class SnapshotFile
    {
        public string? CreatedAt { get; set; }
        public string? Note { get; set; }
        public List<SnapshotEntry>? Slots { get; set; }

        /// <summary>旧格式专有字段：新写出的快照里它是 null，且**不落盘**（免得看文件的人以为两种格式并存）。</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, string?>? Sounds { get; set; }
    }

    /// <summary>
    /// 落盘的一行：事件名 + **未展开**字面值 + 类型名 + <c>.Current</c> 键是否存在。
    /// <para>类型名用枚举名（<c>String</c> / <c>ExpandString</c> / <c>Unknown</c>），不用数字 —— 落盘文件要能被人读懂。</para>
    /// </summary>
    private sealed record SnapshotEntry(string Event, string? RawWav = null,
                                        string? Kind = null, bool KeyExists = true);

    /// <summary>内存快照 → 落盘结构（类型写成可读的枚举名）。</summary>
    private SnapshotFile ToFile() => new()
    {
        CreatedAt = CreatedAt,
        Note = Note,
        Slots = Slots
            .Select(s => new SnapshotEntry(s.Event, s.RawWav, s.Kind.ToString(), s.KeyExists))
            .ToList(),
    };

    /// <summary>落盘结构 → 内存快照，并统计"类型是推断出来的"槽数。</summary>
    private static SchemeSnapshot FromFile(SnapshotFile file)
    {
        var snap = new SchemeSnapshot { Note = file.Note ?? "" };
        if (!string.IsNullOrWhiteSpace(file.CreatedAt)) snap.CreatedAt = file.CreatedAt!;

        if (file.Slots is { Count: > 0 })
        {
            // ── 新格式：类型如实照做 ──
            foreach (var e in file.Slots)
            {
                var (slot, inferred) = Normalize(e);
                snap.Slots.Add(slot);
                if (inferred) snap.InferredCount++;
            }
        }
        else if (file.Sounds is { Count: > 0 })
        {
            // ── 旧格式：只有「事件 → 已展开的值」，没有类型 ──
            // Kind 传 null = 走推断分支，所以 InferredCount 就等于槽数（界面据此提示用户）
            foreach (var (evt, wav) in file.Sounds)
            {
                var (slot, inferred) = Normalize(new SnapshotEntry(evt, wav, null, KeyExists: true));
                snap.Slots.Add(slot);
                if (inferred) snap.InferredCount++;
            }
        }

        snap.LegacyInferred = snap.InferredCount > 0;
        return snap;
    }

    /// <summary>
    /// 一行落盘数据 → 一个原始槽位。
    /// <para>
    /// 认得类型名（<c>String</c> / <c>ExpandString</c>，以及表示"读不出值"的 <c>Unknown</c> / <c>Missing</c> / <c>None</c>）
    /// 就**如实照做** —— 类型保真的关键就在这，22 个 ExpandString 槽绝不能被写成 String。
    /// </para>
    /// <para>
    /// 认不出（旧格式没有类型字段，或将来加了别的类型名）则按值推断，并把 <c>Inferred</c> 标记带出去，
    /// 由界面告诉用户"这次还原的类型是推断的"。**不猜死、也不报错** —— 与 ProfileStore 的处理口径一致。
    /// </para>
    /// </summary>
    /// <returns>归一化后的槽位，以及"类型是否为推断"的标记。</returns>
    private static (EventSlotRaw Slot, bool Inferred) Normalize(SnapshotEntry e)
    {
        var kind = e.Kind?.Trim();

        if (!string.IsNullOrEmpty(kind))
        {
            if (kind.Equals("ExpandString", StringComparison.OrdinalIgnoreCase))
                return (new EventSlotRaw(e.Event, e.RawWav ?? "", RegistryValueKind.ExpandString, e.KeyExists), false);

            if (kind.Equals("String", StringComparison.OrdinalIgnoreCase))
                return (new EventSlotRaw(e.Event, e.RawWav ?? "", RegistryValueKind.String, e.KeyExists), false);

            // "这个槽读不出值"的几种写法：Unknown = 注册表给不出类型（键在但零个值）；Missing / None = 键不存在
            if (kind.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
                || kind.Equals("Missing", StringComparison.OrdinalIgnoreCase)
                || kind.Equals("None", StringComparison.OrdinalIgnoreCase))
                return (new EventSlotRaw(e.Event, null, RegistryValueKind.Unknown, e.KeyExists), false);

            // 认不出的类型名（比如以后加了 DWord）：落到下面的按值推断
        }

        // ── 旧格式 / 认不出的类型名：按值推断 ──
        // 值为空 → 空壳（键在、零个值；旧格式没有 KeyExists，只能按"键在"处理 → Apply 时删值保键）
        if (string.IsNullOrEmpty(e.RawWav))
            return (new EventSlotRaw(e.Event, null, RegistryValueKind.Unknown, e.KeyExists), true);

        // 含 % → 当初多半是 REG_EXPAND_SZ；否则只能当 REG_SZ（旧快照存的是展开值，这一层已经无从分辨）
        return (new EventSlotRaw(e.Event, e.RawWav,
            e.RawWav.Contains('%') ? RegistryValueKind.ExpandString : RegistryValueKind.String,
            e.KeyExists), true);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
