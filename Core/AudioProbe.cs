using System.Runtime.InteropServices;

namespace BSH.Core;

/// <summary>
/// 试听。直接走 Win32 <c>PlaySound</c> —— 用文件名播放指定 wav，
/// 或用别名（如 <c>SystemAsterisk</c>）让系统自己查注册表播当前方案的声音。
/// </summary>
public static class AudioProbe
{
    private const uint SND_ASYNC = 0x0001;
    private const uint SND_NODEFAULT = 0x0002;
    private const uint SND_PURGE = 0x0040;
    private const uint SND_ALIAS = 0x00010000;
    private const uint SND_FILENAME = 0x00020000;

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PlaySound(string? pszSound, IntPtr hmod, uint fdwSound);

    /// <summary>播放一个 wav 文件（异步，不阻塞界面）。文件不存在返回 false。</summary>
    public static bool PlayFile(string? wavPath)
    {
        if (string.IsNullOrWhiteSpace(wavPath) || !File.Exists(wavPath)) return false;
        return PlaySound(wavPath, IntPtr.Zero, SND_ASYNC | SND_FILENAME | SND_NODEFAULT);
    }

    /// <summary>按系统别名播放（走当前注册表设置 —— 用来验证"改完是否立即生效"）。</summary>
    public static bool PlayAlias(string eventName) =>
        PlaySound(eventName, IntPtr.Zero, SND_ASYNC | SND_ALIAS | SND_NODEFAULT);

    /// <summary>停止当前正在播放的试听。</summary>
    public static void Stop() => PlaySound(null, IntPtr.Zero, SND_PURGE);
}
