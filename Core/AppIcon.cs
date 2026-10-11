using System.Drawing;
using System.Reflection;

namespace BSH.Core;

/// <summary>
/// 应用图标（两个叠加的窗口，用户 2026-10-11 提供）。
///
/// <para>图标以<b>嵌入资源</b>随程序集一起分发，运行时从流加载 —— 不依赖任何外部文件路径，
/// 也就不会因为「文件被移动/被 ACL 挡住」而变成白板图标。</para>
/// <para>exe 自身在资源管理器里显示的图标由 <c>BSH.csproj</c> 的 <c>ApplicationIcon</c> 负责；
/// 本类负责托盘与窗体（<see cref="System.Windows.Forms.NotifyIcon"/>、<c>Form.Icon</c>）。</para>
/// </summary>
public static class AppIcon
{
    /// <summary>嵌入资源名（由 csproj 的 LogicalName 指定，与文件路径解耦）。</summary>
    private const string ResourceName = "BSH.AppIcon.ico";

    private static Icon? _cached;

    /// <summary>
    /// 取应用图标。资源缺失或损坏时回退到系统默认图标 —— <b>绝不抛异常</b>：
    /// 图标属于外观，不该让整个程序起不来。
    /// </summary>
    public static Icon Get()
    {
        if (_cached is not null) return _cached;

        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            _cached = s is null ? SystemIcons.Application : new Icon(s);
        }
        catch
        {
            _cached = SystemIcons.Application;
        }

        return _cached;
    }
}
