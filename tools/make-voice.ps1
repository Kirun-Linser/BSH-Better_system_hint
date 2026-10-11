# BSH · 机械女声合成（第一步：用 SAPI 生成干净底音）
#
# 读取 voice\lines.json 的 en 字段，逐条用英文女声合成到 voice\raw\*.wav。
# 第二步用 robotize.py 做"机械/电子"后处理，输出到 voice\out\。
#
# ⚠️ 纪律：本脚本只用 Write-Host 打日志（函数里的 Write-Output 会污染返回值，见 AGENTS.md）。

param(
    [string]$LinesJson = 'D:\O级项目管理\BSH\voice\lines.json',
    [string]$OutDir    = 'D:\O级项目管理\BSH\voice\raw',
    [string]$VoiceMatch = '*Zira*',   # 语音描述模糊匹配（本机完整名：Microsoft Zira Desktop - English (United States)）
    [int]$Rate         = 1,           # SAPI 语速：-10..10（用户选定：+1 中档）
    [int]$Volume       = 100,
    [switch]$Only                      # 只跑前 N 条做样例时配合 -Limit
)

$ErrorActionPreference = 'Stop'

$lines = (Get-Content $LinesJson -Raw -Encoding utf8 | ConvertFrom-Json).lines
$sample = @($lines | Where-Object { $_.event -notlike 'Notification.Looping.*' })
Write-Host ("清单 {0} 条（其中非循环事件 {1} 条）" -f $lines.Count, $sample.Count)

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$voice = New-Object -ComObject SAPI.SpVoice
$picked = $null
foreach ($v in $voice.GetVoices()) {
    if ($v.GetDescription() -like $VoiceMatch) { $picked = $v; break }
}
if ($null -eq $picked) {
    Write-Host "找不到匹配 '$VoiceMatch' 的语音，可用列表："
    foreach ($v in $voice.GetVoices()) { Write-Host ("  - " + $v.GetDescription()) }
    exit 1
}
$voice.Voice  = $picked
$voice.Rate   = $Rate
$voice.Volume = $Volume
Write-Host ("使用语音：{0}" -f $picked.GetDescription())

$made = 0
foreach ($line in $lines) {
    $safe = $line.event -replace '[^\w\-]', '_'
    $file = Join-Path $OutDir ($safe + '.wav')

    $stream = New-Object -ComObject SAPI.SpFileStream
    $stream.Open($file, 3, $false)          # 3 = SSFMCreateForWrite
    $voice.AudioOutputStream = $stream
    $null = $voice.Speak($line.en)
    $stream.Close()
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($stream) | Out-Null

    $made++
    Write-Host ("  [{0,2}/{1}] {2,-32} -> {3}" -f $made, $lines.Count, $line.event, $line.en)
}

$voice.AudioOutputStream = $null
[System.Runtime.InteropServices.Marshal]::ReleaseComObject($voice) | Out-Null
Write-Host "完成：$made 个 wav → $OutDir"
