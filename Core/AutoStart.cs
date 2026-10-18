using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace BSH.Core;

/// <summary>
/// 开机自启动开关：只写 <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> ——
/// 用户级、不需要管理员、随时可撤销。
/// <para>
/// <b>为什么要自启</b>：见 <see cref="ProfileKind.Takeover"/> —— 「BSH 接管」模式下那 18 个提示音
/// 由 BSH 判断，BSH 不在就等于没声音。**「开机自启」+「退出还原」这一对**才让接管在体验上自洽：
/// 开机 → BSH 起来接管 → 用的时候按规则响 → 退出 BSH → 系统提示音自动还回去。
/// </para>
/// <para>
/// <b>默认开启（2026-10-18 用户定）</b>：首次运行（还没有 <c>autostart.json</c> 标记时）自动登记自启，
/// 并写一份标记 —— 之后即使用户在界面 / 托盘里取消，也<b>不会</b>在下次启动时被重新打开。
/// 标记位置：<c>%LOCALAPPDATA%\BSH\autostart.json</c>。
/// </para>
/// </summary>
public static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "BSH";

    /// <summary>自启状态变化后触发（面板复选框与托盘勾选项靠它双向同步）。</summary>
    public static event Action? Changed;

    /// <summary>标记文件：记录"默认值已经应用过 / 用户已经表过态"，避免每次启动反复改写。</summary>
    private static string MarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BSH", "autostart.json");

    /// <summary>用户是否已经在界面 / 托盘里明确表态（勾选或取消过）。</summary>
    public static bool UserChosen => ReadMarker()?.UserChosen ?? false;

    /// <summary>首次运行的默认值是否已经应用过（标记文件存在即视为应用过）。</summary>
    public static bool DefaultApplied => File.Exists(MarkerPath);

    /// <summary>当前是否已登记自启（读注册表；任何异常都当"未启用"）。</summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return k?.GetValue(ValueName) is string v && v.Length > 0;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// 首次运行时应用"默认开启自启"（幂等）：
    /// 标记文件不存在 → 登记自启 + 写标记；之后无论用户在界面里怎么选，都不会再自动打开。
    /// <para>返回 <c>true</c> = 本次真的新登记了自启；注册表写不进去时返回 <c>false</c> 且<b>不写标记</b>（下次启动再试）。</para>
    /// </summary>
    public static bool EnsureDefault()
    {
        if (File.Exists(MarkerPath)) return false;      // 已经处理过（默认值或用户选择）
        if (IsEnabled)                                   // 自启本来就在（老版本用户 / 手工登记过）
        {
            // 顺手把命令刷新成"当前这份程序"：从 dotnet dll 换成发布版 exe、或程序换了目录时，
            // 旧命令会指向一个已经不存在的位置 —— 刷新是幂等的，不改变"用户要自启"这个意愿。
            EnableCore();
            TryWriteMarker(userChosen: false);
            return false;
        }

        var cmd = EnableCore();
        if (cmd is null) return false;                   // 注册表写不进去：不写标记，下次启动重试
        TryWriteMarker(userChosen: false);
        Changed?.Invoke();
        return true;
    }

    /// <summary>登记自启；返回实际写入的命令行，失败返回 <c>null</c>。</summary>
    public static string? Enable()
    {
        var cmd = EnableCore();
        if (cmd is null) return null;
        TryWriteMarker(userChosen: true);
        Changed?.Invoke();
        return cmd;
    }

    /// <summary>取消自启（值本来就不存在也算成功）。</summary>
    public static bool Disable()
    {
        if (!DisableCore()) return false;
        // 标记写失败只影响"下次启动会不会重新默认开启"（纯本机记忆），不该让取消动作显示失败
        TryWriteMarker(userChosen: true);
        Changed?.Invoke();
        return true;
    }

    /// <summary>当前登记的命令行（没登记返回 <c>null</c>），用于界面显示"启的是什么"。</summary>
    public static string? CurrentCommand
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return k?.GetValue(ValueName) as string;
            }
            catch { return null; }
        }
    }

    /// <summary>给界面/CLI 的一行状态描述。</summary>
    public static string Describe() =>
        IsEnabled
            ? $"已开启（{CurrentCommand ?? "(命令为空)"}）"
            : DefaultApplied
                ? "已关闭（用户选择）"
                : "未开启（首次运行时会自动开启）";

    // ── 内部实现 ─────────────────────────────────────────────────────────────

    private static string? EnableCore()
    {
        try
        {
            var cmd = BuildCommand();
            using var k = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (k is null) return null;
            k.SetValue(ValueName, cmd, RegistryValueKind.String);
            return cmd;
        }
        catch { return null; }
    }

    private static bool DisableCore()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            k?.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch { return false; }
    }

    /// <summary>标记文件的落盘形态（只记两个布尔，够判断"默认值是否已应用 / 用户是否表过态"）。</summary>
    private sealed record Marker(
        [property: JsonPropertyName("userChosen")] bool UserChosen,
        [property: JsonPropertyName("defaultApplied")] bool DefaultApplied);

    private static Marker? ReadMarker()
    {
        try
        {
            if (!File.Exists(MarkerPath)) return null;
            return JsonSerializer.Deserialize<Marker>(File.ReadAllText(MarkerPath));
        }
        catch
        {
            return null;      // 文件坏了/读不了：当作没标记（下次启动会补一次默认，不影响功能）
        }
    }

    private static void TryWriteMarker(bool userChosen)
    {
        try
        {
            var dir = Path.GetDirectoryName(MarkerPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(MarkerPath, JsonSerializer.Serialize(new Marker(userChosen, DefaultApplied: true)));
        }
        catch
        {
            // 标记只是"本机记忆"：写不进去不影响自启本身
        }
    }

    /// <summary>
    /// 拼自启命令行。两种运行形态都要照顾：
    /// <list type="bullet">
    /// <item>自包含 exe（发布后的常态）→ 直接写它自己的路径；</item>
    /// <item><c>dotnet BSH.dll</c>（开发 / 验收形态）→ 必须把 dll 路径一并写上，
    /// 否则自启会跑出一个裸 <c>dotnet.exe</c>（无参数，什么都不会发生）。</item>
    /// </list>
    /// </summary>
    private static string BuildCommand()
    {
        var exe = Environment.ProcessPath ?? Application.ExecutablePath;
        var asm = Environment.GetCommandLineArgs().FirstOrDefault();
        bool isDotnet = exe is not null
            && Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        if (isDotnet && !string.IsNullOrEmpty(asm)
            && asm.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return $"\"{exe}\" \"{asm}\"";
        return $"\"{exe}\"";
    }
}
