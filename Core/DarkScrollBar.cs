using System.Drawing.Drawing2D;

namespace BSH.Core;

/// <summary>
/// 深色滚动条：与 <see cref="DataGridView"/> 的滚动位置双向同步，用来替代它自带的系统浅色滚动条。
/// <para>
/// <b>为什么需要它</b>（2026-10-11 探针实测）：<see cref="DataGridView"/> 的滚动条由它<b>自己绘制</b>，
/// 不是标准 <see cref="ScrollBar"/> 外观 —— <c>SetWindowTheme(handle, "DarkMode_Explorer")</c> 虽然返回
/// <c>S_OK</c>，但 <c>AllowDarkModeForWindow</c> 返回 <c>False</c>，所以那条滚动条永远是系统浅色，
/// 深色主题下是一条白刺。</para>
/// <para>
/// <b>为什么不能简单地 <c>ScrollBars = None</c></b>（同一次探针的对照实验）：那样不仅没有滚动条，
/// <b>鼠标滚轮也一起失效</b> —— 实测发 <c>WM_MOUSEWHEEL</c> 后 <c>FirstDisplayedScrollingRowIndex</c>
/// 保持 0 不变；而 <c>ScrollBars = Vertical</c> 时同一条消息让它从 0 变成 5。
/// 59 行的表只显示得下 20 行，关掉滚动条的代价是后 39 行<b>根本够不着</b>。</para>
/// <para>
/// <b>做法</b>：表格保持 <c>ScrollBars = Vertical</c>（滚轮 / 键盘 / 原生滚动逻辑全都留着），
/// 本控件按<b>实测坐标</b>压在它自带那条滚动条的正上方并提到最前（见 <c>MainForm.PlaceScrollBar</c>）。
/// </para>
/// <para>
/// 为什么不干脆把自带滚动条藏起来（<c>Visible = false</c>，实测过：藏起来确实不影响滚轮）：
/// 它的可见性由 <see cref="DataGridView"/> 自己维护，<b>藏不住</b> —— 探针实测「<c>Reload</c> 末尾 +
/// <c>Layout</c> 事件」两处同步隐藏，最终仍测到「原生滚动条可见数 = 1」。而盖住它不依赖任何时序，
/// 它占的那点宽度也正是本控件要用的宽度，数据区可见宽度不变。
/// </para>
/// <para>
/// <b>值的口径</b>：一律用<b>行号</b>而不是像素 —— 内部滚动条的 <c>Value</c> 是像素
/// （实测 <c>Maximum=1534</c> 正好等于 59 行 × 26px），而 <c>Value = index × 行高</c>，
/// 两者在行高固定时等价，但行号制不必依赖行高，也不会在行高变化后错位。
/// </para>
/// <para>
/// <b>配色</b>：只用 <see cref="ThemePalette"/> 里已有的两个语义色 —— 槽用 <see cref="ThemePalette.Muted"/>
/// （弱化背景），滑块用 <see cref="ThemePalette.Border"/>（边框）。<b>刻意不做悬停高亮</b>：
/// 用户 2026-10-11 明确要求「无意义下悬停不要设计」，配色类文档也写明不许再加 hover 色。
/// </para>
/// </summary>
public sealed class DarkScrollBar : Control
{
    /// <summary>滑块的最小高度（行数很少时也要留得出可抓的一段）。</summary>
    private const int MinThumbHeight = 24;

    /// <summary>滑块左右各留的边距。</summary>
    private const int ThumbInset = 2;

    private int _value;
    private int _maximum = 1;
    private int _largeChange = 1;
    private bool _dragging;
    private int _dragStartY;
    private int _dragStartValue;

    /// <summary>建一个空滚动条；颜色与位置由宿主设置。</summary>
    public DarkScrollBar()
    {
        // UserPaint + 双缓冲：轨道与滑块都在 OnPaint 里自己画，拖动时不闪
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        TabStop = false;      // 它是纯鼠标控件，不参与 Tab 焦点链（表格才是键盘导航的主体）
    }

    /// <summary>当前值 = 表格的首行索引（0-based）。</summary>
    public int Value => _value;

    /// <summary>可滚动到的最大首行索引（= 总行数 − 可见行数）。</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Maximum
    {
        get => _maximum;
        set
        {
            _maximum = Math.Max(0, value);
            _value = Math.Clamp(_value, 0, _maximum);
            Invalidate();
        }
    }

    /// <summary>可见行数：决定滑块长度（滑块 = 全部行里"看得见"的那一段）。</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int LargeChange
    {
        get => _largeChange;
        set { _largeChange = Math.Max(1, value); Invalidate(); }
    }

    /// <summary>由<b>用户操作</b>（拖动 / 点轨道 / 滚轮）改变值后触发；程序同步调用不触发，避免回环。</summary>
    public event EventHandler? Scrolled;

    /// <summary>由表格反向同步进来（不触发 <see cref="Scrolled"/>，否则会和表格互相踢皮球）。</summary>
    /// <param name="value">新的首行索引。</param>
    public void SyncFrom(int value)
    {
        int v = Math.Clamp(value, 0, _maximum);
        if (v == _value) { Invalidate(); return; }
        _value = v;
        Invalidate();
    }

    /// <summary>滑块高度（像素）。</summary>
    private int ThumbHeight()
    {
        int total = _maximum + _largeChange;              // ≈ 总行数
        if (total <= 0) return Height;
        int h = (int)((long)Height * _largeChange / total);
        return Math.Clamp(h, MinThumbHeight, Math.Max(MinThumbHeight, Height));
    }

    /// <summary>滑块顶边（像素）。</summary>
    private int ThumbTop(int thumbHeight)
    {
        if (_maximum <= 0) return 0;
        int span = Height - thumbHeight;
        if (span <= 0) return 0;
        return (int)((long)span * _value / _maximum);
    }

    /// <inheritdoc/>
    protected override void OnPaint(PaintEventArgs e)
    {
        var p = AppTheme.Palette;
        // 槽（表格右侧那条侧边）：2026-10-11 用户要求「侧边的底色也换成纯黑」
        // —— 原来用 Palette.Muted(#272F42) 带蓝调，现按用户指定改为纯黑。
        e.Graphics.Clear(Color.Black);

        int thumb = ThumbHeight();
        int top = ThumbTop(thumb);
        var rect = new Rectangle(ThumbInset, top, Math.Max(1, Width - ThumbInset * 2), thumb);

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedRect(rect, Math.Min(4, rect.Width / 2));
        using var brush = new SolidBrush(p.Border);       // 滑块：边框色
        e.Graphics.FillPath(brush, path);
    }

    /// <summary>圆角矩形路径（半径会自动收敛到不超出矩形的一半）。</summary>
    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Max(1, Math.Min(radius, Math.Min(r.Width, r.Height) / 2)) * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>改值并把变化告诉表格（用户操作专用）。</summary>
    private void MoveBy(int delta)
    {
        int v = Math.Clamp(_value + delta, 0, _maximum);
        if (v == _value) return;
        _value = v;
        Invalidate();
        Scrolled?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc/>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;

        int thumb = ThumbHeight();
        int top = ThumbTop(thumb);

        if (e.Y >= top && e.Y < top + thumb)
        {
            // 抓住滑块：记下起点，之后按位移等比换算
            _dragging = true;
            _dragStartY = e.Y;
            _dragStartValue = _value;
            Capture = true;                              // 拖出控件范围也不丢事件
        }
        else
        {
            // 点空白轨道：朝该方向翻一屏（少一页，保证目标行一定进入视野）
            MoveBy((e.Y < top ? -1 : 1) * Math.Max(1, _largeChange - 1));
        }
    }

    /// <inheritdoc/>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;

        int thumb = ThumbHeight();
        int span = Height - thumb;
        if (span <= 0) return;
        int wanted = _dragStartValue + (int)((long)(e.Y - _dragStartY) * _maximum / span);
        MoveBy(Math.Clamp(wanted, 0, _maximum) - _value);
    }

    /// <inheritdoc/>
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging) return;
        _dragging = false;
        Capture = false;
    }

    /// <inheritdoc/>
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        // 鼠标停在滚动条上滚轮也要能用；步长跟系统设置一致（读不到就 3 行）
        int lines = SystemInformation.MouseWheelScrollLines;
        if (lines <= 0) lines = 3;
        MoveBy(e.Delta > 0 ? -lines : lines);
    }
}
