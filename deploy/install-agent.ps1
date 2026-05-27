# =========================================================
# Watashi Agent インストーラ
# =========================================================
# 使い方:
#   1. ビルド済み Agent 一式 (publish 結果) を任意の場所に置く
#   2. このスクリプトをエージェント実行サーバーで管理者権限で実行
#      ./install-agent.ps1 -SourceDir C:\Temp\WatashiAgent -AgentId bastion-a -CentralUrl https://central.internal:8443
# =========================================================

param(
    [Parameter(Mandatory = $true)] [string] $SourceDir,
    [Parameter(Mandatory = $true)] [string] $AgentId,
    [Parameter(Mandatory = $true)] [string] $CentralUrl,
    [string] $InstallDir = "C:\Program Files\WatashiAgent",
    [string] $DataDir    = "C:\ProgramData\WatashiAgent",
    [string] $ServiceName = "WatashiAgent",
    [string] $CertificatePath = "",
    [string] $CertificatePassword = "",
    [string] $CentralCertificateThumbprint = "",
    [string] $SharedSecret = "",
    [bool]   $UseMtls = $false,
    [int]    $MaxConcurrency = 20,
    [string] $ListenUrl = "http://0.0.0.0:8081"
)

$ErrorActionPreference = "Stop"

function Require-Admin {
    $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $p  = New-Object System.Security.Principal.WindowsPrincipal($id)
    if (-not $p.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "管理者権限で実行してください。"
    }
}

Require-Admin

Write-Host "[1/5] 既存サービス停止..."
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

Write-Host "[2/5] ファイルを $InstallDir に配置..."
if (-not (Test-Path $InstallDir)) { New-Item -ItemType Directory -Path $InstallDir | Out-Null }
if (-not (Test-Path $DataDir))    { New-Item -ItemType Directory -Path $DataDir    | Out-Null }
Copy-Item -Path (Join-Path $SourceDir "*") -Destination $InstallDir -Recurse -Force

Write-Host "[3/5] appsettings.json を生成..."
$isHttps = $ListenUrl.StartsWith("https://", [System.StringComparison]::OrdinalIgnoreCase)
$endpointName = if ($isHttps) { "Https" } else { "Http" }
$endpoint = @{ Url = $ListenUrl }
if ($isHttps) {
    if ([string]::IsNullOrWhiteSpace($CertificatePath)) {
        throw "ListenUrl が HTTPS の場合は -CertificatePath に HTTPS サーバ証明書 PFX を指定してください。HTTP 共有秘密モードでは -ListenUrl http://0.0.0.0:8081 を使います。"
    }
    $endpoint.Certificate = @{ Path = $CertificatePath; Password = $CertificatePassword }
}

$appsettings = @{
    Agent = @{
        AgentId        = $AgentId
        ListenUrl      = $ListenUrl
        CentralUrl     = $CentralUrl
        MaxConcurrency = $MaxConcurrency
    }
    ConnectionStrings = @{
        Buffer = "Data Source=$DataDir\agent_buffer.db;Cache=Shared;Foreign Keys=True;"
    }
    Certificate = @{
        Path     = $CertificatePath
        Password = $CertificatePassword
    }
    Auth = @{
        CentralCertificateThumbprint = $CentralCertificateThumbprint
        SharedSecret = $SharedSecret
    }
    Routing = @{
        UseMtls = $UseMtls
    }
    Kestrel = @{
        Endpoints = @{
            $endpointName = $endpoint
        }
    }
    Serilog = @{
        Using = @("Serilog.Sinks.Console", "Serilog.Sinks.File")
        MinimumLevel = @{ Default = "Information" }
        WriteTo = @(
            @{ Name = "Console" },
            @{ Name = "File"; Args = @{ path = "$DataDir\logs\agent-.log"; rollingInterval = "Day"; retainedFileCountLimit = 14; shared = $true } }
        )
    }
    AllowedHosts = "*"
}
$json = $appsettings | ConvertTo-Json -Depth 10
[System.IO.File]::WriteAllText((Join-Path $InstallDir "appsettings.json"), $json, [System.Text.UTF8Encoding]::new($false))

Write-Host "[4/5] Windows サービスとして登録..."
$exe = Join-Path $InstallDir "Watashi.Agent.exe"
if (-not (Test-Path $exe)) { throw "Watashi.Agent.exe が見つかりません: $exe" }
sc.exe create $ServiceName binPath= "`"$exe`"" start= auto DisplayName= "Watashi Agent" | Out-Null
sc.exe description $ServiceName "Watashi CIFS Agent (中央サーバーから踏み台網のSMB操作を中継)" | Out-Null
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null

Write-Host "[5/5] サービス開始..."
Start-Service -Name $ServiceName
Start-Sleep -Seconds 2
$svc = Get-Service -Name $ServiceName
Write-Host "Status: $($svc.Status)"

Write-Host ""
Write-Host "===== インストール完了 ====="
Write-Host "AgentId    : $AgentId"
Write-Host "InstallDir : $InstallDir"
Write-Host "DataDir    : $DataDir"
Write-Host "ListenUrl  : $ListenUrl"
Write-Host "CentralUrl : $CentralUrl"
Write-Host ""
Write-Host "中央サーバーの /api/admin/nodes で同名 ($AgentId) の Agent ノードを登録してください。"
