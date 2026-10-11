namespace BSH.Core.Monitoring;

/// <summary>
/// 监听引擎的<b>进程内宿主</b>（单例）：把「引擎实例 + 最近应用的方案 + 人话状态」收在一处。
/// <para>
/// 为什么要有它：引擎实例必须是<b>唯一一份</b> —— 方案层、托盘、面板若各起一套，
/// 就会出现两套钩子互相叠加、三处状态各说各话。所以：
/// <see cref="ProfileStore"/>（干活）、<see cref="BSH.TrayContext"/>（常驻）、<see cref="BSH.MainForm"/>（界面）
/// 全部经由这里看同一份状态。
/// </para>
/// <para>
/// 策略（用户口径）：<b>启动时不自动开引擎</b> —— 没有明确要求就不该往系统里装钩子。
/// 只有用户点「个性化设置」方案时，才由 <see cref="ProfileStore.Apply"/> 调 <see cref="StartFromRules"/>；
/// 切回「Windows 原样」或 <c>退出</c> 时必须 <see cref="Stop"/>，保证"不监听"就是真的零钩子。
/// </para>
/// </summary>
public static class MonitorHost
{
    private static readonly MonitorEngine _engine = new();
    private static readonly object _gate = new();
    private static string? _activeProfileId;

    /// <summary>本进程共用的引擎实例（自检 <see cref="MonitorSelfTest"/> 另起自己的实例，不走这里）。</summary>
    public static MonitorEngine Engine => _engine;

    /// <summary>引擎是否在跑。</summary>
    public static bool IsRunning => _engine.IsRunning;

    /// <summary>
    /// 最近一次<b>成功应用</b>的方案 id（仅内存，<b>不落盘</b>，重启后回到"未选择"）。
    /// <para>之所以不持久化：启动时不自动开引擎，记住"上次选了谁"只会让界面显示一个并没有生效的状态。</para>
    /// </summary>
    public static string? ActiveProfileId
    {
        get { lock (_gate) return _activeProfileId; }
    }

    /// <summary>记录当前方案（由 <see cref="ProfileStore.Apply"/> 调用）。</summary>
    /// <param name="profileId">方案 id；传 <c>null</c> 表示"未选择 / 不监听"。</param>
    internal static void SetActiveProfile(string? profileId)
    {
        lock (_gate) _activeProfileId = profileId;
    }

    /// <summary>
    /// 按当前规则表启动监听（<b>先停后起</b>）。
    /// <para>
    /// ⚠️ 先 <see cref="Stop"/> 是必须的：<see cref="MonitorEngine.Start"/> 对已在运行的引擎是空操作、
    /// <b>不会</b>重读规则表；用户改完 <c>rules.json</c> 再点一次「个性化设置」时，
    /// 只有先停才能按最新规则重装钩子。
    /// </para>
    /// </summary>
    /// <returns>真正处于运行态的监听器个数（规则表空 / 全禁用时为 0 = 零钩子）。</returns>
    public static int StartFromRules()
    {
        _engine.Stop();
        _engine.Start();
        return RunningSourceCount();
    }

    /// <summary>当前处于运行态的监听器个数（不含启动失败、只算 <c>IsRunning=true</c> 的）。</summary>
    public static int RunningSourceCount() => _engine.Status().Sources.Count(s => s.IsRunning);

    /// <summary>停止引擎并卸载全部钩子 / 隐藏窗口 / 定时器（幂等；退出前必调）。</summary>
    public static void Stop() => _engine.Stop();

    /// <summary>状态快照（规则表路径、条数、各监听器状态）。</summary>
    public static MonitorStatus Status() => _engine.Status();

    /// <summary>事件类别的中文短名（界面 / 菜单 / 报告里用）。</summary>
    /// <param name="kind">事件类别。</param>
    public static string KindLabel(MonitorEventKind kind) => kind switch
    {
        MonitorEventKind.Keyboard => "键盘",
        MonitorEventKind.Mouse => "鼠标",
        MonitorEventKind.WindowSwitch => "窗口切换",
        MonitorEventKind.ProcessStartStop => "进程启停",
        MonitorEventKind.DeviceChange => "设备插拔",
        MonitorEventKind.PowerSession => "电源会话",
        MonitorEventKind.Clipboard => "剪贴板",
        MonitorEventKind.FileSystem => "文件变化",
        _ => kind.ToString(),
    };

    /// <summary>
    /// 人话状态，用于托盘提示与界面状态栏：<c>运行中：3 个监听器（键盘/文件变化）</c>、
    /// <c>运行中：0 个监听器（零规则 = 零钩子）</c>、<c>未运行（不监听）</c>。
    /// </summary>
    public static string DescribeState()
    {
        if (!_engine.IsRunning) return "未运行（不监听）";

        var running = _engine.Status().Sources.Where(s => s.IsRunning).Select(s => KindLabel(s.Kind)).ToArray();
        return running.Length == 0
            ? "运行中：0 个监听器（零规则 = 零钩子）"
            : $"运行中：{running.Length} 个监听器（{string.Join("/", running)}）";
    }
}
