# CifsTool — 社内CIFSファイル管理ツール 最終仕様書 兼 実装ガイド

> この文書は Claude Code で実装するための完全仕様書です。
> 設計の背景や選択肢の議論は省略し、「何をどう作るか」だけを記載しています。
> 上から順に読み、IMPLEMENTATION ORDER（末尾）の順序で実装してください。

---

# PART 1: OVERVIEW

## 何を作るか
FFFTPをオマージュした社内向けCIFSファイル管理ツール。
WPFデスクトップアプリ（ClickOnce配布）から、HTTP/HTTPS経由で中央サーバーに接続し、
サーバーがCIFS共有を操作する。踏み台サーバー経由の接続にも対応（エージェント方式）。

## 主要コンポーネント
1. **CifsTool.Client** — WPF デスクトップアプリ（エンドユーザーが使う）
2. **CifsTool.Server** — ASP.NET Core 中央サーバー（認証・権限・ログ・CIFS操作）
3. **CifsTool.Agent** — ASP.NET Core Worker Service（踏み台に配置、CIFS操作を中継）
4. **CifsTool.Shared** — 共有モデル・DTO・定数

## 前提
- .NET 8
- 閉域網（社内ネットワーク）
- Windows 10/11 クライアント
- 50ユーザー同時接続程度
- データベースは SQLite（中央・エージェント共通）

---

# PART 2: ARCHITECTURE

```
┌──────────────────────┐
│  WPF Client          │  ClickOnce でインストール
│  (.NET 8 + WPF)      │
│  ┌─────────────────┐ │
│  │ローカルFS(左)    │ │
│  │リモートCIFS(右)  │ │  FFFTP風2ペイン
│  └─────────────────┘ │
└──────────┬───────────┘
           │ HTTP or HTTPS (ユーザー選択)
           ▼
┌──────────────────────────────────────────┐
│  Central Server (ASP.NET Core 8)          │
│  ┌──────────┐  ┌──────┐  ┌────────────┐ │
│  │ Auth/JWT  │  │EFCore│  │ SMBLibrary │ │
│  └──────────┘  │SQLite│  └─────┬──────┘ │
│                └──────┘        │ Direct  │
│                          ┌─────┴──────┐  │
│                          │ Router     │  │
│                          └─────┬──────┘  │
└────────────────────────────────┼─────────┘
                      ┌──────────┼──────────┐
               mTLS   │          │          │  mTLS
                      ▼          ▼          ▼
              [Agent A]    [Direct CIFS]  [Agent B]
              (踏み台A)                   (踏み台B)
                 │                           │
                 ▼                           ▼
            [網AのCIFS]                 [網BのCIFS]
```

### 通信プロトコル
- Client ↔ Server: HTTP or HTTPS（ユーザーが接続設定で選択）
- Server ↔ Agent: HTTPS + mTLS 必須
- Server/Agent → CIFS: SMB2/3（SMBLibrary経由）

---

# PART 3: SOLUTION STRUCTURE

```
CifsTool/
├── CifsTool.sln
├── src/
│   ├── CifsTool.Shared/
│   │   ├── CifsTool.Shared.csproj          # net8.0 classlib
│   │   ├── Models/
│   │   │   ├── User.cs
│   │   │   ├── TrustedDevice.cs
│   │   │   ├── RefreshToken.cs
│   │   │   ├── ExecutionNode.cs
│   │   │   ├── CifsHost.cs
│   │   │   ├── CifsShare.cs
│   │   │   ├── PermissionTemplate.cs
│   │   │   ├── UserPermission.cs
│   │   │   ├── AuditLog.cs
│   │   │   └── SystemSetting.cs
│   │   ├── DTOs/
│   │   │   ├── Auth/
│   │   │   │   ├── LoginRequest.cs
│   │   │   │   ├── LoginResponse.cs
│   │   │   │   ├── AutoLoginRequest.cs
│   │   │   │   ├── RefreshRequest.cs
│   │   │   │   ├── RefreshResponse.cs
│   │   │   │   ├── ChangePasswordRequest.cs
│   │   │   │   └── TrustDeviceRequest.cs
│   │   │   ├── Files/
│   │   │   │   ├── FileListRequest.cs
│   │   │   │   ├── FileEntry.cs
│   │   │   │   ├── RenameRequest.cs
│   │   │   │   └── MkdirRequest.cs
│   │   │   ├── Admin/
│   │   │   │   ├── UserDto.cs
│   │   │   │   ├── HostDto.cs
│   │   │   │   ├── ShareDto.cs
│   │   │   │   ├── PermissionTemplateDto.cs
│   │   │   │   ├── UserPermissionDto.cs
│   │   │   │   ├── NodeDto.cs
│   │   │   │   ├── DeviceDto.cs
│   │   │   │   └── AuditLogDto.cs
│   │   │   └── LocationDto.cs
│   │   ├── Constants/
│   │   │   ├── Operations.cs              # "READ","WRITE","DELETE","RENAME"
│   │   │   └── NodeTypes.cs               # "Direct","Agent"
│   │   └── Helpers/
│   │       ├── PathHelper.cs              # NormalizePath, IsPathWithin, GetParent
│   │       └── CryptoHelper.cs            # AES-256-GCM encrypt/decrypt
│   │
│   ├── CifsTool.Server/
│   │   ├── CifsTool.Server.csproj         # net8.0 web
│   │   ├── Program.cs
│   │   ├── appsettings.json
│   │   ├── Data/
│   │   │   ├── AppDbContext.cs
│   │   │   └── Migrations/               # EF Core Migrations
│   │   ├── Services/
│   │   │   ├── AuthService.cs
│   │   │   ├── PermissionService.cs
│   │   │   ├── CifsService.cs             # Direct SMB操作
│   │   │   ├── AgentForwarder.cs           # エージェント経由のSMB操作
│   │   │   ├── NodeRouter.cs               # Direct/Agent ルーティング
│   │   │   ├── AuditLogService.cs
│   │   │   └── EncryptionService.cs        # AES-256-GCM for CIFS credentials
│   │   ├── Endpoints/
│   │   │   ├── AuthEndpoints.cs
│   │   │   ├── FileEndpoints.cs
│   │   │   ├── HostEndpoints.cs
│   │   │   ├── AdminEndpoints.cs
│   │   │   └── InternalEndpoints.cs        # Agent heartbeat/log受信
│   │   └── Middleware/
│   │       ├── AuditMiddleware.cs
│   │       └── ExceptionMiddleware.cs
│   │
│   ├── CifsTool.Agent/
│   │   ├── CifsTool.Agent.csproj          # net8.0 web (Worker)
│   │   ├── Program.cs
│   │   ├── appsettings.json
│   │   ├── Data/
│   │   │   └── AgentDbContext.cs           # SQLite (PendingLogs)
│   │   ├── Services/
│   │   │   ├── CifsService.cs             # SMB操作実行
│   │   │   ├── HeartbeatService.cs        # BackgroundService
│   │   │   └── LogSyncService.cs          # 中央へのログ送信
│   │   └── Endpoints/
│   │       └── AgentEndpoints.cs           # /agent/files/* 等
│   │
│   └── CifsTool.Client/
│       ├── CifsTool.Client.csproj         # net8.0-windows WPF
│       ├── App.xaml / App.xaml.cs
│       ├── Services/
│       │   ├── ApiClient.cs               # HttpClient wrapper
│       │   ├── CredentialStore.cs          # Windows Credential Manager
│       │   ├── SessionManager.cs          # JWT管理、自動リフレッシュ
│       │   └── LocalFileService.cs        # ローカルFS操作
│       ├── ViewModels/
│       │   ├── LoginViewModel.cs
│       │   ├── ChangePasswordViewModel.cs
│       │   ├── MainViewModel.cs           # 2ペイン画面
│       │   ├── LocalPaneViewModel.cs
│       │   ├── RemotePaneViewModel.cs
│       │   ├── ConnectionSettingsViewModel.cs
│       │   └── Admin/
│       │       ├── AdminShellViewModel.cs
│       │       ├── UserManagementViewModel.cs
│       │       ├── HostManagementViewModel.cs
│       │       ├── ShareManagementViewModel.cs
│       │       ├── PermissionTemplateViewModel.cs
│       │       ├── UserPermissionViewModel.cs
│       │       ├── DeviceManagementViewModel.cs
│       │       ├── NodeManagementViewModel.cs
│       │       ├── AuditLogViewModel.cs
│       │       └── SystemSettingsViewModel.cs
│       ├── Views/
│       │   ├── LoginWindow.xaml
│       │   ├── ChangePasswordWindow.xaml
│       │   ├── MainWindow.xaml
│       │   ├── ConnectionSettingsWindow.xaml
│       │   └── Admin/
│       │       ├── AdminWindow.xaml         # TabControl
│       │       ├── UserManagementView.xaml
│       │       ├── HostManagementView.xaml
│       │       ├── ShareManagementView.xaml
│       │       ├── PermissionTemplateView.xaml
│       │       ├── UserPermissionView.xaml  # パスブラウザ含む
│       │       ├── DeviceManagementView.xaml
│       │       ├── NodeManagementView.xaml
│       │       ├── AuditLogView.xaml
│       │       └── SystemSettingsView.xaml
│       └── Converters/
│           ├── BoolToVisibilityConverter.cs
│           └── UtcToLocalConverter.cs
│
└── tests/
    └── CifsTool.Tests/
        ├── CifsTool.Tests.csproj
        ├── PathHelperTests.cs
        ├── PermissionServiceTests.cs
        └── AuthServiceTests.cs
```

---

# PART 4: NUGET PACKAGES

## CifsTool.Shared
```xml
<PackageReference Include="System.Security.Cryptography.Algorithms" />
```
（基本的にほぼパッケージ不要、.NET標準ライブラリで完結）

## CifsTool.Server
```xml
<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="8.*" />
<PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="8.*" />
<PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="8.*" />
<PackageReference Include="BCrypt.Net-Next" Version="4.*" />
<PackageReference Include="Serilog.AspNetCore" Version="8.*" />
<PackageReference Include="SMBLibrary" Version="1.5.*" />
<PackageReference Include="System.IdentityModel.Tokens.Jwt" Version="7.*" />
```

## CifsTool.Agent
```xml
<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="8.*" />
<PackageReference Include="SMBLibrary" Version="1.5.*" />
<PackageReference Include="Serilog.AspNetCore" Version="8.*" />
```

## CifsTool.Client
```xml
<PackageReference Include="CommunityToolkit.Mvvm" Version="8.*" />
<PackageReference Include="Microsoft.Extensions.Http" Version="8.*" />
<PackageReference Include="Microsoft.Extensions.Http.Polly" Version="8.*" />
<PackageReference Include="CredentialManagement" Version="1.0.2" />
```

## CifsTool.Tests
```xml
<PackageReference Include="xunit" Version="2.*" />
<PackageReference Include="Microsoft.EntityFrameworkCore.InMemory" Version="8.*" />
<PackageReference Include="FluentAssertions" Version="6.*" />
```

---

# PART 5: DATABASE

## 5.1 SQLite 構成
- 中央サーバー DB: `C:\ProgramData\CifsTool\cifs_tool.db`
- エージェント DB: `C:\ProgramData\CifsToolAgent\agent_buffer.db`
- DBファイルは**アプリ起動時に自動生成** (`db.Database.MigrateAsync()`)
- ローカルSSD上に配置必須（ネットワーク共有不可）

## 5.2 PRAGMA（接続時に毎回実行）
```sql
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA foreign_keys = ON;
PRAGMA busy_timeout = 5000;
PRAGMA cache_size = -65536;
PRAGMA temp_store = MEMORY;
```

## 5.3 スキーマ（中央サーバー）

```sql
CREATE TABLE Users (
  Id INTEGER PRIMARY KEY AUTOINCREMENT,
  Username TEXT NOT NULL UNIQUE,
  PasswordHash TEXT NOT NULL,
  IsAdmin INTEGER NOT NULL DEFAULT 0,
  IsLocked INTEGER NOT NULL DEFAULT 0,
  FailedLoginCount INTEGER NOT NULL DEFAULT 0,
  PasswordChangedAt TEXT NOT NULL,
  PasswordExpiresAt TEXT NOT NULL,
  MustChangePassword INTEGER NOT NULL DEFAULT 0,
  LastLoginAt TEXT,
  CreatedAt TEXT NOT NULL
);

CREATE TABLE SystemSettings (
  Key TEXT PRIMARY KEY,
  Value TEXT NOT NULL,
  UpdatedAt TEXT NOT NULL,
  UpdatedBy INTEGER REFERENCES Users(Id)
);

CREATE TABLE TrustedDevices (
  Id INTEGER PRIMARY KEY AUTOINCREMENT,
  UserId INTEGER NOT NULL UNIQUE REFERENCES Users(Id),
  MachineName TEXT NOT NULL,
  WindowsUsername TEXT NOT NULL,
  DeviceTokenHash TEXT NOT NULL,
  RegisteredAt TEXT NOT NULL,
  LastUsedAt TEXT NOT NULL,
  IsRevoked INTEGER NOT NULL DEFAULT 0,
  RevokedReason TEXT,
  RevokedAt TEXT
);

CREATE TABLE RefreshTokens (
  Id TEXT PRIMARY KEY,
  UserId INTEGER NOT NULL REFERENCES Users(Id),
  TokenHash TEXT NOT NULL,
  DeviceId INTEGER REFERENCES TrustedDevices(Id),
  IssuedAt TEXT NOT NULL,
  ExpiresAt TEXT NOT NULL,
  LastUsedAt TEXT NOT NULL,
  IsRevoked INTEGER NOT NULL DEFAULT 0,
  ClientIp TEXT
);
CREATE INDEX IX_RefreshTokens_UserId ON RefreshTokens(UserId);

CREATE TABLE ExecutionNodes (
  Id INTEGER PRIMARY KEY AUTOINCREMENT,
  Name TEXT NOT NULL,
  NodeType TEXT NOT NULL CHECK (NodeType IN ('Direct', 'Agent')),
  Endpoint TEXT,
  ClientCertificateThumbprint TEXT,
  IsActive INTEGER NOT NULL DEFAULT 1,
  LastHeartbeatAt TEXT,
  HealthStatus TEXT NOT NULL DEFAULT 'Unknown'
    CHECK (HealthStatus IN ('Healthy', 'Unhealthy', 'Unknown')),
  MaxConcurrency INTEGER NOT NULL DEFAULT 20,
  CreatedAt TEXT NOT NULL
);

CREATE TABLE CifsHosts (
  Id INTEGER PRIMARY KEY AUTOINCREMENT,
  Name TEXT NOT NULL,
  HostAddress TEXT NOT NULL,
  Port INTEGER NOT NULL DEFAULT 445,
  Description TEXT,
  CredUsername TEXT NOT NULL,
  CredPasswordEnc BLOB NOT NULL,
  ExecutionNodeId INTEGER NOT NULL REFERENCES ExecutionNodes(Id),
  CreatedAt TEXT NOT NULL
);

CREATE TABLE CifsShares (
  Id INTEGER PRIMARY KEY AUTOINCREMENT,
  HostId INTEGER NOT NULL REFERENCES CifsHosts(Id),
  ShareName TEXT NOT NULL,
  DisplayName TEXT NOT NULL,
  UNIQUE(HostId, ShareName)
);

CREATE TABLE PermissionTemplates (
  Id INTEGER PRIMARY KEY AUTOINCREMENT,
  Name TEXT NOT NULL UNIQUE,
  CanRead INTEGER NOT NULL DEFAULT 0,
  CanWrite INTEGER NOT NULL DEFAULT 0,
  CanDelete INTEGER NOT NULL DEFAULT 0,
  CanRename INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE UserPermissions (
  Id INTEGER PRIMARY KEY AUTOINCREMENT,
  UserId INTEGER NOT NULL REFERENCES Users(Id),
  ShareId INTEGER NOT NULL REFERENCES CifsShares(Id),
  TemplateId INTEGER NOT NULL REFERENCES PermissionTemplates(Id),
  AllowedPath TEXT NOT NULL,
  DisplayName TEXT,
  CreatedAt TEXT NOT NULL,
  CreatedBy INTEGER REFERENCES Users(Id)
);
CREATE INDEX IX_UserPermissions_UserShare ON UserPermissions(UserId, ShareId);

CREATE TABLE AuditLogs (
  Id INTEGER PRIMARY KEY AUTOINCREMENT,
  Timestamp TEXT NOT NULL,
  UserId INTEGER,
  Username TEXT NOT NULL,
  Operation TEXT NOT NULL,
  HostId INTEGER,
  ShareId INTEGER,
  Path TEXT,
  TargetPath TEXT,
  Result TEXT NOT NULL CHECK (Result IN ('success', 'failure')),
  ErrorMessage TEXT,
  ClientIp TEXT,
  ClientHostname TEXT,
  BytesTransferred INTEGER,
  DurationMs INTEGER,
  Protocol TEXT,
  ExecutionNodeId INTEGER,
  UsedPermissionId INTEGER
);
CREATE INDEX IX_AuditLogs_Timestamp ON AuditLogs(Timestamp DESC);
CREATE INDEX IX_AuditLogs_UserId ON AuditLogs(UserId);
CREATE INDEX IX_AuditLogs_HostId ON AuditLogs(HostId);
```

## 5.4 初期シードデータ
マイグレーション内で投入:
```csharp
// 管理者ユーザー（初回ログイン時にパスワード変更を強制）
("admin", BCrypt.HashPassword("Admin123!@#"), IsAdmin=1, MustChangePassword=1)

// デフォルト実行ノード
("Direct (Local)", NodeType="Direct", HealthStatus="Healthy")

// システム設定
("PasswordExpiryDays", "90")
("PasswordWarningDays", "14")
("AgentMaxConcurrency", "20")
("SessionIdleMinutes", "30")

// デフォルト権限テンプレート
("読取のみ", CanRead=1, CanWrite=0, CanDelete=0, CanRename=0)
("読取+書込", CanRead=1, CanWrite=1, CanDelete=0, CanRename=0)
("フルアクセス", CanRead=1, CanWrite=1, CanDelete=1, CanRename=1)
```

## 5.5 エージェント側スキーマ
```sql
CREATE TABLE PendingLogs (
  Id INTEGER PRIMARY KEY AUTOINCREMENT,
  LogJson TEXT NOT NULL,
  CreatedAt TEXT NOT NULL,
  AttemptCount INTEGER DEFAULT 0
);
```

## 5.6 EF Core DateTime変換（全エンティティ共通）
全 `DateTime` プロパティは ISO 8601 UTC の TEXT で保管:
```csharp
// AppDbContext.OnModelCreating 内でグローバル設定
foreach (var entityType in modelBuilder.Model.GetEntityTypes())
    foreach (var property in entityType.GetProperties()
        .Where(p => p.ClrType == typeof(DateTime) || p.ClrType == typeof(DateTime?)))
    {
        property.SetValueConverter(new ValueConverter<DateTime, string>(
            v => v.ToUniversalTime().ToString("o"),
            v => DateTime.Parse(v, null, DateTimeStyles.RoundtripKind).ToUniversalTime()
        ));
    }
```

## 5.7 バックアップ
```csharp
public static void Backup(string srcConnStr, string destPath)
{
    using var src = new SqliteConnection(srcConnStr);
    using var dst = new SqliteConnection($"Data Source={destPath}");
    src.Open();
    dst.Open();
    src.BackupDatabase(dst);
}
```
日次タスクスケジューラで実行、7日+月次4世代保持。

## 5.8 ログ削除（1年経過分）
日次バッチ:
```sql
DELETE FROM AuditLogs WHERE Timestamp < datetime('now', '-1 year');
```
月次で `VACUUM;` を実行しファイルサイズ縮小。

---

# PART 6: SERVER — 認証・認可

## 6.1 トークン設計
| トークン | TTL | 用途 |
|---|---|---|
| アクセストークン (JWT) | **15分** | API認証、ヘッダ `Authorization: Bearer {token}` |
| リフレッシュトークン | **30日** | アクセストークン再取得、DB検証付き |

## 6.2 JWT Payload
```json
{
  "uid": "1",
  "name": "admin",
  "role": "Admin",
  "exp": 1748500000,
  "iss": "CifsTool",
  "aud": "CifsTool"
}
```
署名: HMAC-SHA256、キーは appsettings.json の `Jwt:Secret`（256bit以上）。

## 6.3 パスワードポリシー
- 12文字以上、英大文字+小文字+数字+記号を各1以上
- 有効期限: SystemSettings "PasswordExpiryDays"（デフォルト90日）
- パスワード変更時に `PasswordExpiresAt = UtcNow + ExpiryDays` を再計算
- 履歴チェック: **なし**（過去パスワード再利用OK）
- ロック: 5回失敗で `IsLocked = true`（管理者解除）

## 6.4 認証フロー

### 通常ログイン `POST /api/auth/login`
```
Request:  { username, password }
Response: { accessToken, refreshToken, refreshTokenId, expiresIn: 900,
            mustChangePassword: bool, passwordExpiresInDays: int? }
```
処理:
1. Username + bcrypt照合
2. IsLocked チェック → ロック中は 403
3. 失敗時 FailedLoginCount++、5回で IsLocked=true
4. 成功時 FailedLoginCount=0、LastLoginAt更新
5. JWT(15min) + RefreshToken(30day) 発行、DB保存
6. MustChangePassword or PasswordExpiresAt 超過 → `mustChangePassword: true`

### 自動ログイン `POST /api/auth/auto-login`
```
Request:  { machineName, windowsUsername, deviceToken }
Response: 同上
```
処理:
1. **HTTPS必須**（HTTP→403）
2. TrustedDevices から (MachineName, WindowsUsername, IsRevoked=false) で検索
3. deviceToken を bcrypt 照合
4. 紐づく User の IsLocked チェック
5. 成功: JWT + RefreshToken 発行、LastUsedAt 更新
6. MustChangePassword / PasswordExpired チェック

### トークンリフレッシュ `POST /api/auth/refresh`
```
Request:  { refreshTokenId, refreshToken }
Response: { accessToken, expiresIn: 900 }
```
処理:
1. RefreshTokens テーブルから Id で取得
2. IsRevoked / ExpiresAt チェック
3. TokenHash を bcrypt 照合
4. **DB状態確認**（リフレッシュごとに毎回）:
   - User.IsLocked → true なら 401
   - DeviceId がある場合 → TrustedDevice.IsRevoked チェック
   - User.MustChangePassword → `mustChangePassword: true` をレスポンスに付与
5. 新しい AccessToken 発行

### パスワード変更 `POST /api/auth/change-password`
```
Request:  { currentPassword, newPassword }
```
処理: 旧PW照合 → ポリシー検証 → Hash更新 → ExpiresAt 再計算 → MustChangePassword=false

### 信頼デバイス登録 `POST /api/auth/trust-device`
```
Request:  { machineName, windowsUsername }
Response: { deviceToken }  ← クライアントが Credential Manager に保存
```
処理（**Serializableトランザクション**）:
1. 既存デバイス全削除（同UserId）
2. ランダム256bitトークン生成
3. bcryptハッシュ化してDB保存
4. 平文トークンをレスポンスに返す

### ログアウト `POST /api/auth/logout`
RefreshToken を IsRevoked=true に。

---

# PART 7: SERVER — 権限モデル

## 7.1 サブパス権限
- 粒度: **(共有, サブパス)** 単位
- 1ユーザー × 1共有 に複数の許可パスを持てる
- AllowedPath: "/" = 共有全体、"/dept-A" = サブパスのみ

## 7.2 パス正規化
```csharp
// PathHelper.cs
public static string NormalizePath(string path)
{
    if (string.IsNullOrWhiteSpace(path)) return "/";
    var p = path.Replace('\\', '/').TrimEnd('/');
    if (!p.StartsWith('/')) p = '/' + p;
    // ".." を展開して解決
    var segments = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
    var stack = new Stack<string>();
    foreach (var seg in segments)
    {
        if (seg == "..") { if (stack.Count > 0) stack.Pop(); }
        else if (seg != ".") stack.Push(seg);
    }
    return "/" + string.Join("/", stack.Reverse());
}

public static bool IsPathWithin(string allowedPath, string requestedPath)
{
    var a = NormalizePath(allowedPath);
    var r = NormalizePath(requestedPath);
    if (a == "/") return true;
    return string.Equals(a, r, StringComparison.OrdinalIgnoreCase)
        || r.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase);
}

public static string GetParent(string path)
{
    var p = NormalizePath(path);
    var idx = p.LastIndexOf('/');
    return idx <= 0 ? "/" : p[..idx];
}
```

## 7.3 権限チェック
```csharp
// PermissionService.cs
public async Task<(bool allowed, int? permissionId)> CanPerformAsync(
    int userId, int shareId, string path, string operation)
{
    var normalized = PathHelper.NormalizePath(path);
    var entries = await _db.UserPermissions
        .Include(p => p.Template)
        .Where(p => p.UserId == userId && p.ShareId == shareId)
        .ToListAsync();

    foreach (var e in entries)
    {
        if (!PathHelper.IsPathWithin(e.AllowedPath, normalized)) continue;
        bool allowed = operation switch
        {
            "READ" => e.Template.CanRead != 0,
            "WRITE" => e.Template.CanWrite != 0,
            "DELETE" => e.Template.CanDelete != 0,
            "RENAME" => e.Template.CanRename != 0,
            _ => false
        };
        if (allowed) return (true, e.Id);
    }
    return (false, null);
}
```

## 7.4 特殊制限
1. **許可パスのルート自体は DELETE / RENAME 不可**:
   ```csharp
   public async Task<bool> IsPermissionRootAsync(int userId, int shareId, string path)
   {
       var n = PathHelper.NormalizePath(path);
       return await _db.UserPermissions
           .AnyAsync(p => p.UserId == userId && p.ShareId == shareId
                       && p.AllowedPath == n);
   }
   ```
   DELETE / RENAME 処理の冒頭でチェック、該当すれば 403。

2. **RENAME で親ディレクトリの変更は禁止**（MOVE抜け穴防止）:
   ```csharp
   var oldParent = PathHelper.GetParent(oldPath);
   var newParent = PathHelper.GetParent(newPath);
   if (!string.Equals(oldParent, newParent, StringComparison.OrdinalIgnoreCase))
       return Results.BadRequest("リネームでは親ディレクトリを変更できません");
   ```

---

# PART 8: SERVER — ファイル操作 API

すべてのファイル操作は、権限チェック + 監査ログ記録 を行う。

## 8.1 エンドポイント一覧

### ユーザー向け
```
GET    /api/hosts                                       → 権限のあるホスト一覧
GET    /api/hosts/{hostId}/shares                       → 権限のある共有一覧
GET    /api/hosts/{hostId}/shares/{shareId}/locations   → 自分の許可パス一覧

GET    /api/files?hostId=&shareId=&path=&page=&sort=    → ファイル一覧 (200件/ページ)
GET    /api/files/download?hostId=&shareId=&path=       → ダウンロード (StreamingResponse)
POST   /api/files/upload                                → アップロード (Streaming)
DELETE /api/files?hostId=&shareId=&path=                → 削除
POST   /api/files/rename                                → リネーム (同一親限定)
POST   /api/files/mkdir                                 → フォルダ作成
```

### 管理者 (Admin ロール必須)
```
GET/POST/PATCH/DELETE  /api/admin/users
POST                   /api/admin/users/{id}/unlock
POST                   /api/admin/users/{id}/reset-password

GET/POST/PATCH/DELETE  /api/admin/hosts
POST                   /api/admin/hosts/{id}/test

GET/POST/PATCH/DELETE  /api/admin/shares

GET/POST/PATCH/DELETE  /api/admin/permission-templates
GET/POST/DELETE        /api/admin/user-permissions
GET                    /api/admin/browse?hostId=&shareId=&path=  (パスブラウザ、管理者は全閲覧可)

GET/DELETE             /api/admin/users/{id}/devices

GET/POST/PATCH/DELETE  /api/admin/nodes
POST                   /api/admin/nodes/{id}/regenerate-key
GET                    /api/admin/nodes/{id}/status

GET                    /api/admin/logs?user=&op=&from=&to=&page=
GET                    /api/admin/logs/export.csv

GET/PUT                /api/admin/settings/{key}
GET                    /api/admin/settings
```

### 内部（エージェント → 中央）
```
POST   /api/internal/heartbeat          ← エージェントが30秒毎
POST   /api/internal/audit-logs/batch   ← エージェントのバッファログ送信
```

## 8.2 /locations レスポンス例
```json
[
  {
    "permissionId": 12,
    "hostId": 1,
    "shareId": 1,
    "path": "/dept-A",
    "displayName": "経理部",
    "hostName": "fileserver01",
    "shareName": "documents",
    "permissions": { "read": true, "write": true, "delete": true, "rename": true }
  }
]
```

## 8.3 ファイル一覧レスポンス例
```json
{
  "currentPath": "/dept-A/2026",
  "entries": [
    { "name": "..", "type": "parent", "canGoUp": true },
    { "name": "archive", "type": "directory", "modifiedAt": "2026-05-01T00:00:00Z" },
    { "name": "report.xlsx", "type": "file", "size": 1234567, "modifiedAt": "2026-05-20T10:30:00Z" }
  ],
  "page": 1,
  "totalCount": 42
}
```
`canGoUp` は、現在パスが許可パスのルートと一致する場合に `false`。

## 8.4 ルーティング（NodeRouter）
```csharp
public async Task<Stream> ExecuteFileOperationAsync(int hostId, ...)
{
    var host = await _db.CifsHosts.Include(h => h.ExecutionNode).FirstAsync(h => h.Id == hostId);
    var node = host.ExecutionNode;

    if (!node.IsActive || node.HealthStatus == "Unhealthy")
        throw new NodeUnreachableException(node);

    if (node.NodeType == "Direct")
        return await _cifsService.Execute(...);
    else
        return await _agentForwarder.ForwardAsync(node, ...);
}
```

## 8.5 ストリーミング転送

### ダウンロード（Server → Client）
```csharp
app.MapGet("/api/files/download", async (HttpContext ctx, ...) =>
{
    // 権限チェック省略
    ctx.Response.Headers.ContentDisposition =
        $"attachment; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";

    if (node.NodeType == "Direct")
    {
        await using var stream = cifsService.OpenRead(host, share, path);
        await stream.CopyToAsync(ctx.Response.Body, 4 * 1024 * 1024);
    }
    else
    {
        using var agentRes = await agentForwarder.GetAsync(node, path,
            HttpCompletionOption.ResponseHeadersRead);  // ★必須: メモリに全部載せない
        await agentRes.Content.CopyToAsync(ctx.Response.Body);
    }
});
```

### アップロード（Client → Server）
```csharp
app.MapPost("/api/files/upload", async (HttpContext ctx, ...) =>
{
    // 権限チェック省略
    if (node.NodeType == "Direct")
    {
        await using var smbStream = cifsService.OpenWrite(host, share, path);
        await ctx.Request.Body.CopyToAsync(smbStream, 4 * 1024 * 1024);
    }
    else
    {
        using var content = new StreamContent(ctx.Request.Body, 4 * 1024 * 1024);
        await agentForwarder.PostAsync(node, path, content);
    }
});
```

---

# PART 9: SERVER — 監査ログ

全ファイル操作の前後で記録。ミドルウェアまたはサービス層で実装。

```csharp
await _auditLogService.LogAsync(new AuditLog
{
    Timestamp = DateTime.UtcNow,
    UserId = currentUser.Id,
    Username = currentUser.Username,
    Operation = "READ",
    HostId = hostId,
    ShareId = shareId,
    Path = path,
    TargetPath = null,
    Result = "success",
    ClientIp = ctx.Connection.RemoteIpAddress?.ToString(),
    BytesTransferred = bytesRead,
    DurationMs = stopwatch.ElapsedMilliseconds,
    Protocol = ctx.Request.IsHttps ? "HTTPS" : "HTTP",
    ExecutionNodeId = node.Id,
    UsedPermissionId = permissionId
});
```

---

# PART 10: AGENT

## 10.1 責務
- 中央サーバーからの命令を受け、自網内のCIFSホストへSMB操作を実行
- 操作ログをローカルSQLiteにバッファ → 中央へ定期送信
- 30秒毎にハートビートを中央へ送信

## 10.2 エンドポイント（Agent側、中央から呼ばれる）
```
GET    /agent/files?host=&share=&path=&credUser=&credPass=   → 一覧
GET    /agent/files/download?...                              → DL (Streaming)
POST   /agent/files/upload?...                                → UL (Streaming)
DELETE /agent/files?...                                       → 削除
POST   /agent/files/rename                                    → RN
POST   /agent/files/mkdir                                     → mkdir
POST   /agent/test-connection                                 → 接続テスト
```

## 10.3 CIFS資格情報
- 中央がリクエスト毎にクエリパラメータ or ヘッダで送付（mTLS保護下）
- エージェントはメモリ上で使用、永続化しない

## 10.4 ハートビート (BackgroundService)
```csharp
protected override async Task ExecuteAsync(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        try
        {
            await _httpClient.PostAsJsonAsync($"{_centralUrl}/api/internal/heartbeat",
                new { AgentId = _config.AgentId, Timestamp = DateTime.UtcNow });
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Heartbeat failed"); }
        await Task.Delay(TimeSpan.FromSeconds(30), ct);
    }
}
```

## 10.5 ログ同期 (BackgroundService)
```csharp
// 10秒毎にPendingLogsを中央へPOST、成功したらDELETE
```

## 10.6 過負荷制御
```csharp
private int _currentLoad = 0;
// エンドポイント冒頭で:
if (Interlocked.Increment(ref _currentLoad) > _maxConcurrency)
{
    Interlocked.Decrement(ref _currentLoad);
    ctx.Response.Headers["Retry-After"] = "5";
    return Results.StatusCode(503);
}
try { /* 処理 */ } finally { Interlocked.Decrement(ref _currentLoad); }
```

## 10.7 応答不能時
中央サーバー側:
- HealthStatus が Unhealthy → 要求を即座に 503 でクライアントへ返す
- 操作中のタイムアウト → **即時エラー（リトライなし）**

タイムアウト設定:
- 一覧/メタ操作: 10秒
- 接続確立: 5秒
- 転送中（無通信）: 60秒

---

# PART 11: CLIENT (WPF)

## 11.1 起動フロー
```
App起動
  ├─ 接続設定あり?
  │   ├─ NO → ConnectionSettingsWindow
  │   └─ YES
  │       ├─ HTTPS + デバイストークンあり?
  │       │   ├─ YES → 自動ログイン試行
  │       │   │   ├─ 成功 (mustChangePassword=false) → MainWindow
  │       │   │   ├─ 成功 (mustChangePassword=true)  → ChangePasswordWindow → MainWindow
  │       │   │   └─ 失敗 → LoginWindow
  │       │   └─ NO → LoginWindow
  │       └─ HTTP → LoginWindow（自動ログイン不可）
  └─ LoginWindow
      ├─ ログイン成功 (mustChangePassword=false) → MainWindow
      └─ ログイン成功 (mustChangePassword=true)  → ChangePasswordWindow → MainWindow
```

## 11.2 接続設定画面 (ConnectionSettingsWindow)
```
┌─────────────────────────────────────┐
│ 接続設定                            │
├─────────────────────────────────────┤
│ サーバーURL: [_____________________]│
│ プロトコル:  ◉ HTTPS  ○ HTTP        │
│             ⚠ HTTPは平文通信です    │
│                                     │
│         [接続テスト] [保存]         │
└─────────────────────────────────────┘
```
設定保存先: `%LocalAppData%\CifsTool\settings.json`

## 11.3 ログイン画面 (LoginWindow)
```
┌─────────────────────────────────────┐
│ ログイン                             │
├─────────────────────────────────────┤
│ ユーザー名: [_____________________] │
│ パスワード: [_____________________] │
│ ☑ このPCを記憶する                  │  ← HTTP時はグレーアウト+ツールチップ
│                                     │
│ ⚠ パスワード残り 13日です           │  ← 警告日数内なら表示
│         [ログイン]                  │
│                                     │
│ [接続設定]                          │
└─────────────────────────────────────┘
```

## 11.4 パスワード変更画面 (ChangePasswordWindow)
```
┌─────────────────────────────────────┐
│ パスワード変更                       │
├─────────────────────────────────────┤
│ ⚠ 有効期限切れのため変更が必要です   │
│                                     │
│ 現在のパスワード: [________________]│
│ 新しいパスワード: [________________]│
│ 確認:            [________________]│
│                                     │
│ ※ 12文字以上、英大小+数字+記号      │
│         [変更]                      │
└─────────────────────────────────────┘
```

## 11.5 メイン画面 (MainWindow) — FFFTP風 2ペイン
```
┌─────────────────────────────────────────────────┐
│ ファイル(F) 表示(V) 管理(A)* ヘルプ(H)           │
├─────────────────────────────────────────────────┤
│ [ホスト ▼] [ロケーション ▼]  接続: HTTPS  [👤]  │
├───────────────────────┬─────────────────────────┤
│ ▶ ローカル             │ ▶ リモート (CIFS)       │
│ C:\Users\...\Documents │ documents:/dept-A/2026/ │
├───────────────────────┼─────────────────────────┤
│ 📁 ..                 │ 📁 .. (ルートで無効)    │
│ 📁 archive/           │ 📁 reports/             │
│ 📄 memo.txt     1KB   │ 📄 budget.xlsx  2.3MB  │
│ 📄 photo.jpg   500KB  │ 📄 readme.txt    1KB   │
│                       │                         │
│                       │                         │
├───────────────────────┴─────────────────────────┤
│ [⇨ UP]  [DL ⇦]  [削除]  [F2:RN]  [新規フォルダ]│
├─────────────────────────────────────────────────┤
│ 転送: report.xlsx  ████████░░ 80%  12/15MB      │
└─────────────────────────────────────────────────┘
（*「管理」メニューは IsAdmin=true の場合のみ Visible）
```

### 操作マッピング
| UI操作 | API呼び出し |
|---|---|
| ローカルペインでフォルダ移動 | System.IO（ローカル） |
| リモートペインでフォルダ移動 | `GET /api/files?path=...` |
| ⇨ UP ボタン / D&D (ローカル→リモート) | `POST /api/files/upload` (StreamContent) |
| DL ⇦ ボタン / D&D (リモート→ローカル) | `GET /api/files/download` → SaveFileDialog |
| 削除ボタン / Del キー | 確認ダイアログ → `DELETE /api/files` |
| F2 / RN ボタン | インライン編集 → `POST /api/files/rename` |
| 新規フォルダ | 名前入力ダイアログ → `POST /api/files/mkdir` |

### リモートペインのルート制限
```csharp
// RemotePaneViewModel
public bool CanGoUp => !string.Equals(
    PathHelper.NormalizePath(CurrentPath),
    PathHelper.NormalizePath(CurrentLocation.Path),
    StringComparison.OrdinalIgnoreCase);
```
`..` エントリとパンくずの上階層は `CanGoUp` で制御。

## 11.6 管理者画面 (AdminWindow)
TabControl で以下のタブ:
1. ユーザー管理
2. ホスト管理
3. 共有管理
4. 権限テンプレート
5. ユーザー権限（**パスブラウザ付き**）
6. 信頼デバイス
7. 実行ノード
8. 操作ログ（フィルタ + CSVエクスポート）
9. システム設定

### ユーザー権限タブ
```
┌────────────────────────────────────────────────┐
│ ユーザー: [tanaka ▼]           [+ 許可パス追加] │
├────────────────────────────────────────────────┤
│ 共有        │ パス              │ テンプレート  │
├─────────────┼───────────────────┼──────────────┤
│ documents   │ /dept-A           │ フルアクセス │
│ documents   │ /shared/templates │ 読取のみ     │
│ archives    │ /                 │ 読取のみ     │
│                    [編集] [削除]               │
└────────────────────────────────────────────────┘
```

「+ 許可パス追加」→ ダイアログで共有選択 → `GET /api/admin/browse` でパスブラウズ → テンプレート選択 → 保存。

## 11.7 セッション管理 (SessionManager)
```csharp
public class SessionManager
{
    private string _accessToken;
    private string _refreshTokenId;
    private string _refreshToken;
    private DateTime _accessTokenExpiry;
    private Timer _idleTimer;  // 30分アイドルで自動ログアウト

    // API呼び出し前に毎回
    public async Task<string> GetValidTokenAsync()
    {
        if (DateTime.UtcNow < _accessTokenExpiry - TimeSpan.FromSeconds(30))
            return _accessToken;
        // リフレッシュ
        var res = await _apiClient.RefreshAsync(_refreshTokenId, _refreshToken);
        if (res.MustChangePassword) { /* ChangePasswordWindow表示 */ }
        _accessToken = res.AccessToken;
        _accessTokenExpiry = DateTime.UtcNow.AddSeconds(res.ExpiresIn);
        return _accessToken;
    }

    public void ResetIdleTimer() { _idleTimer.Change(TimeSpan.FromMinutes(30), Timeout.InfiniteTimeSpan); }
    private void OnIdleTimeout() { /* ログアウト → LoginWindow表示 */ }
}
```

## 11.8 Credential Manager (自動ログイン用)
```csharp
// CredentialStore.cs  (CredentialManagement NuGet)
public void SaveDeviceToken(string machineName, string windowsUser, string token)
{
    var cred = new Credential
    {
        Target = "CifsTool/AutoLogin",
        Username = $"{machineName}\\{windowsUser}",
        Password = token,
        Type = CredentialType.Generic,
        PersistanceType = PersistanceType.LocalComputer
    };
    cred.Save();
}

public (string machineName, string windowsUser, string token)? LoadDeviceToken()
{
    var cred = new Credential { Target = "CifsTool/AutoLogin" };
    if (!cred.Load()) return null;
    var parts = cred.Username.Split('\\', 2);
    return (parts[0], parts[1], cred.Password);
}

public void ClearDeviceToken()
{
    var cred = new Credential { Target = "CifsTool/AutoLogin" };
    cred.Delete();
}
```

## 11.9 転送の進捗表示
```csharp
// StreamContent + IProgress<long>
public async Task UploadWithProgressAsync(string localPath, ..., IProgress<long> progress)
{
    await using var fs = File.OpenRead(localPath);
    var totalBytes = fs.Length;
    var content = new ProgressStreamContent(fs, 4 * 1024 * 1024, progress);
    await _httpClient.PostAsync(url, content);
}
```

---

# PART 12: SECURITY

## 12.1 AES-256-GCM（CIFS資格情報の暗号化）
```csharp
// CryptoHelper.cs
public static byte[] Encrypt(string plaintext, byte[] masterKey)
{
    var nonce = RandomNumberGenerator.GetBytes(12);
    var tag = new byte[16];
    var plainBytes = Encoding.UTF8.GetBytes(plaintext);
    var cipher = new byte[plainBytes.Length];
    using var aes = new AesGcm(masterKey, 16);
    aes.Encrypt(nonce, plainBytes, cipher, tag);
    // [nonce(12)] + [tag(16)] + [cipher(N)]
    return nonce.Concat(tag).Concat(cipher).ToArray();
}

public static string Decrypt(byte[] data, byte[] masterKey)
{
    var nonce = data[..12];
    var tag = data[12..28];
    var cipher = data[28..];
    var plain = new byte[cipher.Length];
    using var aes = new AesGcm(masterKey, 16);
    aes.Decrypt(nonce, cipher, tag, plain);
    return Encoding.UTF8.GetString(plain);
}
```
マスターキーは `appsettings.json` の `Encryption:MasterKey` か環境変数 `CIFSTOOL_MASTER_KEY`。

## 12.2 mTLS（中央 ↔ エージェント）
サーバー側:
```csharp
builder.WebHost.ConfigureKestrel(opts =>
{
    opts.ConfigureHttpsDefaults(https =>
    {
        https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
        https.ClientCertificateValidation = (cert, chain, errors) =>
            _trustedThumbprints.Contains(cert.Thumbprint);
    });
});
```

エージェント側:
```csharp
var handler = new HttpClientHandler();
handler.ClientCertificates.Add(new X509Certificate2("agent.pfx", password));
var httpClient = new HttpClient(handler);
```

## 12.3 ディレクトリトラバーサル防止
全ファイル操作で:
1. ユーザー入力パスを `PathHelper.NormalizePath()` で正規化（`..` 展開）
2. `IsPathWithin(allowedPath, normalizedPath)` で許可範囲内か確認
3. 許可範囲外なら 403

---

# PART 13: CONFIGURATION

## 13.1 appsettings.json (Server)
```json
{
  "ConnectionStrings": {
    "Default": "Data Source=C:\\ProgramData\\CifsTool\\cifs_tool.db;Cache=Shared;Foreign Keys=True;"
  },
  "Jwt": {
    "Secret": "YOUR-256-BIT-SECRET-KEY-HERE-CHANGE-THIS",
    "Issuer": "CifsTool",
    "Audience": "CifsTool",
    "AccessTokenMinutes": 15,
    "RefreshTokenDays": 30
  },
  "Encryption": {
    "MasterKey": "BASE64-ENCODED-256-BIT-KEY"
  },
  "Kestrel": {
    "Endpoints": {
      "Http": { "Url": "http://0.0.0.0:8080" },
      "Https": {
        "Url": "https://0.0.0.0:8443",
        "Certificate": {
          "Path": "server.pfx",
          "Password": ""
        }
      }
    }
  },
  "Serilog": {
    "MinimumLevel": "Information",
    "WriteTo": [
      { "Name": "File", "Args": { "path": "logs/server-.log", "rollingInterval": "Day" } }
    ]
  }
}
```

## 13.2 appsettings.json (Agent)
```json
{
  "Agent": {
    "AgentId": "bastion-a",
    "ListenUrl": "https://0.0.0.0:8443",
    "CentralUrl": "https://central.internal:8443",
    "MaxConcurrency": 20
  },
  "ConnectionStrings": {
    "Buffer": "Data Source=C:\\ProgramData\\CifsToolAgent\\agent_buffer.db;Cache=Shared;Foreign Keys=True;"
  },
  "Certificate": {
    "Path": "agent.pfx",
    "Password": ""
  }
}
```

## 13.3 Client設定 (%LocalAppData%\CifsTool\settings.json)
```json
{
  "serverUrl": "https://central.internal:8443",
  "protocol": "HTTPS",
  "lastLocalPath": "C:\\Users\\kyon\\Documents"
}
```

---

# PART 14: CLICKONCE DEPLOYMENT

## 14.1 発行手順
```bash
# Visual Studio 2022 で CifsTool.Client を ClickOnce 発行
# プロジェクトプロパティ → 発行 → ClickOnce
# または CLI:
dotnet publish -p:PublishProfile=ClickOnceProfile
```

## 14.2 配布サーバー (IIS) MIME設定
```
.application → application/x-ms-application
.manifest    → application/x-ms-manifest
.deploy      → application/octet-stream
```

## 14.3 自動更新
- アプリ起動時にバージョンチェック
- 更新あり → ダイアログ通知（"新しいバージョンがあります。再起動後に更新されます"）
- 操作中は強制再起動しない

## 14.4 コード署名
社内CA発行の証明書で署名。クライアントPCに社内CAルートを配布済みであること。

---

# PART 15: AGENT DEPLOYMENT

## 15.1 インストール
MSI（WiX）またはPowerShellスクリプトで:
1. `C:\Program Files\CifsToolAgent\` にファイル配置
2. appsettings.json 編集（CentralUrl, AgentId, 証明書パス）
3. Windows Service として登録:
   ```powershell
   sc.exe create CifsToolAgent binPath="C:\Program Files\CifsToolAgent\CifsTool.Agent.exe" start=auto
   sc.exe start CifsToolAgent
   ```

---

# PART 16: IMPLEMENTATION ORDER

**この順序で実装してください。各フェーズは前フェーズに依存します。**

## Phase 1: 基盤（2週間）
1. `CifsTool.sln` と4プロジェクト作成
2. NuGet パッケージインストール
3. `CifsTool.Shared` — 全モデル、DTO、PathHelper、CryptoHelper
4. `CifsTool.Server` — Program.cs、AppDbContext、PRAGMA設定
5. EF Core Initial Migration + シードデータ
6. `dotnet ef database update` で DB 自動生成確認
7. 認証エンドポイント（login, refresh, logout, change-password）
8. JWT ミドルウェア設定

## Phase 2: 認証拡張（1週間）
1. 自動ログイン（auto-login, trust-device）
2. パスワード有効期限チェック（login / refresh 内）
3. アカウントロック処理
4. リフレッシュ時のDB状態検証

## Phase 3: 権限モデル（1.5週間）
1. PermissionService（サブパスチェック、IsPermissionRoot）
2. 権限テンプレートCRUD
3. ユーザー権限CRUD
4. `/locations` エンドポイント
5. PathHelper のユニットテスト（正規化、包含判定、トラバーサル）

## Phase 4: CIFS操作API — Direct（2週間）
1. CifsService（SMBLibrary で 一覧/DL/UL/削除/RN/mkdir）
2. FileEndpoints（全操作）
3. RENAME の同一親チェック
4. 許可パスルート DELETE/RN ガード
5. ストリーミング DL/UL

## Phase 5: 監査ログ + 管理者API（1.5週間）
1. AuditLogService
2. 全ファイル操作に監査ログ組込
3. AdminEndpoints（ユーザー/ホスト/共有/権限/デバイス/設定/ログ/CSVエクスポート）
4. `/admin/browse`（管理者用パスブラウザ）

## Phase 6: エージェント（2週間）
1. `CifsTool.Agent` — Program.cs、AgentDbContext
2. AgentEndpoints（files操作）
3. HeartbeatService
4. LogSyncService
5. 過負荷制御（MaxConcurrency）

## Phase 7: ルーティング + 中継（1.5週間）
1. NodeRouter（Direct/Agent判定）
2. AgentForwarder（ストリーミング中継、`ResponseHeadersRead`）
3. InternalEndpoints（heartbeat受信、audit-logs/batch受信）
4. HealthStatus管理（Healthy/Unhealthy自動切替）
5. mTLS設定

## Phase 8: WPFクライアント — メイン（3週間）
1. App.xaml.cs 起動フロー
2. ConnectionSettingsWindow
3. LoginWindow（パスワード警告、デバイス記憶チェック）
4. ChangePasswordWindow
5. MainWindow 2ペインレイアウト
6. LocalPaneViewModel（System.IO）
7. RemotePaneViewModel（API経由、ルート制限）
8. アップロード/ダウンロード + 進捗バー
9. 削除/RN/mkdir
10. SessionManager（JWT自動リフレッシュ、アイドルタイムアウト）
11. CredentialStore（自動ログイン）

## Phase 9: WPFクライアント — 管理（3週間）
1. AdminWindow（TabControl）
2. 各管理タブの View + ViewModel
3. UserPermissionView のパスブラウザ
4. AuditLogView のフィルタ + CSVエクスポート
5. SystemSettingsView

## Phase 10: デプロイ（1週間）
1. ClickOnce 発行プロファイル作成
2. コード署名
3. 配布サーバー設定（IIS MIME type）
4. Agent MSI / PowerShell インストーラ
5. 自動更新の動作確認

## Phase 11: テスト（2週間）
1. PathHelper ユニットテスト
2. PermissionService ユニットテスト
3. AuthService ユニットテスト（ロック、期限切れ、自動ログイン）
4. E2E テスト（ログイン → ファイル一覧 → DL/UL → ログ確認）
5. セキュリティテスト（トラバーサル、RENAME-MOVE、権限外アクセス）
6. エージェント障害テスト（停止時の503確認）

**合計: 約19.5週（約5ヶ月）**

---

# APPENDIX: KEY DESIGN DECISIONS SUMMARY

| 決定事項 | 採用内容 |
|---|---|
| アーキテクチャ | デスクトップ(WPF) + 中央サーバー + 分散エージェント |
| 配布 | ClickOnce |
| DB | SQLite (WAL) — 中央・エージェント共通 |
| 認証 | 独自DB (bcrypt + JWT 15min + Refresh 30day) |
| 自動ログイン | 信頼デバイス方式（PC名+Winユーザー+トークン、1台制約） |
| パスワード | 有効期限90日(設定可)、履歴チェックなし |
| 権限粒度 | (共有, サブパス) 単位、テンプレートで操作種別制御 |
| ファイル移動 | 禁止（RENAMEでの親変更も禁止） |
| 許可ルート | DELETE / RENAME 不可 |
| プロトコル | HTTP / HTTPS 選択式（自動ログイン/PW変更はHTTPS強制） |
| ファイルサイズ | 無制限（ストリーミング転送、4MBチャンク） |
| 同時編集 | ロックなし（最終書込勝ち） |
| 監査ログ | 1年保管、自動削除、UsedPermissionId追跡 |
| エージェント通信方向 | 中央→エージェント Push (mTLS) |
| エージェント障害時 | 即時エラー（リトライなし）、503返却 |
| タイムスタンプ | 全てUTC保管、クライアントでローカル変換 |
| CIFS資格情報暗号化 | アプリ層 AES-256-GCM |

---

## 付録: 実装後の差分（v0.2 反映）

初版仕様 (Phase 1〜11) からの主な変更点。詳細は各ドキュメント参照。

### セキュリティ強化
- **`/api/internal/*` (中央↔Agent) と `/agent/*` (Agent inbound) の認証必須化**
  - mTLS: Agent クライアント証明書サムプリントを `ExecutionNode.ClientCertificateThumbprint` と照合 (中央側 `Agent` ポリシー)
  - Agent inbound: 中央証明書 (`Auth:CentralCertificateThumbprint`) または X-Watashi-Secret ヘッダ (`Auth:SharedSecret`) で識別 (`CentralOrSharedSecret` ポリシー)
- **AgentForwarder の認証情報を URL クエリ → POST body / X-Watashi-Cifs ヘッダ に変更**（アクセスログ漏洩を防止）
- **リフレッシュトークンのローテーション + 再利用検知**（旧トークン失効 + 失効後の提示でファミリー失効）
- **ログインのレート制限**（IP 単位 10/分、`Auth:LoginPerMinutePerIp` で変更可）
- **管理者操作の監査ログ**（`ADMIN_USER_*`, `ADMIN_HOST_*`, `ADMIN_SHARE_*`, `ADMIN_TEMPLATE_*`, `ADMIN_PERMISSION_*`, `ADMIN_NODE_*`, `ADMIN_SETTING_UPDATE`）
- **起動時シークレット検証**（`Jwt:Secret` / `Encryption:MasterKey` のプレースホルダ検出、Production で起動拒否）
- **機微フィールドに `[JsonIgnore]`**（`User.PasswordHash`, `RefreshToken.TokenHash`, `TrustedDevice.DeviceTokenHash`, `CifsHost.CredPasswordEnc`）
- **内部例外メッセージは `Results.Problem` でマスク**（クライアントへスタックトレース漏洩なし）

### アーキテクチャ
- **CIFS レイヤを `Watashi.Shared.Cifs` に統合**（Server/Agent の重複コード約 350 行削除）
- **SMB セッションプール `CifsSessionPool`**（操作毎のハンドシェイクコスト削減、TTL 60s, キー単位 LRU 4）
- **`/api/hosts/catalog` 集約 API**（クライアントの N×M HTTP ループを 1 リクエストに圧縮）
- **`FileEndpoints.ExecuteAsync` 共通ヘルパー**（認可 + 監査 + 例外マップを統一）
- **`AdminViewModelBase`**（Admin VM 共通 try/catch + ObservableCollection 一括差し替え）

### パフォーマンス
- **全 read-only クエリに `AsNoTracking`**
- **PermissionService の per-request メモ化**（`HttpContext.Items`）
- **`SmbWriteStream` で `ArrayPool<byte>.Shared` 利用**（LOH 圧迫排除）
- **`PeriodicTimer` + `ExecuteUpdate/ExecuteDelete`**（NodeHealthMonitor, LogSyncService, TrustedDevice 失効）
- **WPF Admin 画面の 9 並列ロード**（`Task.WhenAll`）
- **ローカルペインの I/O を `Task.Run` + `EnumerateXxx`**（UI スレッドフリーズ解消）
- **ApiClient の `DefaultRequestHeaders.Authorization` 競合を per-request ヘッダ化**
- **CSV エクスポートを `AsNoTracking + Select` 射影 + バッチフラッシュ**（OOM 回避）

### Windows サービス化
- Server / Agent ともに `Microsoft.Extensions.Hosting.WindowsServices` 採用
- インストールスクリプト: `deploy/install-server-service.ps1` / `install-agent-service.ps1`
- 異常終了時の自動再起動（5s → 30s → 60s）

### クライアント
- **App.xaml.cs の `GetAwaiter().GetResult()` を排除**（async/await 化、auto-login に 8 秒タイムアウト）
- **ContinueWith を排除**（async/await 化、`AggregateException` ラップ解消）
- **`AdminWindow` の `Loaded` ハンドラ unsubscribe**（多重実行/メモリリーク対策）
- **`MainWindow.Prompt` を `PromptDialog.xaml` に分離**（imperative WPF 構築の排除）
- **`ConnectionSettingsViewModel` で `IHttpClientFactory` 利用**（ソケットリーク対策）
- **`SessionIdleMinutes` をログイン応答経由でクライアントに配信**（ハードコード解除）
- **`FileEntry.Type` を `FileEntryTypes` 定数化**

### 設定追加
- `Auth:LoginPerMinutePerIp` (デフォルト 10)
- `Auth:CentralCertificateThumbprint` (Agent 側)
- `Auth:SharedSecret` (中央↔Agent 双方)
- `Routing:SharedSecret` (中央側、`Auth:SharedSecret` と同値)
- `Cifs:SessionIdleSeconds` (デフォルト 60)
- `Cifs:MaxSessionsPerKey` (デフォルト 4)
