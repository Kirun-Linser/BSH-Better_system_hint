using BSH.Core;
using BSH.Core.Monitoring;

namespace BSH;

/// <summary>
/// 托盘常驻：NotifyIcon + 右键菜单；主窗口关闭按钮只隐藏，不退出程序。
/// <para>
/// 监听引擎的<b>生命周期归这里管</b>：启动时<b>不</b>自动开引擎（没明确要求就不该装钩子），
/// 用户点「个性化设置」才起；切「Windows 原样」或点「退出」时必须停干净。
/// 菜单与托盘提示上的方案名 / 引擎状态 / 启用规则数，每次弹出菜单前现算一次（<see cref="RefreshState"/>）。
/// </para>
/// </summary>
public sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu = new();

    /// <summary>菜单第一行：只读的"当前状态"（禁用态，仅用于显示）。</summary>
    private readonly ToolStripMenuItem _stateItem = new() { Enabled = false };

    private readonly ToolStripMenuItem _defaultItem = new();
    private readonly ToolStripMenuItem _personalItem = new();
    private readonly ToolStripMenuItem _takeoverItem = new();
    private readonly ToolStripMenuItem _autoStartItem = new() { CheckOnClick = true };
    private readonly ToolStripMenuItem _statusItem = new();
    private MainForm? _form;

    public TrayContext()
    {
        // 三套方案入口放最前：换整套音效是最高频动作
        _defaultItem.Click += (_, _) => ApplyProfile(ProfileStore.WindowsDefaultId);
        _personalItem.Click += (_, _) => ApplyProfile(ProfileStore.PersonalId);
        _takeoverItem.Click += (_, _) => ApplyProfile(ProfileStore.TakeoverId);
        _statusItem.Click += (_, _) => ShowMonitorStatus();

        _menu.Items.Add(_stateItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_defaultItem);
        _menu.Items.Add(_personalItem);
        _menu.Items.Add(_takeoverItem);
        _menu.Items.Add("打开面板…", null, (_, _) => ShowForm());
        _menu.Items.Add("监听规则（可视化编辑）…", null, (_, _) =>
        {
            ShowForm();
            _form?.OpenRules();
        });
        _menu.Items.Add("一键重排（先自动快照）…", null, (_, _) =>
        {
            ShowForm();
            _form?.RunReorder();
        });
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_statusItem);
        _menu.Items.Add("打开音效素材目录", null, (_, _) => OpenMediaDir());
        _menu.Items.Add("打开快照目录", null, (_, _) => OpenSnapshotDir());

        // 开机自启动：勾选即写 HKCU 的 Run 键（用户级，不需要管理员）。
        // 与「BSH 接管」是一对：接管把提示音交给 BSH 判断，那就得保证 BSH 开机就在跑。
        // 2026-10-18 起主面板也有同一个开关，两边靠 AutoStart.Changed 双向同步。
        _autoStartItem.Text = "开机自启动";
        _autoStartItem.Checked = AutoStart.IsEnabled;
        _autoStartItem.Click += (_, _) =>
        {
            if (_autoStartItem.Checked)
            {
                if (AutoStart.Enable() is null)
                {
                    _autoStartItem.Checked = false;
                    MessageBox.Show("写入开机自启动失败（注册表 HKCU 的 Run 键不可写）。",
                        "BSH", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            else if (!AutoStart.Disable())
            {
                _autoStartItem.Checked = true;
                MessageBox.Show("取消开机自启动失败。", "BSH",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        _menu.Items.Add(_autoStartItem);
        AutoStart.Changed += SyncAutoStartItem;
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("退出（会停止监听）", null, (_, _) => ExitApp());

        // 每次弹出前刷新：状态是"现算"的，不用手动同步（引擎可能被界面那边启停过）
        _menu.Opening += (_, _) =>
        {
            RefreshState();
            SyncAutoStartItem();
        };

        _tray = new NotifyIcon
        {
            Icon = Core.AppIcon.Get(),
            Text = "BSH · 更好的系统提示（单击打开面板）",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowForm();
        };

        // 兜底：任何退出路径（含系统注销）都不留钩子
        // 兜底：任何退出路径（含系统注销）都不留钩子；接管模式下还要把系统提示音还原
        Application.ApplicationExit += (_, _) => { SafeStop(); RestoreIfTakeover(); };

        ApplyMenuTheme();                      // 托盘菜单不在控件树里，必须单独上色
        AppTheme.Changed += ApplyMenuTheme;    // 切主题时跟着变

        RefreshState();
    }

    /// <summary>
    /// 给托盘右键菜单上色。
    /// <para>
    /// ⚠️ <b>为什么不能靠 <see cref="AppTheme.ApplyTo"/></b>：<see cref="ContextMenuStrip"/> 不是控件树成员
    /// （它挂给 <see cref="NotifyIcon"/> 而不是某个 Control 的 Controls），那趟递归永远遍历不到它 ——
    /// 深色档下它一直是系统默认的白底黑字。菜单只能用 <see cref="ToolStripRenderer"/> 接管，
    /// 见 <see cref="DarkMenuRenderer"/>。
    /// </para>
    /// <para>浅色档回退成系统默认渲染器，与「浅色档交回系统绘制」的既有约定一致。</para>
    /// </summary>
    private void ApplyMenuTheme()
    {
        var p = AppTheme.Palette;
        _menu.Renderer = p.NativeControls ? null : new DarkMenuRenderer();
        _menu.BackColor = p.Surface;
        _menu.ForeColor = p.Foreground;
        // 菜单项自身的 ForeColor 也要给：渲染器的 OnRenderItemText 之外，系统在部分状态下会直接用它
        foreach (ToolStripItem item in _menu.Items) item.ForeColor = p.Foreground;
    }

    /// <summary>
    /// 刷新菜单文案与托盘提示：当前方案 + 引擎状态 + 「个性化设置」会改多少槽 / 装多少监听器。
    /// <para>这就是"托盘能反映引擎状态"的落点 —— 例如 <c>个性化设置（运行中：3 个监听器）</c>。</para>
    /// <para>
    /// 方案名后面的括号里<b>同时带两件事</b>：写多少事件槽（<c>写48槽/跳F5×11</c>）与监听器个数 ——
    /// 「个性化设置」2026-10-11 起是"换提示音 + 启用监听"，只报监听器个数会误导。
    /// </para>
    /// </summary>
    private void RefreshState()
    {
        var profile = ProfileStore.Find(MonitorHost.ActiveProfileId);
        string name = profile?.Name ?? "未选择";
        bool running = MonitorHost.IsRunning;
        int monitors = running ? MonitorHost.RunningSourceCount() : 0;
        int enabledRules = ProfileStore.EnabledRuleCount();

        var personal = ProfileStore.Find(ProfileStore.PersonalId);
        var personalPlan = personal is null ? null : ProfileStore.PlanDetailed(personal);
        string slotText = personalPlan is null
            ? "—"
            : $"写{personalPlan.SlotWrites.Count}槽"
              + (personalPlan.SkippedSlots.Count == 0
                  ? ""
                  : $"/跳{personalPlan.SkippedSlots.Count}(档{personalPlan.SkippedByFreq?.Count ?? 0}+事件{personalPlan.SkippedByEvent?.Count ?? 0})");

        _stateItem.Text = $"当前：{name}　·　{MonitorHost.DescribeState()}　·　规则表启用 {enabledRules} 条　·　个性化：{slotText}";
        _defaultItem.Text = "切换到「Windows 原样」（还原 59 槽 + 停止监听，先自动快照）…";
        _personalItem.Text = running
            ? $"个性化设置（换提示音 + 运行中：{monitors} 个监听器）…"
            : $"切换到「个性化设置」（换 {personalPlan?.SlotWrites.Count ?? 0} 槽提示音 + 启用监听）…";
        bool takeoverRunning = running
            && string.Equals(MonitorHost.ActiveProfileId, ProfileStore.TakeoverId, StringComparison.OrdinalIgnoreCase);
        _takeoverItem.Text = takeoverRunning
            ? "「BSH 接管」运行中（已清 18 槽 + 监听）…"
            : "切换到「BSH 接管」（清 18 槽，改由规则判断响不响）…";
        _statusItem.Text = "监听状态详情…";

        // NotifyIcon.Text 上限 63 字符，超了会抛异常 —— 这里自己截断
        string text = $"BSH · {name}：{slotText} · {(running ? $"监听中（{monitors} 个监听器）" : "不监听")}";
        _tray.Text = text.Length <= 63 ? text : text[..63];
    }

    /// <summary>状态详情对话框：语音方案改哪些槽、规则表在哪、启用几条、每个监听器在不在跑。</summary>
    private void ShowMonitorStatus()
    {
        var st = MonitorHost.Status();
        var personal = ProfileStore.Find(ProfileStore.PersonalId);
        var plan = personal is null ? null : ProfileStore.PlanDetailed(personal);
        var freq = ProfileStore.VoiceFreqSummary();

        var lines = new List<string>
        {
            $"当前方案：{ProfileStore.Find(MonitorHost.ActiveProfileId)?.Name ?? "未选择"}",
            $"引擎状态：{MonitorHost.DescribeState()}",
            "",
            "「个性化设置」会做什么（只读预演）：",
            $"  ① 写注册表：{plan?.SlotWrites.Count ?? 0} 个事件槽指向语音方案"
                + (plan is null || plan.SkippedSlots.Count == 0
                    ? "（不跳任何事件）"
                    : $"（跳过 {plan.SkippedSlots.Count} 个 = 按档 {plan.SkippedByFreq?.Count ?? 0} 个 [{string.Join("/", plan.SkipFreq ?? [])}]"
                      + $" + 按事件 {plan.SkippedByEvent?.Count ?? 0} 个：{string.Join("、", (plan.SkippedByEvent ?? []).Select(s => s.Event))}）"),
            $"     语音方案：{VoiceScheme.DefaultPath}（缺失 wav {plan?.MissingFiles?.Count ?? 0} 项）",
            $"     freq 台词表：{freq.Path}（存在={freq.Exists}，分布 {string.Join(" ", freq.Stats.Select(kv => $"{kv.Key}={kv.Value}"))}）",
            $"  ② 启监听：{plan?.MonitorLineCount ?? 0} 个监听器（启用规则 {st.EnabledRuleCount} 条）",
            "",
            $"规则表：{st.RulesPath}（存在={st.RulesFileExists}）",
            $"规则条数：{st.RuleCount}　启用：{st.EnabledRuleCount}",
            $"累计触发：{st.TriggeredCount} 次　播放：{st.PlayedCount} 次",
            "",
            "监听器：",
        };

        if (st.Sources.Count == 0)
        {
            lines.Add("  （无 —— 未启动引擎，或启用规则为 0 条 = 零钩子）");
        }
        else
        {
            foreach (var s in st.Sources)
            {
                string state = s.IsRunning ? "运行中" : "未运行";
                string err = string.IsNullOrWhiteSpace(s.LastError) ? "" : $"　提示：{s.LastError}";
                lines.Add($"  {MonitorHost.KindLabel(s.Kind)}（{s.Kind}）：{state}　事件 {s.PublishedCount} 条{err}");
            }
        }

        if (!string.IsNullOrWhiteSpace(st.RulesNote)) lines.Add($"\n规则表来源：{st.RulesNote}");

        MessageBox.Show(string.Join(Environment.NewLine, lines), "BSH · 监听状态",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>把托盘的「开机自启动」勾选同步成注册表的真实状态（面板勾选 / 启动默认值变化都会走到）。</summary>
    private void SyncAutoStartItem()
    {
        bool enabled = AutoStart.IsEnabled;
        if (_autoStartItem.Checked == enabled) return;
        _autoStartItem.Checked = enabled;
    }

    private void ShowForm()
    {
        if (_form is null || _form.IsDisposed)
        {
            _form = new MainForm();
            _form.FormClosing += (_, e) =>
            {
                if (_form.AllowClose) return;
                e.Cancel = true;
                _form.Hide();
            };
        }

        _form.Show();
        if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
        _form.Activate();
        _form.BringToFront();
    }

    /// <summary>
    /// 托盘切方案：先叫出面板（确认对话框需要一个可见的父窗口），再交给 <see cref="MainForm.ApplyProfile"/>。
    /// </summary>
    /// <param name="profileId">方案 id（见 <see cref="ProfileStore"/>）。</param>
    private void ApplyProfile(string profileId)
    {
        ShowForm();
        _form?.ApplyProfile(profileId);
    }

    private static void OpenMediaDir() => OpenInExplorer(SoundScheme.MediaDir);

    private static void OpenSnapshotDir()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BSH", "snapshots");
        Directory.CreateDirectory(dir);
        OpenInExplorer(dir);
    }

    private static void OpenInExplorer(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开目录失败：{ex.Message}", "BSH", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>停止引擎并把异常吞掉（退出路径上不允许因为卸载失败而卡住）。</summary>
    /// <summary>
    /// 若当前是「BSH 接管」，退出前把系统提示音还原成原样（按基线逐项还原，含 REG_EXPAND_SZ 类型与空槽结构）。
    /// <para><b>为什么必须做</b>：接管清掉的那 18 个槽，语义是「改由 BSH 判断」；BSH 一退出就没人判断了，
    /// 那些提示音会静默消失 —— 用户看到的现象是「提示音坏了」。所以退出即还原，开机自启再接管。</para>
    /// <para>失败一律吞掉：退出路径上抛异常会留下半死不活的状态；真失败了还有「Windows 原样」可手动还原。</para>
    /// </summary>
    private static void RestoreIfTakeover()
    {
        try
        {
            if (!string.Equals(MonitorHost.ActiveProfileId, ProfileStore.TakeoverId, StringComparison.OrdinalIgnoreCase))
                return;
            var win = ProfileStore.Find(ProfileStore.WindowsDefaultId);
            if (win is not null) ProfileStore.Apply(win);
        }
        catch
        {
            // 见方法注释：退出路径不抛
        }
    }

    private static void SafeStop()
    {
        try
        {
            MonitorHost.Stop();
        }
        catch
        {
            // 卸载阶段的异常忽略：绝不能因为收尾失败让进程退不掉
        }
    }

    private void ExitApp()
    {
        // 退出前必须卸载：钩子 / 隐藏窗口 / 定时器都是系统级资源，留着就是"看不见的常驻"
        SafeStop();

        // 接管模式下必须把系统提示音还回去：那 18 个槽的含义是「BSH 在跑才由 BSH 判断响不响」，
        // BSH 一退，系统就该恢复自己的行为 —— 否则用户会以为「提示音全坏了」。
        // ⚠️ 必须在 SetActiveProfile(null) 之前判，否则读不到当前方案 id。
        RestoreIfTakeover();

        MonitorHost.SetActiveProfile(null);

        _tray.Visible = false;
        if (_form is { IsDisposed: false })
        {
            _form.AllowClose = true;
            _form.Close();
        }
        ExitThread();
    }
}
