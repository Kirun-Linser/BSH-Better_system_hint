using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace BSH.Core;

/// <summary>界面主题的种类（三档：跟随系统 / 浅色 / 深色），枚举值 0 = <see cref="System"/> 即默认档。</summary>
public enum ThemeKind
{
    /// <summary>
    /// 跟随系统（默认）：读 <c>HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize</c> 下的
    /// <c>AppsUseLightTheme</c>（DWORD，<b>0 = 深色、1 = 浅色</b>）；
    /// 键缺失 / 类型不是 DWORD / 读取失败一律<b>当作浅色</b>，绝不抛异常。
    /// </summary>
    System,

    /// <summary>浅色：沿用 Windows 系统默认配色（= 改造前的观感），按钮与下拉框交回系统视觉样式绘制。</summary>
    Light,

    /// <summary>深色：<c>#0F172A</c> 底 + <c>#F8FAFC</c> 字的暗色配色（设计库规范值）。</summary>
    Dark,
}

/// <summary>
/// 一套主题的全部颜色，按<b>用途</b>具名（不按控件命名）—— 界面各处只引用名字，不写死 RGB。
/// <para>
/// 字段与设计表一一对应：窗体背景 / 前景文字 / 卡片输入区（表格普通行）/ <b>交替行（斑马纹）</b>/
/// 弱化背景 / 弱化文字 / 边框网格线 / 选中行背景 / 选中行文字 /
/// <b>表头背景</b> / <b>表头文字</b> / 强调 / 危险。
/// </para>
/// <para>
/// ⚠️ 这里<b>没有悬停（hover）色</b>，而且不许再加回来：用户 2026-10-11 明确要求「无意义下悬停不要设计」——
/// 悬停高亮连同它的配色项（原 <c>RowHover</c>）已整体删除，表格只剩"斑马纹 + 选中行"两层区分。
/// </para>
/// <para>
/// 比设计表多出一个字段：<see cref="NativeControls"/>（浅色档把按钮 / 下拉框交回系统绘制，
/// 保证与改造前观感一致）。
/// </para>
/// </summary>
public sealed class ThemePalette
{
    /// <summary>窗体背景。</summary>
    public Color Background { get; }

    /// <summary>前景（正文）文字。</summary>
    public Color Foreground { get; }

    /// <summary>卡片 / 输入区 / 下拉框 / 表格<b>普通行</b>底色。</summary>
    public Color Surface { get; }

    /// <summary>表格<b>交替行</b>（斑马纹）底色 —— 与 <see cref="Surface"/> 拉开一点，奇偶行才分得清。</summary>
    public Color AlternatingRow { get; }

    /// <summary>弱化背景：按钮按下态、行头等次要区域。</summary>
    public Color Muted { get; }

    /// <summary>弱化文字：空槽行等次要信息。</summary>
    public Color MutedForeground { get; }

    /// <summary>边框 / 网格线。</summary>
    public Color Border { get; }

    /// <summary>选中行背景。</summary>
    public Color Selection { get; }

    /// <summary>选中行文字（必须与 <see cref="Selection"/> 成对使用）。</summary>
    public Color SelectionForeground { get; }

    /// <summary>表头背景。</summary>
    public Color HeaderBackground { get; }

    /// <summary>表头文字。</summary>
    public Color HeaderForeground { get; }

    /// <summary>强调色：主按钮 / 运行中状态。</summary>
    public Color Accent { get; }

    /// <summary>危险色：失败 / 警告（撞车）状态。</summary>
    public Color Danger { get; }

    /// <summary>
    /// true = 按钮 / 下拉框交回系统视觉样式绘制（浅色档用，保证"保留原来的浅色"）；
    /// false = 自己铺色并关掉控件上的 uxtheme 视觉样式（深色档必须，否则系统视觉样式会把深色底刷成白）。
    /// </summary>
    public bool NativeControls { get; }

    /// <summary>构造一套配色。</summary>
    /// <param name="nativeControls">是否把按钮 / 下拉框交回系统视觉样式绘制。</param>
    /// <param name="background">窗体背景。</param>
    /// <param name="foreground">前景文字。</param>
    /// <param name="surface">卡片 / 输入区 / 表格普通行底色。</param>
    /// <param name="alternatingRow">表格交替行底色。</param>
    /// <param name="muted">弱化背景。</param>
    /// <param name="mutedForeground">弱化文字。</param>
    /// <param name="border">边框 / 网格线。</param>
    /// <param name="selection">选中行背景。</param>
    /// <param name="selectionForeground">选中行文字。</param>
    /// <param name="headerBackground">表头背景。</param>
    /// <param name="headerForeground">表头文字。</param>
    /// <param name="accent">强调色。</param>
    /// <param name="danger">危险色。</param>
    public ThemePalette(
        bool nativeControls, Color background, Color foreground, Color surface, Color alternatingRow,
        Color muted, Color mutedForeground, Color border, Color selection, Color selectionForeground,
        Color headerBackground, Color headerForeground, Color accent, Color danger)
    {
        NativeControls = nativeControls;
        Background = background;
        Foreground = foreground;
        Surface = surface;
        AlternatingRow = alternatingRow;
        Muted = muted;
        MutedForeground = mutedForeground;
        Border = border;
        Selection = selection;
        SelectionForeground = selectionForeground;
        HeaderBackground = headerBackground;
        HeaderForeground = headerForeground;
        Accent = accent;
        Danger = danger;
    }

    /// <summary>按 <c>#RRGGBB</c> 写法取色（设计表给的就是这个形式）。</summary>
    /// <param name="hex">形如 <c>#0F172A</c> 的颜色串。</param>
    /// <returns>不透明的 <see cref="Color"/>（A=255）。</returns>
    public static Color Hex(string hex) => ColorTranslator.FromHtml(hex);
}

/// <summary>
/// 主题总闸：持有深浅两套配色、用户的档位选择（<see cref="Mode"/>：跟随系统 / 浅色 / 深色）
/// 与<b>实际生效的档位</b>（<see cref="Current"/>：只会是浅色或深色），
/// 以及 <c>%LOCALAPPDATA%\BSH\ui.json</c> 里的持久化。
/// <para>
/// ⚠️ <b>两个档位概念必须分清</b>：<see cref="Mode"/> 是用户选的（含"跟随系统"），
/// <see cref="Current"/> 是解析后的实际明暗。判断"现在是不是深色"一律用 <see cref="Current"/>，
/// 只有"显示用户选了什么档"（按钮文案 / 下拉选中项）才用 <see cref="Mode"/>。
/// </para>
/// <para>
/// 用法只有三句：启动时 <see cref="Reload"/> 读盘 → 面板订阅 <see cref="Changed"/> → 点按钮走 <see cref="Cycle"/>。
/// <see cref="Apply"/> 会「立即写盘 + 通知所有已开界面」，所以切换<b>不需要重启</b>。
/// </para>
/// <para>
/// 读盘路径的硬约束：<b>文件缺失、内容损坏、没权限 —— 一律回退默认档（跟随系统），绝不抛异常</b>
/// （界面不能因为一个配色文件起不来）。同理，<see cref="SystemPrefersDark"/> 读注册表也绝不抛异常。
/// </para>
/// </summary>
public static class AppTheme
{
    /// <summary>读取"系统是深色还是浅色"的注册表位置（值名见 <see cref="AppsUseLightThemeValue"/>）。</summary>
    private const string PersonalizeSubKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>系统主题开关：DWORD，<b>0 = 深色、1 = 浅色</b>。</summary>
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    /// <summary>用户选择的档位（首次访问时从 ui.json 读一次；读不到就是"跟随系统"）。</summary>
    private static ThemeKind? _mode;

    /// <summary>是否已经挂上"系统主题变化"的钩子（只挂一次；挂失败就退化为下次启动生效）。</summary>
    private static bool _systemHookInstalled;

    /// <summary>System 档下"上次解析出的深浅"，用于判断系统主题是否真的变了（无关的偏好变化不惊动界面）。</summary>
    private static ThemeKind _systemResolved = ThemeKind.Light;

    /// <summary>
    /// 深色配色：设计库规范值，<b>勿随手改</b>（改这里等于改整套视觉规范）。
    /// <para>
    /// 行区分度是这套值的核心：普通行 <c>#1B2336</c> / 交替行 <c>#151C2E</c> /
    /// 选中行 <c>#1F3A5F</c>（2026-10-11 由 <c>#264F78</c> 调暗：用户反馈「蓝色还是太亮」）。
    /// 选中行与普通行的 RGB 欧氏距离由 <b>80.1 降到 47.2</b>、与交替行 58.3 ——
    /// 明显不再刺眼，但仍远高于"一眼可辨"的门槛 35，选中态不会被看成没选中。
    /// </para>
    /// </summary>
    public static ThemePalette Dark { get; } = new(
        nativeControls: false,
        background: ThemePalette.Hex("#0F172A"),          // 窗体背景
        foreground: ThemePalette.Hex("#F8FAFC"),          // 前景文字
        surface: ThemePalette.Hex("#1B2336"),             // 表格普通行 / 输入区 / 下拉框
        alternatingRow: ThemePalette.Hex("#151C2E"),      // 表格交替行（比普通行略暗）
        muted: ThemePalette.Hex("#272F42"),               // 弱化背景（按钮按下态 / 行头）
        mutedForeground: ThemePalette.Hex("#94A3B8"),     // 弱化文字（空槽）
        border: ThemePalette.Hex("#475569"),              // 边框 / 网格线
        // 选中行背景：用户 2026-10-11 反馈「蓝色还是太亮」⇒ 由 #264F78 调暗到 #1F3A5F。
        // 与普通行 #1B2336 的 RGB 欧氏距离由 80.1 降到 47.2 —— 仍远高于"一眼可辨"的门槛（≥35），
        // 但不再刺眼。（旧的 #1E293B 只有 8.4，几乎看不出选中，那种是不合格的。）
        selection: ThemePalette.Hex("#1F3A5F"),
        selectionForeground: ThemePalette.Hex("#FFFFFF"), // 选中行文字（白字，压在深蓝上仍可读）
        headerBackground: ThemePalette.Hex("#272F42"),    // 表头背景
        headerForeground: ThemePalette.Hex("#F8FAFC"),    // 表头文字
        accent: ThemePalette.Hex("#22C55E"),              // 强调（主按钮 / 运行中）
        danger: ThemePalette.Hex("#EF4444"));             // 危险（失败 / 警告）

    /// <summary>
    /// 浅色配色：窗体与文字取系统默认色（"保留原来的浅色"），表格六色按设计表定死
    /// （普通行 <c>#FFFFFF</c> / 交替行 <c>#F7F9FC</c> / 选中行 <c>#CCE4F7</c> / 选中文字 <c>#0F172A</c> /
    /// 表头 <c>#E8EDF5</c> + <c>#0F172A</c>）。
    /// <para>
    /// 空槽灰 <c>(140,140,140)</c> 与撞车橙 <c>(196,110,0)</c> 沿用了改造前的自定值
    /// —— 它们原本就写在 <c>MainForm.Reload</c> 里，换成别的值就不叫"保留原来的浅色"了。
    /// </para>
    /// </summary>
    public static ThemePalette Light { get; } = new(
        nativeControls: true,
        background: SystemColors.Control,
        foreground: SystemColors.ControlText,
        surface: ThemePalette.Hex("#FFFFFF"),
        alternatingRow: ThemePalette.Hex("#F7F9FC"),
        muted: SystemColors.Control,
        mutedForeground: Color.FromArgb(140, 140, 140),
        border: SystemColors.ControlDark,
        selection: ThemePalette.Hex("#CCE4F7"),
        selectionForeground: ThemePalette.Hex("#0F172A"),
        headerBackground: ThemePalette.Hex("#E8EDF5"),
        headerForeground: ThemePalette.Hex("#0F172A"),
        accent: SystemColors.HotTrack,
        danger: Color.FromArgb(196, 110, 0));

    /// <summary>
    /// 用户选择的档位：<see cref="ThemeKind.System"/> / <see cref="ThemeKind.Light"/> / <see cref="ThemeKind.Dark"/>。
    /// <para>
    /// ⚠️ <b>这不是"最终生效的深浅"</b> —— 跟随系统档下它就是 <see cref="ThemeKind.System"/> 本身。
    /// 界面要判断"现在是不是深色"一律读 <see cref="Current"/>，<b>两者不要混用</b>。
    /// </para>
    /// </summary>
    public static ThemeKind Mode => _mode ??= Load();

    /// <summary>
    /// 实际生效的档位 —— <b>永远只会是 <see cref="ThemeKind.Light"/> 或 <see cref="ThemeKind.Dark"/>，绝不是 System</b>。
    /// <see cref="Mode"/> 为"跟随系统"时现读注册表解析成实际明暗，否则就等于 <see cref="Mode"/> 本身。
    /// </summary>
    public static ThemeKind Current => Mode == ThemeKind.System ? ResolveSystem() : Mode;

    /// <summary>当前生效的配色（<see cref="Current"/> 对应的那一套）。</summary>
    public static ThemePalette Palette => Current == ThemeKind.Dark ? Dark : Light;

    /// <summary>主题变化通知：<see cref="Apply"/> / <see cref="Cycle"/> 之后触发，界面收到后自己重铺颜色。</summary>
    public static event Action? Changed;

    /// <summary>用户选择的存放位置：<c>%LOCALAPPDATA%\BSH\ui.json</c>（内容形如 <c>{"theme":"System"}</c>）。</summary>
    public static string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BSH", "ui.json");

    /// <summary>
    /// 系统深浅的"探针"注入点：<b>非 null 时替代注册表读取</b>，返回值 true = 系统是深色。
    /// <para>
    /// 生产代码永远不设置它（保持 null = 走真实注册表）；存在的意义是让自检脚本能安全地模拟
    /// "系统为深色 / 键缺失回退浅色 / 系统主题变化"，而<b>不必去改注册表</b>。
    /// </para>
    /// </summary>
    public static Func<bool>? SystemDarkProbe { get; set; }

    /// <summary>
    /// 系统当前是不是深色：读 <c>AppsUseLightTheme</c>（0 = 深色、1 = 浅色）。
    /// <para><b>任何异常或读不到都返回 false（= 视为浅色）</b> —— 界面不能因为一个注册表键起不来。</para>
    /// </summary>
    /// <returns>true = 系统正在用深色主题。</returns>
    public static bool SystemPrefersDark()
    {
        var probe = SystemDarkProbe;
        if (probe is not null)
        {
            try { return probe(); }
            catch { return false; }     // 注入的探针自己炸了也当作浅色
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeSubKey, writable: false);
            // 键不在 → GetValue 返回 null → 落到最后 return false（视为浅色）
            // 值类型不是 DWORD（例如被写成字符串）→ is int 不成立 → 同样视为浅色
            if (key?.GetValue(AppsUseLightThemeValue) is int light) return light == 0;
        }
        catch
        {
            // 无权限 / 注册表被策略挡住 / 非 Windows —— 一律视为浅色
        }
        return false;
    }

    /// <summary>切换主题档位：<b>立即写盘 + 通知所有已开界面</b>（不需要重启）。</summary>
    /// <param name="kind">要切换到的档位。</param>
    public static void Apply(ThemeKind kind)
    {
        _mode = kind;
        if (kind == ThemeKind.System) EnsureSystemHook();   // 只有"跟随系统"才需要感知系统变化
        _systemResolved = ResolveSystem();
        Save(kind);
        Changed?.Invoke();
    }

    /// <summary>
    /// 三态循环：<c>跟随系统 → 深色 → 浅色 → 跟随系统</c>，返回切换后的档位。
    /// <para>面板上的「主题：…」按钮就是点一下走一遍这个循环。</para>
    /// </summary>
    /// <returns>切换后生效的档位。</returns>
    public static ThemeKind Cycle()
    {
        var next = Mode switch
        {
            ThemeKind.System => ThemeKind.Dark,
            ThemeKind.Dark => ThemeKind.Light,
            _ => ThemeKind.System,
        };
        Apply(next);
        return next;
    }

    /// <summary>旧名兼容：等价于 <see cref="Cycle"/>（三档出现前只有深 / 浅两档）。</summary>
    /// <returns>切换后生效的档位。</returns>
    public static ThemeKind Toggle() => Cycle();

    /// <summary>
    /// 重新从 ui.json 读一次并设为当前值（<b>不写盘</b>）。
    /// <para>每个面板构造时都调一次，才能保证"托盘里重开面板 / 下次启动"都沿用上次的选择。</para>
    /// </summary>
    /// <returns>读到的档位（读不到就是"跟随系统"）。</returns>
    public static ThemeKind Reload()
    {
        _mode = Load();
        _systemResolved = ResolveSystem();
        EnsureSystemHook();      // 跟随系统档要在运行期感知系统主题变化（订阅失败也不影响界面）
        return _mode.Value;
    }

    /// <summary>
    /// 把当前配色铺到一棵控件树上（按控件类型分派：窗体 / 表格 / 按钮 / 标签 / 输入框 / 下拉框 /
    /// 分组框 / 状态栏 / 容器）。
    /// <para>只改颜色与扁平化外观，<b>不碰任何事件、不碰任何数据</b>。</para>
    /// </summary>
    /// <param name="root">根控件（通常是窗体）。</param>
    public static void ApplyTo(Control root)
    {
        Paint(root);
        foreach (Control child in root.Controls) ApplyTo(child);
    }

    /// <summary>
    /// 按当前配色重新决定某个控件要不要关掉 uxtheme 视觉样式（下拉框专用）：
    /// 适合在控件句柄首次创建 / 重建时调用 —— 句柄没建之前 <c>SetWindowTheme</c> 是无效的。
    /// </summary>
    /// <param name="control">目标控件（通常是 <see cref="ComboBox"/>）。</param>
    public static void RefreshNativeTheme(Control control)
    {
        try { ApplyNativeLook(control, Palette.NativeControls); }
        catch
        {
            // 拿不到 uxtheme（非 Windows / 句柄已销毁）—— 保持系统绘制，不影响功能
        }
    }

    /// <summary>按控件类型上色（只处理认得的类型，其余保持原样）。</summary>
    private static void Paint(Control c)
    {
        var p = Palette;

        switch (c)
        {
            case Form f:                                  // 窗体本体
                f.BackColor = p.Background;
                f.ForeColor = p.Foreground;
                // 标题栏由系统绘制，改 BackColor 无效 —— 必须走 DWM（用户实测「顶端还是白色」的根因）
                ApplyTitleBar(f, !p.NativeControls);
                break;

            case DataGridView g:                          // 表格：背景 / 前景 / 表头 / 网格线 / 选中行 / 交替行
                // 表格右侧/底部的滚动条也由系统绘制（用户实测「侧栏还是白色」）—— 换深色主题
                ApplyScrollBarTheme(g, !p.NativeControls);
                g.BackgroundColor = p.Background;         // 无行区域的空白（默认是深灰 AppWorkspace，深色下会突兀）
                g.BackColor = p.Surface;
                g.ForeColor = p.Foreground;
                g.GridColor = p.Border;
                g.DefaultCellStyle.BackColor = p.Surface;                        // 普通行
                g.DefaultCellStyle.ForeColor = p.Foreground;
                g.DefaultCellStyle.SelectionBackColor = p.Selection;             // 选中行（必须一眼可辨）
                g.DefaultCellStyle.SelectionForeColor = p.SelectionForeground;
                g.AlternatingRowsDefaultCellStyle.BackColor = p.AlternatingRow;  // 交替行（斑马纹）
                g.AlternatingRowsDefaultCellStyle.ForeColor = p.Foreground;
                g.AlternatingRowsDefaultCellStyle.SelectionBackColor = p.Selection;
                g.AlternatingRowsDefaultCellStyle.SelectionForeColor = p.SelectionForeground;
                g.ColumnHeadersDefaultCellStyle.BackColor = p.HeaderBackground;  // 表头
                g.ColumnHeadersDefaultCellStyle.ForeColor = p.HeaderForeground;
                g.ColumnHeadersDefaultCellStyle.SelectionBackColor = p.HeaderBackground;  // 表头被点选时不许变蓝
                g.ColumnHeadersDefaultCellStyle.SelectionForeColor = p.HeaderForeground;
                g.RowHeadersDefaultCellStyle.BackColor = p.HeaderBackground;
                // 表头一律自己上色：只有关掉视觉样式，设计表里的表头色（深 #272F42 / 浅 #E8EDF5）才真正生效
                //（打开时表头由系统绘制，设了颜色也读得到、看不到 —— 两档都会被这句骗过去）
                g.EnableHeadersVisualStyles = false;
                break;

            case Button b:                                // 按钮
                b.ForeColor = p.Foreground;
                // 浅色档由系统视觉样式绘制，此值不参与绘制 —— 取系统按钮底色只是避免读出来是"白按钮"造成误读
                b.BackColor = p.NativeControls ? SystemColors.Control : p.Surface;
                b.UseVisualStyleBackColor = p.NativeControls;
                b.FlatStyle = p.NativeControls ? FlatStyle.Standard : FlatStyle.Flat;
                if (!p.NativeControls)
                {
                    b.FlatAppearance.BorderColor = p.Border;
                    b.FlatAppearance.MouseOverBackColor = p.Selection;
                    b.FlatAppearance.MouseDownBackColor = p.Muted;
                }
                break;

            case TextBox t:                               // 输入框：BackColor 在所有样式下都生效，不用关视觉样式
                t.BackColor = p.Surface;
                t.ForeColor = p.Foreground;
                t.BorderStyle = p.NativeControls ? BorderStyle.Fixed3D : BorderStyle.FixedSingle;
                break;

            case ComboBox cb:                             // 下拉框：见 ApplyNativeLook 的注释（深色必须关视觉样式）
                cb.BackColor = p.Surface;
                cb.ForeColor = p.Foreground;
                cb.FlatStyle = p.NativeControls ? FlatStyle.Standard : FlatStyle.Flat;
                ApplyNativeLook(cb, p.NativeControls);
                EnsureDropdownThemeHook(cb);              // 下拉列表（ComboLBox）展开时才存在 → 挂 DropDown 钩子
                break;

            case GroupBox gb:                             // 分组框
                gb.BackColor = p.Background;
                gb.ForeColor = p.Foreground;
                break;

            case StatusStrip ss:                          // 状态栏：条 + 每一格文字
                ss.BackColor = p.Background;
                ss.ForeColor = p.Foreground;
                foreach (ToolStripItem item in ss.Items) item.ForeColor = p.Foreground;
                break;

            case ToolStrip ts:                            // 其它工具条
                ts.BackColor = p.Background;
                ts.ForeColor = p.Foreground;
                break;

            case ListView lv:                             // 万一日后有人添了列表控件
                lv.BackColor = p.Surface;
                lv.ForeColor = p.Foreground;
                break;

            case Label lb:                                // 标签：只改字色，底色随父容器
                lb.ForeColor = p.Foreground;
                lb.BackColor = Color.Transparent;
                break;

            case Panel panel:                             // 按钮行 / 根布局（TableLayoutPanel、FlowLayoutPanel 都是 Panel 子类）
                panel.BackColor = p.Background;
                panel.ForeColor = p.Foreground;
                break;
        }
    }

    /// <summary>
    /// 下拉框的深色关键一步：<b>关掉控件上的 uxtheme 视觉样式</b>。
    /// <para>
    /// 只设 <c>FlatStyle = Flat</c> + <c>BackColor</c> 是不够的 —— 只要视觉样式还在，
    /// <see cref="ComboBox"/> 的显示区与下拉列表仍会被系统画成白底（这正是"深色下面板顶部两个下拉框还是白的"的根因）。
    /// 关掉之后 <c>BackColor</c> / <c>ForeColor</c> 才真正参与绘制（下拉列表项若日后需要自绘，
    /// 得再挂一个 <c>DrawItem</c> 处理器，本面板当前没有下拉框）。
    /// </para>
    /// <para>浅色档传 <c>null</c> 把视觉样式恢复回去，保证"浅色 = 系统原生观感"。</para>
    /// </summary>
    /// <param name="c">目标控件。</param>
    /// <param name="native">true = 交回系统视觉样式；false = 关闭视觉样式自己上色。</param>
    private static void ApplyNativeLook(Control c, bool native)
    {
        if (!c.IsHandleCreated) return;      // 句柄未建：等 HandleCreated 时由 RefreshNativeTheme 再补一次
        _ = SetWindowTheme(c.Handle, native ? null : " ", native ? null : " ");
    }

    /// <summary>解析"跟随系统"的实际深浅（读不到就是浅色）。</summary>
    private static ThemeKind ResolveSystem() => SystemPrefersDark() ? ThemeKind.Dark : ThemeKind.Light;

    /// <summary>
    /// 挂"系统主题变化"钩子（只挂一次）：<c>AppsUseLightTheme</c> 被改时 Windows 会广播
    /// <see cref="SystemEvents.UserPreferenceChanged"/>，我们在处理器里比对解析结果，
    /// 只有深浅真的变了才广播 <see cref="Changed"/>。
    /// <para>
    /// ⚠️ 订阅失败（宿主没有消息泵 / 平台不支持）时<b>不抛异常、不重试</b> —— 退化为"下次启动生效"，
    /// 界面本身照常工作。
    /// </para>
    /// </summary>
    private static void EnsureSystemHook()
    {
        if (_systemHookInstalled) return;
        try
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _systemResolved = ResolveSystem();
            _systemHookInstalled = true;
        }
        catch
        {
            // 订阅不上就退化为"下次启动生效"（这是刻意选择的降级路径，不是错误）
        }
    }

    /// <summary>
    /// 系统偏好变化：只在"跟随系统"档、且深浅真的变了时才通知界面重铺颜色。
    /// <para>⚠️ 这个回调可能不在 UI 线程上（SystemEvents 有自己的消息窗口线程），
    /// 所以界面侧必须自己 <c>Invoke</c> 回主线程（见 <c>MainForm.ApplyThemeToUi</c>）。</para>
    /// </summary>
    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (Mode != ThemeKind.System) return;

        var resolved = ResolveSystem();
        if (resolved == _systemResolved) return;   // 与深浅无关的偏好变化（壁纸 / 鼠标速度…）不惊动界面

        _systemResolved = resolved;
        Changed?.Invoke();
    }

    /// <summary>
    /// 读 ui.json。任何异常（文件不在、JSON 坏了、没权限）都吞掉并回退默认档（跟随系统）—— 这是硬约束。
    /// <para>兼容旧文件：只有 <c>"Light"</c> / <c>"Dark"</c> 的旧格式照样能读（枚举名没变）。</para>
    /// </summary>
    private static ThemeKind Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return ThemeKind.System;

            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("theme", out var v)
                && v.ValueKind == JsonValueKind.String
                && Enum.TryParse<ThemeKind>(v.GetString(), ignoreCase: true, out var kind))
            {
                return kind;
            }
        }
        catch
        {
            // 文件损坏（例如只剩半个花括号 {）/ 读不到 —— 回退默认档，界面照常起来
        }
        return ThemeKind.System;
    }

    /// <summary>写 ui.json。写盘失败只影响"下次启动记不记得"，不值得让切换动作失败。</summary>
    private static void Save(ThemeKind kind)
    {
        try
        {
            string? dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsPath, $"{{\"theme\":\"{kind}\"}}");
        }
        catch
        {
            // 忽略：主题切换本身已经生效（在内存里），只是没记住
        }
    }

    /// <summary>
    /// 把窗口<b>标题栏</b>切成深色 / 浅色（<c>dwmapi!DwmSetWindowAttribute</c> + <c>DWMWA_USE_IMMERSIVE_DARK_MODE</c>）。
    ///
    /// <para><b>为什么必须这样改</b>：标题栏是<b>非工作区</b>，由 DWM 绘制，改 <c>Form.BackColor</c> 完全无效 ——
    /// 这正是用户实测「顶端还是白色」的根因。</para>
    ///
    /// <para>⚠️ 该属性编号随 Windows 版本不同：<b>20</b>（Win10 2004+ / Win11）与 <b>19</b>（Win10 1903–1909）。
    /// 这里<b>逐个试</b>而不做版本判断 —— 版本判断本身就会成为新的失败点。</para>
    ///
    /// <para>失败（老系统没这个属性 / 句柄未就绪）一律<b>静默忽略</b>：标题栏颜色属于外观，不该影响功能。</para>
    /// <para>⚠️ 本方法刻意<b>不抛异常也不返回错误码</b>（外观问题不该打扰调用方）；真实 HRESULT 与生效的属性号
    /// 留在 <see cref="LastTitleBarHresult"/> / <see cref="LastTitleBarAttribute"/> / <see cref="LastTitleBarTrace"/> 里，
    /// 供自检脚本取证 —— 验收时要贴的就是这几个值。</para>
    /// </summary>
    /// <param name="form">目标窗体（需已创建句柄）。</param>
    /// <param name="dark">true = 深色标题栏。</param>
    public static void ApplyTitleBar(Form form, bool dark)
    {
        if (form is null || !form.IsHandleCreated)
        {
            LastTitleBarAttribute = 0;
            LastTitleBarHresult = EHandle;
            LastTitleBarTrace = "句柄未创建：跳过（等 HandleCreated 后再调）";
            return;
        }

        int value = dark ? 1 : 0;      // 深色档传 1，浅色档传 0 —— 用户看的标题栏颜色就是这一位
        try
        {
            int modern = DwmSetWindowAttribute(form.Handle, 20, ref value, sizeof(int));
            if (modern == 0)
            {
                LastTitleBarAttribute = 20;     // Windows 10 2004+ / Windows 11
                LastTitleBarHresult = 0;
                LastTitleBarTrace = "attr20=S_OK";
                return;
            }

            // 20 号不认（老系统返回 E_INVALIDARG）→ 退回 1903–1909 的 19 号再试一次
            int legacy = DwmSetWindowAttribute(form.Handle, 19, ref value, sizeof(int));
            LastTitleBarAttribute = legacy == 0 ? 19 : 0;
            LastTitleBarHresult = legacy == 0 ? 0 : modern;
            LastTitleBarTrace = $"attr20={HresultText(modern)} → attr19={HresultText(legacy)}";
        }
        catch (Exception ex)
        {
            // 见上方说明：外观失败不该冒泡（dwmapi 缺失 / 非 Windows 会走到这里）
            LastTitleBarAttribute = 0;
            LastTitleBarHresult = unchecked((int)0x80004005);
            LastTitleBarTrace = $"调用失败（已静默降级）：{ex.GetType().Name}";
        }
    }

    /// <summary>最近一次标题栏调用<b>真正生效</b>的属性号：20（Win10 2004+ / Win11）/ 19（Win10 1903–1909）/ 0（都没成功）。</summary>
    public static int LastTitleBarAttribute { get; private set; }

    /// <summary>最近一次标题栏调用的 HRESULT（0 = S_OK；<see cref="EHandle"/> = 句柄还没建；其它负值 = 失败）。</summary>
    public static int LastTitleBarHresult { get; private set; } = EHandle;

    /// <summary>最近一次标题栏调用的取证轨迹（形如 <c>attr20=S_OK</c> 或 <c>attr20=0x80070057(E_INVALIDARG) → attr19=S_OK</c>）。</summary>
    public static string LastTitleBarTrace { get; private set; } = "（尚未调用）";

    /// <summary>最近一次"下拉列表换主题"的 HRESULT（0 = S_OK；<see cref="EHandle"/> = 没做过 / 没拿到列表句柄）。</summary>
    public static int LastDropdownHresult { get; private set; } = EHandle;

    /// <summary>句柄未就绪时返回的 HRESULT（<c>E_HANDLE</c>）—— 用来把"没做"与"做了但失败"区分开。</summary>
    public const int EHandle = unchecked((int)0x80070006);

    /// <summary>HRESULT → 取证用短名（0 = S_OK，其余给出系统消息）。</summary>
    /// <param name="hr">HRESULT。</param>
    private static string HresultText(int hr)
    {
        if (hr == 0) return "S_OK";
        try { return $"0x{hr:X8}({Marshal.GetExceptionForHR(hr)?.Message ?? "未知 HRESULT"})"; }
        catch { return $"0x{hr:X8}"; }
    }

    /// <summary>
    /// 把控件的<b>滚动条</b>切成深色 / 恢复原生（<c>uxtheme!SetWindowTheme</c>）。
    ///
    /// <para>与 <see cref="ApplyNativeLook"/> 的区别很关键：那个是"<b>关掉</b>视觉样式"（传空格），
    /// 本方法却是"<b>换成深色主题</b>"（传 <c>DarkMode_Explorer</c>）。
    /// 滚动条<b>必须</b>用后者 —— 关掉视觉样式只会得到又粗又老式的灰滚动条，比白色更难写。</para>
    ///
    /// <para>⚠️ <c>DarkMode_Explorer</c> 是<b>未文档化</b>的主题名（社区通行做法）。失败时静默保持系统默认。</para>
    /// </summary>
    /// <param name="control">目标控件（如 DataGridView）。</param>
    /// <param name="dark">true = 深色滚动条。</param>
    public static void ApplyScrollBarTheme(Control control, bool dark)
    {
        if (control is null || !control.IsHandleCreated)
        {
            LastScrollBarTrace = "句柄未创建：跳过（等 HandleCreated 后再调）";
            LastScrollBarHresult = EHandle;
            return;
        }

        try
        {
            // 返回值就是 HRESULT：0 = S_OK。留痕供自检取证（验收要贴的就是它）。
            LastScrollBarHresult = SetWindowTheme(control.Handle, dark ? "DarkMode_Explorer" : "Explorer", null);
            LastScrollBarTrace = $"SetWindowTheme(\"{(dark ? "DarkMode_Explorer" : "Explorer")}\") → {HresultText(LastScrollBarHresult)}";
        }
        catch (Exception ex)
        {
            // 未文档化 API：失败就保持系统默认
            LastScrollBarHresult = unchecked((int)0x80004005);
            LastScrollBarTrace = $"调用失败（已静默降级）：{ex.GetType().Name}";
        }
    }

    /// <summary>最近一次滚动条换主题的 HRESULT（0 = S_OK；<see cref="EHandle"/> = 句柄还没建）。</summary>
    public static int LastScrollBarHresult { get; private set; } = EHandle;

    /// <summary>最近一次滚动条换主题的取证轨迹（形如 <c>SetWindowTheme("DarkMode_Explorer") → S_OK</c>）。</summary>
    public static string LastScrollBarTrace { get; private set; } = "（尚未调用）";

    /// <summary>
    /// 给下拉框的<b>下拉列表</b>（ComboLBox）挂"换深色主题"的钩子 —— <b>每个下拉框只会挂一次</b>。
    /// <para>
    /// 为什么必须挂在 <see cref="ComboBox.DropDown"/> 上：下拉列表是<b>独立子窗口</b>，而且<b>展开时才创建</b>，
    /// 所以拿不到它的句柄就没法换主题；展开那一刻用 <c>CB_GETCOMBOBOXINFO</c> 取 <c>hwndList</c> 再换最稳。
    /// </para>
    /// <para>失败静默：拿不到句柄就保持系统默认（浅色）滚动条，功能不受影响。</para>
    /// </summary>
    /// <param name="cb">目标下拉框。</param>
    public static void EnsureDropdownThemeHook(ComboBox cb)
    {
        if (cb is null || DropdownHooked.TryGetValue(cb, out _)) return;   // 已经挂过

        try
        {
            cb.DropDown += (sender, _) =>
            {
                if (sender is ComboBox box) LastDropdownHresult = ThemeDropdownList(box);
            };
            DropdownHooked.Add(cb, DropdownHooked);   // 值只是"已挂"的标记，不参与逻辑
        }
        catch
        {
            // 控件已释放 / 记账失败：下拉列表保持默认滚动条
        }
    }

    /// <summary>给下拉列表换主题（取不到列表句柄 = <see cref="EHandle"/>；成功 = 0）。</summary>
    /// <param name="cb">目标下拉框。</param>
    private static int ThemeDropdownList(ComboBox cb)
    {
        IntPtr list = TryGetDropdownListHandle(cb);
        if (list == IntPtr.Zero) return EHandle;

        try { return SetWindowTheme(list, Current == ThemeKind.Dark ? "DarkMode_Explorer" : "Explorer", null); }
        catch { return unchecked((int)0x80004005); }
    }

    /// <summary>用 <c>CB_GETCOMBOBOXINFO</c> 取下拉列表的窗口句柄（取不到返回 <see cref="IntPtr.Zero"/>）。</summary>
    /// <param name="cb">目标下拉框。</param>
    private static IntPtr TryGetDropdownListHandle(ComboBox cb)
    {
        try
        {
            if (!cb.IsHandleCreated) return IntPtr.Zero;
            var info = new ComboBoxInfo { CbSize = Marshal.SizeOf<ComboBoxInfo>() };
            _ = SendMessage(cb.Handle, CbGetComboBoxInfo, IntPtr.Zero, ref info);
            return info.HwndList;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>已挂过下拉列表钩子的下拉框（弱表：控件被回收时不成为障碍）。</summary>
    private static readonly ConditionalWeakTable<ComboBox, object> DropdownHooked = new();

    /// <summary><c>CB_GETCOMBOBOXINFO</c> 的消息号。</summary>
    private const int CbGetComboBoxInfo = 0x0164;

    /// <summary><c>COMBOBOXINFO</c>：<c>CB_GETCOMBOBOXINFO</c> 的返回结构（只用到 <see cref="ComboBoxInfo.HwndList"/>）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ComboBoxInfo
    {
        /// <summary>结构体字节数：调用前必须填 <c>Marshal.SizeOf</c>，否则系统不认。</summary>
        public int CbSize;

        /// <summary>编辑区矩形。</summary>
        public Rect RcItem;

        /// <summary>下拉按钮矩形。</summary>
        public Rect RcButton;

        /// <summary>下拉按钮状态。</summary>
        public int StateButton;

        /// <summary>下拉框自身句柄。</summary>
        public IntPtr HwndCombo;

        /// <summary>编辑区 / 静态文本句柄。</summary>
        public IntPtr HwndItem;

        /// <summary><b>下拉列表句柄</b>（要的就是它）。</summary>
        public IntPtr HwndList;
    }

    /// <summary>Win32 <c>RECT</c>。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        /// <summary>左。</summary>
        public int Left;

        /// <summary>上。</summary>
        public int Top;

        /// <summary>右。</summary>
        public int Right;

        /// <summary>下。</summary>
        public int Bottom;
    }

    /// <summary>取下拉列表句柄用的 <c>user32!SendMessageW</c>。</summary>
    /// <param name="hWnd">下拉框句柄。</param>
    /// <param name="msg">消息号（<see cref="CbGetComboBoxInfo"/>）。</param>
    /// <param name="wParam">wParam（这里不用）。</param>
    /// <param name="lParam">接收 <c>COMBOBOXINFO</c> 的结构体。</param>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref ComboBoxInfo lParam);

    /// <summary>DWM：设置窗口非工作区（标题栏）属性。0 = S_OK。</summary>
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hWnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    /// <summary>
    /// 关掉 / 恢复某个窗口的视觉样式（<c>uxtheme!SetWindowTheme</c>）。
    /// <para>传 <c>" "</c>（一个空格）而不是空串：空串会被系统当成"没传"而忽略，空格才是通行的关闭写法。</para>
    /// </summary>
    /// <param name="hWnd">目标窗口句柄。</param>
    /// <param name="pszSubAppName">子应用名（null = 恢复默认主题）。</param>
    /// <param name="pszSubIdList">子 ID 列表（null = 恢复默认主题）。</param>
    /// <returns>HRESULT（0 = 成功；失败也不抛，调用方忽略）。</returns>
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetWindowTheme(IntPtr hWnd, string? pszSubAppName, string? pszSubIdList);
}
