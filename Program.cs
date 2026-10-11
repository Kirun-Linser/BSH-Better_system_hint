using System.Globalization;
using System.Text.Json;
using BSH.Core;
using BSH.Core.Monitoring;

namespace BSH;

static class Program
{
    /// <summary>命令行参数名：应用某套方案（会写注册表，仅供自动化验收）。</summary>
    private const string ApplyProfileArg = "--apply-profile";

    /// <summary>命令行参数名：监听引擎的只读体检报告（不写注册表、不常驻）。</summary>
    private const string MonitorReportArg = "--monitor-report";

    /// <summary>命令行参数名：监听引擎自检（真装钩子、真发按键、真写文件）。</summary>
    private const string MonitorSelftestArg = "--monitor-selftest";

    /// <summary>命令行参数名：监听状态（默认只读；加 <c>--live</c> 才真起一次引擎再停掉用于取证）。</summary>
    private const string MonitorStatusArg = "--monitor-status";

    /// <summary>配合 <see cref="MonitorStatusArg"/> 的取证开关：真调用 <see cref="ProfileStore.Apply"/> 一次再收尾。</summary>
    private const string LiveArg = "--live";

    /// <summary>命令行参数名：快照类型保真的只读自检（不写注册表，只写 JSON 文件）。</summary>
    private const string SnapshotCheckArg = "--snapshot-check";

    /// <summary>命令行参数名：槽位还原动作的只读投影自检（不写注册表、连文件都不写；只用 fixture 证明分派）。</summary>
    private const string SlotPlanArg = "--slot-plan";

    /// <summary>命令行参数名：方案预演的只读投影（不写注册表、不写文件；证明"会改哪些槽、跳哪些槽、启哪些监听器"）。</summary>
    private const string ProfilePlanArg = "--profile-plan";

    /// <summary>命令行参数名：列出可用预设（只读，不写任何东西）。</summary>
    private const string PresetListArg = "--preset-list";

    /// <summary>命令行参数名：应用某套预设（会写注册表，仅供自动化验收）。</summary>
    private const string ApplyPresetArg = "--apply-preset";

    /// <summary>报告序列化选项：缩进 + 中文不转义（两份 JSON 报告共用）。</summary>
    private static readonly JsonSerializerOptions JsonPretty = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 入口。无参数 = 托盘常驻；
    /// <c>--report [路径]</c> = 自检并输出 JSON 报告（不启动界面、<b>只读</b>）；
    /// <c>--monitor-report [路径]</c> = 监听引擎体检报告（只读，不装钩子）；
    /// <c>--monitor-status [--live]</c> = 监听状态（默认只读；<c>--live</c> 时真启一次引擎取证再收尾）；
    /// <c>--monitor-selftest &lt;秒数&gt;</c> = 监听引擎自检（临时装钩子，结束即卸载）；
    /// <c>--apply-profile &lt;方案id&gt; [快照路径]</c> = 自动快照后应用方案（两套方案都会写注册表；监听类不拍照）；
    /// <c>--snapshot-check capture|normalize|plan &lt;路径…&gt;</c> = 快照类型保真自检（只读注册表，只写 JSON 文件）；
    /// <c>--slot-plan &lt;快照文件&gt;</c> = 槽位还原动作的只读投影（不写注册表、不写文件，靠 fixture 证明空槽还原语义）；
    /// <c>--profile-plan [方案id]</c> = 方案预演的只读投影：列出会写的事件槽、跳过的频率档、会启的监听器
    /// （不写注册表、不写文件，默认 <c>personal</c>；退出码 0 成功 / 7 方案不存在）。
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        // --report 优先，且永远只读：即使同时带了 --apply-profile 也只出报告，绝不误写注册表
        if (args.Any(a => a.Equals("--report", StringComparison.OrdinalIgnoreCase)))
        {
            WriteReport(args);
            return;
        }

        if (args.Any(a => a.Equals(SnapshotCheckArg, StringComparison.OrdinalIgnoreCase)))
        {
            Environment.ExitCode = SnapshotCheckCommand(args);
            return;
        }

        // 新增的只读自检（纯插入，不动上面 --snapshot-check 与下面 --monitor-* 的分支）：
        // 拿 fixture 证明"空槽还原成什么"——不写注册表、不写文件
        if (args.Any(a => a.Equals(SlotPlanArg, StringComparison.OrdinalIgnoreCase)))
        {
            Environment.ExitCode = SlotPlanCommand(args);
            return;
        }

        // 方案预演的只读投影（纯插入，不动其它分支）：证明「会写哪些槽 / 跳过哪些槽 / 启哪些监听器」
        if (args.Any(a => a.Equals(ProfilePlanArg, StringComparison.OrdinalIgnoreCase)))
        {
            Environment.ExitCode = ProfilePlanCommand(args);
            return;
        }

        if (args.Any(a => a.Equals(ApplyPresetArg, StringComparison.OrdinalIgnoreCase)))
        {
            Environment.ExitCode = ApplyPresetCommand(args);
            return;
        }

        // 预设一览（纯插入，只读）：证明「预设能被发现、坏文件被跳过而不是炸掉」
        if (args.Any(a => a.Equals(PresetListArg, StringComparison.OrdinalIgnoreCase)))
        {
            Environment.ExitCode = PresetListCommand(args);
            return;
        }

        if (args.Any(a => a.Equals(MonitorReportArg, StringComparison.OrdinalIgnoreCase)))
        {
            Environment.ExitCode = MonitorReportCommand(args);
            return;
        }

        if (args.Any(a => a.Equals(MonitorStatusArg, StringComparison.OrdinalIgnoreCase)))
        {
            Environment.ExitCode = MonitorStatusCommand(args);
            return;
        }

        if (args.Any(a => a.Equals(MonitorSelftestArg, StringComparison.OrdinalIgnoreCase)))
        {
            Environment.ExitCode = MonitorSelftestCommand(args);
            return;
        }

        if (args.Any(a => a.Equals(ApplyProfileArg, StringComparison.OrdinalIgnoreCase)))
        {
            Environment.ExitCode = ApplyProfileCommand(args);
            return;
        }

        ApplicationConfiguration.Initialize();

        // 单实例：重复启动时提示已在托盘运行
        using var mutex = new Mutex(true, @"Local\BSH-single-instance", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("BSH 已在运行（见系统托盘）。", "BSH",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // 启动时自动启用「个性化设置」：写事件槽 + 装监听钩子。
        // ⚠️ 2026-10-11 用户实测反馈：托盘启动后若不去点「个性化设置」，监听引擎不会跑，
        //    表现为「规则都配好了却听不到提示音」。用户的意图就是常驻监听 ⇒ 启动即生效。
        TryAutoStartPersonal();

        Application.Run(new TrayContext());
    }

    /// <summary>
    /// 启动时自动应用「个性化设置」方案（= 写事件槽 + 启动监听引擎）。
    ///
    /// <para>为什么不靠用户点按钮：2026-10-11 用户实测——托盘启动后没点那个按钮，
    /// 监听引擎就不跑，表现为「规则都配好了却听不到提示音」。用户的意图本来就是常驻监听，
    /// 所以改成开机即生效。</para>
    ///
    /// <para><b>失败绝不能拦住托盘</b>：启动阶段抛异常会让用户连界面都看不到，
    /// 比「没监听」更糟。所以整段吞异常 —— 状态仍可在面板与托盘菜单上查看。</para>
    /// </summary>
    private static void TryAutoStartPersonal()
    {
        try
        {
            var personal = ProfileStore.Find("personal");
            if (personal is null) return;
            ProfileStore.Apply(personal);
        }
        catch
        {
            // 见上方说明：启动阶段的失败只影响"监听是否起来"，不该影响程序可用性
        }
    }

    /// <summary>
    /// 自检：把当前方案、撞车、待重排项与素材缺失情况写成 JSON，供脚本核对。
    /// <para>⚠️ 只读：不写注册表、不启动引擎、不创建规则表；<c>profiles</c> 段里
    /// 监听模式（personal）会带上"会写哪些事件槽 / 跳过哪些频率档"与规则表接线。</para>
    /// </summary>
    private static void WriteReport(string[] args)
    {
        int idx = Array.FindIndex(args, a => a.Equals("--report", StringComparison.OrdinalIgnoreCase));
        string path = idx >= 0 && idx + 1 < args.Length && !args[idx + 1].StartsWith("--")
            ? args[idx + 1]
            : Path.Combine(Path.GetTempPath(), "bsh-report.json");

        var sounds = SoundScheme.ReadAll();
        var plan = ReorderPlan.Build(sounds);

        var report = new
        {
            generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            mediaDir = SoundScheme.MediaDir,
            mediaWavCount = SoundScheme.AvailableMediaFiles().Count,
            totalEvents = sounds.Count,
            emptyCount = sounds.Count(s => s.IsEmpty),
            emptyEvents = sounds.Where(s => s.IsEmpty).Select(s => s.Event).ToArray(),
            collisions = sounds
                .Where(s => !s.IsEmpty)
                .GroupBy(s => s.FileName, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => new { file = g.Key, count = g.Count(), events = g.Select(x => x.Event).ToArray() })
                .ToArray(),
            reorderPlan = plan.Select(p => new { p.Event, p.NewWav, p.Reason }).ToArray(),
            missingAssets = plan
                .Select(p => p.NewWav)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(f => !File.Exists(Path.Combine(SoundScheme.MediaDir, f)))
                .ToArray(),
            // 两套方案的可用状态与"将要改动什么"（Plan 是纯只读预演，这里不会写任何东西）。
            // personal 2026-10-11 起是"改事件槽 + 启监听"两件事，所以额外给出：
            //   会写哪些槽 / 跳过哪些槽（skipFreq 按档 + skipEvents 按事件，并集）/ 生效的两个跳过键 / freq 分布 —— 全是只读算出来的。
            profiles = ProfileStore.All().Select(p =>
            {
                var (available, status) = ProfileStore.Describe(p);
                var plan = ProfileStore.PlanDetailed(p);
                bool isMonitor = p.Kind == ProfileKind.Monitor;
                var monRules = isMonitor ? RuleStore.Load(out _) : [];
                return new
                {
                    id = p.Id,
                    name = p.Name,
                    kind = p.Kind.ToString(),
                    available,
                    status,
                    planCount = plan.Lines.Count,       // 监听方案 = 将写入的槽 + 将启动的监听器
                    planPreview = plan.Lines.Take(10).ToArray(),
                    // ↓ 仅监听模式有值；windows-default 为 null / 0 / 空数组，读报告时不必猜
                    rulesPath = isMonitor ? RuleStore.DefaultPath : null,
                    rulesFileExists = isMonitor ? RuleStore.Exists : (bool?)null,
                    rulesExamplePath = isMonitor ? RuleStore.ExamplePath : null,
                    ruleCount = isMonitor ? monRules.Count : (int?)null,
                    enabledRules = isMonitor ? monRules.Count(r => r.Enabled) : (int?)null,
                    monitors = isMonitor
                        ? ProfileStore.EnabledMonitorKinds()
                            .Select(k => new { kind = k.ToString(), label = MonitorHost.KindLabel(k) })
                            .ToArray()
                        : [],
                    engineRunning = isMonitor ? MonitorHost.IsRunning : (bool?)null,
                    plannedRegistryWrites = isMonitor ? plan.SlotWrites.Count : plan.Lines.Count,
                    // ↓ 个性化设置专用的"改哪些槽"取证（windows-default 为 null）
                    applyVoiceScheme = isMonitor ? plan.SlotWrites.Count > 0 || plan.SkippedSlots.Count > 0 : (bool?)null,
                    // ↓ 两个跳过键都原样落盘：skipFreq=[] 与"缺键"在报告里必须分辨得出来（前者 = 明确不整档跳）
                    skipFreq = isMonitor ? plan.SkipFreq : null,
                    skipEvents = isMonitor ? plan.SkipEvents : null,
                    slotWriteCount = isMonitor ? plan.SlotWrites.Count : (int?)null,
                    slotSkipCount = isMonitor ? plan.SkippedSlots.Count : (int?)null,
                    // ↓ 跳过总数拆成两半：「按档 N 个 + 按事件 M 个」，N+M 恒等于 slotSkipCount（两名单互斥、不重复计数）
                    slotSkipByFreqCount = isMonitor ? (plan.SkippedByFreq?.Count ?? 0) : (int?)null,
                    slotSkipByEventCount = isMonitor ? (plan.SkippedByEvent?.Count ?? 0) : (int?)null,
                    skippedSlots = isMonitor
                        ? plan.SkippedSlots.Select(s => new { @event = s.Event, freq = s.Freq, label = AppEventCatalog.Label(s.Event) }).ToArray()
                        : [],
                    skippedByFreq = isMonitor
                        ? (plan.SkippedByFreq ?? []).Select(s => new { @event = s.Event, freq = s.Freq }).ToArray()
                        : [],
                    skippedByEvent = isMonitor
                        ? (plan.SkippedByEvent ?? []).Select(s => new { @event = s.Event, freq = s.Freq }).ToArray()
                        : [],
                    slotWriteEvents = isMonitor ? plan.SlotWriteEvents : [],
                    monitorCount = isMonitor ? plan.MonitorLineCount : (int?)null,
                    voicePlanHint = isMonitor ? plan.Hint : null,
                    freqFallbackWrites = isMonitor ? plan.FreqFallbackWrites : (int?)null,
                };
            }).ToArray(),
        };

        File.WriteAllText(path, JsonSerializer.Serialize(report, JsonPretty));
    }

    /// <summary>
    /// 命令行应用方案：<c>--apply-profile &lt;方案id&gt; [快照路径]</c>。
    /// <list type="bullet">
    /// <item><description><b>还原类</b>（windows-default）：先自动快照 → 再写注册表（并停止监听），
    /// 输出 <c>applied &lt;id&gt; changed=&lt;条数&gt; snapshot=&lt;快照绝对路径&gt;</c>。</description></item>
    /// <item><description><b>监听类</b>（personal）：<b>会写注册表</b> —— 按语音方案改写事件槽
    /// （跳过 <c>skipFreq</c> 列出的频率档与 <c>skipEvents</c> 点名的事件，两者取并集），
    /// 成功后再启动监听引擎；不拍照（快照的用途是"还原注册表"，而它本来就该由「Windows 原样」负责），
    /// 输出 <c>applied personal slots=&lt;写入槽数&gt; monitors=&lt;监听器数&gt; snapshot=none registry=written</c>。</description></item>
    /// </list>
    /// <para>退出码：0 成功 / 3 方案 id 不存在 / 4 应用失败（如基线文件缺失 / 只改了一半，见 <see cref="PartialApplyException"/>）。</para>
    /// <para>
    /// ⚠️ 两套方案这条路径<b>都会写注册表</b>，只给自动化验收用；界面流程走 <see cref="MainForm.ApplyProfile"/>。
    /// WinExe 双击运行没有控制台，看不到输出是正常的；被 subprocess / Start-Process 重定向时
    /// stdout 句柄有效，能正常拿到这一行。
    /// </para>
    /// </summary>
    /// <summary>
    /// <c>--preset-list</c>：列出全部可用预设（<b>只读</b>，不写注册表、不写文件）。
    /// <para>输出形如 <c>preset eva-mech  builtin=1 sounds=39 rules=0  EVA 机械女声</c>；
    /// 坏预设不会让它失败，而是列在末尾的「⚠️ 跳过」里。</para>
    /// </summary>
    /// <returns>恒 0（「一个预设都没有」不算错误）。</returns>
    private static int PresetListCommand(string[] args)
    {
        var problems = new List<string>();
        var list = PresetStore.List(problems);

        WriteLine($"presets dir = {PresetStore.PresetsDir}");
        WriteLine($"audio dir   = {PresetStore.SoundsDir}");
        if (list.Count == 0)
            WriteLine("未发现预设（profiles/presets/ 下没有可用的 preset.json）");

        foreach (var p in list)
        {
            // 顺带报告音频缺失数：预设只存映射，音频要能在 audio dir 里找到才谈得上「可应用」
            var preset = PresetStore.Load(p.Id, out _);
            int missing = 0;
            if (preset is not null)
                missing = preset.Sounds.Values.Count(v => !File.Exists(v));
            WriteLine($"preset {p.Id}  builtin={(p.Builtin ? 1 : 0)} sounds={p.SoundCount} rules={p.RuleCount} missing={missing}  {p.Name}");
        }

        foreach (var s in problems) WriteLine($"  ⚠️ 跳过：{s}");
        return 0;
    }

    /// <summary>
    /// <c>--apply-preset &lt;预设id&gt; [快照路径]</c>：应用预设（<b>会写注册表</b>）。
    /// <para><b>顺序与「Windows 原样」一致</b>：先停监听引擎 → 自动快照 → 再逐槽写 ——
    /// 中途失败最坏留下「音换了但没监听」，而反过来会留下「钩子在跑但音没换」。</para>
    /// <para><b>音频缺失的槽跳过并汇总</b>，不整批失败：预设只存映射，音频可能没跟着分发
    /// （体积与音源授权的原因，音频不入公开仓库）。</para>
    /// </summary>
    /// <returns>0 成功 / 3 预设不存在 / 4 全部音频缺失或写入失败。</returns>
    private static int ApplyPresetCommand(string[] args)
    {
        UseUtf8Stdout();

        int idx = Array.FindIndex(args, a => a.Equals(ApplyPresetArg, StringComparison.OrdinalIgnoreCase));
        string id = ReadArg(args, idx + 1);

        var preset = PresetStore.Load(id, out var problem);
        if (preset is null)
        {
            WriteLine($"unknown preset: {id}（{problem}）");
            return 3;
        }

        // 尊重用户的跳过偏好：personal\profile.json 的 skipFreq + skipEvents 是「用户不想被改的槽」，
        // 与用哪套预设无关。少了这一步，应用预设会把 CCSelect / Open / Close 这些刻意留空的槽填上
        // （2026-10-11 实测踩到：哈希从 0ce934c9 变成 2caeb740，差的就是这 3 个槽）。
        var personal = ProfileStore.Find(ProfileStore.PersonalId);
        var skip = personal is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : ProfileStore.PlanDetailed(personal).SkippedSlots
                .Select(s => s.Event).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = preset.Sounds.Where(kv => !File.Exists(kv.Value)).Select(kv => kv.Key).ToList();
        var skipped = preset.Sounds.Keys.Where(k => skip.Contains(k)).ToList();
        var usable = preset.Sounds.Where(kv => File.Exists(kv.Value) && !skip.Contains(kv.Key)).ToList();

        if (usable.Count == 0)
        {
            WriteLine($"preset {id} 没有任何可用音频（{preset.Sounds.Count} 条映射全部缺失），未改动注册表");
            return 4;
        }

        try
        {
            MonitorHost.Stop();      // 先停引擎再写注册表（与 windows-default 同一顺序约定）

            string given = ReadArg(args, idx + 2);
            string snapshotPath = given.Length > 0
                ? given
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "BSH", "snapshots",
                    $"auto-before-preset-{DateTime.Now:yyyyMMdd-HHmmss}.json");

            var full = Path.GetFullPath(snapshotPath);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // 真正的「快照 → 写槽」在 PresetStore.Apply 里（界面与 CLI 共用同一份，避免两套逻辑）
            var r = PresetStore.Apply(preset, skipped, full);

            WriteLine($"applied preset {preset.Info.Id} name={preset.Info.Name} "
                      + $"slots={r.Written} skipped={r.SkippedByUser} missing={r.Missing} "
                      + $"rules={preset.Rules.Count} snapshot={r.SnapshotPath} registry=written");
            if (r.SkippedEvents.Count > 0)
                WriteLine("  跳过的槽：" + string.Join("、", r.SkippedEvents));
            if (missing.Count > 0)
                WriteLine("  跳过的槽（音频缺失）：" + string.Join("、", missing));
            return 0;
        }
        catch (Exception ex)
        {
            WriteLine($"apply preset 失败：{ex.GetType().Name} {ex.Message}");
            return 4;
        }
    }

    private static int ApplyProfileCommand(string[] args)
    {
        // stdout 固定 UTF-8（无 BOM）——脚本侧请用 encoding='utf-8' 读取（路径可能含中文）
        UseUtf8Stdout();

        int idx = Array.FindIndex(args, a => a.Equals(ApplyProfileArg, StringComparison.OrdinalIgnoreCase));
        string id = ReadArg(args, idx + 1);

        var profile = ProfileStore.Find(id);
        if (profile is null)
        {
            WriteLine($"unknown profile: {id}");
            return 3;
        }

        try
        {
            // 监听类：要写真语音方案（会改注册表），但不拍照 —— 复原靠「Windows 原样」的 59 槽还原
            if (profile.Kind == ProfileKind.Monitor)
            {
                var result = ProfileStore.Apply(profile);
                MonitorHost.Stop();     // 进程马上要退出，钩子随进程消失；显式停一次，别留悬挂资源
                WriteLine($"applied {profile.Id} slots={result.Slots} monitors={result.Changed} "
                          + $"slotsWritten={result.SlotsWritten} engineStarted={result.EngineStarted} snapshot=none registry=written");
                return 0;
            }

            string given = ReadArg(args, idx + 2);
            string snapshotPath = given.Length > 0
                ? given
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "BSH", "snapshots",
                    $"auto-before-profile-{DateTime.Now:yyyyMMdd-HHmmss}.json");

            var full = Path.GetFullPath(snapshotPath);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // 顺序不能反：先快照，后写注册表
            SchemeSnapshot.Capture($"应用方案「{profile.Name}」前自动快照").Save(full);
            int changed = ProfileStore.Apply(profile).Changed;

            WriteLine($"applied {profile.Id} changed={changed} snapshot={full}");
            return 0;
        }
        catch (Exception ex)
        {
            WriteLine($"apply failed: {ex.Message}");
            return 4;
        }
    }

    /// <summary>
    /// 监听状态：<c>--monitor-status [--live]</c>。
    /// <para>
    /// <b>默认只读</b>：不启动引擎、不装钩子、不写注册表、不创建规则表 —— 只把"监听模式的接线"
    /// （方案 kind / 规则表路径与存在性 / 启用条数 / 将启动哪些监听器 / 8 类监听器的装机前提）打印出来。
    /// </para>
    /// <para>
    /// 加 <c>--live</c> 才真动手，两步取证，每步结束都收尾：
    /// <list type="number">
    /// <item><description>调用 <see cref="ProfileStore.Apply"/>（与界面「个性化设置」按钮<b>同一条代码路径</b>）
    /// → 报启动了几个监听器 → <see cref="MonitorHost.Stop"/> → 报 <c>IsRunning=False</c>。</description></item>
    /// <item><description>拿<b>公开示例规则表</b>全部临时启用（内存改写，<b>不动文件</b>）起一次引擎，
    /// 证明"有规则时真装钩子"（与上一步的"零规则 = 零钩子"互为对照）→ 再 Stop。</description></item>
    /// </list>
    /// </para>
    /// <para>退出码：0 正常 / 8 <c>--live</c> 时监听器没起来（说明引擎侧有回归）。</para>
    /// </summary>
    private static int MonitorStatusCommand(string[] args)
    {
        UseUtf8Stdout();

        bool live = args.Any(a => a.Equals(LiveArg, StringComparison.OrdinalIgnoreCase));
        var profile = ProfileStore.Find(ProfileStore.PersonalId)!;
        var (_, status) = ProfileStore.Describe(profile);
        var rules = RuleStore.Load(out string rulesNote);
        var plan = ProfileStore.PlanDetailed(profile);
        var monitorLines = plan.Lines.Skip(plan.SlotWrites.Count).ToArray();
        var st = MonitorHost.Status();

        WriteLine("=== BSH 监听状态（--monitor-status）===");
        WriteLine(live
            ? "模式：--live（会真起一次引擎取证，每步结束都收尾；本命令自身不写注册表、不创建规则表）"
            : "模式：只读（不启动引擎、不装钩子、不写注册表、不创建规则表）");

        WriteLine("");
        WriteLine("── 1. 「个性化设置」的接线 ─────────────────────");
        WriteLine($"方案：{profile.Id}「{profile.Name}」kind={profile.Kind}");
        WriteLine($"描述：{status}");
        WriteLine($"会写入的事件槽={plan.SlotWrites.Count}｜跳过={plan.SkippedSlots.Count}"
                  + $"（按档 skipFreq=[{string.Join(", ", plan.SkipFreq ?? [])}] {plan.SkippedByFreq?.Count ?? 0} 个"
                  + $" + 按事件 skipEvents=[{string.Join(", ", plan.SkipEvents ?? [])}] {plan.SkippedByEvent?.Count ?? 0} 个）"
                  + $"｜将启动的监听器={plan.MonitorLineCount}");
        WriteLine($"语音方案：{VoiceScheme.DefaultPath}（存在={File.Exists(VoiceScheme.DefaultPath)}）");
        WriteLine($"  wav 缺失条目={plan.MissingFiles?.Count ?? 0}");
        var freq = ProfileStore.VoiceFreqSummary();
        WriteLine($"freq 台词表：{freq.Path}（存在={freq.Exists}，登记 {freq.EventCount} 个事件）"
                  + $"｜分布 {string.Join(" ", freq.Stats.Select(kv => $"{kv.Key}={kv.Value}"))}");
        WriteLine($"rulesPath={RuleStore.DefaultPath}");
        WriteLine($"rulesFileExists={RuleStore.Exists}");
        WriteLine($"ruleCount={rules.Count} enabledRules={rules.Count(r => r.Enabled)}");
        WriteLine($"规则表来源：{rulesNote}");
        WriteLine($"示例规则表：{RuleStore.ExamplePath}");
        WriteLine($"  存在={RuleStore.ExampleExists}｜{DescribeExample()}");

        WriteLine("");
        WriteLine("── 2. 8 类监听器（本命令不启动它们，只报装机前提） ──");
        foreach (var kind in MonitorSources.All)
        {
            var probe = MonitorSources.Probe(kind, Path.GetTempPath());
            WriteLine($"{kind,-16} 可启动={probe.Startable,-5} 探针后已停止={probe.Stopped,-5} 机制={probe.Mechanism}");
            if (!string.IsNullOrWhiteSpace(probe.Detail)) WriteLine($"{"",16} 证据：{probe.Detail}");
            if (!string.IsNullOrWhiteSpace(probe.Note)) WriteLine($"{"",16} 提示：{probe.Note}");
        }

        WriteLine("");
        WriteLine("── 3. 引擎现状与只读预演 ───────────────────────");
        WriteLine($"engineRunning={MonitorHost.IsRunning}（本命令默认不启动引擎）");
        WriteLine($"engineState={MonitorHost.DescribeState()}");
        WriteLine($"将写入的事件槽（Plan，只读）：{plan.SlotWrites.Count} 条"
                  + $"（跳过 {plan.SkippedSlots.Count} 条 = 按档 {plan.SkippedByFreq?.Count ?? 0} + 按事件 {plan.SkippedByEvent?.Count ?? 0}）");
        WriteLine($"将启动的监听器（Plan，只读）：{(monitorLines.Length == 0 ? "（无 —— 启用规则 0 条 = 零钩子）" : string.Join("；", monitorLines))}");
        WriteLine($"当前已装监听器：{(st.Sources.Count == 0 ? "（无）" : string.Join("  ", st.Sources.Select(s => $"{s.Kind}={s.IsRunning}")))}");

        if (!live)
        {
            WriteLine("");
            WriteLine($"结论：个性化设置 = 写入 {plan.SlotWrites.Count} 个事件槽"
                      + $"（跳过 {plan.SkippedSlots.Count} 个 = 按档 {plan.SkippedByFreq?.Count ?? 0} 个 [{string.Join("/", plan.SkipFreq ?? [])}]"
                      + $" + 按事件 {plan.SkippedByEvent?.Count ?? 0} 个 [{string.Join("/", plan.SkipEvents ?? [])}]）"
                      + $" + 启用监听（当前启用规则 {rules.Count(r => r.Enabled)} 条 → 引擎起了也是零钩子，且本命令未启动它）");
            return 0;
        }

        // ── 取证第一步：走真实方案路径 ──
        // ⚠️ 这一步会**真写注册表**（与界面「个性化设置」按钮同一条路径）；命令结束前不自动还原，
        //    完整复原请用 --apply-profile windows-default（它按基线逐槽还原 + 停引擎）。
        WriteLine("");
        WriteLine("── 4. 实启取证 A：ProfileStore.Apply(personal)（与界面按钮同一条代码路径，会写注册表）──");
        var applied = ProfileStore.Apply(profile);
        WriteLine($"已写入事件槽={applied.Slots}（SlotsWritten={applied.SlotsWritten}）｜已启动监听器={applied.Changed}（启用规则 {rules.Count(r => r.Enabled)} 条）");
        WriteLine($"engineRunning={MonitorHost.IsRunning}");
        var afterStart = MonitorHost.Status();
        WriteLine($"各监听器：{(afterStart.Sources.Count == 0 ? "（无 —— 零规则 = 零钩子，一个钩子都没装）" : string.Join("  ", afterStart.Sources.Select(s => $"{s.Kind}={s.IsRunning}")))}");

        WriteLine("收尾：MonitorHost.Stop()……");
        MonitorHost.Stop();
        var afterStop = MonitorHost.Engine.LastStoppedSources;
        WriteLine($"停止后 engineRunning={MonitorHost.IsRunning}");
        WriteLine($"监听器收尾状态：{(afterStop.Count == 0 ? "（无）" : string.Join("  ", afterStop.Select(s => $"{s.Kind}={s.IsRunning}")))}");

        // ── 取证第二步：有规则时真装钩子（用公开示例规则表，内存里全部临时启用）──
        WriteLine("");
        WriteLine("── 5. 实启取证 B：示例规则全部启用（仅内存改写，不动文件、不发声）──");
        var demoRules = RuleStore.LoadExample(out string exampleNote);
        WriteLine($"示例规则表来源：{exampleNote}");
        int demoOk = 0;
        if (demoRules.Count == 0)
        {
            WriteLine("示例规则表读不到内容，跳过这一步");
        }
        else
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "bsh-status-demo");
            Directory.CreateDirectory(tempDir);
            foreach (var rule in demoRules)
            {
                rule.Enabled = true;                                        // 只是内存对象，文件不动
                if (rule.Kind == MonitorEventKind.FileSystem) rule.Match = tempDir;  // 示例里的目录是占位路径
            }

            MonitorHost.Engine.SuppressPlayback = true;                     // 只记账不发声
            try
            {
                MonitorHost.Engine.Stop();
                MonitorHost.Engine.Start(demoRules);
                foreach (var s in MonitorHost.Status().Sources)
                {
                    if (s.IsRunning) demoOk++;
                    string err = string.IsNullOrWhiteSpace(s.LastError) ? "" : $"　提示：{s.LastError}";
                    WriteLine($"  {s.Kind,-16} IsRunning={s.IsRunning,-5} {MonitorHost.KindLabel(s.Kind)}{err}");
                }
            }
            finally
            {
                MonitorHost.Stop();
                MonitorHost.Engine.SuppressPlayback = false;
            }

            WriteLine($"有规则时启动的监听器={demoOk}/{MonitorSources.All.Count}；停止后 engineRunning={MonitorHost.IsRunning}");
            WriteLine($"监听器收尾状态：{(MonitorHost.Engine.LastStoppedSources.Count == 0 ? "（无）" : string.Join("  ", MonitorHost.Engine.LastStoppedSources.Select(s => $"{s.Kind}={s.IsRunning}")))}");
        }

        WriteLine("");
        WriteLine($"结论：{demoOk}/{MonitorSources.All.Count} 类监听器能装能卸；规则表启用 0 条时走 Apply 启动数=0 —— 零规则 = 零钩子；"
                  + "本命令（只读模式）未调用任何注册表写入 API，也未创建 rules.json"
                  + (live ? "；--live 的第一步按设计真写了注册表，复原请用 --apply-profile windows-default" : ""));
        return demoOk == MonitorSources.All.Count ? 0 : 8;
    }

    /// <summary>示例规则表的一句话体检：解析出几条、覆盖几类、有没有默认启用的（应当全是 disabled）。</summary>
    private static string DescribeExample()
    {
        var example = RuleStore.LoadExample(out _);
        if (example.Count == 0) return "解析不出条目";

        var kinds = example.Select(r => r.Kind).Distinct().ToHashSet();
        int enabled = example.Count(r => r.Enabled);
        return $"解析 {example.Count} 条，覆盖 {kinds.Count}/{MonitorSources.All.Count} 类，启用 {enabled} 条"
               + $"（{(enabled == 0 ? "全部默认禁用 ✓" : "注意：有默认启用的条目")}）";
    }

    /// <summary>
    /// 快照类型保真自检：<c>--snapshot-check capture &lt;输出&gt;</c>、<c>--snapshot-check normalize &lt;输入&gt; &lt;输出&gt;</c>
    /// 或 <c>--snapshot-check plan &lt;输入&gt;</c>。
    /// <para>
    /// ⚠️ 只读：不写注册表、**不调用** <see cref="SchemeSnapshot.Apply"/>（还原会改注册表，不在自检范围内）；
    /// 唯一的写盘就是调用方指定的那个 JSON 文件。给 <c>tools\verify-snapshot-types.py</c> 取证用。
    /// </para>
    /// <para>退出码由 <see cref="SnapshotSelfCheck.Run"/> 给出（0 成功 / 6 参数不对或读写失败）。</para>
    /// </summary>
    private static int SnapshotCheckCommand(string[] args)
    {
        UseUtf8Stdout();

        int idx = Array.FindIndex(args, a => a.Equals(SnapshotCheckArg, StringComparison.OrdinalIgnoreCase));
        var (exitCode, summary) = SnapshotSelfCheck.Run(
            ReadArg(args, idx + 1), ReadArg(args, idx + 2), ReadArg(args, idx + 3));

        WriteLine(summary);
        return exitCode;
    }

    /// <summary>
    /// 槽位还原动作的只读投影自检：<c>--slot-plan &lt;快照文件路径&gt;</c>。
    /// <para>
    /// 逐槽打印"还原会做什么"（<c>SetValue(ExpandString)</c> / <c>DeleteValue</c> / <c>DeleteSubKeyTree</c>），
    /// 用的是与 <see cref="SchemeSnapshot.Apply"/> 完全同源的只读投影
    /// <see cref="SchemeSnapshot.Actions"/>，所以拿造出来的 fixture 就能证明分派，不必真写一次注册表。
    /// </para>
    /// <para>⚠️ 只读：<b>不写注册表、也不写任何文件</b>，唯一输出是 stdout。
    /// 退出码由 <see cref="SlotPlanSelfCheck.Run"/> 给出（0 成功 / 7 参数不对或读文件失败）。</para>
    /// </summary>
    private static int SlotPlanCommand(string[] args)
    {
        UseUtf8Stdout();

        int idx = Array.FindIndex(args, a => a.Equals(SlotPlanArg, StringComparison.OrdinalIgnoreCase));
        var (exitCode, lines) = SlotPlanSelfCheck.Run(ReadArg(args, idx + 1));

        foreach (var line in lines) WriteLine(line);
        return exitCode;
    }

    /// <summary>
    /// 方案预演的只读投影：<c>--profile-plan [方案id]</c>（缺省 <c>personal</c>）。
    /// <para>
    /// 把 <see cref="ProfileStore.PlanDetailed"/> 的结果摊开印出来 —— 会写哪些事件槽（含中文标签）、
    /// 被跳过的槽（<c>skipFreq</c> 按档 + <c>skipEvents</c> 按事件，分开印、各自带事件名）、
    /// <c>voice\lines.json</c> 的 freq 分布、会启哪些监听器。
    /// 这是"个性化设置到底会改什么"的<b>可核对凭据</b>：报告里那个 <c>planCount</c> 就是这里的数字。
    /// </para>
    /// <para>⚠️ 只读：不写注册表、<b>不写任何文件</b>，唯一输出是 stdout。退出码 0 成功 / 7 方案不存在。</para>
    /// </summary>
    private static int ProfilePlanCommand(string[] args)
    {
        UseUtf8Stdout();

        int idx = Array.FindIndex(args, a => a.Equals(ProfilePlanArg, StringComparison.OrdinalIgnoreCase));
        string id = ReadArg(args, idx + 1);
        if (id.Length == 0) id = ProfileStore.PersonalId;

        var profile = ProfileStore.Find(id);
        if (profile is null)
        {
            WriteLine($"unknown profile: {id}");
            return 7;
        }

        var (available, status) = ProfileStore.Describe(profile);
        var plan = ProfileStore.PlanDetailed(profile);

        WriteLine("=== BSH 方案预演（--profile-plan，只读：不写注册表、不写文件）===");
        WriteLine($"方案：{profile.Id}「{profile.Name}」kind={profile.Kind} available={available}");
        WriteLine($"状态：{status}");
        WriteLine($"planCount={plan.Lines.Count}（写入槽 {plan.SlotWrites.Count} + 监听器 {plan.MonitorLineCount}）"
                  + $"｜跳过槽 {plan.SkippedSlots.Count}（按档 {plan.SkippedByFreq?.Count ?? 0} + 按事件 {plan.SkippedByEvent?.Count ?? 0}）");
        // 两个跳过键都原样印出来：[] 与"没配"必须一眼分辨（[] = 明确不跳）
        WriteLine($"生效的跳过键：skipFreq=[{string.Join(", ", plan.SkipFreq ?? [])}]（按频率档整档跳）"
                  + $"｜skipEvents=[{string.Join(", ", plan.SkipEvents ?? [])}]（按事件名精确跳）");

        if (!string.IsNullOrWhiteSpace(plan.Hint)) WriteLine($"提示：{plan.Hint}");
        if (plan.MissingFiles is { Count: > 0 }) WriteLine($"缺失文件的条目（不会写）：{string.Join("、", plan.MissingFiles)}");
        if (plan.FreqFallbackWrites > 0)
            WriteLine($"⚠️ freq 信息不可用 → 降级为全部写入：{plan.FreqFallbackWrites} 条，本次不跳任何频率档");

        if (profile.Kind == ProfileKind.Monitor)
        {
            WriteLine("");
            WriteLine("── voice\\lines.json 的 freq 分布（Test-Path 取证：文件存在则按它跳档）──");
            var freq = ProfileStore.VoiceFreqSummary();
            WriteLine($"文件：{freq.Path} 存在={freq.Exists}｜已登记 freq 的事件 {freq.EventCount} 个");
            if (!string.IsNullOrWhiteSpace(freq.Note)) WriteLine($"  提示：{freq.Note}");
            foreach (var kv in freq.Stats)
            {
                bool skipped = plan.SkipFreq?.Contains(kv.Key, StringComparer.OrdinalIgnoreCase) == true;
                WriteLine($"  {kv.Key} = {kv.Value} 条{(skipped ? "  ← 本次跳过" : "")}");
            }
        }

        WriteLine("");
        WriteLine($"── 会写入注册表的事件槽（{plan.SlotWrites.Count} 条）──");
        foreach (var (evt, wav) in plan.SlotWrites)
            WriteLine($"  写入 {evt,-30} {AppEventCatalog.Label(evt),-22} ← {Path.GetFileName(wav)}");
        if (plan.SlotWrites.Count == 0) WriteLine("  （无）");

        WriteLine("");
        WriteLine($"── 被跳过的事件槽（共 {plan.SkippedSlots.Count} 条 = 按档跳过 {plan.SkippedByFreq?.Count ?? 0} 个 + 按事件跳过 {plan.SkippedByEvent?.Count ?? 0} 个）──");
        foreach (var (evt, freq) in plan.SkippedByFreq ?? [])
            WriteLine($"  跳过 {evt,-30} {AppEventCatalog.Label(evt),-22} freq={freq}　原因=skipFreq（整档）");
        foreach (var (evt, freq) in plan.SkippedByEvent ?? [])
            WriteLine($"  跳过 {evt,-30} {AppEventCatalog.Label(evt),-22} freq={freq}　原因=skipEvents（点名）");
        if (plan.SkippedSlots.Count == 0) WriteLine("  （无 —— 一个都不跳）");
        else WriteLine($"  被跳过的事件名：{string.Join("、", plan.SkippedSlots.Select(s => s.Event))}");

        WriteLine("");
        WriteLine($"── 会启动的监听器（{plan.MonitorLineCount} 条）──");
        foreach (var line in plan.Lines.Skip(plan.SlotWrites.Count)) WriteLine($"  {line}");
        if (plan.MonitorLineCount == 0) WriteLine("  （无 —— 启用规则 0 条 = 零钩子）");

        WriteLine("");
        WriteLine("结论：本命令未调用任何注册表写入 API、未启动引擎、未写任何文件。");
        return 0;
    }

    /// <summary>
    /// 监听引擎体检：<c>--monitor-report [路径]</c>。
    /// 对 8 类监听器各做一次「探针启动 → 读状态 → 立刻停止」，所以报告里的「可启动」是<b>实测值</b>而不是推断。
    /// <para>
    /// ⚠️ 只读：不写注册表、<b>不启动引擎常驻</b>（探针只活几十毫秒就卸载）、不创建个人规则表。
    /// </para>
    /// <para>退出码：0 报告已写出 / 5 报告写盘失败。</para>
    /// </summary>
    private static int MonitorReportCommand(string[] args)
    {
        UseUtf8Stdout();

        int idx = Array.FindIndex(args, a => a.Equals(MonitorReportArg, StringComparison.OrdinalIgnoreCase));
        string given = ReadArg(args, idx + 1);
        string path = given.Length > 0 ? given : Path.Combine(Path.GetTempPath(), "bsh-monitor-report.json");

        var rules = RuleStore.Load(out string rulesNote);

        // 探针：真启动一次再卸载，用来给「可启动 / 失败原因」两列提供证据
        string probeDir = Path.GetTempPath();
        var probes = MonitorSources.All.Select(kind => MonitorSources.Probe(kind, probeDir)).ToArray();

        var report = new
        {
            generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            monitor = new
            {
                rulesPath = RuleStore.DefaultPath,
                rulesFileExists = RuleStore.Exists,
                ruleCount = rules.Count,
                enabledRuleCount = rules.Count(r => r.Enabled),
                rulesNote,
                sampleRuleCount = RuleStore.DefaultExamples().Count,
                sourceCount = probes.Length,
                sources = probes.Select(p => new
                {
                    kind = p.Kind,
                    mechanism = p.Mechanism,
                    available = p.Available,
                    unavailableReason = p.UnavailableReason,
                    startable = p.Startable,
                    probeMs = p.ProbeMs,
                    stoppedAfterProbe = p.Stopped,
                    detail = p.Detail,
                    note = p.Note,
                }).ToArray(),
                engineRunning = false,
                enginePolicy =
                    "引擎默认不常驻：只有显式调用 MonitorEngine.Start()（「个性化设置」方案经 ProfileStore.Apply、"
                    + "或 --monitor-selftest）才会按规则实际用到的类别惰性安装钩子与隐藏窗口；"
                    + "--report / --monitor-report / --monitor-status（不加 --live）都不启动引擎。",
                registryUntouched = true,
            },
        };

        try
        {
            var full = Path.GetFullPath(path);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(full, JsonSerializer.Serialize(report, JsonPretty));
            WriteLine($"monitor-report {full}");
            return 0;
        }
        catch (Exception ex)
        {
            WriteLine($"monitor-report failed: {ex.Message}");
            return 5;
        }
    }

    /// <summary>
    /// 监听引擎自检：<c>--monitor-selftest &lt;秒数&gt;</c>。
    /// 缺省秒数为 4；实测输出与退出码由 <see cref="MonitorSelfTest"/> 负责（0 通过 / 1 键盘不足 / 2 文件不足 / 3 都不足）。
    /// </summary>
    private static int MonitorSelftestCommand(string[] args)
    {
        UseUtf8Stdout();

        int idx = Array.FindIndex(args, a => a.Equals(MonitorSelftestArg, StringComparison.OrdinalIgnoreCase));
        string text = ReadArg(args, idx + 1);
        double seconds = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : 4;

        var outcome = MonitorSelfTest.Run(seconds, WriteLine);

        // 末行固定为可解析的英文摘要（正文是中文叙述）
        WriteLine($"selftest exit={outcome.ExitCode} keyboard={outcome.KeyboardEvents} files={outcome.FileEvents} triggered={outcome.Triggered}");
        return outcome.ExitCode;
    }

    /// <summary>取一个位置参数；越界或下一个又是 <c>--</c> 开头的选项时返回空串。</summary>
    private static string ReadArg(string[] args, int index) =>
        index >= 0 && index < args.Length && !args[index].StartsWith("--") ? args[index] : "";

    /// <summary>把 stdout 固定成 UTF-8（无 BOM）；没有可用 stdout（例如双击运行）时静默忽略。</summary>
    private static void UseUtf8Stdout()
    {
        try
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false))
            {
                AutoFlush = true,
            });
        }
        catch
        {
            // 拿不到 stdout 就算了：界面与快照流程都不依赖控制台
        }
    }

    /// <summary>写一行摘要并立刻冲刷（脚本可能读完一行就走）。</summary>
    private static void WriteLine(string text)
    {
        Console.Out.WriteLine(text);
        Console.Out.Flush();
    }
}
