<#
    编译 + 部署 + 采集日志，一条龙。
    由 build.cmd 双击调用，也可以直接：
      powershell -ExecutionPolicy Bypass -File build-and-test.ps1

    步骤：
      1. 调 build.ps1 编译（系统自带 .NET Framework 编译器）
      2. 结束正在运行的旧实例（我们加了单实例互斥，不结束的话新进程会直接退出）
      3. 覆盖部署到 %LOCALAPPDATA%\InphicBatteryTray\，并清掉旧 tray.log
      4. 启动并等 20 秒，打印 opened / battery 日志行
#>
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$exe = Join-Path $root 'InphicBatteryTray.exe'
$destDir = Join-Path $env:LOCALAPPDATA 'InphicBatteryTray'
$destExe = Join-Path $destDir 'InphicBatteryTray.exe'
$destLog = Join-Path $destDir 'tray.log'

function Fail($msg) {
    Write-Host ''
    Write-Host "===== 失败：$msg =====" -ForegroundColor Red
    Write-Host '把上面的输出整段贴出来即可定位。'
    exit 1
}

# ---- 1. 编译 -------------------------------------------------------------
Write-Host ''
Write-Host '=== 1/4 编译 ===' -ForegroundColor Cyan
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build.ps1')
if ($LASTEXITCODE -ne 0) { Fail "编译失败，exit=$LASTEXITCODE" }
if (-not (Test-Path -LiteralPath $exe)) { Fail "编译没有产出 $exe" }
Write-Host ("  产出: {0:N0} bytes" -f (Get-Item -LiteralPath $exe).Length)

# ---- 2. 结束旧实例 -------------------------------------------------------
Write-Host ''
Write-Host '=== 2/4 结束旧实例 ===' -ForegroundColor Cyan
$old = @(Get-Process -Name 'InphicBatteryTray' -ErrorAction SilentlyContinue)
if ($old.Count -gt 0) {
    $old | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
    Write-Host "  已结束 $($old.Count) 个"
}
else {
    Write-Host '  没有运行中的实例'
}

# ---- 3. 部署 -------------------------------------------------------------
Write-Host ''
Write-Host '=== 3/4 部署 ===' -ForegroundColor Cyan
if (-not (Test-Path -LiteralPath $destDir)) {
    New-Item -ItemType Directory -Path $destDir -Force | Out-Null
}
Copy-Item -LiteralPath $exe -Destination $destExe -Force
if (Test-Path -LiteralPath $destLog) { Remove-Item -LiteralPath $destLog -Force -ErrorAction SilentlyContinue }
Write-Host "  -> $destExe"

# ---- 4. 启动 + 采集 ------------------------------------------------------
Write-Host ''
Write-Host '=== 4/4 启动并等待 20 秒 ===' -ForegroundColor Cyan
Start-Process -FilePath $destExe
Start-Sleep -Seconds 20

$proc = @(Get-Process -Name 'InphicBatteryTray' -ErrorAction SilentlyContinue)
if ($proc.Count -eq 0) {
    Write-Host '  进程没在跑 —— 可能启动即退出，检查下面日志' -ForegroundColor Yellow
}
else {
    Write-Host "  进程存活 PID $($proc[0].Id)"
    try {
        $cpu = $proc[0].TotalProcessorTime.TotalSeconds
        Write-Host ("  累计 CPU 时间 {0:F2}s（20 秒内）" -f $cpu)
    }
    catch { }
}

Write-Host ''
if (Test-Path -LiteralPath $destLog) {
    $lines = @(Get-Content -LiteralPath $destLog)
    Write-Host '===== opened 行（关键：看 usagePage）=====' -ForegroundColor Green
    $opened = $lines | Where-Object { $_ -match 'opened:' }
    if ($opened) { $opened | ForEach-Object { Write-Host "  $_" } }
    else { Write-Host '  （没有 opened 行，说明没匹配到任何接口）' -ForegroundColor Yellow }

    Write-Host ''
    Write-Host '===== battery 行（最后 5 条）=====' -ForegroundColor Green
    $bat = @($lines | Where-Object { $_ -match 'battery ' })
    if ($bat.Count -gt 0) { $bat | Select-Object -Last 5 | ForEach-Object { Write-Host "  $_" } }
    else { Write-Host '  （没有电量行）' -ForegroundColor Yellow }

    Write-Host ''
    Write-Host '===== 日志前 30 行 =====' -ForegroundColor Green
    $lines | Select-Object -First 30 | ForEach-Object { Write-Host "  $_" }
}
else {
    Write-Host "  没有生成 $destLog" -ForegroundColor Yellow
    Write-Host '  说明 exe 所在目录不可写，或者程序启动即崩。' -ForegroundColor Yellow
}

Write-Host ''
Write-Host '===== 完成 =====' -ForegroundColor Cyan
Write-Host '把上面 opened 那几行贴出来，用来决定是否照搬 PR #1 的 UsagePage 硬过滤。'
