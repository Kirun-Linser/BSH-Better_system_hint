namespace BSH.Core;

/// <summary>
/// 托盘右键菜单的深色渲染器。
/// <para>
/// <b>为什么必须单独做这一套</b>：<see cref="ContextMenuStrip"/> <b>不是控件树的成员</b> ——
/// 它挂在 <see cref="NotifyIcon"/> 上（而不是某个 <see cref="Control"/> 的 <c>Controls</c> 里），
/// 所以 <see cref="AppTheme.ApplyTo"/> 那趟递归遍历<b>永远碰不到它</b>，
/// 深色档下它一直是系统默认的白底黑字（2026-10-11 实测发现）。
/// 菜单的配色只能通过 <see cref="ToolStripRenderer"/> 接管。
/// </para>
/// <para>
/// 配色全部取自 <see cref="AppTheme.Palette"/> 里已有的语义色（Surface / Border / Selection /
/// Muted / Foreground / MutedForeground），<b>不引入任何新色值</b>；
/// 而且每次绘制时才读 Palette，所以主题切换后只要重画即可，不需要重建渲染器。
/// </para>
/// </summary>
public sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    /// <summary>建一个跟随当前主题的菜单渲染器。</summary>
    public DarkMenuRenderer() : base(new DarkColorTable()) { }

    /// <inheritdoc/>
    /// <remarks>菜单项文字：可用项用前景色，禁用项（如第一行那个「当前状态」）用弱化色。</remarks>
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        var p = AppTheme.Palette;
        e.TextColor = e.Item.Enabled ? p.Foreground : p.MutedForeground;
        base.OnRenderItemText(e);
    }

    /// <inheritdoc/>
    /// <remarks>子菜单箭头默认是黑的，深色底上看不见，必须跟着改成前景色。</remarks>
    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = AppTheme.Palette.Foreground;
        base.OnRenderArrow(e);
    }

    /// <summary>把 <see cref="ProfessionalColorTable"/> 的各个槽位映射到主题配色。</summary>
    private sealed class DarkColorTable : ProfessionalColorTable
    {
        private static ThemePalette P => AppTheme.Palette;

        /// <summary>菜单外框。</summary>
        public override Color MenuBorder => P.Border;

        /// <summary>悬停项的外框（不再用蓝色：用户反馈那道蓝刺眼）。</summary>
        public override Color MenuItemBorder => P.Border;

        /// <summary>
        /// 悬停 / 高亮项的背景。
        /// <para>2026-10-11 用户反馈「蓝色太刺眼」⇒ 不再用 <see cref="ThemePalette.Selection"/>（#1F3A5F），
        /// 改用 <see cref="ThemePalette.AlternatingRow"/>（#151C2E = RGB 21,28,46，正是用户指定的值）
        /// —— 它本来就是调色板里的既有语义色，不额外引入新色值。表格的选中行不受影响，仍是原来的蓝。</para>
        /// </summary>
        public override Color MenuItemSelected => P.AlternatingRow;

        public override Color MenuItemSelectedGradientBegin => P.AlternatingRow;
        public override Color MenuItemSelectedGradientEnd => P.AlternatingRow;

        /// <summary>按下态：比悬停稍暗，用弱化背景。</summary>
        public override Color MenuItemPressedGradientBegin => P.Muted;
        public override Color MenuItemPressedGradientEnd => P.Muted;

        /// <summary>下拉面板本体（菜单的「纸面」）。</summary>
        public override Color ToolStripDropDownBackground => P.Surface;

        /// <summary>左侧图标边条：与面板同色系，避免出现一条突兀的浅灰。</summary>
        public override Color ImageMarginGradientBegin => P.Muted;
        public override Color ImageMarginGradientMiddle => P.Muted;
        public override Color ImageMarginGradientEnd => P.Muted;

        /// <summary>顶部分组条（本菜单没用到，一并给上以防日后加）。</summary>
        public override Color MenuStripGradientBegin => P.Muted;
        public override Color MenuStripGradientEnd => P.Muted;

        /// <summary>分隔线。</summary>
        public override Color SeparatorDark => P.Border;
        public override Color SeparatorLight => P.Border;

        /// <summary>勾选项背景（为将来可能加的可勾选菜单项准备）。</summary>
        public override Color CheckBackground => P.Selection;
        public override Color CheckSelectedBackground => P.Selection;
        public override Color CheckPressedBackground => P.Selection;

        /// <summary>移动手柄与溢出按钮：深色下都要压暗，否则会是两条灰线。</summary>
        public override Color GripDark => P.Border;
        public override Color GripLight => P.Muted;
        public override Color OverflowButtonGradientBegin => P.Muted;
        public override Color OverflowButtonGradientEnd => P.Muted;

        /// <summary>下拉面板的边框。</summary>
        public override Color ToolStripBorder => P.Border;
    }
}
