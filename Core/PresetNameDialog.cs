using BSH.Core;

namespace BSH;

/// <summary>
/// 一个只问「名字」的小对话框（新建 / 改名预设用）。
/// <para>不引 Microsoft.VisualBasic 的 InputBox：那玩意是浅色的、和深色主题打架，而且它不能设默认值之外的任何东西。</para>
/// </summary>
public sealed class PresetNameDialog : Form
{
    private readonly TextBox _box = new();

    /// <summary>用户确定后输入的名字；取消则为 <c>null</c>。</summary>
    public string? Value { get; private set; }

    /// <summary>建一个问名字的对话框。</summary>
    /// <param name="title">窗口标题。</param>
    /// <param name="prompt">说明文字。</param>
    /// <param name="defaultText">预填的名字。</param>
    public PresetNameDialog(string title, string prompt, string defaultText)
    {
        Text = title;
        ClientSize = new Size(420, 150);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9F);

        var label = new Label { Text = prompt, AutoSize = false, Dock = DockStyle.Top, Height = 34,
                                TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 10, 0) };
        _box.Text = defaultText;
        _box.Dock = DockStyle.Top;
        _box.Margin = new Padding(10);
        _box.SelectAll();

        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, AutoSize = true, Padding = new Padding(8, 3, 8, 3) };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(8, 3, 8, 3) };
        ok.Click += (_, _) => { Value = _box.Text.Trim(); };
        var row = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft,
                                        AutoSize = true, Padding = new Padding(8) };
        row.Controls.Add(cancel);
        row.Controls.Add(ok);

        var pad = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 4, 10, 4) };
        pad.Controls.Add(_box);
        pad.Controls.Add(row);   // 先加分页行：Dock 布局下后加的会贴到上面，所以按钮行要先加
        pad.Controls.Add(label);

        Controls.Add(pad);
        AcceptButton = ok;
        CancelButton = cancel;
        AppTheme.ApplyTo(this);
    }
}
