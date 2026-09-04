# Watashi 開発者ガイド

開発機 (Windows + .NET 10 SDK) でのローカル開発手順をまとめます。Client は .NET 10、Server / Agent / Shared / Tests は .NET 8 を対象にします。

- [前提条件](#前提条件)
- [プロジェクト構成](#プロジェクト構成)
- [ビルド & テスト](#ビルド--テスト)
- [ローカル実行](#ローカル実行)
- [HTTPS 開発環境のセットアップ](#https-開発環境のセットアップ)
- [SMB テスト共有を立てる](#smb-テスト共有を立てる)
- [既知の落とし穴](#既知の落とし穴)

---

## 前提条件

| 項目 | 要件 |
|---|---|
| OS | Windows 10 / 11 |
| .NET SDK | 10.0 (`dotnet --list-sdks` で `10.0.*` が見えること。これで .NET 8 対象プロジェクトもビルド可能) |
| IDE | Visual Studio 2022 / VS Code どちらでも OK |
| 推奨拡張 | C# Dev Kit, XAML Styler |

---

## プロジェクト構成

```
src/
├── Watashi.Shared/    # モデル、DTO、Helper、CIFS レイヤ (SMB セッションプール込み)
├── Watashi.Server/    # 中央サーバー (ASP.NET Core 8)
├── Watashi.Agent/     # エージェント (踏み台に配置)
└── Watashi.Client/    # WPF デスクトップアプリ
    ├── Themes/        # Colors.xaml / Icons.xaml (鳥居アイコン) / Controls.xaml
    ├── Views/         # XAML
    ├── ViewModels/    # MVVM (CommunityToolkit.Mvvm)
    ├── Services/      # ApiClient / SessionManager / CredentialStore
    └── Converters/    # WPF Value Converters
tests/Watashi.Tests/   # xUnit (Auth / Session / Migration / Permission / Crypto / CSV / AdminGuard ほか)
deploy/                # Windows Service インストーラ、ClickOnce 設定
docs/                  # ドキュメント
```

---

## ビルド & テスト

```powershell
# ソリューション全体ビルド
dotnet build Watashi.sln

# 単体テスト
dotnet test tests\Watashi.Tests

# クライアントのみビルド
dotnet build src\Watashi.Client\Watashi.Client.csproj
```

認証・セッション変更時は、全体テストに加えて境界テストを絞って先に実行すると原因を特定しやすい:

```powershell
dotnet test tests\Watashi.Tests\Watashi.Tests.csproj --filter `
  "FullyQualifiedName~SessionManagerTests|FullyQualifiedName~ApiClientUnauthorizedTests|FullyQualifiedName~AccessTokenCredentialValidatorTests|FullyQualifiedName~UsernameCollation"
```

Windows 統合認証の画面確認だけでなく、少なくとも「旧 401 が再ログインを消さない」「refresh 中の再ログインを上書きしない」「大小文字ユーザーの migration が衝突時にロールバックする」を自動テストで固定する。

---

## ローカル実行

```powershell
# 1) 初回だけ bootstrap 管理者を発行
cd src\Watashi.Server
dotnet run -- --bootstrap-admin
# → ランダムな一時パスワードをこの端末に一度だけ表示して終了。控えた値はコミットしない

# 2) サーバー起動 (launchSettings.json で ASPNETCORE_ENVIRONMENT=Development が自動設定される)
dotnet run

# → http://127.0.0.1:18080  (HTTP)
# → https://localhost:18443 (HTTPS, dev-cert 必要)
# → watashi-dev.db が同ディレクトリに自動生成

# 3) 別ウィンドウで疎通確認
Invoke-RestMethod http://127.0.0.1:18080/health
# → {"status":"ok", ...}

# 4) クライアント起動
cd ..\Watashi.Client
# 接続先と更新確認先は同梱 deployment.json で固定。dev では serverUrl を http://127.0.0.1:18080 にしておく
dotnet run
# → ログイン画面 → bootstrap で表示された資格情報でログイン → パスワード変更画面 → メイン画面
```

---

## HTTPS 開発環境のセットアップ

ローカル開発でも HTTPS で動作確認したい場合の手順です。
**ループバック (`127.0.0.1` / `localhost`) では本来 HTTPS は必須ではありませんが、本番想定の動作確認には推奨。**

### ステップ 1 — dev 証明書の作成と信頼

```powershell
# 1) ASP.NET Core の localhost 用 dev 証明書を作成 (自動でユーザー個人ストアに入る)
dotnet dev-certs https

# 2) 信頼ストアに登録 (Windows のセキュリティ警告が出るので「はい」)
dotnet dev-certs https --trust

# 3) 確認 (Exit code 0 が出れば信頼済み)
dotnet dev-certs https --check --trust
```

> **リモートデスクトップ越しに作業している場合**、`--trust` のセキュリティ警告がセキュアデスクトップに表示されダイアログが見えないことがあります。
> その場合は物理コンソールでログインして実行するか、信頼ストアに直接インポート:
> ```powershell
> $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq "CN=localhost" -and $_.HasPrivateKey } | Sort-Object NotAfter -Descending | Select-Object -First 1
> $store = New-Object System.Security.Cryptography.X509Certificates.X509Store("Root", "CurrentUser")
> $store.Open("ReadWrite"); $store.Add($cert); $store.Close()
> ```
> (Windows の確認ダイアログは出ますが、 `--trust` よりは可視化されやすい場合があります)

### ステップ 2 — サーバーの appsettings に HTTPS endpoint を追加

`src/Watashi.Server/appsettings.Development.json` の Kestrel セクション:

```jsonc
"Kestrel": {
  "Endpoints": {
    "Http":  { "Url": "http://127.0.0.1:18080" },
    "Https": { "Url": "https://localhost:18443" }
  }
}
```

> 証明書は明示パス指定不要。Kestrel が Windows 証明書ストアから自動的に dev cert を読み込む。

### ステップ 3 — クライアント側の接続先を切替

接続先は `deployment.json` で固定されているため、HTTPS で検証したいときは
`src\Watashi.Client\deployment.json` の `serverUrl` を `https://localhost:18443` に書き換えて再起動する
(Content/PreserveNewest で bin にコピーされて反映)。
起動後、ログイン画面下の **接続テスト** で `✓ 接続できました。` を確認でき、
ログインするとヘッダー右上の表示が **HTTPS** になる。

### よくあるエラー

| 症状 | 対処 |
|---|---|
| `信頼関係を確立できませんでした` | dev-cert がまだ Root ストアに入っていない。ステップ 1 を再実行 |
| `127.0.0.1` でアクセスすると失敗、`localhost` だと OK | dev-cert の CN は `localhost` のみ。SAN に `127.0.0.1` は含まれない。`localhost` を使うこと |
| 期限切れ | `dotnet dev-certs https --clean` でリセットして作り直し |
| PowerShell 5.1 で curl/Invoke-RestMethod が TLS で落ちる | `[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12` を最初に実行。`curl.exe -k` の方が手軽 |

### HTTPS じゃなくても自動ログインしたい場合

設計上、Client の **「このPCを記憶する」は HTTP/HTTPS 両対応** にしています。
HTTP でも信頼デバイストークンを保存し、次回起動時に自動ログインします。
サーバー側は `Auth:AllowHttpForAutoLogin: true` (Dev) でこの動作を許可しています(本番 `appsettings.json` では `false` がデフォルト)。

HTTP では平文で TrustDevice トークン交換が行われるので、**本番では HTTPS 必須** です。

---

## SMB テスト共有を立てる

リモートペインの動作確認には実際の SMB 共有が必要です。
Windows 10/11 ローカルに簡易共有を立てる例:

```powershell
# 1) フォルダ作成
New-Item -Path "D:\Github\watashitest" -ItemType Directory -Force

# 2) ローカルユーザー作成 (SMB アクセス用)
$pw = ConvertTo-SecureString "P@ssword123" -AsPlainText -Force
New-LocalUser -Name "watashi-test" -Password $pw -PasswordNeverExpires -AccountNeverExpires

# 3) フォルダを SMB 共有 (要管理者権限 PowerShell)
New-SmbShare -Name "watashi-test" -Path "D:\Github\watashitest" -FullAccess "watashi-test"

# 4) NTFS パーミッション
$acl = Get-Acl "D:\Github\watashitest"
$rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
    "watashi-test", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")
$acl.SetAccessRule($rule)
Set-Acl "D:\Github\watashitest" $acl
```

その後、管理画面で:
1. **ホスト**タブ: name=`test`, address=`127.0.0.1`, port=`445`, user=`watashi-test`, password=`P@ssword123`, node=`Direct (Local)`
2. **接続テスト** → ✓ 接続OK
3. **共有**タブ: ShareName=`watashi-test`, DisplayName=`Watashi テスト`
4. **ユーザー権限**タブ: 自分 → サーバー=test → 共有=Watashi テスト → テンプレ=フルアクセス → パス=`/` → 追加

クライアントを再起動 (または「ログアウト → 再ログイン」) するとリモートペインに新しい共有が出てきます。

---

## 既知の落とし穴

### PowerShell から API テストするときの UTF-8 問題

`Invoke-RestMethod -Body $json -ContentType "application/json"` だと、PowerShell が JSON を Windows-31J 系で送ってしまい日本語が `?` 化することがあります。UTF-8 を明示してください:

```powershell
$body = @{ name = "テスト" } | ConvertTo-Json -Compress
$bytes = [System.Text.Encoding]::UTF8.GetBytes($body)
Invoke-RestMethod -Uri "http://127.0.0.1:18080/api/admin/..." `
    -Method Post -Body $bytes -ContentType "application/json; charset=utf-8" -Headers $headers
```

または `curl.exe` を使う:
```powershell
curl.exe -X POST http://127.0.0.1:18080/api/... `
    -H "Content-Type: application/json; charset=utf-8" `
    --data-binary '{"name":"テスト"}'
```

### `dotnet run` 中に再ビルドできない

クライアントが起動している間、`Watashi.Client.exe` がロックされていて再ビルドできません。
ビルドする前に `Get-Process Watashi.Client | Stop-Process -Force` で停止。

### dev-cert の有効期限

ASP.NET Core の dev 証明書は 1 年で期限切れ。
切れた場合は `dotnet dev-certs https --clean` してから `--trust` で再作成。

### 接続先設定 (`deployment.json`) の確認

接続先サーバ、更新確認先、D&D 可否は exe と同じ場所の `deployment.json` で固定される。
起動時に `DeploymentConfig.Apply` が読み込み、`AppSettings` に上書き適用する (`settings.json` より優先)。
dev では `src\Watashi.Client\deployment.json` を編集すれば bin にコピーされて反映する:

```jsonc
{
  "serverUrl": "http://127.0.0.1:18080",
  "updateManifestUrl": "https://watashi.internal/install/Watashi.Client.application",
  "enableDragDrop": true
}
```

- `serverUrl` / `updateManifestUrl` 未設定またはファイル欠落時は「配布設定エラー」を表示して終了する (利用者は接続先を変更できない設計のため、設定画面は出さない)
- `%LocalAppData%\Watashi\settings.json` には最終ローカルパス等のみ保存され、**接続先は保存されない**
- `AppSettings.IsHttps` は `ServerUrl` のスキームから判定する (保存フィールドではなく URL を信用)

### ユーザー CSV のインポート/エクスポートを API から試す

```powershell
$body = '{"username":"admin","password":"YourAdminP@ss!!"}'
$r = Invoke-RestMethod -Uri "http://127.0.0.1:18080/api/auth/login" -Method Post -Body $body -ContentType "application/json"
$token = $r.accessToken

# Export
Invoke-WebRequest -Uri "http://127.0.0.1:18080/api/admin/users/export.csv" `
    -Headers @{ Authorization = "Bearer $token" } -OutFile users.csv

# Import (PowerShell 5.1 は -Form 未対応なので System.Net.Http で multipart を組む)
Add-Type -AssemblyName System.Net.Http
$http = New-Object System.Net.Http.HttpClient
$http.DefaultRequestHeaders.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", $token)
$mpc = New-Object System.Net.Http.MultipartFormDataContent
$bytes = [System.IO.File]::ReadAllBytes("$pwd\import.csv")
$fc = New-Object System.Net.Http.ByteArrayContent(,$bytes)
$fc.Headers.ContentType = New-Object System.Net.Http.Headers.MediaTypeHeaderValue("text/csv")
$mpc.Add($fc, "file", "import.csv")
$mpc.Add((New-Object System.Net.Http.StringContent "add-only"), "mode")  # or "upsert"
$res = $http.PostAsync("http://127.0.0.1:18080/api/admin/users/import.csv", $mpc).Result
$res.Content.ReadAsStringAsync().Result
```

### サーバー DbContext と DbUpdateException

`SaveChangesAsync` が unique 制約で失敗した後、同じ `DbContext` で別の SaveChanges を呼ぶと **失敗エンティティが ChangeTracker に残っていて同じ例外が再発** します。
catch 内で `db.ChangeTracker.Clear()` してから次の操作を行うこと。
監査ログを catch 内で書く場合は特に注意 (実例: `AdminUserEndpoints.cs` の duplicate user handler)。

### TabControl 内の選択変化イベント

`TabControl.SelectionChanged` は **子コントロール (ListBox/ComboBox 等) の選択変化でもバブルアップ** します。
タブ自体の切替を検出したい場合は `e.OriginalSource` と `sender` の参照比較で除外:
```csharp
if (!ReferenceEquals(e.OriginalSource, sender)) return;
```

### `SafeAsync` と successMessage

`AdminViewModelBase.SafeAsync(action, successMessage)` は、action 内で `StatusMessage` を書き換えた場合(バリデーション失敗の早期 return 等)は `successMessage` で上書きしません。
逆に言うと、**action 内で StatusMessage を一切触らず正常終了した場合のみ** successMessage が表示されます。
