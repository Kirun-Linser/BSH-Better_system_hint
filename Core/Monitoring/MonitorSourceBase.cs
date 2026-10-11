namespace BSH.Core.Monitoring;

/// <summary>
/// 监听器基类：把「幂等启停 / 启动失败不崩溃 / 事件只投递不重活」这三件每个监听器都要做的事收敛到一处。
/// <para>子类只实现 <see cref="OnStart"/> 与 <see cref="OnStop"/>，并在底层回调里调用 <see cref="Publish(string)"/>。</para>
/// <para>
/// ⚠️ 铁律：<b>回调线程里只允许投递</b>。<see cref="Publish(string)"/> 会把事件交给线程池再触发
/// <see cref="Raised"/>，所以低层键鼠钩子的回调（系统有 <c>LowLevelHooksTimeout</c> 超时限制，超时会被摘钩子）
/// 永远不会执行字符串匹配、播放声音这类重活。
/// </para>
/// </summary>
public abstract class MonitorSourceBase : IMonitorSource
{
    private readonly object _gate = new();
    private bool _running;
    private long _published;

    /// <inheritdoc />
    public abstract MonitorEventKind Kind { get; }

    /// <summary>底层机制的一句话描述（给 <c>--monitor-report</c> 展示）。</summary>
    public abstract string Mechanism { get; }

    /// <inheritdoc />
    public string? LastError { get; protected set; }

    /// <inheritdoc />
    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    /// <summary>本监听器累计投递的事件条数（自检用来对账，与规则是否命中无关）。</summary>
    public long PublishedCount => Interlocked.Read(ref _published);

    /// <inheritdoc />
    public event Action<MonitorEvent>? Raised;

    /// <inheritdoc />
    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;          // 幂等
            LastError = null;
            try
            {
                OnStart();
                _running = true;
            }
            catch (Exception ex)
            {
                // 任何监听器启动失败都只记原因，绝不把异常抛给调用方（进程不许因此崩溃）
                LastError = Describe(ex);
                _running = false;
                CleanupQuietly();
            }
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        lock (_gate)
        {
            if (!_running) return;          // 幂等
            CleanupQuietly();
            _running = false;
        }
    }

    /// <inheritdoc />
    public virtual void Dispose() => Stop();

    /// <summary>子类在这里装钩子 / 建窗口 / 起定时器；抛异常会被基类捕获成 <see cref="LastError"/>。</summary>
    protected abstract void OnStart();

    /// <summary>子类在这里卸载一切资源；必须可重复调用、不抛异常。</summary>
    protected abstract void OnStop();

    /// <summary>投递一条事件（只在底层回调线程里调用，成本必须极低）。</summary>
    protected void Publish(string detail)
    {
        Interlocked.Increment(ref _published);
        var evt = new MonitorEvent { Kind = Kind, Detail = detail, TimestampUtc = DateTime.UtcNow };

        // 显式写泛型参数：否则会被 QueueUserWorkItem(WaitCallback, object) 这个重载抢走，状态退化成 object
        var state = ((MonitorSourceBase)this, evt);
        ThreadPool.QueueUserWorkItem<(MonitorSourceBase Source, MonitorEvent Event)>(static s =>
        {
            try
            {
                s.Source.Raised?.Invoke(s.Event);
            }
            catch
            {
                // 订阅方（引擎）抛异常不得反噬监听器：这里静默吞掉，引擎自己保证不抛
            }
        }, state, preferLocal: false);
    }

    /// <summary>把异常压成一行可读文本（报告里要展示，不能带换行）。</summary>
    protected static string Describe(Exception ex) =>
        $"{ex.GetType().Name}: {ex.Message.Replace('\r', ' ').Replace('\n', ' ')}";

    private void CleanupQuietly()
    {
        try
        {
            OnStop();
        }
        catch (Exception ex)
        {
            LastError ??= Describe(ex);
        }
    }
}
