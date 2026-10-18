using System.Diagnostics;
using BSH.Core;
using BSH.Core.Monitoring;

namespace BSH;

/// <summary>
/// 「监听规则」可视化编辑器（2026-10-18 用户要求：规则表不再只能手改 JSON）。
/// <para>
/// 数据源/目的都是 <c>profiles\personal\rules.json</c>（个人配置，不入公开仓库），
/// 读写统一走 <see cref="RuleStore"/>，所以文件格式、容错口径与命令行 / 引擎完全一致。
/// </para>
/// <para>
/// 设计口径：
/// <list type="bullet">
/// <item>表格只读，改字段走「编辑…」对话框；<b>「启用」列可以直接勾</b>（最高频的开关不该多一步）。</item>
/// <item><b>顺序有意义</b>：引擎对同一事件只兑现第一条命中的规则，所以提供上移 / 下移。</item>
/// <item><b>保存前自动备份</b>现有 rules.json 到 <c>%APPDATA%\BSH\snapshots\rules-backup-*.json</c>。</item>
/// <item>保存后若监听引擎正在跑，询问是否<b>立即按新规则重装钩子</b>（否则新规则要等下次切方案才生效）。</item>
/// </list>
/// </para>
/// </summary>
public sealed class RulesForm : Form
{
    private readonly DataGridView _grid = new();
    private readonly Label _hint = new();

    private readonly Button _btnEdit;
    private readonly Button _btnDelete;
    private readonly Button _btnUp;
    private readonly Button _btnDown;

    private List<MonitorRule> _rules = [];
    private bool _loading;      // true = 正在按代码重建表格（屏蔽 CellValueChanged）
    private bool _dirty;        // true = 有未保存的修改

    /// <summary>快照/备份目录（与主面板共用同一个；这里只放规则表备份，不碰声音快照）。</summary>
    private static string BackupDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BSH", "snapshots");

    /// <summary>建一个空的规则编辑器；数据在构造函数末尾加载。</summary>
    public RulesForm()
    {
        _btnEdit = MakeButton("编辑…", EditSelected);
        _btnDelete = MakeButton("删除", DeleteSelected);
        _btnUp = MakeButton("上移", () => MoveSelected(-1));
        _btnDown = MakeButton("下移", () => MoveSelected(+1));

        Text = "BSH · 监听规则";
        Icon = Core.AppIcon.Get();
        ClientSize = new Size(980, 560);
        MinimumSize = new Size(780, 420);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9F);

        BuildUi();
        AppTheme.ApplyTo(this);
        LoadRules();
    }

    // ───────────────────────────── 界面 ─────────────────────────────

    private void BuildUi()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = false;                        // 「启用」列要能直接勾，其它列逐列设只读
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.AllowUserToOrderColumns = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.BorderStyle = BorderStyle.None;
        _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _grid.RowTemplate.Height = 26;
        _grid.EnableHeadersVisualStyles = false;   // 与主面板同理：不关掉，表头色设了也看不到
        _grid.ShowCellToolTips = false;

        var enabledCol = new DataGridViewCheckBoxColumn
        {
            HeaderText = "启用",
            FillWeight = 6,
            ReadOnly = false,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        };
        _grid.Columns.Add(enabledCol);
        _grid.Columns.Add(TextCol("名称", 14));
        _grid.Columns.Add(TextCol("类别", 12));
        _grid.Columns.Add(TextCol("条件（match）", 36));
        _grid.Columns.Add(TextCol("播放", 24));
        _grid.Columns.Add(TextCol("冷却 ms", 8));

        // 复选框点击后要提交编辑，否则 CellValueChanged 不会触发
        _grid.CellContentClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex == 0) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CellValueChanged += OnCellValueChanged;
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) EditSelected(); };
        _grid.SelectionChanged += (_, _) => UpdateButtons();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoSize = true,
            Padding = new Padding(6, 6, 6, 6),
        };
        buttons.Controls.Add(MakeButton("新增…", AddRule));
        buttons.Controls.Add(_btnEdit);
        buttons.Controls.Add(_btnDelete);
        buttons.Controls.Add(_btnUp);
        buttons.Controls.Add(_btnDown);
        buttons.Controls.Add(MakeButton("保存", () => SaveRules()));
        buttons.Controls.Add(MakeButton("重新载入", ReloadRules));
        buttons.Controls.Add(MakeButton("用示例作起点…", LoadExampleAsStart));
        buttons.Controls.Add(MakeButton("打开规则文件…", OpenRulesFile));
        buttons.Controls.Add(MakeButton("关闭", Close));

        _hint.Dock = DockStyle.Bottom;
        _hint.AutoSize = false;
        _hint.Height = 28;
        _hint.Padding = new Padding(8, 0, 8, 0);
        _hint.TextAlign = ContentAlignment.MiddleLeft;

        // 按钮行 / 状态提示都放在最外层：按钮多到会换行时，不能让 AutoSize 行把提示条挤掉
        // （主面板 2026-10-18 实测踩过：TableLayoutPanel 里的 AutoSize 行会把后面的行挤出客户区）。
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(_grid, 0, 0);
        root.Controls.Add(buttons, 0, 1);
        Controls.Add(root);
        Controls.Add(_hint);   // 先加 Fill 再加 Bottom：后添加的控件先停靠，提示条因此固定在底部
    }

    private static DataGridViewTextBoxColumn TextCol(string header, int weight) => new()
    {
        HeaderText = header,
        FillWeight = weight,
        ReadOnly = true,
        SortMode = DataGridViewColumnSortMode.NotSortable,
    };

    private static Button MakeButton(string text, Action onClick)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(0, 0, 6, 0),
            Padding = new Padding(6, 2, 6, 2),
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    // ───────────────────────────── 数据 ─────────────────────────────

    /// <summary>从磁盘读规则表并重建表格（不写盘）。</summary>
    private void LoadRules()
    {
        _rules = [.. RuleStore.Load(out string note)];
        RebuildGrid();
        _dirty = false;
        _hint.Text = $"{note}　·　文件：{RuleStore.DefaultPath}";
        UpdateButtons();
    }

    /// <summary>按 <see cref="_rules"/> 重建表格；<paramref name="selectIndex"/> 指定重建后选中哪一行。</summary>
    private void RebuildGrid(int? selectIndex = null)
    {
        int keep = selectIndex ?? (_grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0].Index : -1);

        _loading = true;
        try
        {
            _grid.Rows.Clear();
            foreach (var r in _rules)
            {
                int i = _grid.Rows.Add(
                    r.Enabled,
                    string.IsNullOrWhiteSpace(r.Name) ? r.Id : r.Name,
                    MonitorHost.KindLabel(r.Kind),
                    string.IsNullOrWhiteSpace(r.Match) ? "（任意）" : r.Match,
                    PlayText(r),
                    r.CooldownMs);
                _grid.Rows[i].Tag = r;
            }
        }
        finally
        {
            _loading = false;
        }

        if (_grid.Rows.Count > 0)
        {
            int target = Math.Clamp(keep, 0, _grid.Rows.Count - 1);
            _grid.ClearSelection();
            _grid.Rows[target].Selected = true;
            _grid.CurrentCell = _grid.Rows[target].Cells[1];
        }
        UpdateButtons();
    }

    /// <summary>播放方式的人读摘要：wav 优先于语音事件；两个都空 = 只记账不出声。</summary>
    private static string PlayText(MonitorRule r)
    {
        if (!string.IsNullOrWhiteSpace(r.Sound))
        {
            string name = Path.GetFileName(r.Sound);
            return string.IsNullOrEmpty(name) ? "wav：" + r.Sound : "wav：" + name;
        }
        if (!string.IsNullOrWhiteSpace(r.SpeakKey)) return "语音：" + r.SpeakKey;
        return "（只记账）";
    }

    /// <summary>「启用」列被勾/取消：同步到内存模型并标脏。</summary>
    private void OnCellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_loading || e.RowIndex < 0 || e.ColumnIndex != 0) return;
        if (e.RowIndex >= _rules.Count) return;

        _rules[e.RowIndex].Enabled = _grid.Rows[e.RowIndex].Cells[0].Value is true;
        MarkDirty();
    }

    private void MarkDirty()
    {
        _dirty = true;
        _hint.Text = "有未保存的修改 —— 点「保存」写入 rules.json（保存前会自动备份）";
    }

    private MonitorRule? SelectedRule =>
        _grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0].Tag as MonitorRule : null;

    private void UpdateButtons()
    {
        bool has = SelectedRule is not null;
        int idx = _grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0].Index : -1;
        _btnEdit.Enabled = has;
        _btnDelete.Enabled = has;
        _btnUp.Enabled = has && idx > 0;
        _btnDown.Enabled = has && idx >= 0 && idx < _rules.Count - 1;
    }

    // ───────────────────────────── 行操作 ─────────────────────────────

    private void AddRule()
    {
        using var dlg = new RuleEditDialog(null);
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Result is null) return;

        _rules.Add(dlg.Result);
        RebuildGrid(_rules.Count - 1);
        MarkDirty();
    }

    private void EditSelected()
    {
        var rule = SelectedRule;
        if (rule is null) return;
        int idx = _rules.IndexOf(rule);
        if (idx < 0) return;

        using var dlg = new RuleEditDialog(rule);
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Result is null) return;

        _rules[idx] = dlg.Result;
        RebuildGrid(idx);
        MarkDirty();
    }

    private void DeleteSelected()
    {
        var rule = SelectedRule;
        if (rule is null) return;
        int idx = _rules.IndexOf(rule);
        if (idx < 0) return;

        var answer = MessageBox.Show(this,
            $"确定删除规则「{(string.IsNullOrWhiteSpace(rule.Name) ? rule.Id : rule.Name)}」？"
            + $"{Environment.NewLine}（只改内存中的这张表，点「保存」后才写入 rules.json）",
            "删除规则", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;

        _rules.RemoveAt(idx);
        RebuildGrid(Math.Min(idx, _rules.Count - 1));
        MarkDirty();
    }

    /// <summary>上移 / 下移一行：顺序决定"同一事件只兑现第一条命中"的优先级。</summary>
    /// <param name="delta">-1 = 上移，+1 = 下移。</param>
    private void MoveSelected(int delta)
    {
        var rule = SelectedRule;
        if (rule is null) return;
        int idx = _rules.IndexOf(rule);
        int target = idx + delta;
        if (idx < 0 || target < 0 || target >= _rules.Count) return;

        (_rules[idx], _rules[target]) = (_rules[target], _rules[idx]);
        RebuildGrid(target);
        MarkDirty();
    }

    // ───────────────────────────── 保存 / 载入 ─────────────────────────────

    /// <summary>保存到 rules.json：先备份旧文件，再写盘；引擎在跑时询问是否立即重装钩子。</summary>
    /// <returns>true = 已保存（或没有需要保存的内容）；false = 保存失败或被取消。</returns>
    private bool SaveRules()
    {
        if (!_dirty) return true;

        // 先备份现有文件（个人配置，改坏了要能回头）
        string? backup = null;
        try
        {
            if (RuleStore.Exists)
            {
                Directory.CreateDirectory(BackupDir);
                backup = Path.Combine(BackupDir, $"rules-backup-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                File.Copy(RuleStore.DefaultPath, backup, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"备份现有规则表失败，已中止保存：{ex.Message}", "BSH · 监听规则",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        try
        {
            RuleStore.Save(_rules);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"写入规则表失败：{ex.Message}", "BSH · 监听规则",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        _dirty = false;
        int enabled = _rules.Count(r => r.Enabled);
        _hint.Text = $"已保存 {_rules.Count} 条规则（启用 {enabled} 条）"
                     + (backup is null ? "" : $"　·　旧文件备份：{backup}");

        // 引擎在跑：新规则不会自动生效（引擎启动时才按规则装钩子）——问一句，避免"改了没反应"
        if (MonitorHost.IsRunning)
        {
            var answer = MessageBox.Show(this,
                "规则已保存。监听引擎正在运行，是否立即按新规则重装监听钩子？"
                + $"{Environment.NewLine}（不重装的话，新规则要等下次切换方案 / 重启后才生效）",
                "BSH · 监听规则", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer == DialogResult.Yes)
            {
                try
                {
                    int started = MonitorHost.StartFromRules();   // 先停后起，按最新规则重装
                    _hint.Text = $"已保存并重装监听：{started} 个监听器（{MonitorHost.DescribeState()}）";
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"重装监听失败：{ex.Message}{Environment.NewLine}可切换一次方案再试。",
                        "BSH · 监听规则", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        return true;
    }

    private void ReloadRules()
    {
        if (_dirty)
        {
            var answer = MessageBox.Show(this, "有未保存的修改，重新载入会丢弃它们。继续？",
                "BSH · 监听规则", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes) return;
        }
        LoadRules();
    }

    /// <summary>用公开示例（8 类各一条、全部禁用）作为起点：只装进内存，保存后才写盘。</summary>
    private void LoadExampleAsStart()
    {
        var answer = MessageBox.Show(this,
            "把示例规则（8 类各一条、全部禁用、声音留空）装进表格作为起点？"
            + $"{Environment.NewLine}（不会立刻写盘；点「保存」才覆盖 rules.json，覆盖前自动备份）",
            "BSH · 监听规则", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;

        _rules = [.. RuleStore.LoadExample(out string note)];
        RebuildGrid();
        MarkDirty();
        _hint.Text = $"已载入示例（{note}）—— 有未保存的修改，点「保存」写入";
    }

    private void OpenRulesFile()
    {
        try
        {
            if (!RuleStore.Exists)
            {
                MessageBox.Show(this,
                    $"规则表还不存在：{RuleStore.DefaultPath}{Environment.NewLine}"
                    + "先在表格里新增 / 载入示例并保存，就会创建它。",
                    "BSH · 监听规则", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Process.Start(new ProcessStartInfo { FileName = RuleStore.DefaultPath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"打开规则文件失败：{ex.Message}", "BSH · 监听规则",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ───────────────────────────── 关闭 ─────────────────────────────

    /// <inheritdoc/>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_dirty)
        {
            var answer = MessageBox.Show(this,
                "规则表有未保存的修改，要先保存吗？",
                "BSH · 监听规则", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel)
            {
                e.Cancel = true;
                return;
            }
            if (answer == DialogResult.Yes && !SaveRules())
            {
                e.Cancel = true;
                return;
            }
        }
        base.OnFormClosing(e);
    }
}

/// <summary>
/// 「新增 / 编辑规则」对话框：只负责收集字段与基本校验，不碰磁盘。
/// <para>
/// 字段与 <see cref="MonitorRule"/> 一一对应；播放方式有两条（wav 优先于语音事件），
/// 两个都空 = 只记账不出声（示例里鼠标 / 窗口那几条就是这样）。
/// </para>
/// </summary>
internal sealed class RuleEditDialog : Form
{
    private readonly MonitorRule _source;
    private readonly bool _isNew;

    private readonly TextBox _name = new();
    private readonly CheckBox _enabled = new();
    private readonly ComboBox _kind = new();
    private readonly TextBox _match = new();
    private readonly TextBox _app = new();
    private readonly ComboBox _speakKey = new();
    private readonly TextBox _sound = new();
    private readonly NumericUpDown _cooldown = new();
    private readonly Label _kindHint = new();

    /// <summary>确定后的新规则对象（取消时为 null）。</summary>
    public MonitorRule? Result { get; private set; }

    /// <summary>打开对话框：<paramref name="rule"/> 为 null = 新增。</summary>
    public RuleEditDialog(MonitorRule? rule)
    {
        _isNew = rule is null;
        _source = rule ?? new MonitorRule
        {
            Id = $"user-{DateTime.Now:HHmmssfff}",
            Name = "",
            Enabled = true,
            Kind = MonitorEventKind.Keyboard,
            CooldownMs = 800,
        };

        Text = _isNew ? "新增规则" : "编辑规则";
        Icon = Core.AppIcon.Get();
        ClientSize = new Size(720, 640);
        MinimumSize = new Size(640, 560);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9F);

        BuildUi();
        FillFrom(_source);
        AppTheme.ApplyTo(this);
    }

    private void BuildUi()
    {
        int row = 0;
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 12,
            Padding = new Padding(12, 10, 12, 6),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130F));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        AddRow(table, row++, "名称", _name);
        AddRow(table, row++, "", _enabled);
        _enabled.Text = "启用（禁用 = 不参与匹配、也不装监听器）";
        _enabled.AutoSize = true;

        // 类别
        _kind.DropDownStyle = ComboBoxStyle.DropDownList;
        _kind.DisplayMember = nameof(KindOption.Text);
        foreach (var kind in Enum.GetValues<MonitorEventKind>())
            _kind.Items.Add(new KindOption(kind, MonitorHost.KindLabel(kind)));
        _kind.SelectedIndexChanged += (_, _) => UpdateKindHint();
        AddRow(table, row++, "事件类别", _kind);

        _kindHint.AutoSize = true;
        _kindHint.MaximumSize = new Size(560, 0);
        _kindHint.ForeColor = Color.Gray;
        table.Controls.Add(_kindHint, 1, row++);

        AddRow(table, row++, "条件", _match);

        AddRow(table, row++, "限定应用", _app);
        var appHint = new Label
        {
            Text = "仅「进程启停」有效：事件里的进程名（如 chrome.exe）；其它类别填了会永不命中，留空即可。",
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            ForeColor = Color.Gray,
        };
        table.Controls.Add(appHint, 1, row++);

        // 播放方式：wav 优先于语音事件；两个都空 = 只记账
        _speakKey.DropDownStyle = ComboBoxStyle.DropDown;
        foreach (var key in SpeakKeyOptions()) _speakKey.Items.Add(key);
        AddRow(table, row++, "语音事件", _speakKey);

        var soundPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        _sound.Width = 320;
        var browse = new Button { Text = "浏览…", AutoSize = true, Padding = new Padding(4, 1, 4, 1) };
        browse.Click += (_, _) => BrowseSound();
        var clear = new Button { Text = "清空", AutoSize = true, Padding = new Padding(4, 1, 4, 1) };
        clear.Click += (_, _) => _sound.Text = "";
        soundPanel.Controls.Add(_sound);
        soundPanel.Controls.Add(browse);
        soundPanel.Controls.Add(clear);
        AddRow(table, row++, "wav 文件", soundPanel);

        _cooldown.Minimum = 0;
        _cooldown.Maximum = 600_000;
        _cooldown.Increment = 100;
        _cooldown.Width = 120;
        AddRow(table, row++, "冷却（ms）", _cooldown);

        var note = new Label
        {
            Text = "播放优先级：wav 文件 > 语音事件；两个都空 = 只记账不出声。"
                   + "冷却 = 同一条规则的最小触发间隔（0 = 不限制）。",
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            ForeColor = Color.Gray,
        };
        table.Controls.Add(note, 1, row++);

        // 文本类输入统一填满第二列（TextBox 默认宽度只有 100，截图实测会缩成一小条）
        foreach (Control c in new Control[] { _name, _match, _app, _kind, _speakKey })
            c.Dock = DockStyle.Fill;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(0, 6, 0, 0),
        };
        var cancel = new Button { Text = "取消", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        var ok = new Button { Text = "确定", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
        ok.Click += (_, _) => Confirm();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        table.Controls.Add(buttons, 0, row);
        table.SetColumnSpan(buttons, 2);

        Controls.Add(table);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private static void AddRow(TableLayoutPanel table, int row, string label, Control control)
    {
        if (label.Length > 0)
            table.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 6, 4, 0) }, 0, row);
        table.Controls.Add(control, 1, row);
    }

    private void FillFrom(MonitorRule rule)
    {
        _name.Text = rule.Name;
        _enabled.Checked = rule.Enabled;
        int kindIndex = 0;
        for (int i = 0; i < _kind.Items.Count; i++)
        {
            if (_kind.Items[i] is KindOption opt && opt.Kind == rule.Kind) { kindIndex = i; break; }
        }
        _kind.SelectedIndex = kindIndex;
        _match.Text = rule.Match ?? "";
        _app.Text = rule.App;
        _speakKey.Text = rule.SpeakKey ?? "";
        _sound.Text = rule.Sound ?? "";
        _cooldown.Value = Math.Clamp(rule.CooldownMs, 0, 600_000);
        UpdateKindHint();
    }

    private void UpdateKindHint()
    {
        _kindHint.Text = _kind.SelectedItem is KindOption k
            ? "条件写法：" + MatchHint(k.Kind)
            : "";
    }

    /// <summary>各类别 <c>match</c> 的写法提示（与 <see cref="MonitorRule.Match"/> 的文档一致）。</summary>
    private static string MatchHint(MonitorEventKind kind) => kind switch
    {
        MonitorEventKind.Keyboard => "虚拟键名，如 CapsLock,VolumeUp,VolumeDown（留空或 * = 任意键）",
        MonitorEventKind.Mouse => "事件名，如 LeftDown,MiddleUp,WheelUp,WheelDown（留空或 * = 任意）",
        MonitorEventKind.WindowSwitch => "进程名（notepad.exe）或窗口标题关键词（留空或 * = 任意）",
        MonitorEventKind.ProcessStartStop => "进程名，可加 start / stop 前缀限定方向，如 start steam.exe",
        MonitorEventKind.DeviceChange => "Arrival / Remove",
        MonitorEventKind.PowerSession => "AC,DC,Suspend,Resume,BatteryLow,Lock,Unlock,Logon,Logoff",
        MonitorEventKind.Clipboard => "Text / Image / Files（多类型用 + 组合，如 Text+Image）",
        MonitorEventKind.FileSystem => @"目录绝对路径，如 C:\Users\<你>\Downloads（不递归）",
        _ => "",
    };

    private void BrowseSound()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "选择要播放的 wav",
            Filter = "WAV 音频 (*.wav)|*.wav|所有文件 (*.*)|*.*",
            InitialDirectory = Directory.Exists(SoundScheme.MediaDir) ? SoundScheme.MediaDir : "",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) _sound.Text = dlg.FileName;
    }

    /// <summary>语音事件下拉的候选：优先用装机语音方案里的事件名，其次用注册表现有事件，最后给几个常见项。</summary>
    private static IEnumerable<string> SpeakKeyOptions()
    {
        var scheme = VoiceScheme.TryLoad();
        if (scheme is not null && scheme.Sounds.Count > 0)
            return scheme.Sounds.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase);

        var events = SoundScheme.ReadAll().Select(s => s.Event).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        return events.Count > 0
            ? events
            : [".Default", "SystemAsterisk", "SystemExclamation", "SystemHand", "DeviceConnect"];
    }

    private void Confirm()
    {
        if (string.IsNullOrWhiteSpace(_name.Text))
        {
            MessageBox.Show(this, "名称不能为空。", "BSH · 监听规则", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _name.Focus();
            return;
        }
        if (_kind.SelectedItem is not KindOption kind)
        {
            MessageBox.Show(this, "请选择事件类别。", "BSH · 监听规则", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!string.IsNullOrWhiteSpace(_sound.Text) && !File.Exists(_sound.Text.Trim()))
        {
            var answer = MessageBox.Show(this,
                $"wav 文件不存在：{_sound.Text}{Environment.NewLine}仍然保存这条规则吗？",
                "BSH · 监听规则", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
        }
        if (!string.IsNullOrWhiteSpace(_sound.Text) && !string.IsNullOrWhiteSpace(_speakKey.Text))
        {
            // 不阻塞：按文档语义 wav 优先，这里只提醒一句
            MessageBox.Show(this, "同时填了 wav 与语音事件：按规则 wav 优先，语音事件会被忽略。",
                "BSH · 监听规则", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        Result = new MonitorRule
        {
            Id = _source.Id,
            Name = _name.Text.Trim(),
            Enabled = _enabled.Checked,
            Kind = kind.Kind,
            Match = string.IsNullOrWhiteSpace(_match.Text) ? null : _match.Text.Trim(),
            App = _app.Text.Trim(),
            Sound = string.IsNullOrWhiteSpace(_sound.Text) ? null : _sound.Text.Trim(),
            SpeakKey = string.IsNullOrWhiteSpace(_speakKey.Text) ? null : _speakKey.Text.Trim(),
            CooldownMs = (int)_cooldown.Value,
        };

        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>下拉项：显示中文名，保存 <see cref="MonitorEventKind"/>。</summary>
    private sealed record KindOption(MonitorEventKind Kind, string Text);
}
