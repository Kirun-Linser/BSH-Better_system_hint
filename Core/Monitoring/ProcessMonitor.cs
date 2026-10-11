using System.Diagnostics;

namespace BSH.Core.Monitoring;

/// <summary>
/// 进程启动 / 退出监听：每 <see cref="DefaultPollMs"/> 毫秒对 <c>Process.GetProcesses()</c> 做一次快照对比。
/// <para>
/// 刻意<b>不用 WMI</b>（<c>Win32_ProcessStartTrace</c>）：WMI 事件订阅需要额外权限、在部分机器上还会拖慢启动，
/// 而进程名粒度 1.5 秒的延迟完全可以接受。代价是「启动后立刻退出的短命进程」可能被漏掉（见文件末尾的风险说明）。
/// </para>
/// <para>
/// 事件明细固定为 <c>start 名字</c> / <c>stop 名字</c>（名字统一小写 <c>.exe</c> 结尾），
/// 规则可以写 <c>steam.exe</c>（两个方向都触发）或 <c>start steam.exe</c> / <c>stop steam.exe</c> 限定方向。
/// </para>
/// </summary>
public sealed class ProcessMonitor : MonitorSourceBase
{
    /// <summary>默认轮询间隔（毫秒）。</summary>
    /// <remarks>
    /// 2026-10-11 用户反馈「打开/关闭有延迟」后从 1500 下调到 300。
    /// 实测本机 <c>Process.GetProcesses()</c>（332 个进程）单次约 7ms（最大 14ms），
    /// 300ms 间隔 ≈ 2.4% 单核 —— 换来延迟从 0~1.5s 降到 0~0.3s，值。
    /// </remarks>
    public const int DefaultPollMs = 300;

    private readonly int _pollMs;
    private System.Threading.Timer? _timer;
    private HashSet<string>? _snapshot;
    private int _polling;       // 防止上一次轮询没跑完又进来（进程枚举偶尔会很慢）

    /// <summary>创建监听器。</summary>
    /// <param name="pollMs">轮询间隔（毫秒），小于 200 会被抬到 200，避免把机器拖满。</param>
    public ProcessMonitor(int pollMs = DefaultPollMs)
    {
        _pollMs = Math.Max(200, pollMs);
    }

    /// <inheritdoc />
    public override MonitorEventKind Kind => MonitorEventKind.ProcessStartStop;

    /// <inheritdoc />
    public override string Mechanism => $"轮询 Process.GetProcesses() 快照对比（每 {_pollMs} ms）";

    /// <summary>上一次快照里的进程名个数（诊断用；未启动时为 0）。</summary>
    public int LastSnapshotSize => _snapshot?.Count ?? 0;

    /// <inheritdoc />
    protected override void OnStart()
    {
        // 先取一份基线快照：启动瞬间已在运行的进程不补报事件（否则一启动就刷屏）
        _snapshot = Snapshot();
        _timer = new System.Threading.Timer(Poll, null, _pollMs, _pollMs);
    }

    /// <inheritdoc />
    protected override void OnStop()
    {
        _timer?.Dispose();
        _timer = null;
        _snapshot = null;
        Interlocked.Exchange(ref _polling, 0);
    }

    /// <summary>轮询回调（线程池线程）：对比快照并投递差异。</summary>
    private void Poll(object? state)
    {
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0) return;   // 上一轮还没跑完，本轮跳过

        try
        {
            var current = Snapshot();
            var previous = _snapshot;
            _snapshot = current;
            if (previous is null) return;

            foreach (var name in current)
            {
                if (!previous.Contains(name)) Publish($"start {name}");
            }

            foreach (var name in previous)
            {
                if (!current.Contains(name)) Publish($"stop {name}");
            }
        }
        catch
        {
            // 轮询失败不许把定时器打挂：下一轮照常继续
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    /// <summary>取当前进程名快照（统一「小写 + .exe」）。</summary>
    private static HashSet<string> Snapshot()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                set.Add(p.ProcessName.ToLowerInvariant() + ".exe");
            }
            catch
            {
                // 受保护进程 / 刚好退出的进程：跳过
            }
            finally
            {
                p.Dispose();
            }
        }

        return set;
    }
}
