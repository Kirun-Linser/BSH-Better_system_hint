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
/// </summary>
public static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "BSH";

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

    /// <summary>登记自启；返回实际写入的命令行，失败返回 <c>null</c>。</summary>
    public static string? Enable()
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

    /// <summary>取消自启（值本来就不存在也算成功）。</summary>
    public static bool Disable()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            k?.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch { return false; }
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
