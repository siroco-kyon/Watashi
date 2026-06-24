<#
.SYNOPSIS
  Back up the Watashi SQLite database and prune daily/monthly generations.

.DESCRIPTION
  Calls the Watashi.Server.exe backup mode, which uses Microsoft.Data.Sqlite
  BackupDatabase() so the database can remain online.

.EXAMPLE
  powershell.exe -NoProfile -File C:\Apps\Watashi\backup.ps1
#>
param(
    [string]$DatabasePath = "C:\ProgramData\Watashi\watashi.db",
    [string]$BackupDirectory = "C:\ProgramData\Watashi\backups",
    [string]$ServerInstallDir = "$env:ProgramFiles\Watashi\Server",
    [ValidateRange(1, 3650)][int]$DailyRetentionDays = 7,
    [ValidateRange(1, 120)][int]$MonthlyRetentionMonths = 12
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if (-not (Test-Path -LiteralPath $DatabasePath -PathType Leaf)) {
    throw "DatabasePath '$DatabasePath' was not found."
}
if (-not (Test-Path -LiteralPath $ServerInstallDir -PathType Container)) {
    throw "ServerInstallDir '$ServerInstallDir' was not found."
}

$serverExe = Join-Path $ServerInstallDir "Watashi.Server.exe"
if (-not (Test-Path -LiteralPath $serverExe -PathType Leaf)) {
    throw "Watashi.Server.exe '$serverExe' was not found."
}

New-Item -ItemType Directory -Force -Path $BackupDirectory | Out-Null
$now = Get-Date
$dailyPath = Join-Path $BackupDirectory ("watashi-{0}.db" -f $now.ToString("yyyyMMdd"))
$temporaryPath = "$dailyPath.tmp"
Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue

try {
    & $serverExe --backup --database $DatabasePath --output $temporaryPath
    if ($LASTEXITCODE -ne 0) {
        throw "Watashi.Server.exe backup failed with exit code $LASTEXITCODE."
    }
    Move-Item -LiteralPath $temporaryPath -Destination $dailyPath -Force
}
catch {
    Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
    throw
}

# Keep the backup from the first day of each month as a monthly generation.
if ($now.Day -eq 1) {
    $monthlyPath = Join-Path $BackupDirectory ("watashi-monthly-{0}.db" -f $now.ToString("yyyyMM"))
    Copy-Item -LiteralPath $dailyPath -Destination $monthlyPath -Force
}

$dailyThreshold = $now.Date.AddDays(-$DailyRetentionDays)
Get-ChildItem -LiteralPath $BackupDirectory -Filter "watashi-????????.db" -File |
    Where-Object { $_.LastWriteTime -lt $dailyThreshold } |
    Remove-Item -Force

$monthlyThreshold = [datetime]::new($now.Year, $now.Month, 1).AddMonths(-$MonthlyRetentionMonths)
Get-ChildItem -LiteralPath $BackupDirectory -Filter "watashi-monthly-??????.db" -File |
    Where-Object { $_.LastWriteTime -lt $monthlyThreshold } |
    Remove-Item -Force

Write-Host "Backup completed: $dailyPath"
