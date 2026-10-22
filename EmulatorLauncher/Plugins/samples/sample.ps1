# RetroNexus プラグインのサンプル用スクリプト(値は %TEMP%\retronexus-sample-<Name>.txt に保存)
param([string]$Name = 'x', [string]$Mode = 'get', [string]$Value = '', [string]$Default = '')
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$f = Join-Path $env:TEMP "retronexus-sample-$Name.txt"

# ---- 値を1行のJSONにする関数 ----
function Get-ClockLine {
    $now = Get-Date
    (@{ success = $true; value = $now.ToString('HH:mm:ss'); detail = $now.ToString('yyyy/MM/dd (ddd)') } | ConvertTo-Json -Compress)
}
function Get-RandomLine {
    $n = Get-Random -Minimum 0 -Maximum 101
    (@{ success = $true; value = $n; detail = 'ランダムな値(テスト)' } | ConvertTo-Json -Compress)
}
function Get-CpuTempLine {
    # OS標準の性能カウンター「Thermal Zone Information」(ケルビン)。機種によっては取得できない。
    $temp = $null
    try {
        $samples = (Get-Counter '\Thermal Zone Information(*)\Temperature' -ErrorAction Stop).CounterSamples
        $max = ($samples | Measure-Object -Property CookedValue -Maximum).Maximum
        if ($max -gt 273) { $temp = [math]::Round($max - 273.15) }
    } catch { }
    if ($null -ne $temp) {
        (@{ success = $true; value = $temp; detail = 'サーマルゾーンの温度' } | ConvertTo-Json -Compress)
    } else {
        (@{ success = $true; value = '--'; detail = 'この機器では取得できません' } | ConvertTo-Json -Compress)
    }
}

# ---- 常駐型(stream)の例 ----
# 指定のミリ秒ごとに、1行のJSONを出し続ける。標準入力が閉じられたら(パネルが閉じたら)終了する。
function Invoke-Stream([int]$IntervalMs, [scriptblock]$Line) {
    # 注意: [Console]::In.ReadLineAsync() は、Windows PowerShell では同期的に待ってしまい止まる。
    #       標準入力は、バイト単位の ReadAsync で見張る(0バイトが返ったら、閉じられた合図)。
    $stdin = [Console]::OpenStandardInput()
    $buffer = New-Object byte[] 256
    $pending = $stdin.ReadAsync($buffer, 0, $buffer.Length)
    while ($true) {
        [Console]::Out.WriteLine((& $Line))
        [Console]::Out.Flush()
        if ($pending.Wait($IntervalMs)) {
            if ($pending.Result -le 0) { break }
            $pending = $stdin.ReadAsync($buffer, 0, $buffer.Length)
        }
    }
}

switch ($Mode) {
    'set' {
        Set-Content -Path $f -Value $Value -Encoding UTF8
        '{"success":true}'
    }
    'get' {
        $v = if (Test-Path $f) { (Get-Content $f -Raw).Trim() } else { $Default }
        (@{ success = $true; value = $v } | ConvertTo-Json -Compress)
    }
    'clock' { Get-ClockLine }
    'random' { Get-RandomLine }
    'click' {
        $n = 1
        if (Test-Path $f) { $n = [int](Get-Content $f -Raw).Trim() + 1 }
        Set-Content -Path $f -Value $n -Encoding UTF8
        '{"success":true}'
    }
    'cputemp' { Get-CpuTempLine }
    'clock-stream' { Invoke-Stream 1000 { Get-ClockLine } }
    'random-stream' { Invoke-Stream 2000 { Get-RandomLine } }
    'cputemp-stream' { Invoke-Stream 3000 { Get-CpuTempLine } }
    'fail' {
        '{"success":false,"message":"わざと失敗(テスト)"}'
        exit 0
    }
}
