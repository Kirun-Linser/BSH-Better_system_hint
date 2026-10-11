using System.Diagnostics;
using BSH.Core;
using BSH.Core.Monitoring;

namespace BSH;

/// <summary>
/// 「应用提示音」对话框：给特定应用配「启动 / 退出」时的提示音。
/// <para>
/// <b>为什么单独一个窗</b>：主面板那张表是「系统的 39 个事件槽」，而这里是「监听规则」——
/// 两者是不同的东西（前者是注册表里的事件音，后者是 BSH 自己监听并补播的），放一张表里会混淆。
/// </para>
/// <para>
/// <b>只做打开与关闭</b>（用户 2026-10-11 指定）：规则固定为 <see cref="MonitorEventKind.ProcessStartStop"/>，
/// <c>app</c> 管哪个程序、<c>match</c> 只管 start / stop。其它监听类别暂不做界面。
/// </para>
/// <para>保存后若「个性化设置」正在生效，会顺手把引擎按新规则重装（先停后起），不需要用户再点一次方案。</para>
/// </summary>
public sealed class AppSoundForm : Form
{
    private readonly DataGridView _list = new();
    private readonly ComboBox _procBox = new();
    private readonly ComboBox _actionBox = new();
    private readonly ComboBox _soundBox = new();
    private readonly Label _hint = new();
    private readonly List<MonitorRule> _rules = [];
    private readonly List<string> _soundNames = [];

    /// <summary>建一个空的对话框（内容在 OnLoad 里装）。</summary>
    public AppSoundForm()
    {
        Text = "应用提示音 · 启动 / 退出";
        ClientSize = new Size(760, 460);
        MinimumSize = new Size(620, 380);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9F);
        BuildUi();
        AppTheme.ApplyTo(this);
    }

    private void BuildUi()
    {
        _list.Dock = DockStyle.Fill;
        _list.ReadOnly = true;
        _list.AllowUserToAddRows = false;
        _list.AllowUserToDeleteRows = false;
        _list.AllowUserToResizeRows = false;
        _list.RowHeadersVisible = false;
        _list.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _list.MultiSelect = false;
        _list.BorderStyle = BorderStyle.None;
        _list.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        _list.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _list.EnableHeadersVisualStyles = false;   // 与主面板同理：不关掉，表头色设了也看不到
        _list.RowTemplate.Height = 26;
        _list.Columns.Add(Col("应用", 26));
        _list.Columns.Add(Col("动作", 14));
        _list.Columns.Add(Col("音效", 46));
        _list.Columns.Add(Col("状态", 14));

        _procBox.DropDownStyle = ComboBoxStyle.DropDown;   // 允许手输：程序没在跑时也能配
        _procBox.Width = 200;
        _actionBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _actionBox.Items.AddRange(["启动", "退出"]);
        _actionBox.SelectedIndex = 0;
        _actionBox.Width = 80;
        _soundBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _soundBox.Width = 240;

        var add = new Button { Text = "添加", AutoSize = true, Padding = new Padding(6, 3, 6, 3) };
        add.Click += (_, _) => AddRule();
        var toggle = new Button { Text = "启用 / 停用", AutoSize = true, Padding = new Padding(6, 3, 6, 3) };
        toggle.Click += (_, _) => ToggleSelected();
        var del = new Button { Text = "删除", AutoSize = true, Padding = new Padding(6, 3, 6, 3) };
        del.Click += (_, _) => DeleteSelected();

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(6, 6, 6, 6) };
        bottom.Controls.Add(new Label { Text = "程序", AutoSize = true, Padding = new Padding(0, 6, 2, 0) });
        bottom.Controls.Add(_procBox);
        bottom.Controls.Add(new Label { Text = "动作", AutoSize = true, Padding = new Padding(8, 6, 2, 0) });
        bottom.Controls.Add(_actionBox);
        bottom.Controls.Add(new Label { Text = "音效", AutoSize = true, Padding = new Padding(8, 6, 2, 0) });
        bottom.Controls.Add(_soundBox);
        bottom.Controls.Add(add);
        bottom.Controls.Add(toggle);
        bottom.Controls.Add(del);

        _hint.Dock = DockStyle.Fill;
        _hint.AutoSize = false;
        _hint.Padding = new Padding(8, 0, 8, 0);
        _hint.TextAlign = ContentAlignment.MiddleLeft;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.Controls.Add(_list, 0, 0);
        root.Controls.Add(bottom, 0, 1);
        root.Controls.Add(_hint, 0, 2);
        Controls.Add(root);

        _list.DoubleClick += (_, _) => ToggleSelected();
    }

    private static DataGridViewTextBoxColumn Col(string h, int w) => new()
    {
        HeaderText = h, FillWeight = w, ReadOnly = true,
        SortMode = DataGridViewColumnSortMode.NotSortable,
    };

    /// <inheritdoc/>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        LoadRules();
        LoadProcesses();
        LoadSounds();
    }

    /// <summary>把 rules.json 里与进程启停有关的规则读进来（含手写的、没有 app 字段的老规则）。</summary>
    private void LoadRules()
    {
        _rules.Clear();
        _rules.AddRange(RuleStore.Load(out _).Where(r => r.Kind == MonitorEventKind.ProcessStartStop));
        Render();
    }

    private void LoadProcesses()
    {
        try
        {
            var names = Process.GetProcesses()
                .Select(p => { try { return p.ProcessName + ".exe"; } catch { return ""; } })
                .Where(n => n.Length > 1)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _procBox.Items.Clear();
            foreach (var n in names) _procBox.Items.Add(n);
            _hint.Text = $"正在运行 {names.Count} 个进程（可下拉选，也可以手输没在跑的程序名）";
        }
        catch (Exception ex)
        {
            _hint.Text = $"读进程列表失败：{ex.Message}（仍可手输程序名）";
        }
    }

    /// <summary>音效候选 = 语音方案里的那些（用事件名当 speakKey，播的就是那条语音）。</summary>
    private void LoadSounds()
    {
        _soundNames.Clear();
        _soundBox.Items.Clear();
        var scheme = VoiceScheme.TryLoad();
        if (scheme is null)
        {
            _hint.Text = "找不到语音方案（%LOCALAPPDATA%/BSH/sounds/voice-scheme.json）—— 先在面板里应用一次「个性化设置」。";
            return;
        }
        // Resolve() 已经过滤掉「文件不存在」的项，所以下拉里都是真能播的
        foreach (var (ev, wav) in scheme.Resolve())
        {
            _soundNames.Add(ev);
            _soundBox.Items.Add($"{ev}　（{Path.GetFileName(wav)}）");
        }
        if (_soundBox.Items.Count > 0) _soundBox.SelectedIndex = 0;
    }

    private void Render()
    {
        _list.Rows.Clear();
        foreach (var r in _rules)
        {
            // 老式规则（没有 app 字段、match 里塞了一长串 start X,start Y…）在「应用」列会撑爆格子，
            // 所以只标一个来源；要看全文可以去 rules.json —— 这里不是规则编辑器，是「应用提示音」列表。
            var i = _list.Rows.Add(
                string.IsNullOrWhiteSpace(r.App) ? "（旧式规则 · 见 rules.json）" : r.App,
                DirectionText(r.Match),
                string.IsNullOrWhiteSpace(r.SpeakKey) ? (r.Sound ?? "（不出声）") : $"语音：{r.SpeakKey}",
                r.Enabled ? "启用" : "停用");
            _list.Rows[i].Tag = r;
        }
        if (_rules.Count == 0)
            _hint.Text = "还没有应用提示音规则。上面选好「程序 / 动作 / 音效」后点「添加」。";
    }

    private static string DirectionText(string? match) => match switch
    {
        "start" => "启动",
        "stop" => "退出",
        null or "" => "启动或退出",
        _ => match.Contains("stop", StringComparison.OrdinalIgnoreCase) ? "退出" : "启动",
    };

    private void AddRule()
    {
        var proc = (_procBox.Text ?? "").Trim();
        if (proc.Length == 0) { Warn("先选或输入一个程序名（例如 chrome.exe）。"); return; }
        if (!proc.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            // 不强改用户输入，但要说清后果：app 是整词匹配，少 .exe 就永远不命中
            var ok = MessageBox.Show(this,
                $"「{proc}」不像完整的进程名（通常以 .exe 结尾）。{Environment.NewLine}"
                + "app 是整词匹配，写不全会让这条规则永不命中。仍要添加吗？",
                "应用提示音", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (ok != DialogResult.OK) return;
        }
        if (_soundBox.SelectedIndex < 0) { Warn("先选一个音效。"); return; }

        var action = _actionBox.SelectedIndex == 1 ? "stop" : "start";
        var key = _soundNames[_soundBox.SelectedIndex];
        if (_rules.Any(r => string.Equals(r.App, proc, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(r.Match, action, StringComparison.OrdinalIgnoreCase)))
        {
            Warn($"已经有一条「{proc} / {DirectionText(action)}」了。"); return;
        }

        _rules.Add(new MonitorRule
        {
            Id = ("app-" + action + "-" + proc)
                 .Replace(".exe", "", StringComparison.OrdinalIgnoreCase)
                 .Replace(" ", "").ToLowerInvariant(),
            Name = $"{proc} {DirectionText(action)}时播「{key}」",
            Enabled = true,
            Kind = MonitorEventKind.ProcessStartStop,
            App = proc,
            Match = action,
            SpeakKey = key,
            CooldownMs = 1500,
        });
        Save();
    }

    private void ToggleSelected()
    {
        if (_list.SelectedRows.Count == 0) { Warn("先在上面的表里选一条。"); return; }
        var r = (MonitorRule)_list.SelectedRows[0].Tag!;
        r.Enabled = !r.Enabled;
        Save();
    }

    private void DeleteSelected()
    {
        if (_list.SelectedRows.Count == 0) { Warn("先在上面的表里选一条。"); return; }
        var r = (MonitorRule)_list.SelectedRows[0].Tag!;
        if (MessageBox.Show(this, $"删除「{r.Name}」？", "应用提示音",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        _rules.Remove(r);
        Save();
    }

    /// <summary>写回 rules.json；若「个性化设置」正在生效，顺手把引擎按新规则重装（先停后起）。</summary>
    private void Save()
    {
        try
        {
            RuleStore.Save(_rules);
            Render();
            int n = _rules.Count(r => r.Enabled);
            _hint.Text = $"已保存：共 {_rules.Count} 条，其中启用 {n} 条。";

            if (MonitorHost.ActiveProfileId == ProfileStore.PersonalId)
            {
                MonitorHost.Stop();
                int started = MonitorHost.StartFromRules();
                _hint.Text += $"　引擎已按新规则重装（监听器 {started} 个）。";
            }
            else
            {
                _hint.Text += "　（当前不在「个性化设置」，改动会在下次应用它时生效）";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存失败：{ex.Message}", "应用提示音",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void Warn(string msg) =>
        MessageBox.Show(this, msg, "应用提示音", MessageBoxButtons.OK, MessageBoxIcon.Information);
}
