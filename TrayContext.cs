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
    private readonly ToolStripMenuItem _statusItem = new();
    private MainForm? _form;

    public TrayContext()
    {
        // 两个方案入口放最前：换整套音效是最高频动作
        _defaultItem.Click += (_, _) => ApplyProfile(ProfileStore.WindowsDefaultId);
        _personalItem.Click += (_, _) => ApplyProfile(ProfileStore.PersonalId);
        _statusItem.Click += (_, _) => ShowMonitorStatus();

        _menu.Items.Add(_stateItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_defaultItem);
        _menu.Items.Add(_personalItem);
        _menu.Items.Add("打开面板…", null, (_, _) => ShowForm());
        _menu.Items.Add("一键重排（先自动快照）…", null, (_, _) =>
        {
            ShowForm();
            _form?.RunReorder();
        });
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_statusItem);
        _menu.Items.Add("打开音效素材目录", null, (_, _) => OpenMediaDir());
        _menu.Items.Add("打开快照目录", null, (_, _) => OpenSnapshotDir());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("退出（会停止监听）", null, (_, _) => ExitApp());

        // 每次弹出前刷新：状态是"现算"的，不用手动同步（引擎可能被界面那边启停过）
        _menu.Opening += (_, _) => RefreshState();

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
        Application.ApplicationExit += (_, _) => SafeStop();

        RefreshState();
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
