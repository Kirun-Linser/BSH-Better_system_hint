using System.Runtime.InteropServices;

namespace BSH.Core.Monitoring;

/// <summary>
/// 剪贴板监听：<c>AddClipboardFormatListener</c> + 隐藏顶层窗口接收 <c>WM_CLIPBOARDUPDATE</c>。
/// <para>
/// 事件明细是内容类型（<c>Text</c> / <c>Image</c> / <c>Files</c>，多类型用 <c>+</c> 连接，如 <c>Text+Files</c>）；
/// 读不出类型时上报 <c>Unknown</c>（剪贴板同一时刻只允许一个进程打开，被占用是常态，不是故障）。
/// </para>
/// <para>⚠️ 只报类型、<b>不读内容</b>：内容不进日志、不进事件明细，避免把密码等敏感数据带到规则表与报告里。</para>
/// </summary>
public sealed class ClipboardMonitor : MonitorSourceBase
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    private HiddenMessageWindow? _window;

    /// <inheritdoc />
    public override MonitorEventKind Kind => MonitorEventKind.Clipboard;

    /// <inheritdoc />
    public override string Mechanism => "AddClipboardFormatListener + WM_CLIPBOARDUPDATE";

    /// <inheritdoc />
    protected override void OnStart()
    {
        var window = new HiddenMessageWindow { OnMessage = HandleMessage };

        if (!window.Start())
        {
            throw new InvalidOperationException(window.LastError ?? "隐藏消息窗口创建失败");
        }

        if (!AddClipboardFormatListener(window.Handle))
        {
            int err = Marshal.GetLastWin32Error();
            window.Dispose();
            throw new InvalidOperationException($"AddClipboardFormatListener 失败（Win32 {err}）");
        }

        _window = window;
    }

    /// <inheritdoc />
    protected override void OnStop()
    {
        if (_window is not null && _window.Handle != IntPtr.Zero)
        {
            RemoveClipboardFormatListener(_window.Handle);
        }

        _window?.Dispose();
        _window = null;
    }

    /// <summary>窗口过程（窗口线程）：只认剪贴板更新。</summary>
    private bool HandleMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != WM_CLIPBOARDUPDATE) return false;

        Publish(ReadKinds());
        return true;
    }

    /// <summary>
    /// 判断剪贴板里当前有哪些类型。窗口线程是 STA，满足剪贴板 API 的单元要求；
    /// 剪贴板被别的进程占用时 <see cref="System.Windows.Forms.Clipboard"/> 会抛，重试两次后报 <c>Unknown</c>。
    /// </summary>
    private static string ReadKinds()
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var kinds = new List<string>(3);
                if (System.Windows.Forms.Clipboard.ContainsText()) kinds.Add("Text");
                if (System.Windows.Forms.Clipboard.ContainsImage()) kinds.Add("Image");
                if (System.Windows.Forms.Clipboard.ContainsFileDropList()) kinds.Add("Files");
                return kinds.Count > 0 ? string.Join("+", kinds) : "Unknown";
            }
            catch when (attempt < 2)
            {
                // 剪贴板被占用：短暂等待后重试（总等待不超过 80 ms，不会拖住窗口线程）
                Thread.Sleep(40);
            }
            catch
            {
                return "Unknown";
            }
        }
    }

    // ── Win32 ────────────────────────────────────────────────────────────────

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
