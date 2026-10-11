namespace BSH.Core;

/// <summary>
/// 方案类型 —— 决定 <see cref="SoundProfile.Source"/> 里的东西该怎么解释。
/// </summary>
public enum ProfileKind
{
    /// <summary>还原基线：把 59 个槽逐项写回"改造前原始值"（含改造前就是静音的槽）。</summary>
    RestoreBaseline,

    /// <summary>
    /// 语音方案：把事件指向 voice-scheme.json 里列出的自制 wav。
    /// <para>⚠️ 已<b>不在</b>内置方案里使用（「个性化设置」2026-10-10 起改为 <see cref="Monitor"/>），
    /// 保留枚举值是为了以后想回到"整表改写注册表"的形态时不用动数据格式。</para>
    /// </summary>
    VoiceScheme,

    /// <summary>
    /// 监听模式：<b>两件事一起做</b>（2026-10-11 起）—— 先把系统声音事件槽指向自制语音
    /// （<c>%LOCALAPPDATA%\BSH\sounds\voice-scheme.json</c>，按 <c>profiles\personal\profile.json</c> 的
    /// <c>applyVoiceScheme</c> / <c>skipFreq</c> 决定写哪些槽），<b>成功后再</b>按规则表
    /// （<c>profiles\personal\rules.json</c>）启动监听引擎，在规则命中的那一刻播放个性化提示音。
    /// <para>
    /// 为什么两件事都要：系统自带的提示音不够全面（且要换成自己的声音），
    /// 所以 ① 用语音接管 59 个事件槽里的绝大部分，② 再由监听器盯键盘 / 鼠标 / 窗口 / 进程 / 设备 /
    /// 电源 / 剪贴板 / 文件，该响的时候按规则补一声。
    /// </para>
    /// <para>⚠️ 顺序是"先写注册表、成功后再启引擎"：中断时最坏留下"音换了但没监听"（零常驻资源，
    /// 「Windows 原样」可完整复原），而不是"钩子在跑但音没换"。</para>
    /// </summary>
    Monitor,

    /// <summary>
    /// BSH 接管：把系统的一部分提示音关掉（写空串），改由监听引擎按规则决定（2026-10-11 用户定）。
    /// <para>
    /// 用户原话是「BSH 运行时替代原生系统的提示音，全部由 BSH 判断，这样也能解决无任何操作时还有提示音」。
    /// 但技术上不能真「全部」—— 引擎只感知得到键盘/鼠标/窗口/进程/设备/电源/剪贴板/文件这 8 类，
    /// 而 Maximize / CCSelect / SystemAsterisk / 通知 / UAC 这些东西它看不见。
    /// </para>
    /// <para>
    /// 所以按用户选定的混合策略（见 ProfileStore.TakeoverSilenceSlots）：
    /// ① 引擎能替代的 10 个（进程启停 / 设备 / 会话 / 电池）清空后交给规则；
    /// ② 界面类 8 个（感知不到，但正是「无操作时还响」的主力）一并清掉；
    /// ③ 其余 21 个一律保留（崩溃 / UAC / 各类通知…），清了就等于永远没有这个提示。
    /// </para>
    /// </summary>
    Takeover,
}

/// <summary>
/// 一套可一键切换的提示音方案（目前内置两套：Windows 原样 / 个性化设置）。
/// 只描述"是什么、数据在哪"，不含任何行为 —— 干活的是 <see cref="ProfileStore"/>。
/// </summary>
/// <param name="Id">稳定标识，命令行 <c>--apply-profile &lt;id&gt;</c> 用的就是它。</param>
/// <param name="Name">界面显示名。</param>
/// <param name="Description">一句话说明（用于提示与文档）。</param>
/// <param name="Kind">方案类型，决定 <paramref name="Source"/> 怎么解释。</param>
/// <param name="Source">
/// 数据来源文件：基线 json 的绝对路径（<see cref="ProfileKind.RestoreBaseline"/>）、
/// 语音方案 json 的绝对路径（<see cref="ProfileKind.VoiceScheme"/>），
/// 或监听规则表 <c>rules.json</c> 的绝对路径（<see cref="ProfileKind.Monitor"/>）。
/// </param>
public sealed record SoundProfile(
    string Id,
    string Name,
    string Description,
    ProfileKind Kind,
    string? Source);
