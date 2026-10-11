namespace BSH.Core.Monitoring;

/// <summary>
/// 一条监听事件。<see cref="Detail"/> 的具体格式由各监听器定义（见 <see cref="MonitorRule.Match"/> 的说明）。
/// </summary>
public sealed record MonitorEvent
{
    /// <summary>事件类别（哪个监听器报的）。</summary>
    public MonitorEventKind Kind { get; init; }

    /// <summary>事件明细：键盘是键名、文件是完整路径、进程是 <c>start xxx.exe</c> / <c>stop xxx.exe</c> 等。</summary>
    public string Detail { get; init; } = "";

    /// <summary>事件发生时间（UTC 时钟；展示时再转本地时间）。</summary>
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// 监听器统一接口：八种机制各自实现一份，引擎只认这个接口。
/// <para>约定：<see cref="Start"/> 必须幂等且<b>不许把异常抛给调用方</b>（失败时 <see cref="IsRunning"/> 保持 <c>false</c>，
/// 原因写进 <see cref="LastError"/>）；<see cref="Stop"/> / <see cref="Dispose"/> 必须能干净卸载钩子、窗口与定时器。</para>
/// </summary>
public interface IMonitorSource : IDisposable
{
    /// <summary>本监听器负责的事件类别。</summary>
    MonitorEventKind Kind { get; }

    /// <summary>当前是否在监听（<see cref="Start"/> 失败后为 <c>false</c>）。</summary>
    bool IsRunning { get; }

    /// <summary>上一次启动 / 运行期失败的原因；一切正常时为 <c>null</c>（供 <c>--monitor-report</c> 展示）。</summary>
    string? LastError { get; }

    /// <summary>收到事件时触发。<b>注意：可能在线程池线程上触发</b>，订阅方自己保证线程安全。</summary>
    event Action<MonitorEvent>? Raised;

    /// <summary>开始监听（幂等）。</summary>
    void Start();

    /// <summary>停止监听并卸载资源（幂等）。</summary>
    void Stop();
}
