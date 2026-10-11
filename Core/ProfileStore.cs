using System.Text.Json;
using System.Text.Json.Serialization;
using BSH.Core.Monitoring;
using Microsoft.Win32;

namespace BSH.Core;

/// <summary>
/// 方案登记与执行层。
/// <para>内置两套方案：<c>windows-default</c>（Windows 原样）与 <c>personal</c>（个性化设置）。</para>
/// <para>
/// 两套方案的动作别用一句话概括（2026-10-11 起 <c>personal</c> 也写注册表了）：
/// <list type="bullet">
/// <item><description><c>windows-default</c>（<see cref="ProfileKind.RestoreBaseline"/>）：<b>先停监听引擎</b>，
/// 再把 59 槽逐项还原成改造前的原始值 —— "Windows 原样" = 不监听 + 原样音。</description></item>
/// <item><description><c>personal</c>（<see cref="ProfileKind.Monitor"/>）：<b>先按语音方案改写事件槽、
/// 成功后再启动监听引擎</b>（个性化 = 真正接管系统提示音 + 启用监听）。
/// 改写范围由 <c>profiles\personal\profile.json</c> 的 <c>applyVoiceScheme</c> / <c>skipFreq</c> / <c>skipEvents</c> 决定：
/// 两个跳过键<b>叠加生效（并集）</b> —— <c>skipFreq</c> 按频率档整档跳、<c>skipEvents</c> 按事件名精确跳，
/// 后者用来"整档都要、只掐掉其中几个"或"整档跳、再补几个"。</description></item>
/// </list>
/// </para>
/// <para>
/// 分工仍然很明确：<see cref="Plan"/> / <see cref="PlanDetailed"/> 纯只读（只看不动，<b>连写注册表的量都不试算</b>），
/// <see cref="Apply"/> 才动手 —— 但它<b>自己绝不快照</b>，快照由调用方负责
/// （界面按钮 / 命令行入口都会先 <see cref="SchemeSnapshot.Capture"/> 一次）。
/// </para>
/// </summary>
public static class ProfileStore
{
    /// <summary>基线文件名（位于 <c>profiles\windows-default\</c> 下）。</summary>
    private const string BaselineFileName = "baseline-appevents.json";

    /// <summary>个性化方案的开关文件名（位于 <c>profiles\personal\</c> 下）。</summary>
    private const string PersonalConfigFileName = "profile.json";

    /// <summary>方案 <c>windows-default</c> 的 id。</summary>
    public const string WindowsDefaultId = "windows-default";

    /// <summary>方案 <c>personal</c> 的 id。</summary>
    public const string PersonalId = "personal";

    /// <summary>方案 <c>takeover</c> 的 id。</summary>
    public const string TakeoverId = "takeover";

    /// <summary>
    /// 「BSH 接管」要清空的事件槽（写空串 ⇒ 系统不再播它）。
    /// <para><b>第一组 10 个：引擎能替代的</b> —— 清掉系统槽后由监听规则补。这组是「系统提示音不够全面」的正解：
    /// 系统只给一个笼统的 Open，而 BSH 能按具体程序区分（app 字段）。</para>
    /// <para><b>第二组 8 个：界面类</b> —— 引擎感知不到（Maximize/Minimize 不是前台切换；
    /// CCSelect/菜单是 UI 内部行为），但恰恰是「无操作时还响」的主力（后台程序也会触发它们）。
    /// 用户选择「清掉」：宁可没声，也不要误响。</para>
    /// <para><b>其余 21 个刻意保留</b>：崩溃 / UAC / 各通知 / 邮件 / 传真 / 主题 / 近场 / .Default ——
    /// 引擎看不见它们，清了就等于永远失去这个提示。</para>
    /// </summary>
    public static readonly IReadOnlyList<string> TakeoverSilenceSlots =
    [
        // ① 交给 BSH 的 10 个（进程启停 2 + 设备 3 + 会话 3 + 电池 2）
        "Open", "Close",
        "DeviceConnect", "DeviceDisconnect", "DeviceFail",
        "WindowsLogon", "WindowsLogoff", "WindowsUnlock",
        "LowBatteryAlarm", "CriticalBatteryAlarm",
        // ② 界面类 8 个（感知不到，但易被后台程序误触发 ⇒ 清掉）
        "Maximize", "Minimize", "RestoreUp", "RestoreDown",
        "CCSelect", "MenuCommand", "MenuPopup", "ShowBand",
    ];

    /// <summary>
    /// 个性化方案的开关文件路径：<c>&lt;工程根&gt;\profiles\personal\profile.json</c>。
    /// <para>里面的 <c>applyVoiceScheme</c> / <c>skipFreq</c> / <c>skipEvents</c> 决定「个性化设置」改不改注册表、改哪些槽。</para>
    /// </summary>
    public static string PersonalConfigPath =>
        Path.Combine(ProjectRoot, "profiles", "personal", PersonalConfigFileName);

    /// <summary>
    /// 工程根目录：从 <see cref="AppContext.BaseDirectory"/> 沿目录向上找含 <c>BSH.csproj</c> 的那一层。
    /// 开发时是 <c>bin\Release\net10.0-windows\</c>，这样能回溯到工程根去读 <c>profiles\</c>；
    /// 真找不到（如绿色版拷贝到别处）就退回可执行文件所在目录。
    /// </summary>
    public static string ProjectRoot => _projectRoot.Value;

    private static readonly Lazy<string> _projectRoot = new(() =>
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BSH.csproj"))) return dir.FullName;
        }
        return AppContext.BaseDirectory;
    });

    /// <summary>内置方案清单（顺序即界面按钮顺序）。</summary>
    public static IReadOnlyList<SoundProfile> All() =>
    [
        new SoundProfile(
            WindowsDefaultId,
            "Windows 原样",
            "把 59 个系统提示音逐项还原成改造前的原始值（改造前就静音的槽也一并还原为静音）。",
            ProfileKind.RestoreBaseline,
            Path.Combine(ProjectRoot, "profiles", "windows-default", BaselineFileName)),

        new SoundProfile(
            PersonalId,
            "个性化设置",
            "改写系统提示音 + 启用监听：先按 %LOCALAPPDATA%\\BSH\\sounds\\voice-scheme.json 把系统声音事件槽指向自制语音"
            + "（跳过 profiles\\personal\\profile.json 里 skipFreq 列的频率档与 skipEvents 列的事件名，两者取并集），"
            + "成功后再按 profiles\\personal\\rules.json 里启用的规则安装监听器，命中时播放个性化提示音。",
            ProfileKind.Monitor,
            RuleStore.DefaultPath),

        new SoundProfile(
            TakeoverId,
            "BSH 接管",
            "把 18 个系统声音事件槽清空（系统不再自己响），改由 BSH 的监听规则按程序判断；"
            + "崩溃 / UAC / 各类通知等 21 个槽保持系统原样。"
            + "⚠️ BSH 没在运行时，这些被清掉的提示音就是没有 —— 退出 BSH 会自动还原成系统提示音。",
            ProfileKind.Takeover,
            RuleStore.DefaultPath),
    ];

    /// <summary>按 id 找方案（忽略大小写）；找不到返回 <c>null</c>。</summary>
    public static SoundProfile? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : All().FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 可用性与人话描述 —— 点击方案按钮前先问它，不可用就别动注册表。
    /// </summary>
    /// <returns><c>Available=false</c> 时 <c>Status</c> 就是给用户看的失败原因。</returns>
    public static (bool Available, string Status) Describe(SoundProfile p)
    {
        switch (p.Kind)
        {
            case ProfileKind.RestoreBaseline:
            {
                var slots = LoadBaseline(p, out bool legacyInferred);
                if (slots is null) return (false, $"找不到基线文件：{p.Source}");

                // 旧格式基线没有类型字段，只能推断 —— 要在界面上说出来，
                // 别让用户以为这次还原是逐字节的（22 个 ExpandString 槽就是在这点上最容易被改坏）
                string note = legacyInferred ? " · 旧格式基线（类型已推断）" : "";
                return (true, $"基线 {slots.Count} 项（文件：{p.Source}）{note}");
            }

            case ProfileKind.VoiceScheme:
            {
                var scheme = VoiceScheme.TryLoad(p.Source);
                if (scheme is null) return (false, $"找不到语音方案文件：{p.Source}");

                int usable = scheme.Resolve().Count;
                int missing = scheme.MissingFiles().Count;
                // 一条能落地的都没有 = 不可用（避免"点了确认但什么都没发生"）
                return (usable > 0, $"语音方案 {usable} 项、缺失 {missing} 项");
            }

            case ProfileKind.Takeover:
            {
                // 报清楚：会清空几个、其中几个交给引擎、再报规则条数
                int n = TakeoverSilenceSlots.Count;
                int rules = EnabledRuleCount();
                return (true, $"清空 {n} 个槽（10 个改由 BSH 规则判断 + 8 个界面音关掉）；启用规则 {rules} 条");
            }

            case ProfileKind.Monitor:
            {
                // 个性化 = 改事件槽 + 启用监听（2026-10-11 起两件事一起做）。
                // 先报注册表那一半（写多少槽、跳过哪些频率档），再报监听那一半（规则条数、覆盖哪几类监听器）。
                // 两侧都**不算故障**：语音方案文件缺失 → 只跳过注册表那一半；规则表不存在 / 全禁用 → 零钩子。
                var cfg = LoadPersonalConfig(out string cfgNote);
                var voicePath = VoiceScheme.DefaultPath;        // ⚠️ 监听类方案的 p.Source 是规则表路径，不是语音方案路径
                var scheme = VoiceScheme.TryLoad(voicePath);
                var (freqs, freqStats) = ReadVoiceFreqs(out string freqNote);
                var writes = ResolveVoiceSchemeWrites(scheme, cfg, freqs);

                string writeText;
                if (!cfg.ApplyVoiceScheme)
                {
                    writeText = "不改注册表（applyVoiceScheme=false）";
                }
                else if (scheme is null)
                {
                    writeText = $"找得到语音方案：没有（{voicePath}）→ 跳过改注册表那一半";
                }
                else if (writes.Writes.Count == 0 && writes.Skipped.Count == 0)
                {
                    writeText = $"语音方案一条能落地的都没有（可用 0 项、缺失 {writes.MissingFiles.Count} 项）→ 跳过改注册表那一半";
                }
                else
                {
                    // 两个跳过键分列：只报"跳过 N 个"说不清是哪把刀砍的，
                    // 必须能看出「按档跳过几个 + 按事件跳过几个」并带上事件名。
                    string freqText = writes.SkippedByFreq.Count == 0
                        ? "按档跳过 0 个"
                        : $"按档跳过 {writes.SkippedByFreq.Count} 个（skipFreq=[{string.Join(", ", writes.SkipFreq)}]："
                          + $"{string.Join("、", writes.SkippedByFreq.Select(s => s.Event))}）";
                    string evtText = writes.SkippedByEvent.Count == 0
                        ? "按事件跳过 0 个"
                        : $"按事件跳过 {writes.SkippedByEvent.Count} 个（skipEvents："
                          + $"{string.Join("、", writes.SkippedByEvent.Select(s => s.Event))}）";
                    string missText = writes.MissingFiles.Count == 0 ? "" : $"；另有 {writes.MissingFiles.Count} 项 wav 文件缺失会被跳过";
                    writeText = $"写入 {writes.Writes.Count} 个事件槽（语音方案可用 {writes.Writes.Count + writes.MissingFiles.Count} 项），"
                                + freqText + " + " + evtText + missText;
                }

                var rules = RuleStore.Load(out string note);
                int enabled = rules.Count(r => r.Enabled);
                var kinds = EnabledKinds(rules);
                string monitorText = enabled == 0
                    ? $"启用监听：规则表启用 0 条（{RuleStore.DefaultPath}）→ 引擎起了也是零钩子"
                    : $"启用监听：规则 {enabled} 条 → {string.Join("/", kinds.Select(MonitorHost.KindLabel))}";

                // 三截：干什么（写不写注册表）｜启哪些监听器｜数据文件在哪。
                // 尾部两句只在"文件情况不标准"时才带，避免每次状态栏都糊一大段。
                string tail = "";
                if (!File.Exists(voicePath)) tail += $"；语音方案文件不存在（{voicePath}）";
                else if (!string.IsNullOrWhiteSpace(freqNote)) tail += $"；{freqNote}";
                if (!string.IsNullOrWhiteSpace(cfgNote)) tail += $"；{cfgNote}";
                if (!RuleStore.Exists) tail += $"；规则表不存在（{RuleStore.DefaultPath}），可参考公开示例 {RuleStore.ExampleFileName} 起手";

                return (true, $"{writeText} + {monitorText}｜规则表 {RuleStore.DefaultPath}{tail}");
            }

            default:
                return (false, $"未知的方案类型：{p.Kind}");
        }
    }

    /// <summary>
    /// 只读预演：列出"这一按下去会改动哪些事件"（用于确认对话框与 <c>--report</c>）。
    /// 本方法<b>不写任何东西</b>，包括不写注册表、不写文件。
    /// <para>
    /// 对「个性化设置」返回的清单<b>同时含两截</b>：先是要写的事件槽（<c>事件 → 语音文件名</c>），
    /// 后是要装的监听器；被跳过的槽（<c>skipFreq</c> 按档 / <c>skipEvents</c> 按事件）<b>不在</b>清单里，
    /// 想分开取值请用 <see cref="PlanDetailed"/>。
    /// </para>
    /// </summary>
    public static List<string> Plan(SoundProfile p) => PlanDetailed(p).Lines;

    /// <summary>
    /// 只读预演的<b>结构化</b>形态：把「写哪些注册表槽 / 跳过哪些槽 / 启哪些监听器」拆成三份。
    /// <para>
    /// 与 <see cref="Plan"/> 同源，同样<b>纯只读</b> —— 它只读语音方案、<c>profile.json</c>、<c>voice\lines.json</c>
    /// 与规则表，<b>不</b>预写注册表、<b>不</b>试算注册表当前值，所以反复调用零副作用。
    /// </para>
    /// <para>报告（<c>--report</c> / <c>--profile-plan</c>）需要"写 50 条、跳过 9 条"这类可核对数字，就用它 ——
    /// 跳过部分还能拆成「<see cref="ProfilePlan.SkippedByFreq"/> 按档几个 + <see cref="ProfilePlan.SkippedByEvent"/> 按事件几个」。</para>
    /// </summary>
    /// <param name="p">方案。</param>
    public static ProfilePlan PlanDetailed(SoundProfile p) => p.Kind switch
    {
        ProfileKind.RestoreBaseline => PlanRestoreBaseline(p),
        ProfileKind.VoiceScheme => PlanVoiceScheme(p.Source, LoadPersonalConfig(out _), ReadVoiceFreqs(out _).Freqs, Array.Empty<string>()),
        ProfileKind.Monitor => PlanPersonal(p),
        ProfileKind.Takeover => PlanTakeover(p),
        _ => new ProfilePlan([], [], [], [], 0, 0),
    };

    /// <summary>
    /// 「Windows 原样」的预演：逐槽与现场比对，列出<b>真正要改</b>的那些（已经是原样的不重复写）。
    /// </summary>
    /// <summary>
    /// 「BSH 接管」的只读预演：列出要清空的槽，再报会启用哪几类监听器。
    /// <para>不判断"现场是否已经是空的" —— 写空串是幂等的，预演宁多勿少（与 Apply 的实际动作一致）。</para>
    /// </summary>
    private static ProfilePlan PlanTakeover(SoundProfile p)
    {
        var lines = new List<string>();
        var writes = new List<(string Event, string Wav)>();
        foreach (var evt in TakeoverSilenceSlots)
        {
            writes.Add((evt, ""));
            lines.Add($"{evt}  →  （清空：改由 BSH 判断）");
        }
        var kinds = EnabledMonitorKinds();
        lines.Add($"启用监听：{EnabledRuleCount()} 条规则，覆盖 {kinds.Count} 类");
        return new ProfilePlan(lines, [.. writes.Select(x => x.Event)], writes, [], 0, kinds.Count);
    }

    private static ProfilePlan PlanRestoreBaseline(SoundProfile p)
    {
        var lines = new List<string>();
        var slots = LoadBaseline(p, out _);
        if (slots is null) return new ProfilePlan(lines, [], [], [], 0, 0);

        // 当前值 → 基线值，只列真正不同的（已经是原样的槽不重复写）。
        // ⚠️ 比对必须走 ReadAllRaw —— 用「未展开字面值 + 类型」比：
        //    若拿展开后的值来比，22 个 ExpandString 槽会永远被判成"要改"，预演就成了谎报。
        var current = SoundScheme.ReadAllRaw()
            .ToDictionary(s => s.Event, s => s, StringComparer.OrdinalIgnoreCase);

        foreach (var slot in slots)
        {
            if (!current.TryGetValue(slot.Event, out var now))
                now = new EventSlotRaw(slot.Event, null, RegistryValueKind.Unknown, false);

            if (slot.Missing)
            {
                // 静音槽的预演：现场"已经读不出 wav"（键不存在，或键在但零个值）才算原样；
                // 只要现场有默认值（包括被写成空串的"显式静音"）就得列出来 —— 还原会去删掉它。
                // 判定必须与 Apply 的实际动作一致，否则又会出现"预演说不用动、点下去却改了注册表"。
                if (!SilentSlotAlreadyMatches(now)) lines.Add($"{slot.Event}  →  （静音：删默认值、保留键）");
                continue;
            }

            // 值与类型任一不同都算要改 —— 类型不同（String ↔ ExpandString）也是破坏原样
            if (!SameWav(now.RawWav, slot.Wav) || now.Kind != slot.ValueKind)
                lines.Add($"{slot.Event}  →  {DisplayWav(slot.Wav)}");
        }

        return new ProfilePlan(lines, [], [], [], 0, 0);
    }

    /// <summary>
    /// 语音方案的预演：按"文件真在磁盘上"过滤后列出 <c>事件 → 语音文件名</c>，并按
    /// <paramref name="cfg"/> 的 <c>skipFreq</c>（整档）与 <c>skipEvents</c>（点名）分流。
    /// </summary>
    /// <param name="source">语音方案文件路径（<c>VoiceScheme</c> 方案用 <see cref="SoundProfile.Source"/>）。</param>
    /// <param name="cfg">个性化开关（决定跳过哪些频率档、哪些事件名）。</param>
    /// <param name="freqs">事件 → 频率档（来自 <c>voice\lines.json</c>）。</param>
    /// <param name="monitorLines">要追加在后面的监听器清单（纯语音方案传空数组）。</param>
    private static ProfilePlan PlanVoiceScheme(string? source, PersonalProfileConfig cfg,
                                               IReadOnlyDictionary<string, string> freqs,
                                               IReadOnlyList<string> monitorLines)
    {
        var scheme = VoiceScheme.TryLoad(source);
        string? hint = null;
        if (scheme is null)
        {
            hint = $"语音方案文件不存在或读不出来：{source}";
            return new ProfilePlan([.. monitorLines], [], [], [], 0, monitorLines.Count);
        }

        var writes = ResolveVoiceSchemeWrites(scheme, cfg, freqs);
        int freqFallback = freqs.Count == 0 ? writes.Writes.Count : 0;      // freq 信息缺失 → 全部照写

        var lines = new List<string>();
        foreach (var (evt, wav) in writes.Writes) lines.Add($"{evt}  →  {Path.GetFileName(wav)}");
        lines.AddRange(monitorLines);

        return new ProfilePlan(lines, writes.Writes.Select(w => w.Event).ToArray(),
                               writes.Writes, writes.Skipped, freqFallback, monitorLines.Count,
                               hint, writes.MissingFiles, writes.SkipFreq,
                               writes.SkipEvents, writes.SkippedByFreq, writes.SkippedByEvent);
    }

    /// <summary>
    /// 「个性化设置」的预演：<b>两截都给</b> —— 先语音方案会写哪些事件槽（含跳过明细），再会启哪些监听器。
    /// <para>纯只读：只读文件，绝不预先写注册表、也不试算现场值。</para>
    /// </summary>
    private static ProfilePlan PlanPersonal(SoundProfile p)
    {
        var cfg = LoadPersonalConfig(out _);
        var (freqs, _) = ReadVoiceFreqs(out _);
        var scheme = VoiceScheme.TryLoad(VoiceScheme.DefaultPath);

        // 要启的监听器（与原来的口径一致：一条启用规则都没有 → 空清单 = 零钩子）
        var monitorLines = new List<string>();
        var rules = RuleStore.Load(out _);
        foreach (var kind in EnabledKinds(rules))
        {
            int n = rules.Count(r => r.Enabled && r.Kind == kind);
            monitorLines.Add($"{MonitorHost.KindLabel(kind)}（{kind}）：启用 {n} 条规则 → {MonitorSources.Describe(kind)}");
        }

        // applyVoiceScheme=false 时"不写注册表"是刻意的选择，不是缺文件 —— 两条路径分开走，
        // 免得报告里把"用户关了开关"说成"文件丢了"。
        if (!cfg.ApplyVoiceScheme)
        {
            var lines = new List<string>(monitorLines);
            return new ProfilePlan(lines, [], [], [], 0, monitorLines.Count,
                                   Hint: "profile.json 里 applyVoiceScheme=false → 本次不改注册表");
        }

        if (scheme is null)
        {
            var lines = new List<string>(monitorLines);
            return new ProfilePlan(lines, [], [], [], 0, monitorLines.Count,
                                   Hint: $"语音方案文件不存在或读不出来：{VoiceScheme.DefaultPath}");
        }

        return PlanVoiceScheme(VoiceScheme.DefaultPath, cfg, freqs, monitorLines);
    }

    /// <summary>
    /// 应用方案。返回 <see cref="ApplyResult"/>（<c>Changed</c> 的口径随方案类型而变）：
    /// <list type="bullet">
    /// <item><description><see cref="ProfileKind.RestoreBaseline"/>：<b>实际改动条数</b>（写入的槽 + 真被删掉值/删掉键的静音槽）；<c>Monitors=0</c>。</description></item>
    /// <item><description><see cref="ProfileKind.VoiceScheme"/>：写入的事件槽数；<c>Monitors=0</c>。</description></item>
    /// <item><description><see cref="ProfileKind.Monitor"/>：<c>Slots</c>=按语音方案写入的事件槽数，<c>Changed</c>=<b>真正启动的监听器个数</b>。</description></item>
    /// </list>
    /// <para>⚠️ 本方法不会自动快照 —— 「Windows 原样」的调用方必须先 <see cref="SchemeSnapshot.Capture"/> 再调它。</para>
    /// 
    /// </summary>
    /// <exception cref="FileNotFoundException">方案数据文件不存在。</exception>
    /// <exception cref="NotSupportedException">方案类型不认识。</exception>
    /// <exception cref="PartialApplyException">「个性化设置」写到一半失败（详见异常里的 <see cref="PartialApplyException.Partial"/>）。</exception>
    public static ApplyResult Apply(SoundProfile p)
    {
        switch (p.Kind)
        {
            case ProfileKind.RestoreBaseline:
            {
                // 「Windows 原样」= 不监听：先卸载全部钩子 / 隐藏窗口，再去还原注册表。
                // 顺序不能反 —— 若先还原注册表、还原过程抛异常，引擎就会带着钩子留在系统里，
                // 而用户看到的却是"已经切到 Windows 原样"。
                MonitorHost.Stop();

                var slots = LoadBaseline(p, out _)
                    ?? throw new FileNotFoundException($"找不到基线文件：{p.Source}");

                int n = 0;
                foreach (var slot in slots)
                {
                    if (slot.Missing)
                    {
                        // 基线里就没有值的槽 = 改造前本来就是静音（本机 SystemExit / WindowsLogoff）。
                        // 基线的落盘格式只有 String / ExpandString / Missing 三种 Kind，**记不下**
                        // "键在、无值"与"键不存在"的区别；而 Windows 原样是前者，所以按"键在、无值"恢复：
                        // 删默认值、**保留** .Current 子键。
                        //   · 子键现在有值（被别的方案写过）→ 值被删掉，键留下 = 原样；
                        //   · 子键现在没值 → DeleteValue 对缺失值不抛异常，什么都不动；
                        //   · 子键压根不存在 → RestoreSlot 走 OpenSubKey（不是 CreateSubKey），
                        //     返回 false 什么都不做，让它继续不存在 —— 绝不凭空把键建出来。
                        if (SoundScheme.RestoreSlot(slot.Event, keyExists: true, rawWav: null, kind: null)) n++;
                        continue;
                    }

                    // 类型必须一并还原：22 个槽原本是 REG_EXPAND_SZ、字面值 %SystemRoot%\media\X.wav，
                    // 一律按 REG_SZ 写回会同时改掉类型与字面值 —— 读出来虽然一样，却已不是"Windows 原样"。
                    SoundScheme.SetSound(slot.Event, slot.Wav ?? "", slot.ValueKind);
                    n++;
                }
                return new ApplyResult(p.Id, n, 0, SlotsWritten: false, EngineStarted: false, Note: null);
            }

            case ProfileKind.VoiceScheme:
            {
                var scheme = VoiceScheme.TryLoad(p.Source)
                    ?? throw new FileNotFoundException($"找不到语音方案文件：{p.Source}");

                int n = 0;
                foreach (var (evt, wav) in scheme.Resolve())
                {
                    SoundScheme.SetSound(evt, wav);
                    n++;
                }
                return new ApplyResult(p.Id, n, 0, SlotsWritten: true, EngineStarted: false, Note: null);
            }

            case ProfileKind.Monitor:
            {
                // ══ 「个性化设置」= 两件事一起做：① 把系统声音事件槽指向自制语音 ② 启动监听引擎 ══
                //
                // 【顺序为什么是「先写注册表、成功后再启引擎」】
                // 反过来（先启引擎再写注册表）的最坏结果是「钩子在跑、音却没换」：用户以为个性化已生效，
                // 实际系统提示音还是老样子，而且引擎已经常驻，排查时先看到的是一堆正在运行的钩子 —— 噪音大。
                // 现在的顺序最坏结果是「音换了但没监听」：只改了一批注册表值，零常驻资源，
                // 而且这个状态正好被 windows-default 的"停引擎 + 还原 59 槽"完整覆盖 —— 一句话就能说清、一键就能复原。
                //
                // 【中途异常会留下什么】（与 windows-default 的"先停引擎再还原"对称）
                //   · 写第 k 个槽时抛（k ≥ 1）：前 k-1 个槽已被改写，引擎仍是上一次的状态（通常未运行）。
                //     本方法**不吞异常、也不回滚** —— 回滚等于再写一遍注册表，失败概率同样存在，
                //     只会把现场搅得更难判定；改为抛 PartialApplyException 说明"可能只改了一部分"，
                //     并记下 ActiveProfileId（界面/托盘因此显示"语音已改、监听未起"而不是假装无事发生）。
                //   · 监听引擎启动时抛：注册表已整体改完，只剩"没监听"。同上抛异常 + 记 ActiveProfileId，
                //     且异常里带上确切的已写槽数。
                //   · 两种情况都能用「Windows 原样」完全复原（它是按基线逐槽还原、且先停引擎，与本路径无耦合）。
                // 跳过口径与预演完全同源（都走 ResolveVoiceSchemeWrites）：skipFreq 按档跳 + skipEvents 按事件跳，
                // 取并集 —— 所以本方法这里不需要再判一次跳过，免得出现"预演跳过 N 个、真写却写了 N+1 个"。
                var cfg = LoadPersonalConfig(out _);
                var (freqs, _) = ReadVoiceFreqs(out _);

                int slots = 0;
                if (cfg.ApplyVoiceScheme)
                {
                    var scheme = VoiceScheme.TryLoad(VoiceScheme.DefaultPath);
                    if (scheme is not null)
                    {
                        try
                        {
                            slots = WriteVoiceSchemeSlots(scheme, cfg, freqs, write: true);
                        }
                        catch (Exception ex)
                        {
                            // 写注册表这一半失败：**不吞异常、也不回滚**。
                            // 回滚等于再写一遍注册表（失败概率同样存在），只会把现场搅得更难判定；
                            // 改为如实抛出 + 记下方案 id，让界面/托盘显示"语音可能只改了一半、监听未起"。
                            MonitorHost.SetActiveProfile(p.Id);
                            throw new PartialApplyException(
                                $"「{p.Name}」改写系统提示音失败：{ex.Message}。"
                                + "可能只改写了一部分事件槽，请用「Windows 原样」还原后再试。", ex);
                        }
                    }
                }

                try
                {
                    int started = MonitorHost.StartFromRules();
                    MonitorHost.SetActiveProfile(p.Id);
                    return new ApplyResult(p.Id, started, slots, SlotsWritten: slots > 0, EngineStarted: true);
                }
                catch (Exception ex)
                {
                    // 注册表已改完、引擎没起来：如实记下方案 id，别让界面显示"什么都没发生"
                    MonitorHost.SetActiveProfile(p.Id);
                    throw new PartialApplyException(
                        $"「{p.Name}」已写入 {slots} 个事件槽，但监听引擎启动失败：{ex.Message}", ex);
                }
            }

            case ProfileKind.Takeover:
            {
                // 与 Monitor 同一顺序约定：先写注册表（清槽），成功后再启引擎。
                // 中断时最坏留下「系统不响、BSH 也没监听」——那是安静的，而不是「引擎在跑但槽还没清」。
                int cleared = 0;
                foreach (var evt in TakeoverSilenceSlots)
                {
                    SoundScheme.SetSound(evt, "");
                    cleared++;
                }

                int started = MonitorHost.StartFromRules();
                MonitorHost.SetActiveProfile(p.Id);
                return new ApplyResult(p.Id, started, cleared, SlotsWritten: true, EngineStarted: true);
            }

            default:
                throw new NotSupportedException($"未知的方案类型：{p.Kind}");
        }
    }

    // ───────────────────────────── 只读查询（报告 / 界面共用） ─────────────────────────────

    /// <summary>
    /// 监听模式当前<b>启用</b>的规则条数（只读，不启动引擎、不写文件）。
    /// <para>界面状态栏与 <c>--report</c> 都用它，避免两处各写一套统计口径。</para>
    /// </summary>
    public static int EnabledRuleCount() => RuleStore.Load(out _).Count(r => r.Enabled);

    /// <summary>
    /// 监听模式当前启用规则<b>覆盖到的事件类别</b>（按 <see cref="MonitorSources.All"/> 的固定顺序；只读）。
    /// </summary>
    public static IReadOnlyList<MonitorEventKind> EnabledMonitorKinds() => EnabledKinds(RuleStore.Load(out _));

    /// <summary>
    /// <c>voice\lines.json</c> 的频率档统计（只读，供 <c>--report</c> / <c>--profile-plan</c> 与
    /// <see cref="Describe"/> 取证"F5 到底有几条"）。
    /// </summary>
    /// <returns>
    /// <c>Path</c>=台词表路径、<c>Exists</c>=文件在不在、<c>EventCount</c>=登记了 freq 的事件数、
    /// <c>Stats</c>=各档条数（F5→F0 降序）、<c>Note</c>=读不到时的一句话说明（正常为空串）。
    /// </returns>
    public static (string Path, bool Exists, int EventCount, IReadOnlyList<KeyValuePair<string, int>> Stats, string Note)
        VoiceFreqSummary()
    {
        var (freqs, stats) = ReadVoiceFreqs(out string note);
        return (VoiceLinesPath, File.Exists(VoiceLinesPath), freqs.Count, stats, note);
    }

    // ───────────────────────────── 内部工具 ─────────────────────────────

    /// <summary>
    /// 规则表里**启用**的规则覆盖到的事件类别（按 <see cref="MonitorSources.All"/> 的固定顺序去重）。
    /// <para>顺序固定是刻意的：报告、预演、界面三处的列表顺序必须一致，否则对不上号。</para>
    /// </summary>
    /// <param name="rules">规则表（可含禁用项，这里只看启用的）。</param>
    private static List<MonitorEventKind> EnabledKinds(IReadOnlyList<MonitorRule> rules)
    {
        var used = rules.Where(r => r.Enabled).Select(r => r.Kind).ToHashSet();
        return [.. MonitorSources.All.Where(used.Contains)];
    }

    // ───────────────────────── 语音方案 ↔ 注册表（个性化设置专用） ─────────────────────────

    /// <summary>
    /// 按语音方案与 <c>profile.json</c> 的开关，<b>算出会经过筛选的那批槽并按需写入注册表</b>。
    /// </summary>
    /// <remarks>
    /// 预演与执行共用同一份筛选逻辑，杜绝"预演说不用动、点下去却改了注册表"这类谎报。
    /// <para>
    /// <b>两个跳过键叠加（并集）</b>，判定顺序固定为「<b>先按 <c>skipFreq</c> 跳档，再按 <c>skipEvents</c> 跳具体事件</b>」：
    /// 事件在 <c>voice\lines.json</c> 里的 <c>freq</c> 落在 <see cref="PersonalProfileConfig.SkipFreq"/> 里就跳过；
    /// 否则再按事件名去 <see cref="PersonalProfileConfig.SkipEvents"/> 里点名跳过（<b>与 freq 无关</b>，
    /// 所以"整档不要、但其中某一个要"可以用 <c>skipFreq=[...]</c> 之外的 <c>skipEvents</c> 精确表达 ——
    /// 反过来"档不跳、只跳某几个"就把 <c>skipFreq</c> 写成 <c>[]</c>）。
    /// </para>
    /// <para>
    /// 两处都命中时只记一次，归到"按档跳过"（先命中的那把刀记账）⇒ 跳过总数 = 按档数 + 按事件数，不重复计数。
    /// <c>freq</c> 未知（文件缺失 / 事件没登记）只影响按档判定，<b>不影响</b>按事件判定（按名字点名的照跳）。
    /// </para>
    /// </remarks>
    /// <param name="scheme">已加载的语音方案。</param>
    /// <param name="cfg">个性化开关。</param>
    /// <param name="freqs">事件 → 频率档（来自 <c>voice\lines.json</c>）。</param>
    /// <param name="write"><c>true</c>=真写注册表（<see cref="Apply"/>）；<c>false</c>=只算不写（预演）。</param>
    /// <returns>写入条数（仅 <paramref name="write"/> 为 true 时有意义）。</returns>
    private static int WriteVoiceSchemeSlots(VoiceScheme scheme, PersonalProfileConfig cfg,
                                             IReadOnlyDictionary<string, string> freqs, bool write)
    {
        var resolved = ResolveVoiceSchemeWrites(scheme, cfg, freqs);
        if (!write) return resolved.Writes.Count;

        int n = 0;
        foreach (var (evt, wav) in resolved.Writes)
        {
            // 值类型一律 REG_SZ：语音方案是自写的新值，本没有"原本是什么类型"可言；
            // 想回到原类型/原字面值，走「Windows 原样」（它按基线保真还原，含 22 个 ExpandString）。
            SoundScheme.SetSound(evt, wav);
            n++;
        }
        return n;
    }

    /// <summary>
    /// 把语音方案 + 开关解算成"该写哪些槽 / 跳过哪些槽"（<b>纯计算，不碰注册表</b>）。
    /// </summary>
    /// <param name="scheme">语音方案；为 <c>null</c> 时返回空解算结果。</param>
    /// <param name="cfg">个性化开关（决定跳哪些频率档）。</param>
    /// <param name="freqs">事件 → 频率档。</param>
    private static VoiceSchemeWrites ResolveVoiceSchemeWrites(VoiceScheme? scheme, PersonalProfileConfig cfg,
                                                              IReadOnlyDictionary<string, string> freqs)
    {
        if (scheme is null) return new VoiceSchemeWrites([], [], [], [], [], [], []);

        // 两把刀各自归一化：skipFreq 统一大写（F5），skipEvents 去空去重保留书写形态；两处匹配都忽略大小写。
        var skipFreq = NormalizeFreqs(cfg.SkipFreq);
        var skipEvents = NormalizeEvents(cfg.SkipEvents);
        var writes = new List<(string Event, string Wav)>();
        var skippedByFreq = new List<(string Event, string Freq)>();
        var skippedByEvent = new List<(string Event, string Freq)>();

        foreach (var (evt, wav) in scheme.Resolve())     // Resolve 已按"文件真在磁盘上"过滤并排好序
        {
            freqs.TryGetValue(evt, out string? freq);

            // 先按档判、再按事件判（顺序是刻意的）：两处都命中时归"按档跳过"，只记一次，
            // 这样「按档 N 个 + 按事件 M 个」恰好等于跳过总数，报告里的数字相加不会对不上。
            if (ShouldSkipFreq(freq, skipFreq)) skippedByFreq.Add((evt, freq ?? ""));
            else if (ShouldSkipEvent(evt, skipEvents)) skippedByEvent.Add((evt, freq ?? ""));
            else writes.Add((evt, wav));
        }

        // 并集：两个名单互斥，拼接后就是"被跳过"的全集，顺序沿用 Resolve（与写入清单同源，便于左右对照）。
        var skipped = new List<(string Event, string Freq)>(skippedByFreq);
        skipped.AddRange(skippedByEvent);

        return new VoiceSchemeWrites(writes, skipped, skippedByFreq, skippedByEvent,
                                     scheme.MissingFiles(), skipFreq, skipEvents);
    }

    /// <summary>
    /// 读 <c>voice\lines.json</c>（仓库内文件）取「事件 → 频率档」。
    /// <para>
    /// 读不到（文件不存在 / 解析失败）<b>不算故障</b>：返回空表，调用方据此降级为"全部写入"。
    /// 返回的 <paramref name="stats"/> 按 F5→F0 排序，供报告贴出各档条数。
    /// </para>
    /// </summary>
    /// <param name="note">给用户看的一句话（只有异常情况才非空）。</param>
    private static (Dictionary<string, string> Freqs, List<KeyValuePair<string, int>> Stats) ReadVoiceFreqs(out string note)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var stats = new List<KeyValuePair<string, int>>();
        note = "";

        var path = VoiceLinesPath;
        if (!File.Exists(path))
        {
            note = $"找不到 {path} → 读不到 freq，本次不跳任何频率档（全部写入）";
            return (map, stats);
        }

        try
        {
            var doc = JsonSerializer.Deserialize<VoiceLinesFile>(File.ReadAllText(path), JsonOpts);
            var lines = doc?.Lines ?? [];
            var tally = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines)
            {
                string freq = (line.Freq ?? "").Trim();
                if (line.Event.Length == 0 || freq.Length == 0) continue;
                map[line.Event] = freq;
                tally[freq] = tally.TryGetValue(freq, out int c) ? c + 1 : 1;
            }

            // F5 → F0 降序：F5/F4 是高频界面音（最需要跳的），报告里先看到它们
            stats = [.. tally.OrderByDescending(kv => kv.Key, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex)
        {
            note = $"{path} 解析失败（{ex.Message}）→ 读不到 freq，本次不跳任何频率档（全部写入）";
            map.Clear();
            stats.Clear();
        }
        return (map, stats);
    }

    /// <summary>
    /// 读个性化开关文件 <c>profiles\personal\profile.json</c>。
    /// <para>文件不存在 / 解析失败 → 返回内置默认值（<c>applyVoiceScheme=true</c>、<c>skipFreq=["F5"]</c>、
    /// <c>skipEvents=[]</c>），并在 <paramref name="note"/> 里说明是"用的默认值"而不是"用户配的"。</para>
    /// </summary>
    /// <param name="note">给用户看的一句话（只有异常情况才非空）。</param>
    private static PersonalProfileConfig LoadPersonalConfig(out string note)
    {
        note = "";
        var path = PersonalConfigPath;

        if (!File.Exists(path))
        {
            note = $"开关文件不存在（{path}）→ 采用内置默认：改写语音方案、跳过 F5、不按事件跳";
            return new PersonalProfileConfig(true, ["F5"], []);
        }

        try
        {
            var cfg = JsonSerializer.Deserialize<PersonalProfileConfig>(File.ReadAllText(path), JsonOpts);
            if (cfg is null)
            {
                note = $"开关文件解析不出内容（{path}）→ 采用内置默认：改写语音方案、跳过 F5、不按事件跳";
                return new PersonalProfileConfig(true, ["F5"], []);
            }
            return cfg with { SkipFreq = NormalizeFreqs(cfg.SkipFreq), SkipEvents = NormalizeEvents(cfg.SkipEvents) };
        }
        catch (Exception ex)
        {
            note = $"开关文件解析失败（{path}：{ex.Message}）→ 采用内置默认：改写语音方案、跳过 F5、不按事件跳";
            return new PersonalProfileConfig(true, ["F5"], []);
        }
    }

    /// <summary>
    /// 频率档数组归一化：去空、去重、统一大写，并保持用户写的顺序。
    /// <para><c>null</c>（键没写）→ 内置默认 <c>["F5"]</c>；<c>[]</c>（写成空数组）→ 空数组 = <b>一个都不跳</b>。</para>
    /// </summary>
    private static string[] NormalizeFreqs(string[]? freqs)
    {
        if (freqs is null) return ["F5"];        // 键缺失 → 默认跳过极高频界面音

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var raw in freqs)
        {
            string f = (raw ?? "").Trim().ToUpperInvariant();
            if (f.Length == 0 || !seen.Add(f)) continue;
            list.Add(f);
        }
        return [.. list];
    }

    /// <summary>该事件的频率档在不在"跳过"名单里（缺 freq 的事件一律不跳）。</summary>
    private static bool ShouldSkipFreq(string? freq, string[] skipFreq)
    {
        if (skipFreq.Length == 0) return false;                              // 用户明确要求一个都不跳
        if (string.IsNullOrWhiteSpace(freq)) return false;                   // freq 未知 → 不跳（宁可多写，别漏）
        return skipFreq.Contains(freq.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 事件名数组归一化：去空、去重（忽略大小写），并保持用户写的顺序。
    /// <para>
    /// <c>null</c>（键没写）→ 空数组 = <b>一个事件都不跳</b>：<c>skipEvents</c> 是"点名黑名单"，
    /// 没写就是不点名（不像 <c>skipFreq</c> 那样有 <c>["F5"]</c> 的历史默认值）。
    /// <c>[]</c>（写成空数组）与缺键同义。
    /// </para>
    /// <para>这里<b>不</b>统一大小写：事件名按 Windows 习惯书写（<c>MenuCommand</c>），保留原样便于回显用户配置；
    /// 匹配时走 <see cref="StringComparer.OrdinalIgnoreCase"/>，所以写 <c>menucommand</c> 同样生效。</para>
    /// </summary>
    private static string[] NormalizeEvents(string[]? events)
    {
        if (events is null || events.Length == 0) return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var raw in events)
        {
            string e = (raw ?? "").Trim();
            if (e.Length == 0 || !seen.Add(e)) continue;
            list.Add(e);
        }
        return [.. list];
    }

    /// <summary>该事件名在不在"按事件跳过"名单里（纯按名字判，<b>与该事件的 freq 无关</b>）。</summary>
    private static bool ShouldSkipEvent(string evt, string[] skipEvents)
    {
        if (skipEvents.Length == 0) return false;                            // 没点名 = 一个都不跳
        return skipEvents.Contains(evt, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>仓库内的语音台词表路径：<c>&lt;工程根&gt;\voice\lines.json</c>（只有它带 <c>freq</c> 标记）。</summary>
    private static string VoiceLinesPath => Path.Combine(ProjectRoot, "voice", "lines.json");

    /// <summary>
    /// 个性化方案开关文件（<c>profiles\personal\profile.json</c>）里与本类有关的字段。
    /// <para>只声明用得上的键：这个文件同时还是给用户看的说明文档（含 <c>note</c> 长文本），多声明反而容易与它脱节。</para>
    /// </summary>
    /// <param name="ApplyVoiceScheme">true = 应用方案时把事件槽指向语音方案；false = 只启监听、不碰注册表。</param>
    /// <param name="SkipFreq">要跳过不写的频率档（<c>F5</c>…<c>F0</c>）；<c>[]</c> = 一个都不跳，缺键 = 默认 <c>["F5"]</c>。</param>
    /// <param name="SkipEvents">
    /// 要按<b>事件名</b>精确跳过的事件（如 <c>MenuCommand</c>）；<c>[]</c> / 缺键 = 一个事件都不跳。
    /// <para>与 <paramref name="SkipFreq"/> <b>叠加生效（并集）</b>：它能把"整档都要、只要掐掉其中几个"
    /// 表达出来（把 <c>skipFreq</c> 写成 <c>[]</c>），也能在整档跳的基础上再补几个 —— 粒度到单个事件。</para>
    /// </param>
    private sealed record PersonalProfileConfig(
        [property: JsonPropertyName("applyVoiceScheme")] bool ApplyVoiceScheme,
        [property: JsonPropertyName("skipFreq")] string[]? SkipFreq,
        [property: JsonPropertyName("skipEvents")] string[]? SkipEvents);

    /// <summary><c>voice\lines.json</c> 里用得上的两截：<c>lines[]</c> 的 <c>event</c> 与 <c>freq</c>。</summary>
    /// <param name="Lines">台词行。</param>
    private sealed record VoiceLinesFile(
        [property: JsonPropertyName("lines")] List<VoiceLine>? Lines);

    /// <summary>台词行的一行：事件名 + 频率档（其余字段如 <c>zh</c>/<c>en</c>/<c>group</c> 本类不关心）。</summary>
    /// <param name="Event">事件名。</param>
    /// <param name="Freq">频率档（F5/F4/F3/F2/F1/F0）。</param>
    private sealed record VoiceLine(
        [property: JsonPropertyName("event")] string Event,
        [property: JsonPropertyName("freq")] string? Freq);

    /// <summary>语音方案解算结果：该写哪些槽、跳过哪些槽（按档 / 按事件分列）、哪些条目缺文件、用的两把跳过刀。</summary>
    /// <param name="Writes">会写入的事件槽（事件名 → 语音 wav 绝对路径）。</param>
    /// <param name="Skipped">被跳过的事件槽<b>并集</b>（事件名 + 它所属的频率档）= 按档 + 按事件，顺序同 <c>Resolve</c>。</param>
    /// <param name="SkippedByFreq">其中「按频率档整档跳」的那批（附它命中的档，便于核对）。</param>
    /// <param name="SkippedByEvent">其中「按事件名点名跳」的那批（附它所属的档，便于核对）。</param>
    /// <param name="MissingFiles">方案里列了、但磁盘上文件已丢的事件（不阻塞，只是不写）。</param>
    /// <param name="SkipFreq">本次实际生效的跳过频率档。</param>
    /// <param name="SkipEvents">本次实际生效的按事件跳过名单。</param>
    private sealed record VoiceSchemeWrites(
        List<(string Event, string Wav)> Writes,
        List<(string Event, string Freq)> Skipped,
        List<(string Event, string Freq)> SkippedByFreq,
        List<(string Event, string Freq)> SkippedByEvent,
        List<string> MissingFiles,
        string[] SkipFreq,
        string[] SkipEvents);

    /// <summary>
    /// 基线文件的一行：事件名 + 改造前的 wav + **值类型**。
    /// <para>
    /// <c>Kind</c> 是 <c>String</c> / <c>ExpandString</c> / <c>Missing</c> 三者之一
    /// （见 <c>tools\capture-baseline.ps1</c> 的采集口径）。旧格式基线没有这个字段，
    /// 反序列化后为 <c>null</c>，由 <see cref="Normalize"/> 推断。
    /// </para>
    /// </summary>
    private sealed record BaselineEntry(string Event, string? Wav, string? Kind = null);

    /// <summary>归一化后的槽：明确知道该写什么类型、或者该把 <c>.Current</c> 键删掉。</summary>
    /// <param name="Event">事件名。</param>
    /// <param name="Wav">要写回的**未展开**字面值；<paramref name="Missing"/> 为 true 时是 null。</param>
    /// <param name="ValueKind">要写回的注册表类型（<paramref name="Missing"/> 为 true 时无意义）。</param>
    /// <param name="Missing">true = 基线里该槽就没有值，还原动作是删掉 <c>.Current</c> 键（= 静音）。</param>
    /// <param name="Inferred">true = 类型是从旧格式基线推断出来的，不是采集时记下来的。</param>
    private sealed record BaselineSlot(string Event, string? Wav, RegistryValueKind ValueKind,
                                      bool Missing, bool Inferred);

    /// <summary>
    /// 把落盘的一行归一化成"该怎么写回去"。
    /// <para>
    /// 新格式（带 <c>Kind</c>）：如实照做 —— 类型保真的关键就在这，尤其 22 个
    /// <c>ExpandString</c> 槽绝不能被写成 <c>String</c>。
    /// </para>
    /// <para>
    /// 旧格式（无 <c>Kind</c>）：按「<c>Wav</c> 为 null → Missing；含 <c>%</c> → ExpandString；
    /// 否则 String」推断，并标记 <see cref="BaselineSlot.Inferred"/>，
    /// 由 <see cref="Describe"/> 如实告诉用户"类型是推断的"。
    /// </para>
    /// </summary>
    private static BaselineSlot Normalize(BaselineEntry e)
    {
        var kind = e.Kind?.Trim();

        if (!string.IsNullOrEmpty(kind))
        {
            if (kind.Equals("Missing", StringComparison.OrdinalIgnoreCase))
                return new BaselineSlot(e.Event, null, RegistryValueKind.String, Missing: true, Inferred: false);

            if (kind.Equals("ExpandString", StringComparison.OrdinalIgnoreCase))
                return new BaselineSlot(e.Event, e.Wav ?? "", RegistryValueKind.ExpandString, false, false);

            if (kind.Equals("String", StringComparison.OrdinalIgnoreCase))
                return new BaselineSlot(e.Event, e.Wav ?? "", RegistryValueKind.String, false, false);

            // 认不出的类型名（比如以后加了 DWord）：不猜死也不报错，退回按值推断并标记为"已推断"
        }

        // ── 旧格式 / 认不出的类型名：按值推断 ──
        if (e.Wav is null)
            return new BaselineSlot(e.Event, null, RegistryValueKind.String, Missing: true, Inferred: true);

        return new BaselineSlot(e.Event, e.Wav,
            e.Wav.Contains('%') ? RegistryValueKind.ExpandString : RegistryValueKind.String,
            Missing: false, Inferred: true);
    }

    /// <summary>读基线文件并归一化；文件不存在或解析失败返回 null（调用方据此判"不可用"）。</summary>
    /// <param name="p">方案（用它的 <see cref="SoundProfile.Source"/> 当基线文件路径）。</param>
    /// <param name="legacyInferred">true = 基线里至少有一条的类型是旧格式推断出来的。</param>
    private static List<BaselineSlot>? LoadBaseline(SoundProfile p, out bool legacyInferred)
    {
        legacyInferred = false;

        var path = p.Source;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var entries = JsonSerializer.Deserialize<List<BaselineEntry>>(File.ReadAllText(path), JsonOpts);
            if (entries is null) return null;

            var slots = entries.Select(Normalize).ToList();
            legacyInferred = slots.Any(s => s.Inferred);
            return slots;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 静音槽（基线 <c>Kind=Missing</c>）现在是否**已经就是原样**：<c>.Current</c> 键不存在，
    /// 或者键在但**没有默认值**（Windows 原样的"键在、无值"空壳）。
    /// <para>
    /// ⚠️ 判定必须用 <see cref="EventSlotRaw.HasDefaultValue"/>，**不能**用 <see cref="EventSlotRaw.IsSilent"/>：
    /// "键 + 空串"（被人显式写成静音）虽然也读不出 wav，但它**有**默认值，还原时会走
    /// <see cref="SlotRestoreAction.DeleteValue"/> 把它删掉 —— 用 IsSilent 判会漏报，
    /// 又变成"预演说不用动、点下去却改了注册表"的谎报。
    /// </para>
    /// <para>
    /// 本判定只服务于 <see cref="Plan"/>（预演 = 与现场比对出真正会变的槽）；
    /// <see cref="Apply"/> 不再用它 —— Apply 照 <see cref="SchemeSnapshot.Actions"/> 逐条还原，
    /// 幂等性由 <see cref="SoundScheme.RestoreSlot"/> 的返回值（"确实动了没有"）保证。
    /// </para>
    /// </summary>
    /// <param name="now">该槽当前的原样读数；键不存在时传 null。</param>
    private static bool SilentSlotAlreadyMatches(EventSlotRaw? now) => now is null || !now.HasDefaultValue;

    /// <summary>两个 wav 是否"等价"：空与空相同；空与非空不同；其余按 Windows 路径规则忽略大小写比较。</summary>
    private static bool SameWav(string? a, string? b)
    {
        bool ea = string.IsNullOrWhiteSpace(a), eb = string.IsNullOrWhiteSpace(b);
        if (ea || eb) return ea == eb;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>把 wav 路径显示成人话（静音槽显示"（静音）"）。</summary>
    private static string DisplayWav(string? wav) =>
        string.IsNullOrWhiteSpace(wav) ? "（静音）" : Path.GetFileName(wav);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}

/// <summary>
/// 一次方案预演的<b>结构化结果</b>（由 <see cref="ProfileStore.PlanDetailed"/> 产出）。
/// <para>
/// 与 <see cref="ProfileStore.Plan"/> 同源：那边是给人看的一行行文本，这边把三份数据拆开，
/// 好让报告直接贴数字（"写 48 条、跳过 11 条"）、界面按钮判断"到底有没有事可做"。
/// </para>
/// </summary>
/// <param name="Lines">人话清单（写入的事件槽 + 监听器），顺序即执行顺序。</param>
/// <param name="SlotWriteEvents">会改写的事件槽名（只含注册表那一半；纯监听方案为空）。</param>
/// <param name="SlotWrites">会写入的事件槽（事件名 + 语音 wav 绝对路径）。</param>
/// <param name="SkippedSlots">
/// 被跳过的事件槽<b>并集</b>（<c>skipFreq</c> 按档跳 + <c>skipEvents</c> 按事件跳；事件名 + 频率档）。
/// 总数 = <paramref name="SkippedByFreq"/> + <paramref name="SkippedByEvent"/>，不重复计数。
/// </param>
/// <param name="FreqFallbackWrites">
/// freq 信息整个读不到时"照写"的条数（&gt;0 表示本次<b>没有</b>跳任何档，是降级行为）；读到 freq 时为 0。
/// </param>
/// <param name="MonitorLineCount">清单里属于"要启的监听器"的行数。</param>
/// <param name="Hint">降级/异常时的一句话说明（正常为 <c>null</c>）。</param>
/// <param name="MissingFiles">语音方案里列了但文件已丢的事件（不会写）。</param>
/// <param name="SkipFreq">本次实际生效的跳过频率档（粗刀：整档跳）。</param>
/// <param name="SkipEvents">本次实际生效的按事件跳过名单（细刀：点名跳）。</param>
/// <param name="SkippedByFreq">其中"按频率档整档跳"的明细（没有则为 <c>null</c> / 空）。</param>
/// <param name="SkippedByEvent">其中"按事件名点名跳"的明细（没有则为 <c>null</c> / 空）。</param>
public sealed record ProfilePlan(
    List<string> Lines,
    IReadOnlyList<string> SlotWriteEvents,
    IReadOnlyList<(string Event, string Wav)> SlotWrites,
    IReadOnlyList<(string Event, string Freq)> SkippedSlots,
    int FreqFallbackWrites,
    int MonitorLineCount,
    string? Hint = null,
    IReadOnlyList<string>? MissingFiles = null,
    string[]? SkipFreq = null,
    string[]? SkipEvents = null,
    IReadOnlyList<(string Event, string Freq)>? SkippedByFreq = null,
    IReadOnlyList<(string Event, string Freq)>? SkippedByEvent = null);

/// <summary>
/// 一次方案应用的<b>结果</b>（由 <see cref="ProfileStore.Apply"/> 返回）。
/// <para>
/// 「个性化设置」现在同时做两件事，所以结果必须分得开：<see cref="Slots"/> 是"换了几个事件槽的音"，
/// <see cref="Changed"/> 沿用旧口径 ="这次真正改动了多少"（监听方案下 = 启动了几个监听器）。
/// </para>
/// </summary>
/// <param name="ProfileId">方案 id。</param>
/// <param name="Changed">
/// 旧口径的改动数：还原类 = 实际还原的槽数；监听类 = <b>真正启动的监听器个数</b>（零规则时 0 = 零钩子）。
/// </param>
/// <param name="Slots">按语音方案写入的事件槽数（"Windows 原样"与纯语音方案之外的情形为 0）。</param>
/// <param name="SlotsWritten">本次是否真写过注册表。</param>
/// <param name="EngineStarted">本次是否真启动过监听引擎。</param>
/// <param name="Note">补充说明（正常为 <c>null</c>）。</param>
public sealed record ApplyResult(
    string ProfileId,
    int Changed,
    int Slots,
    bool SlotsWritten,
    bool EngineStarted,
    string? Note = null);

/// <summary>
/// 「个性化设置」<b>做到一半失败</b>时抛出：注册表改了一部分 / 改完了但引擎没起来。
/// <para>
/// 为什么要单独一个类型：这两种状态<b>不能</b>当成普通失败处理 —— 系统的提示音可能已经被改掉，
/// 界面与命令行必须把"音可能改了、但监听没起"如实告诉用户，并指向「Windows 原样」复原。
/// </para>
/// </summary>
public sealed class PartialApplyException : Exception
{
    /// <summary>用一段人话说明构造异常（原文异常挂在 <see cref="Exception.InnerException"/> 上）。</summary>
    /// <param name="message">给用户看的说明。</param>
    /// <param name="inner">原始异常。</param>
    public PartialApplyException(string message, Exception inner) : base(message, inner)
    {
    }
}
