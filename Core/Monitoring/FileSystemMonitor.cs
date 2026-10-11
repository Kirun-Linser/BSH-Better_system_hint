namespace BSH.Core.Monitoring;

/// <summary>
/// 文件系统监听：每个目标目录一个 <see cref="FileSystemWatcher"/>。
/// <para>
/// 目录来自规则表里 <see cref="MonitorEventKind.FileSystem"/> 规则的 <see cref="MonitorRule.Match"/>；
/// 引擎在 <c>Start</c> 之前用 <see cref="SetRoots"/> 把目录集合灌进来。
/// </para>
/// <para>
/// 事件明细是<b>被变更项的完整路径</b>，前面带变更类型，形如 <c>Created C:\Users\11\Downloads\a.txt</c>；
/// 规则写目录路径即可命中该目录下的所有变化（见 <see cref="MonitorMatcher"/>）。
/// </para>
/// <para><c>IncludeSubdirectories</c> 固定为 <c>false</c>：只盯这一层，避免监视 <c>%TEMP%</c> 这类目录时被整棵子树的噪声淹没。</para>
/// </summary>
public sealed class FileSystemMonitor : MonitorSourceBase
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private string[] _roots = [];

    /// <summary>当前生效的监视目录（规范化后的绝对路径）。</summary>
    public IReadOnlyList<string> Roots => _roots;

    /// <inheritdoc />
    public override MonitorEventKind Kind => MonitorEventKind.FileSystem;

    /// <inheritdoc />
    public override string Mechanism => "FileSystemWatcher（每个目录一个，单层不递归）";

    /// <summary>
    /// 设置要监视的目录（<b>必须在 <see cref="MonitorSourceBase.Start"/> 之前调用</b>）。
    /// 不存在的目录会被剔除，并记进 <see cref="MonitorSourceBase.LastError"/> 作为提示。
    /// </summary>
    public void SetRoots(IEnumerable<string>? directories)
    {
        var accepted = new List<string>();
        var missing = new List<string>();

        foreach (var raw in directories ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            string full;
            try
            {
                full = Path.GetFullPath(raw.Trim());
            }
            catch
            {
                missing.Add(raw);
                continue;
            }

            if (Directory.Exists(full)) accepted.Add(full);
            else missing.Add(full);
        }

        _roots = [.. accepted.Distinct(StringComparer.OrdinalIgnoreCase)];
        LastError = missing.Count > 0 ? $"以下目录不存在、已跳过：{string.Join("；", missing)}" : null;
    }

    /// <inheritdoc />
    protected override void OnStart()
    {
        if (_roots.Length == 0)
        {
            throw new InvalidOperationException("没有可监视的目录（规则表里没有可用的 FileSystem 规则 Match）");
        }

        foreach (var dir in _roots)
        {
            var watcher = new FileSystemWatcher(dir)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                             | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                InternalBufferSize = 64 * 1024,     // 默认 8KB 在繁忙目录上很容易溢出丢事件
            };

            watcher.Created += (_, e) => Publish($"Created {e.FullPath}");
            watcher.Changed += (_, e) => Publish($"Changed {e.FullPath}");
            watcher.Deleted += (_, e) => Publish($"Deleted {e.FullPath}");
            watcher.Renamed += (_, e) => Publish($"Renamed {e.FullPath}");
            watcher.Error += (_, e) =>
            {
                // 缓冲区溢出等运行期故障：记原因即可，不抛异常（重新 Start 可自愈）
                LastError = $"FileSystemWatcher({dir}) 出错：{Describe(e.GetException())}";
            };

            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
    }

    /// <inheritdoc />
    protected override void OnStop()
    {
        foreach (var watcher in _watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch
            {
                // 释放阶段的异常忽略
            }
        }

        _watchers.Clear();
    }
}
