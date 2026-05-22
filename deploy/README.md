# Watashi デプロイ手順

## 構成

```
┌────────────────────────┐
│ Watashi.Server         │ Windows Service or IIS in-process
│ (中央サーバー)         │ Kestrel HTTP/HTTPS, JWT, SQLite
└─┬──────────────────────┘
  │ ClickOnce 配布
  ▼
┌────────────────────────┐
│ IIS (配布サーバー)     │ 静的ファイル + MIME 設定 (IIS-MIME.md)
└─┬──────────────────────┘
  │ HTTPS
  ▼
┌────────────────────────┐
│ Watashi.Client (WPF)   │ ClickOnce で自動更新
└────────────────────────┘

┌────────────────────────┐
│ Watashi.Agent          │ install-agent.ps1 で Windows Service 化
│ (踏み台に配置)         │ mTLS で中央と通信
└────────────────────────┘
```

## 1. 中央サーバー (Watashi.Server)

```powershell
# ビルド
dotnet publish src\Watashi.Server\Watashi.Server.csproj -c Release -r win-x64 --self-contained -o C:\Apps\WatashiServer

# 設定
notepad C:\Apps\WatashiServer\appsettings.json
#   - Jwt:Secret を 32 バイト以上のランダム文字列に
#   - Encryption:MasterKey を 32 バイトの Base64 に
#   - Kestrel:Endpoints:Https:Certificate に PFX のパスとパスワード
#   - ConnectionStrings:Default は本番では C:\ProgramData\Watashi\watashi.db のまま

# Windows サービス化（任意）
sc.exe create WatashiServer binPath= "C:\Apps\WatashiServer\Watashi.Server.exe" start= auto
sc.exe start WatashiServer
```

初回起動で `C:\ProgramData\Watashi\watashi.db` が自動生成され、`admin` / `Admin123!@#` でログイン可能 (初回ログインでパスワード変更が必要)。

## 2. ClickOnce クライアント

`src/Watashi.Client/Properties/PublishProfiles/ClickOnceProfile.pubxml` を編集:
- `<PublishUrl>` 〜 配布先 (UNC/HTTP どちらでも可)
- `<InstallUrl>` 〜 ユーザーが開く URL
- `<ManifestCertificateThumbprint>` 〜 社内 Code Signing 証明書

発行:

```powershell
dotnet publish src\Watashi.Client\Watashi.Client.csproj -c Release `
    -p:PublishProfile=ClickOnceProfile
```

配布サーバーは IIS-MIME.md の MIME 設定を完了させること。

## 3. エージェント (踏み台)

```powershell
# ビルド (Server と同じマシン or CI で実行)
dotnet publish src\Watashi.Agent\Watashi.Agent.csproj -c Release -r win-x64 --self-contained -o C:\Temp\WatashiAgent

# 踏み台サーバーへ配置
robocopy C:\Temp\WatashiAgent \\bastion-a\c$\Temp\WatashiAgent /E

# 踏み台で
.\install-agent.ps1 `
    -SourceDir C:\Temp\WatashiAgent `
    -AgentId bastion-a `
    -CentralUrl https://central.internal:8443 `
    -CertificatePath C:\certs\bastion-a.pfx `
    -CertificatePassword "xxx"
```

中央サーバーの `/api/admin/nodes` で:
- Name = `bastion-a` (install-agent.ps1 と一致させる)
- NodeType = `Agent`
- Endpoint = `https://bastion-a:8443`
- ClientCertificateThumbprint = 踏み台が中央へ提示する証明書のサムプリント

の Agent ノードを登録。

## 4. 自動バックアップ (タスクスケジューラ)

```powershell
schtasks.exe /Create /SC DAILY /TN "Watashi DB Backup" `
    /TR "powershell.exe -File C:\Apps\WatashiServer\backup.ps1" /ST 02:00 /RL HIGHEST
```

`backup.ps1` で `cifs_tool.db` を `BackupDatabase()` ベースで日次バックアップ + 月次世代管理。

## 5. ログ削除バッチ

```powershell
schtasks.exe /Create /SC DAILY /TN "Watashi Log Cleanup" `
    /TR "sqlite3 C:\ProgramData\Watashi\watashi.db \"DELETE FROM AuditLogs WHERE Timestamp < datetime('now', '-1 year');\"" /ST 03:00
```
