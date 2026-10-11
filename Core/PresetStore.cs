using System.Text.Json;
using BSH.Core.Monitoring;

namespace BSH.Core;

/// <summary>预设一览项（列表用，不含具体内容）。</summary>
/// <param name="Id">目录名 = 预设 id。</param>
/// <param name="Name">人读名字。</param>
/// <param name="Description">一句话说明。</param>
/// <param name="Builtin">是否内置（内置 = 只读，界面上不给删）。</param>
/// <param name="SoundCount">声音映射条数。</param>
/// <param name="RuleCount">规则条数。</param>
public sealed record PresetInfo(string Id, string Name, string Description, bool Builtin, int SoundCount, int RuleCount);

/// <summary>一个预设的完整内容：一套「事件 → 音频」映射 + 一套监听规则。</summary>
public sealed class Preset
{
    /// <summary>一览信息。</summary>
    public required PresetInfo Info { get; init; }

    /// <summary>事件名 → 音频（**已解析成绝对路径**）。</summary>
    public Dictionary<string, string> Sounds { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>该预设自带的监听规则。</summary>
    public List<MonitorRule> Rules { get; init; } = [];
}

/// <summary>
/// 预设仓库：只读扫描 <c>&lt;工程根&gt;\profiles\presets\&lt;id&gt;\preset.json</c>。
/// <para>
/// <b>预设是什么</b>：一套「事件 → 音频」映射 + 一套监听规则，可整体应用到系统。
/// 与「方案（<see cref="SoundProfile"/>）」的区别是<b>它是可分发的内容包</b>：
/// 内置预设随仓库走，用户预设可以自己建、导入、导出。
/// </para>
/// <para>
/// <b>容错口径与 <see cref="RuleStore"/> 一致</b>：目录不存在、JSON 坏、字段缺 —— 一律
/// <b>跳过该预设并记原因，绝不抛异常</b>（一个坏文件不该让整个界面起不来）。
/// </para>
/// <para>
/// <b>音频路径解析规则</b>（<c>sounds</c> 的值）：
/// ① 纯文件名（<c>xxx.wav</c>）→ 在 <c>%LOCALAPPDATA%\BSH\sounds\</c> 下找（现有那 39 条的存放处）；
/// ② 以 <c>sounds/</c> 开头 → 相对<b>预设目录</b>找（预设自带音频时用）；
/// ③ 其它 → 当绝对路径用。
/// 这样内置预设可以「只带映射、复用装机音频」，而第三方预设可以「自带音频、整包分发」。
/// </para>
/// </summary>
public static class PresetStore
{
    /// <summary>预设根目录。</summary>
    public static string PresetsDir => Path.Combine(ProfileStore.ProjectRoot, "profiles", "presets");

    /// <summary>装机音频目录（与 <c>tools\install-voice.ps1</c> 写入的位置一致）。</summary>
    public static string SoundsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BSH", "sounds");

    /// <summary>给 <see cref="JsonDocument.Parse(string, JsonDocumentOptions)"/> 用（它要的是另一个类型）。</summary>
    private static readonly JsonDocumentOptions DocOpts = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>给 <see cref="JsonSerializer"/> 用（反序列化规则时靠它忽略大小写、容忍注释与尾逗号）。</summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>列出全部可用预设（按内置优先、名字排序）。坏预设被跳过，原因在 <paramref name="problems"/> 里。</summary>
    /// <param name="problems">可选的收集器：每条「跳过原因」追加到这里。</param>
    public static IReadOnlyList<PresetInfo> List(List<string>? problems = null)
    {
        var list = new List<PresetInfo>();
        if (!Directory.Exists(PresetsDir)) return list;

        foreach (var dir in Directory.EnumerateDirectories(PresetsDir))
        {
            var id = Path.GetFileName(dir);
            var file = Path.Combine(dir, "preset.json");
            if (!File.Exists(file)) { problems?.Add($"{id}：缺 preset.json"); continue; }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file), DocOpts);
                var root = doc.RootElement;
                var sounds = root.TryGetProperty("sounds", out var s) ? s.EnumerateObject().Count() : 0;
                var rules = root.TryGetProperty("rules", out var r) && r.ValueKind == JsonValueKind.Array
                    ? r.GetArrayLength() : 0;
                list.Add(new PresetInfo(
                    id,
                    GetString(root, "name") is { Length: > 0 } n ? n : id,
                    GetString(root, "description") ?? "",
                    root.TryGetProperty("builtin", out var b) && b.ValueKind == JsonValueKind.True,
                    sounds, rules));
            }
            catch (Exception ex)
            {
                problems?.Add($"{id}：{ex.GetType().Name} {ex.Message}");
            }
        }

        return list
            .OrderByDescending(p => p.Builtin)
            .ThenBy(p => p.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>读取一个预设的完整内容；不存在或解析失败返回 <c>null</c>（并写入 <paramref name="problem"/>）。</summary>
    /// <param name="id">预设 id（= 目录名）。</param>
    /// <param name="problem">失败原因。</param>
    public static Preset? Load(string id, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(id)) { problem = "预设 id 为空"; return null; }

        var dir = Path.Combine(PresetsDir, id);
        var file = Path.Combine(dir, "preset.json");
        if (!File.Exists(file)) { problem = $"找不到预设：{file}"; return null; }

        try
        {
            var info = List(null).FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            if (info is null) { problem = $"预设 {id} 未通过列表校验"; return null; }

            using var doc = JsonDocument.Parse(File.ReadAllText(file), DocOpts);
            var root = doc.RootElement;

            var sounds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("sounds", out var s) && s.ValueKind == JsonValueKind.Object)
                foreach (var p in s.EnumerateObject())
                {
                    var raw = p.Value.GetString();
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    sounds[p.Name] = ResolveAudio(dir, raw!);
                }

            var rules = new List<MonitorRule>();
            if (root.TryGetProperty("rules", out var r) && r.ValueKind == JsonValueKind.Array)
                foreach (var e in r.EnumerateArray())
                {
                    var rule = e.Deserialize<MonitorRule>(JsonOpts);
                    if (rule is not null) rules.Add(rule);
                }

            return new Preset { Info = info, Sounds = sounds, Rules = rules };
        }
        catch (Exception ex)
        {
            problem = $"{ex.GetType().Name} {ex.Message}";
            return null;
        }
    }

    /// <summary>把 <c>sounds</c> 里的值解析成绝对路径（规则见类文档）。</summary>
    public static string ResolveAudio(string presetDir, string raw)
    {
        if (raw.StartsWith("sounds/", StringComparison.OrdinalIgnoreCase)
            || raw.StartsWith("sounds\\", StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(Path.Combine(presetDir, raw));

        if (Path.IsPathRooted(raw)) return raw;

        return Path.Combine(SoundsDir, raw);
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
