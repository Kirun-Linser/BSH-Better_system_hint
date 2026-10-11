using System.Text.Json;
using System.Text.Json.Serialization;

namespace BSH.Core.Monitoring;

/// <summary>
/// 监听规则表的读写：<c>profiles\personal\rules.json</c>。
/// <para>
/// ⚠️ 这个文件属于<b>个人配置</b>，已在 <c>.gitignore</c> 里排除，不入公开仓库。
/// </para>
/// <para>
/// 行为约定：文件不存在 → <see cref="Load"/> 返回<b>空表</b>并把原因写进 <c>note</c>（<b>不会</b>顺手创建文件），
/// 想要起点可以直接抄<b>入库的公开示例</b> <see cref="ExamplePath"/>（8 类监听各一条、全部默认禁用）；
/// <see cref="Save"/> 会自动建目录。
/// </para>
/// <para>
/// 「零规则 = 零钩子」：<see cref="Load"/> 返回空表时，引擎按规则惰性装钩子，
/// 结果就是一个监听器都不会起 —— 这是监听模式的正常状态，不是故障。
/// </para>
/// </summary>
public static class RuleStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,     // 允许 JSON 里写注释，手改起来不容易写坏
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>真实规则表文件名（个人配置，<b>不入库</b>）。</summary>
    public const string FileName = "rules.json";

    /// <summary>公开示例规则表文件名（位于 <see cref="PersonalDir"/> 下，<b>入库</b>）。</summary>
    public const string ExampleFileName = "rules.example.json";

    /// <summary>规则表路径：<c>&lt;工程根&gt;\profiles\personal\rules.json</c>。</summary>
    public static string DefaultPath => Path.Combine(ProfileStore.ProjectRoot, "profiles", "personal", FileName);

    /// <summary>公开示例规则表路径：<c>&lt;工程根&gt;\profiles\personal\rules.example.json</c>。</summary>
    public static string ExamplePath => Path.Combine(ProfileStore.ProjectRoot, "profiles", "personal", ExampleFileName);

    /// <summary>个人方案目录（保存时自动创建）。</summary>
    public static string PersonalDir => Path.Combine(ProfileStore.ProjectRoot, "profiles", "personal");

    /// <summary>规则表文件当前是否存在。</summary>
    public static bool Exists => File.Exists(DefaultPath);

    /// <summary>公开示例规则表是否存在。</summary>
    public static bool ExampleExists => File.Exists(ExamplePath);

    /// <summary>
    /// 读取规则表。
    /// </summary>
    /// <param name="note">一段人读说明：读到了几条 / 文件不存在 / 解析失败原因。</param>
    /// <returns>规则列表（文件不存在或解析失败时为空表，绝不抛异常）。</returns>
    public static IReadOnlyList<MonitorRule> Load(out string note) =>
        LoadFile(DefaultPath, "规则文件", out note);

    /// <summary>
    /// 读取<b>公开示例</b>规则表（<see cref="ExamplePath"/>）。
    /// <para>只读：<b>绝不</b>把它复制成 <c>rules.json</c>，也绝不创建任何文件 —— 要不要用由用户决定。</para>
    /// </summary>
    /// <param name="note">一段人读说明（同 <see cref="Load"/>）。</param>
    public static IReadOnlyList<MonitorRule> LoadExample(out string note) =>
        LoadFile(ExamplePath, "示例规则表", out note);

    /// <summary>
    /// 读取任意一个规则表文件。<paramref name="label"/> 只影响提示文案（"规则文件" / "示例规则表"）。
    /// <para>
    /// 不存在的文件<b>不会</b>被创建；解析失败也只返回空表并说明原因，绝不抛异常 ——
    /// 规则表写坏了不该让整个程序起不来。
    /// </para>
    /// </summary>
    /// <param name="path">要读的文件路径。</param>
    /// <param name="label">提示文案里的称呼。</param>
    /// <param name="note">一段人读说明。</param>
    public static IReadOnlyList<MonitorRule> LoadFile(string path, string label, out string note)
    {
        if (!File.Exists(path))
        {
            note = $"{label}不存在（{path}），已返回空表；可参考 {ExampleFileName} 起手（{ExamplePath}）";
            return [];
        }

        try
        {
            var json = File.ReadAllText(path);
            var rules = JsonSerializer.Deserialize<List<MonitorRule>>(json, JsonOpts) ?? [];

            // 补 Id：冷却记账以 Id 为键，空 Id 会让同一文件里的多条规则互相干扰
            for (int i = 0; i < rules.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(rules[i].Id)) rules[i].Id = $"rule-{i + 1}";
                if (string.IsNullOrWhiteSpace(rules[i].Name)) rules[i].Name = rules[i].Id;
            }

            note = $"{label}已读取 {rules.Count} 条规则（启用 {rules.Count(r => r.Enabled)} 条）";
            return rules;
        }
        catch (Exception ex)
        {
            note = $"{label}解析失败，已返回空表：{ex.GetType().Name}: {ex.Message}";
            return [];
        }
    }

    /// <summary>写入规则表（自动创建 <c>profiles\personal\</c> 目录）。</summary>
    /// <exception cref="IOException">磁盘写入失败时抛出，由调用方决定怎么提示。</exception>
    public static void Save(IEnumerable<MonitorRule> rules)
    {
        Directory.CreateDirectory(PersonalDir);
        File.WriteAllText(DefaultPath, JsonSerializer.Serialize(rules.ToList(), JsonOpts));
    }

    /// <summary>内置示例（内存字符串）：覆盖全部 8 类事件。<b>优先用入库的 <see cref="ExamplePath"/></b>（带 $comment 说明），这里只是兜底。</summary>
    public static string SampleJson => JsonSerializer.Serialize(DefaultExamples(), JsonOpts);

    /// <summary>
    /// 内置示例规则（<b>只返回内存对象，不落盘</b>）。这些条目的声音都留空，
    /// 只有配好 <c>sound</c> 或 <c>speakKey</c> 之后才会真出声。
    /// </summary>
    public static IReadOnlyList<MonitorRule> DefaultExamples() =>
    [
        new() { Id = "capslock", Name = "按 CapsLock 时提醒", Kind = MonitorEventKind.Keyboard, Match = "CapsLock", SpeakKey = "MenuCommand", CooldownMs = 300 },
        new() { Id = "mouse-wheel", Name = "鼠标滚轮", Kind = MonitorEventKind.Mouse, Match = "WheelUp,WheelDown", CooldownMs = 200 },
        new() { Id = "switch-notepad", Name = "切到记事本", Kind = MonitorEventKind.WindowSwitch, Match = "notepad.exe" },
        new() { Id = "steam-start", Name = "Steam 启动", Kind = MonitorEventKind.ProcessStartStop, Match = "start steam.exe" },
        new() { Id = "usb", Name = "U 盘插拔", Kind = MonitorEventKind.DeviceChange, Match = "Arrival,Remove", SpeakKey = "DeviceConnect", CooldownMs = 500 },
        new() { Id = "power", Name = "插拔电源 / 锁屏", Kind = MonitorEventKind.PowerSession, Match = "AC,DC,Lock,Unlock", SpeakKey = "SystemHand" },
        new() { Id = "clip", Name = "复制文本", Kind = MonitorEventKind.Clipboard, Match = "Text", CooldownMs = 1000 },
        new() { Id = "downloads", Name = "下载目录有变化", Kind = MonitorEventKind.FileSystem, Match = @"C:\Users\<你的用户名>\Downloads", CooldownMs = 500 },
    ];
}
