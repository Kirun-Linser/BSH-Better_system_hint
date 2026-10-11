using System.Reflection;
using BSH.Core;
using BSH.Core.Monitoring;

namespace BSH;

/// <summary>
/// 主面板：59 个系统声音事件的现状总览 + 试听 / 替换 / 一键重排 / 快照还原。
/// 界面全部用代码构建（不依赖 Designer 文件），便于后续手改。
/// <para>
/// 两套方案的按钮文案与提示都写明了真实动作：<b>「个性化设置」= 换提示音（写注册表）+ 启用监听</b>，
/// <b>「Windows 原样」= 还原 59 槽 + 停止监听</b>；状态栏第三格常显"当前方案 · 引擎状态 · 提示音写多少槽"。
/// </para>
/// <para>
/// <b>主题</b>：按钮行末尾的三态按钮（跟随系统 → 深色 → 浅色）是唯一的切换入口，
/// 颜色全部来自 <see cref="AppTheme.Palette"/>，本文件里不写死任何 RGB。
/// 表格的行区分度靠三层底色：普通行 / 交替行（斑马纹）/ 选中行 —— 详见 <see cref="RecolorRows"/>。
/// </para>
/// </summary>
public sealed class MainForm : Form
{
    /// <summary>
    /// 事件总览表。用 <see cref="DataGridView"/> 而不是原来的 <see cref="ListView"/>：
    /// 列宽要按 <see cref="DataGridViewColumn.FillWeight"/> 比例随窗口自适应（ListView 没有这套机制），
    /// 单元格还能挂 ToolTip、行高与表头高也能单独调。填充 / 选中 / 双击的对外行为与原来完全一致。
    /// </summary>
    private readonly DataGridView _list = new();

    private readonly ToolStripStatusLabel _summary = new();
    private readonly ToolStripStatusLabel _profile = new();
    private readonly ToolStripStatusLabel _hint = new();

    /// <summary>主题切换按钮：三态循环（跟随系统 → 深色 → 浅色），文案形如「主题：跟随系统」。</summary>
    private readonly Button _themeButton = new();

    /// <summary>按钮提示：必须留住引用，否则 <see cref="ToolTip"/> 会被 GC 回收、提示直接消失。</summary>
    private readonly ToolTip _tips = new();

    /// <summary>
    /// 把 ToolTip 也纳入深色体系。
    /// <para>⚠️ 2026-10-11 用户实测反馈：「悬停的描述」是<b>白底黑字</b>、而且<b>横跨整个屏幕宽</b> ——
    /// ToolTip 由系统绘制、完全不受 <c>BackColor</c> 影响，必须开 <c>OwnerDraw</c> 自绘；
    /// 宽度则要靠 <c>Popup</c> 事件自己限，否则长文案会一路撑到屏幕边缘。</para>
    /// </summary>
    private void SetUpToolTip()
    {
        _tips.OwnerDraw = true;
        _tips.Draw += (_, e) =>
        {
            var p = AppTheme.Palette;
            using var back = new SolidBrush(p.Surface);
            using var border = new Pen(p.Border);
            e.Graphics.FillRectangle(back, e.Bounds);
            e.Graphics.DrawRectangle(border, e.Bounds.X, e.Bounds.Y, e.Bounds.Width - 1, e.Bounds.Height - 1);
            TextRenderer.DrawText(e.Graphics, e.ToolTipText, e.Font,
                new Rectangle(e.Bounds.X + 5, e.Bounds.Y + 3, e.Bounds.Width - 10, e.Bounds.Height - 6),
                p.Foreground, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak);
        };
        _tips.Popup += (_, e) =>
        {
            // 限宽 560：再宽就看不清哪一行是哪一行了
            e.ToolTipSize = new Size(Math.Min(560, e.ToolTipSize.Width + 12), e.ToolTipSize.Height + 8);
        };
    }

    private readonly string _snapshotDir;

    private List<EventSound> _sounds = [];

    /// <summary>为 true 时关闭按钮真的退出（托盘菜单"退出"会先置位）。</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AllowClose { get; set; }

    public MainForm()
    {
        _snapshotDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BSH", "snapshots");
        Directory.CreateDirectory(_snapshotDir);

        // 主题：每次新建面板都从 %LOCALAPPDATA%\BSH\ui.json 重新读一次
        //（缺失或损坏 → 浅色，见 AppTheme.Load），所以"关掉再开 / 下次启动"必然沿用上次的选择。
        AppTheme.Reload();
        AppTheme.Changed += ApplyThemeToUi;

        Text = "BSH · 更好的系统提示";
        Icon = Core.AppIcon.Get();      // 窗体左上角 / 任务栏 与托盘用同一枚图标
        ClientSize = new Size(1020, 620);
        MinimumSize = new Size(820, 460);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);

        BuildUi();
        SetUpToolTip();        // ToolTip 也是外观的一部分：深色自绘 + 限宽（见方法注释）
        ApplyThemeToUi();      // 先上色再填数据：行颜色依赖当前主题
        Reload();
    }

    private void BuildUi()
    {
        // ── 列表：列宽随窗口自适应（Fill + FillWeight），行高与表头高放宽一点让中英文混排不难看 ──
        _list.Dock = DockStyle.Fill;
        _list.ReadOnly = true;
        _list.AllowUserToAddRows = false;
        _list.AllowUserToDeleteRows = false;
        _list.AllowUserToResizeRows = false;      // 行高统一由 RowTemplate 定，防止用户拖乱
        _list.AllowUserToOrderColumns = false;    // 列顺序固定：FillWeight 是按列序号配的
        _list.RowHeadersVisible = false;
        _list.SelectionMode = DataGridViewSelectionMode.FullRowSelect;   // ≈ 原来的 FullRowSelect
        _list.MultiSelect = false;
        _list.BorderStyle = BorderStyle.None;
        // ⚠️ 2026-10-11 用户截图指出"表头下方有一条白线"：
        // 那是表头边框 —— 默认 ColumnHeadersBorderStyle = Single，由**系统用白色**绘制，
        // BackColor / GridColor 都管不到它，必须显式关掉。
        _list.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        _list.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _list.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; // 各列按 FillWeight 分宽度
        _list.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _list.RowTemplate.Height = 26;            // 行高稍增：中英文混排更好看
        _list.ShowCellToolTips = true;            // 列宽不够时悬停看全文（ToolTip 在 Reload 里逐格设）
        // ⚠️ 2026-10-11 用户截图指出"右侧白块"：DataGridView 的滚动条是**它自己画的**，
        // 不是标准 ScrollBar 控件 —— 实测 SetWindowTheme(handle,"DarkMode_Explorer") 虽返回 S_OK，
        // 但 AllowDarkModeForWindow 返回 False（窗口根本不支持深色外观），所以那条滚动条永远是系统的浅色。
        // 唯一干净的解法是**不让它画**：关掉内置滚动条，靠鼠标滚轮/键盘导航（59 行列表滚轮足够用）。
        _list.ScrollBars = ScrollBars.None;
        // 打开双缓冲：DataGridView 默认关着它，选中 / 滚动时会先刷一遍底色再画内容 ——
        // 视觉上就是"慢半拍"地闪一下。它是 protected 属性，只能反射开（WinForms 通行做法；
        // 失败无副作用，只是少一点顺滑，功能与颜色完全不受影响）。
        try
        {
            typeof(DataGridView)
                .GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(_list, true);
        }
        catch
        {
            // 反射被裁剪 / 属性改名：保持默认（只是没有这一层顺滑）
        }
        // 表格自己的句柄与窗体句柄是分两步创建的：滚动条换主题只能等它建好再补做一次
        _list.HandleCreated += (_, _) => ApplyNativeChrome();
        // 列宽比例（设计规格，勿随手改）：当前音效最长，所以给它 38 —— 它正是原来被截断的那一列
        _list.Columns.Add(NewColumn("事件", 22));
        _list.Columns.Add(NewColumn("说明", 24));
        _list.Columns.Add(NewColumn("分组", 8));
        _list.Columns.Add(NewColumn("当前音效", 38));
        _list.Columns.Add(NewColumn("状态", 8));
        // 双击（且确实点在数据行上）才试听：原来 ListView 点空白不会误触发
        _list.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) Preview(); };
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) { Preview(); e.Handled = true; }
        };
        // ⚠️ 2026-10-11：此处原有"鼠标悬停行提亮"（CellMouseEnter/Leave + MouseLeave 三个订阅）。
        // 用户明确要求「无意义下悬停不要设计」⇒ 已整体移除；顺带解决了「悬停选中总是慢半拍」
        //（以前每次鼠标进出单元格都要 InvalidateRow 两行）。选中反馈只由点击决定，即时生效。

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            // 必须允许换行：按钮行总宽（约 1185px）大于默认窗口宽，不换行的话
            // 尾部按钮会被裁掉看不见（主题切换按钮正好在末尾，会被整颗裁掉）。
            WrapContents = true,
            Padding = new Padding(6, 6, 6, 6),
            AutoSize = true,
        };
        // ── 按钮行 ──
        // 主题按钮、方案按钮、操作按钮全部排在同一行；真正干活的地方各自只有一处
        // （ApplyProfile / AppTheme.Apply / 各操作），不存在第二套逻辑。

        // 两套方案放最前：它们是"一键切换整套行为"的主入口。
        // ⚠️ 2026-10-11 用户要求「简单易懂的选项无 ToolTip」：
        // 按钮文案只留最核心的两个词，细节（还原几个槽、监听哪些事件、跳过谁、规则表在哪）
        // 一律不进 ToolTip —— 那属于实现，写在 README 与 profile.json 里，不该让用户悬停才看懂。
        var btnDefault = MakeButton("Windows 原样", () => ApplyProfile(ProfileStore.WindowsDefaultId));
        var btnPersonal = MakeButton("个性化设置", () => ApplyProfile(ProfileStore.PersonalId));

        buttons.Controls.Add(btnDefault);
        buttons.Controls.Add(btnPersonal);
        buttons.Controls.Add(MakeButton("▶ 试听", Preview));
        buttons.Controls.Add(MakeButton("替换音效…", Replace));
        buttons.Controls.Add(MakeButton("设为静音", Mute));
        buttons.Controls.Add(MakeButton("一键重排…", RunReorder));
        buttons.Controls.Add(MakeButton("保存快照…", SaveSnapshot));
        buttons.Controls.Add(MakeButton("还原快照…", RestoreSnapshot));
        buttons.Controls.Add(MakeButton("刷新", Reload));

        // 主题切换放按钮行末尾：点一下即时生效（AppTheme.Apply 内部写盘并广播，不需要重启）
        _themeButton.AutoSize = true;
        _themeButton.Margin = new Padding(0, 0, 6, 0);
        _themeButton.Padding = new Padding(4, 2, 4, 2);
        _themeButton.Click += (_, _) => ToggleTheme();
        buttons.Controls.Add(_themeButton);

        var status = new StatusStrip { Dock = DockStyle.Fill, SizingGrip = false };
        status.Items.Add(_summary);
        status.Items.Add(new ToolStripStatusLabel { Spring = true });
        // 第二格：当前方案 + 引擎状态 + 启用规则数（监听模式的核心状态，常显）
        status.Items.Add(_profile);
        status.Items.Add(new ToolStripStatusLabel
        {
            BorderSides = ToolStripStatusLabelBorderSides.Left,
            BorderStyle = Border3DStyle.Etched,
        });
        status.Items.Add(_hint);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        // 列宽必须显式钉成 100%：TableLayoutPanel 不设 ColumnStyles 时列宽按"最宽子项"算，
        // 按钮行的 preferred 宽（约 1185px）会把列撑得比窗口还宽 —— 那样窗口拉窄时
        // 表格宽度纹丝不动，FillWeight 也就无从按比例分配（实测：窗口 860 与 1020 下列宽完全相同）。
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(_list, 0, 0);
        root.Controls.Add(buttons, 0, 1);
        root.Controls.Add(status, 0, 2);
        Controls.Add(root);

        _hint.Text = "双击行 = 试听";
    }

    /// <summary>按 <paramref name="fillWeight"/> 建一列：宽度不写死，交给 Fill 模式按权重分配。</summary>
    /// <param name="header">表头文字。</param>
    /// <param name="fillWeight">占宽权重（本面板的规格是 22 / 24 / 8 / 38 / 8）。</param>
    private static DataGridViewTextBoxColumn NewColumn(string header, int fillWeight) => new()
    {
        HeaderText = header,
        FillWeight = fillWeight,
        ReadOnly = true,
        SortMode = DataGridViewColumnSortMode.NotSortable,   // 只读总览表，不提供排序免得误以为能改
    };


    /// <summary>
    /// 主题切换按钮：三态循环（跟随系统 → 深色 → 浅色），切完把结果写进状态栏提示（"被记住了"这件事要看得见）。
    /// <para>颜色本身由 <see cref="AppTheme.Apply"/> 广播 → <see cref="ApplyThemeToUi"/> 铺开，这里只管文案。</para>
    /// </summary>
    private void ToggleTheme()
    {
        var kind = AppTheme.Cycle();
        _hint.Text = ThemeHint(kind);
    }

    /// <summary>切换后的状态栏提示（"跟随系统"要顺带说明系统当前解析成深还是浅，否则等于没说）。</summary>
    /// <param name="kind">切换后的档位。</param>
    private static string ThemeHint(ThemeKind kind) => kind switch
    {
        ThemeKind.System => $"已切换到「跟随系统」：系统当前为{KindWord(AppTheme.Current)}主题（已记住，下次启动沿用）",
        ThemeKind.Dark => "已切换到深色主题（已记住，下次启动沿用）",
        _ => "已切换到浅色主题（已记住，下次启动沿用）",
    };

    /// <summary>档位 → 中文词（按钮文案与提示共用）。</summary>
    /// <param name="kind">档位。</param>
    private static string KindWord(ThemeKind kind) => kind switch
    {
        ThemeKind.System => "跟随系统",
        ThemeKind.Dark => "深色",
        _ => "浅色",
    };

    /// <summary>
    /// 把当前主题铺到整个面板（窗体 / 表格 / 按钮 / 下拉框 / 状态栏 / 容器），并刷新按钮文案、下拉选中项与行颜色。
    /// <para>
    /// 挂在 <see cref="AppTheme.Changed"/> 上，所以别的窗口切主题时这里也会跟着变；
    /// "跟随系统"档下系统主题一变，<see cref="AppTheme"/> 的 <c>SystemEvents</c> 钩子也会广播到这里。
    /// </para>
    /// </summary>
    private void ApplyThemeToUi()
    {
        if (IsDisposed) return;    // 已销毁的窗体不再响应（托盘复用实例 + 探针反复开关时会遇到）

        // ⚠️ SystemEvents 的回调可能不在 UI 线程上（它有自己的消息窗口线程）→ 先弹回主线程再碰控件
        if (InvokeRequired)
        {
            try { BeginInvoke(ApplyThemeToUi); }
            catch { /* 句柄已销毁：这次刷新丢掉即可 */ }
            return;
        }

        AppTheme.ApplyTo(this);
        // 铺色的同时会顺手改标题栏与滚动条，但那两件事依赖真实句柄 —— 句柄还没建时会被静默跳过，
        // 所以这里再显式补一次（句柄已建 = 幂等重设；没建 = 由 OnHandleCreated 兜底）。
        ApplyNativeChrome();

        // 文案要反映"用户选的档"（含「跟随系统」这个词，而不是解析后的明暗）→ 读 Mode；
        // 配色与"现在是不是深色"的判断一律读 Current。两者刻意分开，见 AppTheme 的类文档。
        _themeButton.Text = $"主题：{KindWord(AppTheme.Mode)}";
        // 设计表里"强调 = 主按钮"的落点：主题切换是本面板唯一由本次改动新增的按钮，
        // 给它强调色不影响任何原有控件的观感（浅色档走系统绘制，设了也不生效）。
        if (AppTheme.Current == ThemeKind.Dark)
        {
            _themeButton.ForeColor = AppTheme.Palette.Accent;
            _themeButton.FlatAppearance.BorderColor = AppTheme.Palette.Accent;
        }
        else
        {
            // 必须显式还原：否则从深色切回浅色后按钮仍是绿字（浅色档 ForeColor 参与绘制）
            _themeButton.ForeColor = AppTheme.Palette.Foreground;
            _themeButton.FlatAppearance.BorderColor = AppTheme.Palette.Border;
        }

        RecolorRows();
        Invalidate(true);
    }

    /// <summary>
    /// 按每行的状态重算行文字色（空槽灰 / 撞车危险色 / 正常跟随主题）与选中行配色。
    /// <para>状态从"状态"列的文本读回 —— 它就是 <see cref="Reload"/> 写进去的那个词，不另存一份状态。</para>
    /// <para>
    /// ⚠️ 行级 <c>ForeColor</c> 会连选中态一起盖掉：深色档下选中行是深蓝底，若沿用撞车红 / 空槽灰，
    /// 文字就糊在背景里了。所以行级必须<b>同时</b>显式给出 <c>SelectionForeColor</c>（深色 = 白字）。
    /// </para>
    /// </summary>
    private void RecolorRows()
    {
        foreach (DataGridViewRow row in _list.Rows)
        {
            row.DefaultCellStyle.ForeColor = ColorForState(row.Cells[4].Value as string);
            row.DefaultCellStyle.SelectionBackColor = AppTheme.Palette.Selection;
            row.DefaultCellStyle.SelectionForeColor = AppTheme.Palette.SelectionForeground;
            // 行背景留空（Color.Empty）= 继续继承 DefaultCellStyle / AlternatingRowsDefaultCellStyle，
            // 这样斑马纹不会被行级颜色吃掉。
            // ⚠️ 2026-10-11 用户明确要求「无意义下悬停不要设计」⇒ 悬停高亮已整体移除，
            // 连带解决了「鼠标悬停选中总是慢半拍」—— 以前每次鼠标进出单元格都要重绘两行。
            row.DefaultCellStyle.BackColor = Color.Empty;
        }
    }

    /// <summary>状态 → 文字色：空槽用弱化文字、撞车用危险色、正常行不设（跟随主题的前景色）。</summary>
    /// <param name="state">"空" / "撞车" / "正常"。</param>
    private static Color ColorForState(string? state) => state switch
    {
        "空" => AppTheme.Palette.MutedForeground,
        "撞车" => AppTheme.Palette.Danger,
        _ => Color.Empty,      // 空 = 不设行级颜色，让单元格跟随主题（浅色下就是原来的系统前景色）
    };

    /// <summary>
    /// 句柄创建之后补做"只能对真实窗口句柄做"的两件外观事：DWM 深色标题栏 + uxtheme 深色滚动条。
    /// <para>
    /// ⚠️ <b>为什么非要在这里再来一次</b>：构造函数里的 <see cref="ApplyThemeToUi"/> 跑在句柄创建<b>之前</b>，
    /// 那时 <see cref="AppTheme.ApplyTitleBar"/> / <see cref="AppTheme.ApplyScrollBarTheme"/> 都会因为
    /// "没有句柄"而静默跳过 —— 结果就是启动时已经是深色档，<b>标题栏与表格滚动条却还是白的</b>
    /// （用户 2026-10-11 的实机截图正是这两条）。
    /// </para>
    /// <para>
    /// 窗体与子控件的句柄分两步创建，所以挂了两个入口：窗体的 <see cref="OnHandleCreated"/> 与表格自己的
    /// <c>HandleCreated</c>。两者都幂等：重复调用只是再设一次同样的值。
    /// </para>
    /// </summary>
    private void ApplyNativeChrome()
    {
        bool dark = AppTheme.Current == ThemeKind.Dark;
        AppTheme.ApplyTitleBar(this, dark);                                        // 标题栏：20 → 19 回退，失败静默
        if (_list.IsHandleCreated) AppTheme.ApplyScrollBarTheme(_list, dark);      // 表格滚动条：DarkMode_Explorer
    }

    /// <summary>窗体句柄创建：把深色标题栏补上（必须在句柄之后，理由见 <see cref="ApplyNativeChrome"/>）。</summary>
    /// <param name="e">事件参数。</param>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyNativeChrome();
    }

    /// <summary>释放时退订主题广播，避免已销毁的窗体被后续的切换动作碰到。</summary>
    /// <param name="disposing">true = 由 Dispose() 主动释放。</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing) AppTheme.Changed -= ApplyThemeToUi;
        base.Dispose(disposing);
    }

    /// <summary>
    /// 刷新状态栏第二格：<c>方案：个性化设置 · 引擎：运行中：3 个监听器（键盘/文件变化）· 提示音：写 48 槽（跳 F5 11 个）</c>。
    /// <para>状态是现算的（引擎可能被托盘那边启停过），所以每次 <see cref="Reload"/> 与方案切换后都要调一次。</para>
    /// </summary>
    public void UpdateProfileStatus()
    {
        string name = ProfileStore.Find(MonitorHost.ActiveProfileId)?.Name ?? "未选择（不监听）";
        _profile.Text = $"方案：{name}　·　引擎：{MonitorHost.DescribeState()}　·　{PersonalPlanText()}";
    }

    /// <summary>
    /// 「个性化设置」那一半的注册表动作摘要（只读算出，供状态栏显示）：
    /// <c>提示音：写 48 槽（跳 F5 11 个）+ 监听器 0 个</c>。
    /// <para>用 <see cref="ProfileStore.PlanDetailed"/> 现算，不额外读注册表、不写任何东西。</para>
    /// </summary>
    private static string PersonalPlanText()
    {
        var profile = ProfileStore.Find(ProfileStore.PersonalId);
        if (profile is null) return "提示音：—";

        var plan = ProfileStore.PlanDetailed(profile);
        // 跳过数拆成「按档 + 按事件」：状态栏空间有限，只报数字不报名单（名单在 --profile-plan 与状态详情里）
        string skip = plan.SkippedSlots.Count == 0
            ? "不跳"
            : $"跳 {plan.SkippedSlots.Count} 个（档 {plan.SkippedByFreq?.Count ?? 0} + 事件 {plan.SkippedByEvent?.Count ?? 0}）";
        return $"提示音：写 {plan.SlotWrites.Count} 槽（{skip}）＋监听器 {plan.MonitorLineCount} 个";
    }

    private static Button MakeButton(string text, Action onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Margin = new Padding(0, 0, 6, 0), Padding = new Padding(4, 2, 4, 2) };
        b.Click += (_, _) => onClick();
        return b;
    }

    // ───────────────────────────── 数据 ─────────────────────────────

    /// <summary>重新读取注册表并刷新列表。</summary>
    public void Reload()
    {
        _sounds = SoundScheme.ReadAll();

        var dupes = _sounds
            .Where(s => !s.IsEmpty)
            .GroupBy(s => s.FileName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _list.SuspendLayout();
        _list.Rows.Clear();
        foreach (var s in _sounds)
        {
            var state = s.IsEmpty ? "空" : dupes.Contains(s.FileName) ? "撞车" : "正常";
            string label = AppEventCatalog.Label(s.Event);
            string group = AppEventCatalog.Group(s.Event);

            var row = _list.Rows[_list.Rows.Add(s.Event, label, group, s.FileName, state)];
            row.Tag = s;

            // 逐格挂完整值：即使 FillWeight 分到的宽度仍放不下，悬停也能看到全文。
            // 「当前音效」格里放的就是文件名本身（语音方案把它命名成"说明,原文件名.wav"），
            // 所以完整值 = FileName，不再拼说明（拼了会重复）。
            row.Cells[0].ToolTipText = s.Event;
            row.Cells[1].ToolTipText = label;
            row.Cells[2].ToolTipText = group;
            row.Cells[3].ToolTipText = s.IsEmpty
                ? "（空槽，没有指向任何音效文件）"
                : $"{s.FileName}{Environment.NewLine}{s.WavPath}";
            row.Cells[4].ToolTipText = state;

            row.DefaultCellStyle.ForeColor = ColorForState(state);
        }
        _list.ResumeLayout();

        int empty = _sounds.Count(x => x.IsEmpty);
        _summary.Text = $"共 {_sounds.Count} 个事件　·　空槽 {empty}　·　撞车 {dupes.Count} 组";
        UpdateProfileStatus();
    }

    private EventSound? Selected =>
        _list.SelectedRows.Count > 0 ? _list.SelectedRows[0].Tag as EventSound : null;

    // ───────────────────────────── 操作 ─────────────────────────────

    private void Preview()
    {
        var s = Selected;
        if (s is null) return;
        if (s.IsEmpty)
        {
            _hint.Text = $"{s.Event} 当前为空（静音），没有可试听的声音";
            return;
        }

        bool ok = AudioProbe.PlayFile(s.WavPath);
        _hint.Text = ok ? $"正在试听：{s.FileName}" : $"播放失败：{s.WavPath}";
    }

    private void Replace()
    {
        var s = Selected;
        if (s is null) return;

        using var dlg = new OpenFileDialog
        {
            Title = $"为「{AppEventCatalog.Label(s.Event)}」选择音效",
            Filter = "WAV 音频 (*.wav)|*.wav|所有文件 (*.*)|*.*",
            InitialDirectory = Directory.Exists(SoundScheme.MediaDir) ? SoundScheme.MediaDir : "",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        SoundScheme.SetSound(s.Event, dlg.FileName);
        _hint.Text = $"{s.Event} → {Path.GetFileName(dlg.FileName)}";
        Reload();
    }

    private void Mute()
    {
        var s = Selected;
        if (s is null) return;
        SoundScheme.ClearSound(s.Event);
        _hint.Text = $"{s.Event} 已设为静音";
        Reload();
    }

    /// <summary>一键重排：预览 → 自动快照 → 应用（托盘菜单也调这个）。</summary>
    public void RunReorder()
    {
        Reload();
        var plan = ReorderPlan.Build(_sounds);
        if (plan.Count == 0)
        {
            MessageBox.Show(this, "当前方案没有需要重排的地方（无空槽、无撞车，或已按映射表处理过）。",
                "一键重排", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var preview = string.Join(Environment.NewLine, plan.Select(p => $"{p.Event}  →  {p.NewWav}    （{p.Reason}）"));
        var answer = MessageBox.Show(this,
            $"将执行 {plan.Count} 项改动：{Environment.NewLine}{Environment.NewLine}{preview}" +
            $"{Environment.NewLine}{Environment.NewLine}执行前会自动保存快照（可一键还原）。是否继续？",
            "一键重排", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (answer != DialogResult.OK) return;

        var snapPath = Path.Combine(_snapshotDir, $"auto-before-reorder-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        SchemeSnapshot.Capture("一键重排前自动快照").Save(snapPath);

        var log = ReorderPlan.Apply(plan);
        Reload();

        MessageBox.Show(this,
            $"完成 {log.Count} 项。{Environment.NewLine}快照已存：{snapPath}",
            "一键重排", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>
    /// 切换到某套方案（方案按钮与托盘菜单都走这里）。
    /// <para>
    /// 两套方案的流程<b>不一样</b>，按 <see cref="SoundProfile.Kind"/> 分流：
    /// </para>
    /// <list type="bullet">
    /// <item><description><see cref="ProfileKind.Monitor"/>（个性化设置）：可用性 → 只读预演（写哪些槽 + 启哪些监听器）
    /// → 确认 → <b>先写注册表、成功后再启引擎</b>。<b>不拍照</b>（复原由「Windows 原样」负责）。</description></item>
    /// <item><description><see cref="ProfileKind.RestoreBaseline"/>（Windows 原样）：可用性 → 预演（将还原哪些槽）
    /// → 确认 → <b>自动快照</b> → 还原注册表 + 停止监听。</description></item>
    /// </list>
    /// <para>快照目录与「一键重排」共用同一个 <c>_snapshotDir</c>，还原入口因此是统一的一份。</para>
    /// </summary>
    /// <param name="profileId">方案 id：<see cref="ProfileStore.WindowsDefaultId"/> 或 <see cref="ProfileStore.PersonalId"/>。</param>
    public void ApplyProfile(string profileId)
    {
        Reload();

        var profile = ProfileStore.Find(profileId);
        if (profile is null)
        {
            MessageBox.Show(this, $"未知的方案：{profileId}", "切换方案",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var title = $"切换到「{profile.Name}」";

        // 1) 可用性：文件不在就直接中止，绝不写注册表、也不装钩子
        var (available, status) = ProfileStore.Describe(profile);
        if (!available)
        {
            MessageBox.Show(this,
                $"方案「{profile.Name}」当前不可用：{Environment.NewLine}{status}",
                title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // 2) 监听模式走自己的流程（写注册表 + 启引擎，但不需要快照）
        if (profile.Kind == ProfileKind.Monitor)
        {
            ApplyMonitorProfile(profile, status);
            return;
        }

        // 3) 只读预演：这一步只看不动
        var plan = ProfileStore.Plan(profile);
        if (plan.Count == 0)
        {
            MessageBox.Show(this,
                $"当前已经是「{profile.Name}」，没有需要改动的项。{Environment.NewLine}{status}",
                title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var preview = string.Join(Environment.NewLine, plan.Take(10));
        if (plan.Count > 10) preview += $"{Environment.NewLine}… 其余 {plan.Count - 10} 项";

        // 4) 确认
        var answer = MessageBox.Show(this,
            $"将改动 {plan.Count} 项：{Environment.NewLine}{Environment.NewLine}{preview}" +
            $"{Environment.NewLine}{Environment.NewLine}执行前会自动保存快照（可一键还原）。" +
            $"同时会停止监听引擎（Windows 原样 = 不监听）。是否继续？",
            title, MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (answer != DialogResult.OK) return;

        // 5) 自动快照（与 RunReorder 同一套 _snapshotDir 逻辑）
        var snapPath = Path.Combine(_snapshotDir,
            $"auto-before-profile-{profile.Id}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        SchemeSnapshot.Capture($"{title}前自动快照").Save(snapPath);

        // 6) 真正写入（+ 卸载监听）+ 汇报
        int n = ProfileStore.Apply(profile).Changed;
        Reload();
        _hint.Text = $"已切换到「{profile.Name}」：{n} 项";

        MessageBox.Show(this,
            $"完成：{n} 个事件已按「{profile.Name}」写入；监听引擎已停止。{Environment.NewLine}快照：{snapPath}",
            title, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>
    /// 「个性化设置」的执行流程：<b>两件事一起做</b> —— ① 按语音方案改写系统声音事件槽
    /// （跳过 <c>skipFreq</c> 的档 + <c>skipEvents</c> 点名的事件，取并集）
    /// ② 启动监听引擎；返回时状态栏已刷新。
    /// <para>
    /// 顺序是刻意的：<b>先写注册表、成功后再启引擎</b>。中途失败最坏留下"音换了但没监听"
    /// （零常驻资源，且「Windows 原样」能完整复原）；反过来最坏会留下"钩子在跑但音没换"，噪音大得多。
    /// </para>
    /// <para>
    /// 写注册表只写<b>没被两个跳过键筛掉的那些槽</b>（当前配置 = 50 槽；改 <c>profile.json</c> 即变），
    /// 值类型一律 REG_SZ；想回到原始类型/字面值（含 22 个 ExpandString）必须走「Windows 原样」。
    /// </para>
    /// </summary>
    /// <param name="profile">方案（kind 必须是 <see cref="ProfileKind.Monitor"/>）。</param>
    /// <param name="status"><see cref="ProfileStore.Describe"/> 给出的人话状态，用于提示。</param>
    private void ApplyMonitorProfile(SoundProfile profile, string status)
    {
        var title = $"切换到「{profile.Name}」";
        var plan = ProfileStore.PlanDetailed(profile);   // 只读预演 = 会写哪些槽 + 会启哪些监听器
        int enabled = ProfileStore.EnabledRuleCount();
        int slots = plan.SlotWrites.Count;

        // 两件事都没得做才算"无事可做"：写 0 槽**且**零规则
        if (slots == 0 && plan.MonitorLineCount == 0)
        {
            MessageBox.Show(this,
                $"「{profile.Name}」当前没有可写入的提示音槽，也没有启用的监听规则，不会改动任何东西（零规则 = 零钩子）。"
                + $"{Environment.NewLine}{Environment.NewLine}{status}",
                title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            UpdateProfileStatus();
            return;
        }

        // 预览：先语音槽（前 10 条），再监听器；末尾说明一共多少条
        var preview = string.Join(Environment.NewLine, plan.Lines.Take(10));
        if (plan.Lines.Count > 10) preview += $"{Environment.NewLine}… 其余 {plan.Lines.Count - 10} 项";

        // 跳过说明要能看出是哪把刀砍的：按档（含档名）+ 按事件（含点名的事件）
        string skipText = plan.SkippedSlots.Count == 0
            ? "不跳过任何事件"
            : $"跳过 {plan.SkippedSlots.Count} 个 = 按档 {plan.SkippedByFreq?.Count ?? 0} 个 [{string.Join("/", plan.SkipFreq ?? [])}]"
              + $" + 按事件 {plan.SkippedByEvent?.Count ?? 0} 个 [{string.Join("/", (plan.SkippedByEvent ?? []).Select(s => s.Event))}]";

        var answer = MessageBox.Show(this,
            $"将做两件事：{Environment.NewLine}" +
            $"① 把 {slots} 个系统声音事件槽指向你的语音（{skipText}）；{Environment.NewLine}" +
            $"② 启动 {plan.MonitorLineCount} 个监听器（启用规则 {enabled} 条）。" +
            $"{Environment.NewLine}{Environment.NewLine}{preview}" +
            $"{Environment.NewLine}{Environment.NewLine}会写注册表（HKCU\\AppContexts 下的系统事件音）。" +
            $"「Windows 原样」可把 59 槽逐项还原回去。是否继续？",
            title, MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (answer != DialogResult.OK) return;

        ApplyResult result;
        try
        {
            result = ProfileStore.Apply(profile);
        }
        catch (PartialApplyException ex)
        {
            // 做到一半失败：必须把"可能已经改过音"说清楚，并指向复原入口
            Reload();
            _hint.Text = $"「{profile.Name}」做到一半失败";
            MessageBox.Show(this,
                $"「{profile.Name}」没有完整完成：{Environment.NewLine}{ex.Message}{Environment.NewLine}{Environment.NewLine}" +
                $"当前状态：{MonitorHost.DescribeState()}。{Environment.NewLine}" +
                $"可用「Windows 原样」把 59 个槽逐项还原回去。",
                title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Reload();
        _hint.Text = $"已切换到「{profile.Name}」：写 {result.Slots} 槽 + {result.Changed} 个监听器";

        MessageBox.Show(this,
            $"完成：{result.Slots} 个系统提示音槽已指向你的语音，{result.Changed} 个监听器已在运行。" +
            $"{Environment.NewLine}引擎状态：{MonitorHost.DescribeState()}" +
            $"{Environment.NewLine}规则表：{RuleStore.DefaultPath}" +
            $"{Environment.NewLine}（点「Windows 原样」可随时把 59 槽还原 + 停止监听）",
            title, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>
    /// 兼容旧入口（托盘曾经直接调它）：转调 <see cref="ApplyProfile"/> 的「个性化设置」。
    /// 界面上不再有独立的"应用语音方案…"按钮 —— 统一走两个方案按钮，避免两个入口两套逻辑。
    /// </summary>
    public void ApplyVoiceScheme() => ApplyProfile(ProfileStore.PersonalId);

    private void SaveSnapshot()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "保存当前声音方案快照",
            Filter = "JSON (*.json)|*.json",
            InitialDirectory = _snapshotDir,
            FileName = $"snapshot-{DateTime.Now:yyyyMMdd-HHmmss}.json",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        SchemeSnapshot.Capture("手动快照").Save(dlg.FileName);
        _hint.Text = $"快照已保存：{dlg.FileName}";
    }

    private void RestoreSnapshot()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "选择要还原的快照",
            Filter = "JSON (*.json)|*.json",
            InitialDirectory = _snapshotDir,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var snap = SchemeSnapshot.Load(dlg.FileName);

        // 旧格式快照没有类型字段，类型是加载时按值推断的 —— 必须在这里说清楚，
        // 别让用户以为这次还原是逐字节的（22 个 ExpandString 槽最容易在这点上被改坏）
        string legacyNote = snap.LegacyInferred
            ? $"{Environment.NewLine}⚠ 旧格式快照：{snap.InferredCount} 项的**类型是按值推断**的，可能不是当初的真实类型。"
            : "";

        // 快照里读不出值的槽（"键在、无值"的空壳键，或键根本不存在）还原时**不写值**，
        // 但要按键结构恢复：键在 → 删掉默认值、保留子键；键不存在 → 删键。
        // 写入条数因此会少于总槽数 —— 先说清楚，别让用户以为漏了
        string silentNote = snap.NoValueCount > 0
            ? $"{Environment.NewLine}其中 {snap.NoValueCount} 个槽在快照里就读不出值（空壳键/键不存在），"
              + "还原时按原结构恢复（删值保键 / 删键），不写值。"
            : "";

        var answer = MessageBox.Show(this,
            $"将把 {snap.Count} 个事件还原为快照状态（按快照里记下的原值类型写回）。{Environment.NewLine}" +
            $"快照时间：{snap.CreatedAt}　备注：{snap.Note}{legacyNote}{silentNote}" +
            $"{Environment.NewLine}{Environment.NewLine}是否继续？",
            "还原快照", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
        if (answer != DialogResult.OK) return;

        int n = snap.Apply();
        Reload();
        _hint.Text = snap.LegacyInferred
            ? $"已还原 {n} 个事件（旧格式快照，类型已推断）"
            : $"已还原 {n} 个事件";
    }
}
