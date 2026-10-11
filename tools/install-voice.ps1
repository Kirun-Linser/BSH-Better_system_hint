# BSH · 安装语音方案（幂等）
#
# 读 voice\lines.json，把 voice\out\*.wav 按「中文,英文.wav」命名复制到：
#   1) %LOCALAPPDATA%\BSH\sounds\        ← 系统实际读取的位置（注册表指向这里）
#   2) -MirrorDir（可选）                ← 人类可读的镜像目录（如桌面某个目录）
# 并生成 voice-scheme.json（事件名 → wav 绝对路径）。
#
# ⚠️ 为什么不放工作目录：AGENTS.md 第六节 —— 会话工作目录受 ACL 沙箱约束时，
#    以用户身份运行的程序（含系统音频链路）可能读不到里面的文件。
#
# 用法：
#   pwsh -File tools\install-voice.ps1
#   pwsh -File tools\install-voice.ps1 -MirrorDir "$env:USERPROFILE\Desktop\BSH-voice" -Clean

param(
    [string]$OutDir    = '',   # 默认取脚本旁 ../voice/edge-batch/out（见下方补默认值）
    [string]$LinesJson = '',
    [string]$TargetDir = (Join-Path $env:LOCALAPPDATA 'BSH\sounds'),
    [string]$MirrorDir = '',
    [switch]$Clean,          # 清空目标目录里旧的 .wav 再复制
    [switch]$Check
)

$ErrorActionPreference = 'Stop'

# 默认路径相对脚本自身定位：换台机器 / 换目录都不用改脚本
if ($OutDir    -eq '') { $OutDir    = Join-Path $PSScriptRoot '../voice/edge-batch/out' }
if ($LinesJson -eq '') { $LinesJson = Join-Path $PSScriptRoot '../voice/lines.json' }

if (-not (Test-Path $OutDir))    { Write-Host "找不到语音目录：$OutDir"; exit 1 }
if (-not (Test-Path $LinesJson)) { Write-Host "找不到清单文件：$LinesJson"; exit 1 }

$lines = (Get-Content $LinesJson -Raw -Encoding utf8 | ConvertFrom-Json).lines
Write-Host ("清单 {0} 条" -f $lines.Count)

# 事件名 → 安全文件名（SAPI 产物用的是这个名字）
function Get-SafeName([string]$event) { return ($event -replace '[^\w\-]', '_') }
# 事件名 → 「中文,英文.wav」（交付命名）
function Get-PrettyName($line) {
    # 英文末尾的句号/感叹号是给 TTS 判断语调用的，文件名里摘掉，否则会出现 "打开,Execution..wav" 双点
    $en = ([string]$line.en).TrimEnd('.', '!', '?')
    $raw = "{0},{1}.wav" -f $line.zh, $en
    return ($raw -replace '[\\/:*?"<>|]', '_')
}

$map = [ordered]@{}
$missing = @()
$rows = @()
foreach ($l in $lines) {
    $src = Join-Path $OutDir ((Get-SafeName $l.event) + '.wav')
    if (-not (Test-Path $src)) { $missing += $l.event; continue }
    $pretty = Get-PrettyName $l
    $rows += [pscustomobject]@{ Event = $l.event; Src = $src; Pretty = $pretty }
    $map[$l.event] = (Join-Path $TargetDir $pretty)
}
Write-Host ("可映射 {0} 条；缺 wav {1} 条" -f $map.Count, $missing.Count)
if ($missing.Count -gt 0) { Write-Host ("  缺失：" + ($missing -join ', ')) }

if ($Check) {
    Write-Host "（-Check）只读模式，未写入"
    Write-Host ("目标现状：{0}" -f $(if (Test-Path $TargetDir) { (Get-ChildItem "$TargetDir\*.wav" -ErrorAction SilentlyContinue).Count.ToString() + ' 个 wav' } else { '不存在' }))
    exit 0
}

New-Item -ItemType Directory -Force -Path $TargetDir | Out-Null
if ($Clean) { Remove-Item "$TargetDir\*.wav" -Force -ErrorAction SilentlyContinue }

foreach ($r in $rows) {
    Copy-Item $r.Src (Join-Path $TargetDir $r.Pretty) -Force
}

$scheme = [pscustomobject]@{
    generatedAt = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
    note        = 'BSH 语音方案（采用版）：事件 → wav 绝对路径'
    voice       = 'Edge TTS Michelle(-10Hz) + Aria(+0Hz) → EQ 塑形（Michelle 底 + 35% Aria 谱形）→ V3 轻柔（升 6Hz + 频谱倾斜）'
    naming      = '中文,英文.wav'
    sounds      = $map
}
$scheme | ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8 (Join-Path $TargetDir 'voice-scheme.json')
Write-Host ("已安装 {0} 个 wav → {1}" -f $rows.Count, $TargetDir)

if ($MirrorDir -ne '') {
    New-Item -ItemType Directory -Force -Path $MirrorDir | Out-Null
    Remove-Item "$MirrorDir\*.wav" -Force -ErrorAction SilentlyContinue
    foreach ($r in $rows) { Copy-Item $r.Src (Join-Path $MirrorDir $r.Pretty) -Force }
    Write-Host ("镜像 {0} 个 wav → {1}" -f $rows.Count, $MirrorDir)
}

Write-Host ("方案文件：{0}" -f (Join-Path $TargetDir 'voice-scheme.json'))
