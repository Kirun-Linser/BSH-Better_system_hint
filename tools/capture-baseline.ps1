<#
.SYNOPSIS
    采集「Windows 原样」基线：HKCU\AppEvents\Schemes\Apps\.Default 下每个事件 .Current 的
    **未展开字面值 + 注册表值类型**。

.DESCRIPTION
    为什么必须带类型：本机 59 个槽里 22 个原本存的是 REG_EXPAND_SZ，字面值为
    %SystemRoot%\media\X.wav；若按 C# 展开后的值、且一律以 REG_SZ 写回，读出来虽然一样，
    但**字面值与类型都被改掉**，就不再是逐字节的 Windows 原样。因此基线必须记录：
      String       —— REG_SZ，字面值原样写回
      ExpandString —— REG_EXPAND_SZ，必须用 RegistryValueKind.ExpandString 写回
      Missing      —— 该事件 .Current 键当时根本不存在（= 静音），还原时要**删键**而不是写空值

    本脚本对注册表**纯只读**（只 OpenSubKey 读取，绝不 CreateSubKey / SetValue / DeleteSubKeyTree）。
    -Check 时连磁盘都不写（只读预演）。

.PARAMETER Out
    基线输出路径，默认 ..\profiles\windows-default\baseline-appevents.json。

.PARAMETER Check
    只读预演：打印类型统计、联合哈希、三条代表性记录，并与磁盘上现有基线做差异对比，但不写盘。

.EXAMPLE
    pwsh -File tools\capture-baseline.ps1 -Check     # 只读预演
    pwsh -File tools\capture-baseline.ps1            # 采集并覆盖基线文件

.NOTES
    ⚠️ PowerShell 纪律：本文件的函数里**一律用 Write-Host 打日志**，绝不用 Write-Output ——
    函数的全部未捕获输出都会成为返回值，日志混进去会把写出的 JSON 文件弄坏。
#>
[CmdletBinding()]
param(
    [string]$Out = (Join-Path $PSScriptRoot '..\profiles\windows-default\baseline-appevents.json'),
    [switch]$Check
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 只动 HKCU（用户级）：与 Core\SoundScheme.cs 的 BaseKeyPath 必须保持一致
$BasePath = 'AppEvents\Schemes\Apps\.Default'

<#
.SYNOPSIS
    读取 59 个槽，返回记录数组（Event / Wav / Kind）。只读，不打印任何东西到输出流。
.OUTPUTS
    [pscustomobject[]]，每项含 Event(string)、Wav(string 或 $null)、Kind(String|ExpandString|Missing)
#>
function Get-BaselineRecords {
    [CmdletBinding()]
    param([string]$RegistryPath = $BasePath)

    $root = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($RegistryPath)
    if ($null -eq $root) { throw "找不到注册表键：HKCU\$RegistryPath" }

    try {
        $records = [System.Collections.Generic.List[object]]::new()
        foreach ($name in $root.GetSubKeyNames()) {
            $sub = $root.OpenSubKey("$name\.Current")
            if ($null -eq $sub) {
                # .Current 键不存在 = 该事件当时就是静音（本机 WindowsLogoff / SystemExit 即如此）
                $records.Add([pscustomobject]@{ Event = $name; Wav = $null; Kind = 'Missing' })
                continue
            }

            try {
                if ($sub.GetValueNames() -notcontains '') {
                    # 键在、默认值不在：没有可写回的字面值，按 Missing 处理（还原时删键同样得到静音）
                    Write-Warning "$name\.Current 存在但没有默认值 —— 按 Missing 处理"
                    $records.Add([pscustomobject]@{ Event = $name; Wav = $null; Kind = 'Missing' })
                    continue
                }

                # ⚠️ 关键：DoNotExpandEnvironmentNames —— 要的是**字面值** %SystemRoot%\media\X.wav，
                #    不是展开后的 C:\WINDOWS\media\X.wav。展开后原样就再也回不去了。
                $raw = $sub.GetValue('', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                $kindName = [string]$sub.GetValueKind('')
                if ($kindName -notin @('String', 'ExpandString')) {
                    Write-Warning "$name\.Current 的值类型是 $kindName（非 String/ExpandString）—— 原样记录"
                }

                $records.Add([pscustomobject]@{
                    Event = $name
                    Wav   = if ($null -eq $raw) { $null } else { [string]$raw }
                    Kind  = $kindName
                })
            }
            finally { $sub.Dispose() }
        }
        return $records.ToArray()
    }
    finally { $root.Dispose() }
}

<#
.SYNOPSIS
    计算 59 槽的「未展开值 + 类型」联合哈希（SHA256，小写十六进制）。
.DESCRIPTION
    ⚠️ 哈希的输入**只允许**未展开字面值与值类型 —— 用展开值算哈希会让
    "类型被改成 REG_SZ 但读出来一样"这种破坏**检测不出来**，验收就失去意义。
    字段以 \n 分隔，$null 用 <null> 占位（与空串区分开）。
#>
function Get-SlotJointHash {
    [CmdletBinding()]
    param([object[]]$Records)

    $sb = [System.Text.StringBuilder]::new()
    foreach ($r in ($Records | Sort-Object -Property Event)) {
        $wav = if ($null -eq $r.Wav) { '<null>' } else { [string]$r.Wav }
        [void]$sb.Append([string]$r.Event).Append("`n").Append([string]$r.Kind).Append("`n").Append($wav).Append("`n")
    }

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($sb.ToString()))
        return ([BitConverter]::ToString($bytes) -replace '-', '').ToLowerInvariant()
    }
    finally { $sha.Dispose() }
}

<#
.SYNOPSIS
    把 Missing 槽再细分一次：.Current 键**不存在** / 键存在但**没有默认值**。
.DESCRIPTION
    实测（2026-10-10）本机 SystemExit 与 WindowsLogoff 属于后者：.Current 键在，但 ValueCount=0
    （reg query 也证实键下无任何值）。两者读取语义都是"静音"，但还原动作不同 ——
    删键会破坏"空壳键仍然存在"这一结构，所以必须把这个差别显式报出来，不能含糊成"键不存在"。
    本函数只读。
#>
function Get-MissingBreakdown {
    [CmdletBinding()]
    param([object[]]$Records)

    $absent = 0
    $presentNoValue = 0
    $root = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($BasePath)
    try {
        foreach ($r in ($Records | Where-Object { $_.Kind -eq 'Missing' })) {
            $sub = $root.OpenSubKey("$($r.Event)\.Current")
            if ($null -eq $sub) { $absent++ } else { $presentNoValue++; $sub.Dispose() }
        }
    }
    finally { $root.Dispose() }

    return [pscustomobject]@{ KeyAbsent = $absent; KeyPresentNoValue = $presentNoValue }
}

<#
.SYNOPSIS
    把记录数组写成 UTF-8（无 BOM）JSON 文件；返回写入的绝对路径。
.DESCRIPTION
    ⚠️ 本轮实测踩到的坑：不能把 `-InputObject $Records` 与 `-AsArray` 叠在一起用 ——
    会多包一层变成 [[{...},{...}]]（双数组）。PowerShell 侧看不出来（ConvertFrom-Json 默认展开一层，
    条数照样显示 59），但 C# 的 JsonSerializer<List<...>> 会直接解析失败。
    正确姿势是走管道再 -AsArray：元素逐个进管道，由 -AsArray 组装成 JSON 数组。
    写盘前另做一次结构预检（用 .NET 的 JsonDocument，与 C# 侧同一族解析器）。
#>
function Write-BaselineFile {
    [CmdletBinding()]
    param([string]$Path, [object[]]$Records)

    $json = if ($PSVersionTable.PSVersion.Major -ge 7) {
        $Records | ConvertTo-Json -Depth 4 -AsArray
    } else {
        ConvertTo-Json -InputObject @($Records) -Depth 4
    }

    # ── 写盘前预检：顶层必须是数组、条数一致、每条都有 Event / Wav / Kind ──
    $doc = [System.Text.Json.JsonDocument]::Parse($json)
    try {
        if ($doc.RootElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Array) {
            throw "预检失败：生成的 JSON 顶层不是数组（实际是 $($doc.RootElement.ValueKind)）"
        }
        $n = $doc.RootElement.GetArrayLength()
        if ($n -ne $Records.Count) { throw "预检失败：JSON 里 $n 条 ≠ 采集到的 $($Records.Count) 条" }
        foreach ($el in $doc.RootElement.EnumerateArray()) {
            if ($el.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { throw "预检失败：数组里出现非对象元素" }
            foreach ($name in @('Event', 'Wav', 'Kind')) {
                $probe = [System.Text.Json.JsonElement]::new()
                if (-not $el.TryGetProperty($name, [ref]$probe)) { throw "预检失败：有记录缺少 $name 字段" }
            }
        }
    }
    finally { $doc.Dispose() }

    $full = [System.IO.Path]::GetFullPath($Path)
    $dir = [System.IO.Path]::GetDirectoryName($full)
    if (-not [System.IO.Directory]::Exists($dir)) { [void][System.IO.Directory]::CreateDirectory($dir) }
    [System.IO.File]::WriteAllText($full, $json + "`n", [System.Text.UTF8Encoding]::new($false))
    return $full
}

<#
.SYNOPSIS
    读磁盘上已有的基线文件，返回 (Event|Kind|Wav) 字符串集合；文件不存在返回空集合。
.DESCRIPTION
    用 JsonDocument 严格解析：顶层必须是数组、元素必须是对象 —— 这样"双数组"这类结构损坏
    会当场被看出来，而不是被 ConvertFrom-Json 的自动展开掩盖过去。解析失败按"没有基线"处理。
#>
function Read-ExistingKeys {
    [CmdletBinding()]
    param([string]$Path)

    $full = [System.IO.Path]::GetFullPath($Path)
    if (-not [System.IO.File]::Exists($full)) { return @() }

    try {
        $doc = [System.Text.Json.JsonDocument]::Parse([System.IO.File]::ReadAllText($full))
    } catch {
        Write-Warning "已有基线无法解析，按'没有基线'处理：$($_.Exception.Message)"
        return @()
    }

    try {
        if ($doc.RootElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Array) {
            Write-Warning "已有基线的顶层不是 JSON 数组（结构已损坏）—— 按'没有基线'处理"
            return @()
        }

        $keys = [System.Collections.Generic.List[string]]::new()
        foreach ($el in $doc.RootElement.EnumerateArray()) {
            if ($el.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
                Write-Warning "已有基线里出现非对象元素（结构已损坏）—— 按'没有基线'处理"
                return @()
            }
            $names = @($el.EnumerateObject() | ForEach-Object { $_.Name })
            $kind = if ($names -contains 'Kind') { $el.GetProperty('Kind').GetString() } else { '(无Kind字段)' }
            $wavEl = $el.GetProperty('Wav')
            $wav = if ($wavEl.ValueKind -eq [System.Text.Json.JsonValueKind]::Null) { '<null>' } else { $wavEl.GetString() }
            $keys.Add("$($el.GetProperty('Event').GetString())|$kind|$wav")
        }
        return $keys.ToArray()
    }
    finally { $doc.Dispose() }
}

# ───────────────────────────── 主流程 ─────────────────────────────

$records = @(Get-BaselineRecords)
$target = [System.IO.Path]::GetFullPath($Out)

Write-Host "[PowerShell] $($PSVersionTable.PSVersion)"
Write-Host "[注册表] HKCU\$BasePath（纯只读采集）"
Write-Host "[槽数] $($records.Count)"

$counts = @{}
foreach ($r in $records) { $counts[$r.Kind] = 1 + [int]$counts[$r.Kind] }
$statLine = ($counts.GetEnumerator() | Sort-Object -Property Name | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join '  '
Write-Host "[类型统计] $statLine"

$breakdown = Get-MissingBreakdown -Records $records
Write-Host "[Missing 明细] 键不存在=$($breakdown.KeyAbsent)  键存在但无默认值=$($breakdown.KeyPresentNoValue)"

Write-Host "[联合哈希(SHA256/未展开值+类型)] $(Get-SlotJointHash -Records $records)"

# 三条代表性记录（String 优先选有值的，便于人眼核对）
$pickString = $records | Where-Object { $_.Kind -eq 'String' -and $null -ne $_.Wav -and $_.Wav -ne '' } | Select-Object -First 1
$pickExpand = $records | Where-Object { $_.Kind -eq 'ExpandString' } | Select-Object -First 1
$pickMissing = $records | Where-Object { $_.Kind -eq 'Missing' } | Select-Object -First 1
foreach ($p in @(
        [pscustomobject]@{ Tag = 'String'; Rec = $pickString },
        [pscustomobject]@{ Tag = 'ExpandString'; Rec = $pickExpand },
        [pscustomobject]@{ Tag = 'Missing'; Rec = $pickMissing })) {
    $rec = $p.Rec
    if ($null -eq $rec) {
        Write-Host "[代表-$($p.Tag)] （本机没有这种槽）"
    } else {
        $wav = if ($null -eq $rec.Wav) { 'null' } else { $rec.Wav }
        Write-Host "[代表-$($p.Tag)] Event=$($rec.Event)  Kind=$($rec.Kind)  Wav=$wav"
    }
}

if ($Check) {
    Write-Host "[模式] -Check 只读预演：不写任何文件（目标：$target）"
    $newKeys = @(foreach ($r in $records) {
            $wav = if ($null -eq $r.Wav) { '<null>' } else { [string]$r.Wav }
            "$($r.Event)|$($r.Kind)|$wav"
        })
    $oldKeys = @(Read-ExistingKeys -Path $Out)
    if ($oldKeys.Count -eq 0) {
        Write-Host "[差异] 磁盘上没有可解析的基线 —— 正式跑一次即可生成"
    } else {
        $added = @($newKeys | Where-Object { $_ -notin $oldKeys })
        $removed = @($oldKeys | Where-Object { $_ -notin $newKeys })
        Write-Host "[差异] 磁盘基线 $($oldKeys.Count) 条；本次采集 $($newKeys.Count) 条；不同 $($added.Count + $removed.Count) 条（新增 $($added.Count) / 消失 $($removed.Count)）"
        foreach ($d in ($added | Select-Object -First 5)) { Write-Host "  + $d" }
        foreach ($d in ($removed | Select-Object -First 5)) { Write-Host "  - $d" }
    }
} else {
    $written = Write-BaselineFile -Path $Out -Records $records
    Write-Host "[写入] $written（$($records.Count) 条，UTF-8 无 BOM）"
}
