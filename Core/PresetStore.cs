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
/// ① 纯文件名（<c>xxx.wav</c>）→ 在 <c>%LOCALAPPDATA%/BSH/sounds/</c> 下找（现有那 39 条的存放处）；
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

    /// <summary>
    /// 预设自带音频的存放根目录：<c>%LOCALAPPDATA%/BSH/presets/&lt;id&gt;/sounds/</c>。
    /// <para>⚠️ <b>为什么不放仓库里的 <c>profiles/presets/&lt;id&gt;/sounds/</c></b>：那是<b>会话工作目录</b>，
    /// 受 ACL 沙箱约束时以用户身份运行的程序（含系统音频链路）可能读不到 —— 这正是
    /// <c>tools\install-voice.ps1</c> 把音频装到 <c>%LOCALAPPDATA%</c> 的同一条理由。
    /// 预设<b>定义</b>（preset.json）留在仓库里随代码走，音频则必须落在工作目录之外。</para>
    /// </summary>
    public static string PresetAudioDir(string presetId) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "BSH", "presets", presetId, "sounds");

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
                    sounds[p.Name] = ResolveAudio(id, raw!);
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

    /// <summary>应用预设的结果。</summary>
    /// <param name="Written">实际写入的槽数。</param>
    /// <param name="SkippedByUser">按跳过偏好跳过的槽数。</param>
    /// <param name="Missing">音频缺失而跳过的槽数。</param>
    /// <param name="SkippedEvents">跳过的槽名（含用户跳过与音频缺失两类）。</param>
    /// <param name="MissingEvents">其中因音频缺失而跳过的。</param>
    /// <param name="SnapshotPath">应用前自动快照的落盘路径。</param>
    public sealed record PresetApplyResult(
        int Written, int SkippedByUser, int Missing,
        IReadOnlyList<string> SkippedEvents, IReadOnlyList<string> MissingEvents,
        string SnapshotPath);

    /// <summary>
    /// 把一个预设应用到系统：<b>先自动快照，再逐槽写注册表</b>。
    /// <para>⚠️ <b>停监听引擎是调用方的责任</b> —— 顺序必须与「Windows 原样」一致（先停引擎再写注册表），
    /// 而「什么时候该停」属于流程决策，不属于仓库层。界面与 CLI 都按这个约定调用。</para>
    /// <para><b>音频缺失的槽跳过并计数，不整批失败</b>：预设只存映射，音频可能没跟着分发。</para>
    /// </summary>
    /// <param name="preset">要应用的预设。</param>
    /// <param name="skipEvents">不该被改写的槽（来自 personal/profile.json 的 skipFreq + skipEvents）。</param>
    /// <param name="snapshotPath">自动快照的落盘路径。</param>
    public static PresetApplyResult Apply(Preset preset, IEnumerable<string>? skipEvents, string snapshotPath)
    {
        var skip = new HashSet<string>(skipEvents ?? [], StringComparer.OrdinalIgnoreCase);
        var skippedByUser = new List<string>();
        var missing = new List<string>();
        var todo = new List<KeyValuePair<string, string>>();

        foreach (var kv in preset.Sounds)
        {
            if (skip.Contains(kv.Key)) { skippedByUser.Add(kv.Key); continue; }
            if (!File.Exists(kv.Value)) { missing.Add(kv.Key); continue; }
            todo.Add(kv);
        }

        // 顺序不能反：先快照，后写注册表
        SchemeSnapshot.Capture($"应用预设「{preset.Info.Name}」前自动快照").Save(snapshotPath);
        foreach (var kv in todo) SoundScheme.SetSound(kv.Key, kv.Value);

        var allSkipped = skippedByUser.Concat(missing).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        return new PresetApplyResult(todo.Count, skippedByUser.Count, missing.Count, allSkipped, missing, snapshotPath);
    }

    // ───────────────────────────── 用户可增删改（内置预设只读） ─────────────────────────────

    /// <summary>
    /// 新建一个<b>空预设</b>（只有名字，<c>sounds</c>/<c>rules</c> 都空）—— 用户以后往里填。
    /// <para>默认名由调用方给（界面上是「个性化设置」）；id 与名字无关，用序号自动生成，避免中文/空格当目录名。</para>
    /// </summary>
    /// <param name="name">预设显示名。</param>
    /// <param name="problem">失败原因。</param>
    /// <returns>新预设 id；失败返回 <c>null</c>。</returns>
    public static string? Create(string name, out string? problem)
    {
        problem = null;
        var clean = (name ?? "").Trim();
        if (clean.Length == 0) { problem = "名字不能为空"; return null; }

        try
        {
            Directory.CreateDirectory(PresetsDir);
            // 找一个没被占用的序号 id
            string id;
            int i = 1;
            do { id = $"my-{i++}"; } while (Directory.Exists(Path.Combine(PresetsDir, id)));

            var dir = Path.Combine(PresetsDir, id);
            Directory.CreateDirectory(dir);
            Directory.CreateDirectory(Path.Combine(dir, "sounds"));

            var preset = new Dictionary<string, object?>
            {
                ["id"] = id,
                ["name"] = clean,
                ["description"] = "用户自建预设（空壳，尚未填入声音映射）",
                ["builtin"] = false,
                ["note"] = "sounds 为空 = 应用它不会改任何槽。要往里加声音，可把 %LOCALAPPDATA%/BSH/presets//"
                           + id + "/sounds/ 下的文件名按「事件名 -> 文件名」写进 sounds；"
                           + "也可以用「替换音效…」改好系统槽后，用 --capture-preset 捕获（未实现）。",
                ["sounds"] = new Dictionary<string, string>(),
                ["rules"] = Array.Empty<object>(),
            };
            var json = System.Text.Json.JsonSerializer.Serialize(preset,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(dir, "preset.json"), json);
            return id;
        }
        catch (Exception ex)
        {
            problem = $"{ex.GetType().Name} {ex.Message}";
            return null;
        }
    }

    /// <summary>改预设的显示名（内置预设也允许改：改的只是本地这份 json 的 name 字段）。</summary>
    /// <param name="id">预设 id。</param>
    /// <param name="newName">新名字。</param>
    /// <param name="problem">失败原因。</param>
    public static bool Rename(string id, string newName, out string? problem)
    {
        problem = null;
        var clean = (newName ?? "").Trim();
        if (clean.Length == 0) { problem = "名字不能为空"; return false; }

        var file = Path.Combine(PresetsDir, id, "preset.json");
        if (!File.Exists(file)) { problem = $"找不到预设：{id}"; return false; }

        try
        {
            var text = File.ReadAllText(file);
            using var doc = JsonDocument.Parse(text, DocOpts);
            var dict = new Dictionary<string, JsonElement>();
            foreach (var p in doc.RootElement.EnumerateObject()) dict[p.Name] = p.Value;

            var outDict = new Dictionary<string, object?>();
            bool replaced = false;
            foreach (var kv in dict)
            {
                if (kv.Key.Equals("name", StringComparison.OrdinalIgnoreCase)) { outDict[kv.Key] = clean; replaced = true; }
                else outDict[kv.Key] = kv.Value;
            }
            if (!replaced) outDict["name"] = clean;

            File.WriteAllText(file, JsonSerializer.Serialize(outDict,
                new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception ex)
        {
            problem = $"{ex.GetType().Name} {ex.Message}";
            return false;
        }
    }

    /// <summary>删除一个<b>用户自建</b>预设（含它的 sounds 目录）。内置预设拒绝删除。</summary>
    /// <param name="id">预设 id。</param>
    /// <param name="problem">失败原因。</param>
    public static bool Delete(string id, out string? problem)
    {
        problem = null;
        var info = List().FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        if (info is null) { problem = $"找不到预设：{id}"; return false; }
        if (info.Builtin) { problem = "内置预设不能删除（那是软件自带的那套）"; return false; }

        try
        {
            var dir = Path.Combine(PresetsDir, id);
            Directory.Delete(dir, recursive: true);

            // 装机目录里那份音频也一并清掉（那是应用时复制/生成的，留着没人用）
            var audio = PresetAudioDir(id);
            if (Directory.Exists(audio)) Directory.Delete(audio, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            problem = $"{ex.GetType().Name} {ex.Message}";
            return false;
        }
    }

    /// <summary>把 <c>sounds</c> 里的值解析成绝对路径（规则见类文档）。</summary>
    /// <param name="presetId">预设 id（<c>sounds/</c> 前缀要按它定位到 %LOCALAPPDATA% 下）。</param>
    /// <param name="raw">preset.json 里写的值。</param>
    public static string ResolveAudio(string presetId, string raw)
    {
        if (raw.StartsWith("sounds/", StringComparison.OrdinalIgnoreCase)
            || raw.StartsWith("sounds\\", StringComparison.OrdinalIgnoreCase))
            // Path.GetFileName 同时认 / 与 \ 两种分隔符，省得在 C# 源码里写反斜杠字面量
            return Path.Combine(PresetAudioDir(presetId), Path.GetFileName(raw));

        if (Path.IsPathRooted(raw)) return raw;

        return Path.Combine(SoundsDir, raw);
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
