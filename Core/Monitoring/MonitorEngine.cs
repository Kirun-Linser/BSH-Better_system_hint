namespace BSH.Core.Monitoring;

/// <summary>单个监听器的运行状态（给 <c>--monitor-report</c> 与后续界面用）。</summary>
/// <param name="Kind">事件类别。</param>
/// <param name="Mechanism">底层机制。</param>
/// <param name="IsRunning">是否在监听。</param>
/// <param name="PublishedCount">该监听器累计投递的事件条数。</param>
/// <param name="LastError">失败 / 提示信息。</param>
public sealed record MonitorSourceStatus(
    MonitorEventKind Kind,
    string Mechanism,
    bool IsRunning,
    long PublishedCount,
    string? LastError);

/// <summary>引擎整体状态快照。</summary>
/// <param name="EngineRunning">引擎是否在跑。</param>
/// <param name="RulesPath">规则表路径。</param>
/// <param name="RulesFileExists">规则表文件是否存在。</param>
/// <param name="RuleCount">规则总数。</param>
/// <param name="EnabledRuleCount">启用中的规则数。</param>
/// <param name="TriggeredCount">规则命中（触发）次数。</param>
/// <param name="PlayedCount">真正播放成功的声音次数。</param>
/// <param name="SuppressedPlaybackCount">被抑制的播放次数（自检模式）。</param>
/// <param name="RulesNote">规则表来源说明（读盘 / 内存 / 解析失败）。</param>
/// <param name="Sources">已启动的监听器状态。</param>
public sealed record MonitorStatus(
    bool EngineRunning,
    string RulesPath,
    bool RulesFileExists,
    int RuleCount,
    int EnabledRuleCount,
    long TriggeredCount,
    long PlayedCount,
    long SuppressedPlaybackCount,
    string? RulesNote,
    IReadOnlyList<MonitorSourceStatus> Sources);

/// <summary>
/// 监听引擎：<b>规则表 → 需要哪些监听器 → 事件 → 匹配 → 冷却 → 播放</b> 的总装。
/// <para>
/// 三个设计要点：
/// <list type="number">
/// <item><description><b>惰性启动</b>：只按规则表里<b>实际用到的</b> Kind 起监听器 —— 没有规则就一个钩子都不装（这是「默认不常驻」的实现方式）。</description></item>
/// <item><description><b>默认不常驻</b>：引擎不会自己启动。调用方显式 <see cref="Start"/>（自检走这条）之后才会有钩子/窗口存在。</description></item>
/// <item><description><b>订阅方不抛</b>：事件从线程池进来，引擎内部所有动作都吞异常，绝不让规则/播放的问题反噬监听器。</description></item>
/// </list>
/// </para>
/// <para>
/// 接入方式（2026-10-10 起）：由 <see cref="MonitorHost"/> 持有<b>唯一</b>实例，
/// 「个性化设置」方案（<see cref="ProfileKind.Monitor"/>）经 <c>ProfileStore.Apply</c> 启动它，
/// 「Windows 原样」与托盘退出都会把它停掉；<c>--monitor-report</c> / <c>--monitor-status</c> 仍保持只读、不启引擎。
/// </para>
/// </summary>
public sealed class MonitorEngine : IDisposable
{
    private readonly object _gate = new();
    private readonly List<MonitorRule> _rules = [];
    private readonly Dictionary<MonitorEventKind, IMonitorSource> _sources = [];
    private readonly Dictionary<string, DateTime> _lastTriggerUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<MonitorEvent> _recent = [];

    /// <summary>连构造都失败的类别（如未知枚举值）：原因记在这里，由 <see cref="Status"/> 一并报出。</summary>
    private readonly Dictionary<MonitorEventKind, string> _failedKinds = [];

    private VoiceScheme? _voice;
    private bool _voiceLoaded;
    private bool _running;
    private long _triggered;
    private long _played;
    private long _suppressed;

    /// <summary>为 <c>true</c> 时只做匹配与记账、不真的发声（自检用；默认 <c>false</c>）。</summary>
    public bool SuppressPlayback { get; set; }

    /// <summary>内存里保留的最近事件条数上限（自检与诊断用）。</summary>
    public int RecentCapacity { get; set; } = 500;

    /// <summary>引擎是否在跑。</summary>
    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    /// <summary>当前生效的规则表。</summary>
    public IReadOnlyList<MonitorRule> Rules
    {
        get { lock (_gate) return _rules.ToArray(); }
    }

    /// <summary>规则表来源说明。</summary>
    public string? RulesNote { get; private set; }

    /// <summary>
    /// 最近一次 <see cref="Stop"/> 之后各监听器的收尾状态（<c>IsRunning</c> 应全为 <c>false</c>）。
    /// 这是「钩子 / 窗口确实已卸载」的可查证据；引擎未启动过 Stop 时为空。
    /// </summary>
    public IReadOnlyList<MonitorSourceStatus> LastStoppedSources
    {
        get { lock (_gate) return _lastStopped; }
    }

    private MonitorSourceStatus[] _lastStopped = [];

    /// <summary>规则命中次数。</summary>
    public long TriggeredCount => Interlocked.Read(ref _triggered);

    /// <summary>
    /// 启动引擎。
    /// </summary>
    /// <param name="rules">
    /// 规则表；传 <c>null</c> 表示从 <see cref="RuleStore.DefaultPath"/> 读盘（默认行为）。
    /// 自检传内存规则，避免动用户的个人配置。
    /// </param>
    public void Start(IReadOnlyList<MonitorRule>? rules = null)
    {
        lock (_gate)
        {
            if (_running) return;

            if (rules is null)
            {
                rules = RuleStore.Load(out var note);
                RulesNote = note;
            }
            else
            {
                RulesNote = $"使用内存规则表（{rules.Count} 条，未读盘）";
            }

            _rules.Clear();
            _rules.AddRange(rules);
            _lastTriggerUtc.Clear();

            // 惰性启动：只起规则里真正用到的监听器；一条规则都没有 → 一个钩子都不装
            foreach (var kind in NeededKinds(_rules))
            {
                IMonitorSource? source = null;
                try
                {
                    source = MonitorSources.Create(kind);

                    // 文件监听的目录来自规则 Match（可多条）
                    if (source is FileSystemMonitor fileMonitor)
                    {
                        fileMonitor.SetRoots(FileSystemDirs(_rules));
                    }

                    source.Raised += OnEvent;
                    source.Start();          // 启动失败不抛：原因记在 source.LastError
                    _sources[kind] = source;
                }
                catch (Exception ex)
                {
                    source?.Dispose();
                    _sources.Remove(kind);
                    // 单个监听器起不来不影响其它监听器，原因通过 Status() 暴露
                    _failedKinds[kind] = $"{ex.GetType().Name}: {ex.Message}";
                }
            }

            _running = true;
        }
    }

    /// <summary>停止引擎并卸载全部钩子 / 窗口 / 定时器（幂等）。</summary>
    public void Stop()
    {
        lock (_gate)
        {
            var stopped = new List<MonitorSourceStatus>(_sources.Count);

            foreach (var (kind, source) in _sources)
            {
                source.Raised -= OnEvent;
                try
                {
                    source.Stop();
                    source.Dispose();
                }
                catch
                {
                    // 卸载阶段的异常忽略：Stop 必须走完，否则会留下挂起的钩子
                }

                // Stop 之后再读一次状态：IsRunning=false 才算真的卸载干净
                stopped.Add(new MonitorSourceStatus(
                    kind,
                    MonitorSources.Describe(kind),
                    source.IsRunning,
                    source is MonitorSourceBase b ? b.PublishedCount : 0,
                    source.LastError));
            }

            _lastStopped = [.. stopped];
            _sources.Clear();
            _failedKinds.Clear();
            _running = false;
        }
    }

    /// <inheritdoc />
    public void Dispose() => Stop();

    /// <summary>取最近的原始事件（按时间正序，最多 <paramref name="max"/> 条）。</summary>
    public IReadOnlyList<MonitorEvent> RecentEvents(int max = 100)
    {
        lock (_gate)
        {
            if (max <= 0 || _recent.Count == 0) return [];
            return _recent.Skip(Math.Max(0, _recent.Count - max)).ToArray();
        }
    }

    /// <summary>
    /// 自检用：取某类监听器的实例（未启动 / 启动失败返回 <c>null</c>）。
    /// <para>仅供 <see cref="MonitorSelfTest"/> 做「自投递消息」类的事件级验证（设备 / 电源），不对外暴露。</para>
    /// </summary>
    /// <param name="kind">监听类别。</param>
    internal IMonitorSource? FindSource(MonitorEventKind kind)
    {
        lock (_gate) return _sources.TryGetValue(kind, out var source) ? source : null;
    }

    /// <summary>取状态快照。</summary>
    public MonitorStatus Status()
    {
        lock (_gate)
        {
            var sources = _sources
                .Select(kv => new MonitorSourceStatus(
                    kv.Key,
                    MonitorSources.Describe(kv.Key),
                    kv.Value.IsRunning,
                    kv.Value is MonitorSourceBase b ? b.PublishedCount : 0,
                    kv.Value.LastError))
                .ToArray();

            // 启动阶段就失败的类别不在 _sources 里，单独补进状态，别让报告漏掉
            var failed = _failedKinds
                .Where(kv => !_sources.ContainsKey(kv.Key))
                .Select(kv => new MonitorSourceStatus(kv.Key, MonitorSources.Describe(kv.Key), false, 0, kv.Value))
                .ToArray();

            return new MonitorStatus(
                _running,
                RuleStore.DefaultPath,
                RuleStore.Exists,
                _rules.Count,
                _rules.Count(r => r.Enabled),
                TriggeredCount,
                Interlocked.Read(ref _played),
                Interlocked.Read(ref _suppressed),
                RulesNote,
                [.. sources, .. failed]);
        }
    }

    /// <summary>规则命中的处理：先记账，再匹配，最后播放（全部在引擎侧，绝不在钩子回调里）。</summary>
    private void OnEvent(MonitorEvent evt)
    {
        Record(evt);

        MonitorRule? hit = null;
        lock (_gate)
        {
            if (!_running) return;

            var now = DateTime.UtcNow;
            foreach (var rule in _rules)
            {
                if (!rule.Enabled || rule.Kind != evt.Kind) continue;
                if (!MonitorMatcher.IsMatch(rule, evt.Detail)) continue;

                // 冷却（防抖）：同一条规则在 CooldownMs 内只触发一次
                if (rule.CooldownMs > 0
                    && _lastTriggerUtc.TryGetValue(rule.Id, out var last)
                    && (now - last).TotalMilliseconds < rule.CooldownMs)
                {
                    continue;
                }

                _lastTriggerUtc[rule.Id] = now;
                hit = rule;          // 同一事件只兑现第一条命中的规则，避免多重播放互相打断
                break;
            }

            if (hit is null) return;

            Interlocked.Increment(ref _triggered);
            if (SuppressPlayback)
            {
                Interlocked.Increment(ref _suppressed);
                return;
            }

            Play(hit);
        }
    }

    /// <summary>播放规则指定的声音：优先 <see cref="MonitorRule.Sound"/>，否则用 <see cref="MonitorRule.SpeakKey"/> 去语音方案里取。</summary>
    private void Play(MonitorRule rule)
    {
        try
        {
            var wav = ResolveWav(rule);
            if (wav is null) return;

            if (AudioProbe.PlayFile(wav)) Interlocked.Increment(ref _played);
        }
        catch
        {
            // 播放失败（文件被删、声卡异常）不得影响引擎继续跑
        }
    }

    /// <summary>解析出要播的 wav 路径；解析不出来返回 <c>null</c>。语音方案只加载一次。</summary>
    private string? ResolveWav(MonitorRule rule)
    {
        if (!string.IsNullOrWhiteSpace(rule.Sound)) return rule.Sound;
        if (string.IsNullOrWhiteSpace(rule.SpeakKey)) return null;

        if (!_voiceLoaded)
        {
            _voice = VoiceScheme.TryLoad();
            _voiceLoaded = true;
        }

        if (_voice is not null
            && _voice.Sounds.TryGetValue(rule.SpeakKey, out var wav)
            && !string.IsNullOrWhiteSpace(wav))
        {
            return wav;
        }

        return null;
    }

    private void Record(MonitorEvent evt)
    {
        lock (_gate)
        {
            _recent.Enqueue(evt);
            while (_recent.Count > Math.Max(1, RecentCapacity)) _recent.Dequeue();
        }
    }

    /// <summary>规则表里实际用到的监听类别（按 <see cref="MonitorSources.All"/> 的固定顺序去重）。</summary>
    private static List<MonitorEventKind> NeededKinds(IEnumerable<MonitorRule> rules)
    {
        var used = rules.Where(r => r.Enabled).Select(r => r.Kind).ToHashSet();
        return [.. MonitorSources.All.Where(used.Contains)];
    }

    /// <summary>文件监听要盯的目录集合（取所有启用规则里的 Match）。</summary>
    private static List<string> FileSystemDirs(IEnumerable<MonitorRule> rules) =>
        [.. rules
            .Where(r => r.Enabled && r.Kind == MonitorEventKind.FileSystem)
            .SelectMany(r => MonitorMatcher.Split(r.Match))];
}
