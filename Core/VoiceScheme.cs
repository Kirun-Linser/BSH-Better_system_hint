using System.Text.Json;

namespace BSH.Core;

/// <summary>
/// 语音方案：事件名 → 自制语音 wav 的绝对路径。
/// 由 <c>tools/install-voice.ps1</c> 生成到
/// <c>%LOCALAPPDATA%\BSH\sounds\voice-scheme.json</c>（放在工作目录之外，
/// 因为工作目录受 ACL 限制时系统音频可能读不到文件）。
/// </summary>
public sealed class VoiceScheme
{
    public string GeneratedAt { get; set; } = "";
    public string Note { get; set; } = "";
    public string Voice { get; set; } = "";
    public Dictionary<string, string> Sounds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>默认方案文件路径。</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BSH", "sounds", "voice-scheme.json");

    /// <summary>读取方案；文件不存在或解析失败返回 null。</summary>
    public static VoiceScheme? TryLoad(string? path = null)
    {
        var p = string.IsNullOrWhiteSpace(path) ? DefaultPath : path;
        if (!File.Exists(p)) return null;
        try
        {
            return JsonSerializer.Deserialize<VoiceScheme>(File.ReadAllText(p), JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>能落地的条目（方案里有、磁盘上文件也真在）。</summary>
    public List<(string Event, string Wav)> Resolve() =>
        Sounds
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value) && File.Exists(kv.Value))
            .Select(kv => (kv.Key, kv.Value))
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>方案里列了、但文件已丢失的事件（用于提示，不阻塞）。</summary>
    public List<string> MissingFiles() =>
        Sounds
            .Where(kv => string.IsNullOrWhiteSpace(kv.Value) || !File.Exists(kv.Value))
            .Select(kv => kv.Key)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// 反序列化选项。
    /// <para>
    /// ⚠️ 必须开 <see cref="JsonSerializerOptions.PropertyNameCaseInsensitive"/>：
    /// 方案文件里写的是小写驼峰 <c>"sounds"</c>（由 <c>tools/install-voice.ps1</c> 的 ConvertTo-Json 产出），
    /// 而这里的属性叫 <c>Sounds</c>，System.Text.Json 默认区分大小写 ——
    /// 不开这一项会"文件读到了、Sounds 却是空字典"，表现为方案 0 项（2026-10-10 修）。
    /// </para>
    /// </summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}
