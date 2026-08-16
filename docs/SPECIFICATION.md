# Watashi 機能仕様書

社内 CIFS/SMB ファイルサーバーへ、中央サーバー経由で安全にアクセスするための
Windows デスクトップツール **Watashi** の、全機能を 1 つにまとめた詳細仕様書。

> この 1 文書で全体像が掴めることを目的とする。
> 「何ができるか」の俯瞰は [FEATURES.md](FEATURES.md)、操作手順は [USER-GUIDE.md](USER-GUIDE.md)、
> 構築は [SETUP.md](SETUP.md)、開発は [DEVELOPMENT.md](DEVELOPMENT.md) を参照。

---

## 目次

1. [概要と設計思想](#1-概要と設計思想)
2. [システム構成・アーキテクチャ](#2-システム構成アーキテクチャ)
3. [配布と接続先の固定 (deployment.json / ClickOnce)](#3-配布と接続先の固定-deploymentjson--clickonce)
4. [認証・セッション管理](#4-認証セッション管理)
5. [権限モデル](#5-権限モデル)
6. [ファイル操作](#6-ファイル操作)
7. [クライアント UI](#7-クライアント-ui)
8. [管理機能 (Admin)](#8-管理機能-admin)
9. [ノードルーティングと Agent (踏み台)](#9-ノードルーティングと-agent-踏み台)
10. [API 一覧](#10-api-一覧)
11. [データモデル](#11-データモデル)
12. [セキュリティ](#12-セキュリティ)
13. [設定項目](#13-設定項目)
14. [運用・デプロイ](#14-運用デプロイ)
15. [用語集](#15-用語集)

---

## 1. 概要と設計思想

### 1.1 何をするツールか

Watashi は、社員が自分の PC から社内の CIFS/SMB ファイルサーバー (ファイル共有) へ
**FFFTP 風の 2 ペイン UI** でアクセスし、アップロード・ダウンロード・削除・リネーム・
フォルダ作成を行うためのツール。クライアントは CIFS サーバーへ直接つながず、
必ず**中央サーバー**を経由する。中央サーバーが認証・認可・監査を一手に担う。

```
[社員PC: WPF クライアント]  ──HTTP(S)──>  [中央サーバー]  ──SMB──>  [CIFS ファイルサーバー]
                                              │
                                              └──HTTP(S)──> [Agent (踏み台)] ──SMB──> [隔離網の CIFS]
```

### 1.2 設計の中心思想

| 原則 | 内容 |
|---|---|
| **中央集権** | 認証・認可・監査・暗号化された資格情報の保持はすべて中央サーバー。CIFS の資格情報はクライアントに渡らない |
| **接続先はクライアントで変えさせない** | 接続先サーバーと一部機能の可否は、配布時に管理者が `deployment.json` で固定。利用者は変更不可 (後述 §3) |
| **最小権限** | ユーザーは「(共有, サブパス) 単位」で許可された範囲だけを操作できる。許可範囲外はパス正規化 + 範囲チェックで拒否 |
| **全操作を監査** | ファイル操作も管理操作もすべて監査ログに記録 |
| **多層防御 (defense in depth)** | 機微フィールドは `[JsonIgnore]`、内部例外はマスク、トークンはローテーション + 盗難検知 |
| **末端の到達網に応じた中継** | 中央から直接届かない CIFS は Agent (踏み台) 経由。1 段のゲートウェイチェーンも可能 |

### 1.3 想定利用者

- **エンドユーザー (一般社員)**: WPF クライアントでファイル操作のみ。接続先や設定は意識しない
- **管理者**: 同じクライアントの「管理」タブでユーザー・ホスト・共有・権限・ノード・ログ・設定を管理
- **運用者**: 中央サーバー / Agent を Windows サービスとして構築・保守

---

## 2. システム構成・アーキテクチャ

### 2.1 技術スタック

- **言語/基盤**: .NET 8 / C#
- **クライアント**: WPF (`net8.0-windows`)、MVVM (CommunityToolkit.Mvvm の `[ObservableProperty]` / `[RelayCommand]`)
- **サーバー / Agent**: ASP.NET Core Minimal API、`Microsoft.Extensions.Hosting.WindowsServices` で常駐サービス化
- **DB**: SQLite + EF Core (マイグレーション、WAL モード、`AsNoTracking`、`ExecuteUpdate`/`ExecuteDelete`)
- **ログ**: Serilog (日次ローテーション)
- **CIFS/SMB**: SMBLibrary (SMB2/3)
- **テスト**: xUnit + FluentAssertions

### 2.2 ソリューション構成 (5 プロジェクト)

| プロジェクト | 役割 |
|---|---|
| **Watashi.Client** | WPF デスクトップアプリ。エンドユーザー UI + 管理 UI |
| **Watashi.Server** | 中央サーバー。認証・認可・監査・ルーティング・CIFS アクセスの中枢 |
| **Watashi.Agent** | 踏み台。中央から受けた操作を SMB に変換して実行 |
| **Watashi.Shared** | 3 者で共有するモデル・DTO・定数・ヘルパー・CIFS セッションプール |
| **Watashi.Tests** | xUnit テストプロジェクト |

### 2.3 リクエストの流れ (ファイル一覧の例)

1. クライアントが `GET /api/files?...` に Bearer トークン付きで要求
2. サーバーが JWT を検証 → `PermissionService` で「そのユーザーが対象 (共有, パス) を LIST 可能か」を判定
3. 対象ホストの `ExecutionNode` を見て **Direct (中央が直接 SMB)** か **Agent (踏み台へ HTTP 転送)** かを `NodeRouter` が振分
4. SMB で一覧取得 → クライアントへ JSON 返却
5. 操作前後で `AuditLog` に記録

### 2.4 共通ヘルパー `ExecuteAsync`

サーバーの全ファイル操作は `ExecuteAsync` 共通ヘルパーを通る。
**認可 → コンテキスト構築 → 実行 → 監査ログ → エラーマップ**を統一処理し、
内部例外メッセージは `Results.Problem` でマスクして汎用メッセージのみクライアントへ返す。

---

## 3. 配布と接続先の固定 (deployment.json / ClickOnce)

> **本章は本システムのガバナンスの要。** 「クライアントから接続先を変えられない」ことが設計上の必須要件。

### 3.1 deployment.json (配布時固定設定)

接続先サーバーと一部機能の可否は、**アプリに同梱する読み取り専用の `deployment.json`** で
配布時に管理者が固定する。利用者 (クライアント) からは変更できない。

```json
{
  "serverUrl": "https://watashi.internal",
  "updateManifestUrl": "https://watashi.internal/install/Watashi.Client.application",
  "enableDragDrop": true
}
```

| キー | 意味 |
|---|---|
| `serverUrl` | 接続先の中央サーバー URL。`https`/`http` のスキームで `IsHttps` を判定 |
| `updateManifestUrl` | 起動時に取得する ClickOnce 配置マニフェスト URL。直接 exe 起動時もこの URL で公開バージョンを確認 |
| `enableDragDrop` | ドラッグ&ドロップ機能を有効にするか。管理者が配布時に決定 |

仕様:

- `deployment.json` は exe と同じ場所に置く **Content (CopyToOutputDirectory=PreserveNewest)**
- 起動時に `DeploymentConfig` が読み込み、`AppSettings` の `ServerUrl` / `UpdateManifestUrl` / `EnableDragDrop` を**上書き**する
  (これらのプロパティは `[JsonIgnore]` で、利用者ごとの `settings.json` には保存されない)
- ClickOnce はマニフェストでファイルのハッシュを検証するため、`deployment.json` を後から書き換えると
  起動できない。**接続先を変えるには再発行 (re-publish) が必要**
- `deployment.json` 欠落 / `serverUrl` または `updateManifestUrl` 未設定時は **「配布設定エラー」を表示して終了**。
  接続先を末端で変更させない設計のため、設定画面は出さない (起動の fatal-gate)

### 3.2 利用者ごとの可変設定 settings.json

`%LocalAppData%\Watashi\settings.json` には**接続先を含まない**、利用者ごとの軽微な状態のみ保存する
(例: 最終ローカルパス `LastLocalPath`)。

### 3.3 接続テスト画面 (編集 UI は持たない)

ログイン画面の「接続テスト」は、**固定された接続先に対する疎通確認専用**。
`ConnectionSettingsViewModel` は接続先 URL と D&D 状態を**読み取り専用で表示**し、
`/health` を叩いて結果を出すだけ。接続先を編集する UI は存在しない。

### 3.4 D&D 機能の配布時ゲート

ドラッグ&ドロップは `deployment.json` の `enableDragDrop` でゲートされる。
実装は `MainWindow.DragDrop.cs` に隔離され、`EnableDragDrop` が false なら
イベントハンドラ自体を登録しない (`if (!settings.EnableDragDrop) return;`)。

### 3.5 ClickOnce 配布

- 配布 URL (例: `https://watashi.internal/install/Watashi.Client.application`) からインストール
- 自動アップデート対応。ショートカット起動時は ClickOnce が起動前に確認し、直接 exe 起動時もアプリ本体が `updateManifestUrl` を確認して新しい版があれば ClickOnce 更新を起動する
- 発行ワークフローは [DEVELOPMENT.md](DEVELOPMENT.md) を参照

---

## 4. 認証・セッション管理

### 4.1 ログイン方式

| 方式 | 説明 |
|---|---|
| **手動ログイン** | ユーザー名 + パスワード。パスワード欄は 👁 アイコンで平文/マスク切替 |
| **自動ログイン (デバイス記憶)** | PC 名 + Windows ユーザー名 + デバイストークンで次回から自動。チェックボックスで有効化 |

### 4.2 トークン (JWT)

- **アクセストークン**: JWT (HS256)、有効期限 **15 分**。クライアントが `Authorization: Bearer` で付与
- **リフレッシュトークン**: 有効期限 **30 日**。アクセストークン失効時に自動更新
- access token には `PasswordChangedAt` の ticks を資格情報バージョン (`cv`) として含め、認証時に DB の現在値と照合
- クライアント (`SessionManager`) は token + session generation の snapshot で自動リフレッシュとアイドルタイマーを管理
- 認証済みリクエストにも session generation を記録し、遅れて返った旧セッションの 401 が再ログイン後の状態を消さない
- クレーム: `uid` (ユーザー ID)、`role` (`Admin` / `User`) など

### 4.3 リフレッシュトークンのローテーション + 盗難検知

- リフレッシュ毎に**新トークンを発行 + 旧トークンを即失効**
- **失効済みトークンの再提示を検知すると、そのファミリー全体を失効** (盗難検知)
- リフレッシュ時に DB 状態を都度確認:
  - `User.IsLocked` → 401
  - 紐づく `TrustedDevice.IsRevoked` → 401
  - `User.MustChangePassword` → レスポンスにフラグ付与
- パスワード変更時は既存リフレッシュトークンを無効化
- access token の認証時にも `User.IsLocked` を都度確認し、ロック中は発行済み token も即時拒否
- 管理者フラグ変更時も資格情報バージョンを進め、昇格前/降格前の access・refresh token を即時失効

### 4.4 自動ログイン (信頼デバイス) の詳細

- **HTTP/HTTPS どちらでも利用可**。ただし HTTP は平文のため警告ツールチップを表示し、
  サーバー側 `Auth:AllowHttpForAutoLogin` が `false` なら HTTP 自動ログインを拒否
- **1 ユーザー = 既定3デバイス**（`TrustedDeviceLimit` で1〜20）。同じ端末・Windowsユーザーを
  再登録したときだけ旧トークンを失効し、異なる端末は上限まで併存する
  (`TrustDevice` は serializable トランザクションで原子的に登録/ローテーション)
- デバイストークンは 256bit ランダム → サーバーでは **bcrypt 保存** (`DeviceTokenHash` は `[JsonIgnore]`)、
  平文は **Windows Credential Manager** にユーザー単位で暗号化保存 (他 Windows ユーザーから読めない)
- 管理者の「全デバイス失効」で無効化 (退職者対応など)、失効理由 (`admin_revoked`) と日時を記録
- 利用者は `GET/DELETE /api/auth/devices` で自分の端末だけを一覧・個別失効できる
- `Auth:WindowsAuth:EnableSso=true` では許可ドメインのWindows本人情報から通常JWTを発行。パスワード経路は維持
- 起動時の自動ログインは **8 秒タイムアウト**で UI フリーズを防止

### 4.5 パスワードポリシー

- **12 文字以上**
- 英大文字・英小文字・数字・記号を**各 1 文字以上**
- 履歴チェックなし (運用ポリシーで補完想定)
- 有効期限デフォルト **90 日** (設定変更可)、残り **14 日**以内で警告
- NG 例: `password123`, `Short1!` / OK 例: `Spring2026!Welcome`
- 期限切れ / 管理者リセット時は**パスワード変更画面を強制表示** (`MustChangePassword`)

### 4.6 アカウント保護

- **15 回連続失敗で自動ロック** (`MaxFailedLoginAttempts`、既定 15、管理画面で変更可)。解除は管理者のみ
- ユーザー列挙対策: 存在しないユーザーでもロックせず、エラーメッセージは共通
- **ログインレート制限**: `/api/auth/login` と `/api/auth/auto-login` に
  **IP 単位の固定ウィンドウ (デフォルト 10 回/分、`login-ip`)**。超過で 429

### 4.7 初回パスワード設定 (本人による設定)

管理者が初期パスワードを決めて配布する手順を持たない。ユーザーは**パスワード未設定**
(`IsPasswordSetupPending=true`) で作成され、本人が初回ログイン時に自分で決める。

**本人確認**: 「Windows ログオン名 = 社内 GID = Watashi ユーザー名」という運用ルールを使い、
Windows 統合認証で認証された OS アカウント名と対象ユーザー名を照合する。
サーバーが受け取る自己申告値ではなく、Kerberos/NTLM で認証された ID を使う。

| エンドポイント | 用途 |
|---|---|
| `POST /api/auth/win/prepare-login` | ID から次の画面 (パスワード入力 / 初回設定) を判定 |
| `POST /api/auth/win/initialize-password` | 初回パスワードを確定し、そのままトークンを発行 |
| `GET /api/auth/win/whoami` | 導入時の疎通確認 (同梱 IIS 設定ではオン) |

- 設定値は `Auth:WindowsAuth` 配下。`Mode` は `None` / `IIS` / `Negotiate`。
  同梱 `appsettings.json` は IIS 本番向けに `IIS` を指定し、設定自体が無い場合のコード既定値は `None`
- OS アカウント名は `DOMAIN\GID` / `GID@domain` / `GID` の 3 形態を正規化して照合。
  ドメイン部の扱いは `DomainMatch` (`IgnoreDomain` / `AllowList`) で切り替える
  - `AllowList` はユーザー単位のドメイン紐付けではない。複数の独立ドメインを許可する場合、
    GID は許可ドメイン全体で一意であること。同名 GID が存在し得る環境は管理者発行の初期 PW 経路を使う
- Watashi の `Username` は SQLite `NOCASE` の検索・一意制約で、英字の大文字小文字を区別しない。
  既存 DB に大小文字だけ異なる重複がある場合、移行はデータを選別せず安全に停止する
- **ユーザー列挙対策**: 本人確認できた未設定アカウント以外は、不明な ID も通常アカウントも
  一律 `mode=password` を返す。未設定ユーザーへの通常ログインも、存在しないユーザーと同一の応答
- **未設定ユーザーは bcrypt 照合の手前で遮断する**。照合に任せると本人の試行で
  `FailedLoginCount` が積み上がり、設定前にロックされてしまう
- 確定は `IsPasswordSetupPending` を条件にした 1 文の更新で行い、二重送信・同時実行でも
  成立するのは 1 回だけ
- 受付期限は `PasswordSetupExpiryDays` (既定 0 = 無期限)
- 監査ログ: `PASSWORD_SETUP_REQUESTED` / `PASSWORD_SETUP_IDENTITY_MISMATCH` /
  `PASSWORD_SETUP_SUCCEEDED` / `PASSWORD_SETUP_REJECTED`

**第二経路 (Windows 認証が使えない場合)**: ドメイン非参加端末などでは、管理者が
`POST /api/admin/users/{id}/reset-password` で初期パスワードを発行し、従来どおり
`MustChangePassword=true` で初回ログイン時に変更させる。

### 4.8 アイドルタイムアウト・ログアウト

- アイドルタイムアウトはサーバーの `SessionIdleMinutes` (デフォルト 30 分) 由来。
  ログイン応答で `IdleMinutes` をクライアントへ伝え、無操作で自動ログアウト
- ログアウト前に access token の更新を完了し、ローテーション後の最新 refresh token を失効させる。
  その後 Credential Manager のデバイストークンを削除してログイン画面へ戻る
- アイドル通知は session generation と操作 generation を再照合し、操作再開後や再ログイン後に残った古い timer callback を無視
- 認証済み API / refresh が 401 を返した場合は、メイン画面の表示前後を問わずローカル状態を破棄して再ログインへ戻る
- アプリを開いたままパスワード期限へ到達した場合は、次の refresh で強制変更画面を表示。任意変更画面を開いている最中なら同じ画面を強制モードへ切り替える

---

## 5. 権限モデル

### 5.1 粒度: (共有, サブパス)

権限は **「どの共有の、どのサブパス配下を、どのテンプレートで操作できるか」** という単位。
1 ユーザー × 1 共有 に**複数のサブパス権限**を持てる。

```
ユーザー alice
  └─ 共有「経理部」(Share)
       ├─ /dept-A/2026   … 読取+書込
       └─ /dept-A/shared … 読取のみ
```

### 5.2 権限テンプレート (PermissionTemplate)

操作の集合を名前付きで定義: `CanRead` / `CanWrite` / `CanDelete` / `CanRename` のフラグ。

- 初期シード (新規 DB): **フルアクセス (Id=1)** / 読取+書込 / 読取のみ
- クライアント表示は**権限スコア降順**にソート (フルアクセスが先頭、既存 DB でも)
- 使用中テンプレートは削除不可

### 5.3 権限セット (PermissionBundle)

部署/役割ごとに複数の権限行をひとまとめにしたもの。
セット = (名前, 説明, [(共有, テンプレート, サブパス, 表示名)…])。

- ユーザー権限画面から「セット適用」で一括付与 (重複行はスキップ or 上書き)
- セットを削除しても**適用済みのユーザー権限は残る** (スナップショット展開方式)

### 5.4 操作種別 (Operations)

`READ` / `WRITE` / `DELETE` / `RENAME` / `LIST` / `DOWNLOAD` / `UPLOAD` / `MKDIR`。
管理操作は `ADMIN_*` プレフィックス (全 admin 操作が監査対象)。

### 5.5 制約 (誤操作・隠れ移動の防止)

- **許可パスのルート自体は DELETE / RENAME 不可** (例: `/dept-A` 自体は消せない、中身は可)
- **RENAME は同一親ディレクトリ内に限定** (フォルダを跨いだ隠れ MOVE を禁止)
- 範囲外操作は 403、監査ログに `denied` 記録

### 5.6 ディレクトリトラバーサル防御 (2 段)

1. ユーザー入力パスを `PathHelper.NormalizePath()` で正規化 (`..` 展開)
2. `IsPathWithin(allowedPath, normalizedPath)` で許可範囲内か確認
3. 範囲外なら 403

`PermissionService` の判定結果は **per-request メモ化** (`HttpContext.Items`)。
同一リクエスト内で何度 `CanPerformAsync` を呼んでも DB アクセスは 1 回。

---

## 6. ファイル操作

### 6.1 対応操作

| 操作 | API | 補足 |
|---|---|---|
| 一覧 | `GET /api/files/incremental` | 200件ずつの安定snapshot cursor + ソート |
| 横断検索 | `POST /api/files/search` | 全許可ルート、取消/走査件数/時間/結果上限付き |
| ダウンロード | `GET /api/files/download` | ストリーミング |
| アップロード | `POST /api/files/upload` | ストリーミング |
| 削除 | `DELETE /api/files` | 管理ごみ箱へ移動、保管期間内は復元可能 |
| リネーム | `POST /api/files/rename` | 同一親限定 |
| フォルダ作成 | `POST /api/files/mkdir` | |
| コピー | `POST /api/files/copy` | コピー元を保持。フォルダー間移動は非搭載 |

### 6.2 再開可能な転送

- v1ストリーミングAPIとの互換を維持しつつ、クライアント転送はv2 upload session/rangeを使用
- 最大8MiB chunk、chunk SHA-256、全体SHA-256、ETag、offset、冪等keyで通信断/再起動後に再開
- 永続キューは同一コピー先を直列化し、一時障害だけを指数バックオフで自動再試行
- Direct / Agent / Gateway の全経路で同じ整合性検証を行う

### 6.3 ダウンロードの完全性 (.part 方式)

ダウンロードはジョブ固有の一時ファイルに書き、全体SHA-256検証後に正式名へ原子的に切り替える。
一時停止/通信断では確定済みoffsetを保持する。ジョブ削除時は一時ファイルを回収する。

### 6.4 ローカル I/O の非ブロッキング

ローカルペインは `EnumerateDirectories/Files` で増分ブラウズし、
全ローカル I/O を `Task.Run` でバックグラウンド実行 (UI スレッドをブロックしない)。

---

## 7. クライアント UI

### 7.1 画面構成

```
ログイン → (初回/期限切れなら) パスワード変更 → メイン画面 (2 ペイン)
                                                   └─ 管理者は「管理」ウィンドウ
```

### 7.2 メイン画面 (FFFTP 風 2 ペイン)

- **ヘッダー**: 鳥居ロゴ + Watashi、右上にユーザー名・プロトコル (HTTP/HTTPS)・管理ボタン (管理者のみ)・更新・ログアウト・情報
- **左ペイン (ローカル) 💻**: PC のフォルダ。📂 ボタンで Windows のフォルダ選択ダイアログ
- **右ペイン (リモート) ⛩**: 中央サーバー経由の CIFS 共有。「場所」ドロップダウンで切替
- **ステータスバー**: 接続プロトコル ●、直近の操作/エラー (時刻 + 発生元)、ログイン中ユーザー、転送進捗 %

ViewModel 構成: `MainViewModel` (統括) + `LocalPaneViewModel` / `RemotePaneViewModel` / `TransferViewModel`。

### 7.3 操作・ショートカット

| 操作 | キー/UI |
|---|---|
| フォルダ展開 | ダブルクリック |
| 親フォルダへ | `Backspace` / `..` (権限ルートではグレー) |
| 戻る/進む/上へ | `Alt + ←` / `→` / `↑` |
| 全体更新 (両ペイン) | `F5` |
| 削除 | 削除ボタン / `Delete` (確認ダイアログ付き) |
| リネーム | `F2` / 右クリック (同一フォルダ内のみ、`/` `\` 不可) |
| 新規フォルダ | ツールバー / 右クリック (`PromptDialog`) |
| パス直接移動 | パス欄に入力 + `Enter` |

### 7.4 右クリックメニュー

開く/展開、アップロード/ダウンロード、リネーム (F2)、削除 (Del)、新規フォルダ、
エクスプローラで表示 (ローカルのみ)、パスをコピー。

### 7.5 ドラッグ&ドロップ (配布時に有効化されている場合のみ)

- ローカル一覧 → リモート一覧: アップロード
- リモート一覧 → ローカル一覧: ダウンロード
- エクスプローラ等の外部 → リモート一覧: アップロード
- `enableDragDrop=false` の配布では機能自体が無効

### 7.6 UI のきめ細かさ

- 朱色 (鳥居) アクセントのモダンテーマ、Card レイアウト、ホバーフィードバック
- 長いファイル名/パスはツールチップで全文表示 (`CharacterEllipsis`)
- 空状態 (ユーザー 0 件 / 付与パス 0 件 / リモート場所無し / フィルタ無一致) に案内文
- ListBox/ListView 仮想化で大量データでも軽量
- ペインごとのフィルタ/検索ボックス、マルチセレクト一括転送、転送キャンセル + 二重起動防止、転送速度/ETA + エラーバナー

---

## 8. 管理機能 (Admin)

`IsAdmin=true` のユーザーがログインするとメニューに「管理」が出る。
管理ウィンドウは TabControl 構成。**全 admin 操作は監査ログ (`ADMIN_*`) に記録される。**

### 8.1 ユーザー
一覧 / 追加 / 削除 / 管理者フラグ変更 / ロック解除 / 初期 PW 発行 / 初回設定に戻す。
**新規ユーザーはパスワードを持たず、`IsPasswordSetupPending=true` で作成される** (§4.7)。
管理者リセット (`初期PW発行`) の場合のみ `MustChangePassword=true`。
**CSV インポート** (`Username,IsAdmin`、新規のみ/上書きの 2 モード、行単位エラー表示。上書きは `IsAdmin` のみ更新しパスワードには触れない) と
**CSV エクスポート** (棚卸し用、BOM 付き UTF-8、`PasswordStatus` 列付き)。

### 8.2 ホスト (CIFS ファイルサーバー)
表示名 / ホスト名・IP / ポート (デフォルト 445) / CIFS 資格情報 / 実行ノード。
接続テスト (登録済み共有で SMB セッション確立を試行)。
資格情報は **AES-256-GCM** で暗号化保存、`CredPasswordEnc` は `[JsonIgnore]`。

### 8.3 共有
ホストに紐づく SMB 共有名 + 表示名。同一ホストで `ShareName` ユニーク制約。

### 8.4 テンプレート (権限テンプレート)
名前 + READ/WRITE/DELETE/RENAME のチェックボックス。
表示は権限スコア降順。使用中は削除不可。

### 8.5 ユーザー権限 (パスブラウザ付き)
- 左サイドバー: ユーザー一覧 (絞り込み + 件数バッジ、仮想化)
- 右パネル: 付与済みパス + 「＋ パスを追加」
- サーバー → 共有のドロップダウン階層、テンプレートは強い順 (デフォルト フルアクセス)
- 実ディレクトリツリーから許可パスを選択
- **📦 セットから一括適用** / **📋 他ユーザーから全件コピー** (重複はスキップ or 上書き)

### 8.6 権限セット (PermissionBundle)
部署/役割単位で権限行をまとめて登録 → ユーザーへ一括適用 (§5.3)。

### 8.7 信頼デバイス
ユーザーごとの複数自動ログインデバイス一覧 / 「全デバイス失効」(`ExecuteUpdateAsync` で原子的)。
利用者自身にも自分の端末だけを一覧・個別失効する画面を提供する。

### 8.8 実行ノード
一覧 / 追加 / 削除 / 共有秘密の再生成 / 状態確認。
`NodeType` = `Direct` (中央自身) / `Agent` (踏み台)。`MaxConcurrency` (デフォルト 20)。
削除前にホストでの使用チェック。mTLS モードでは `ClientCertificateThumbprint` で識別。

### 8.9 操作ログ
フィルタ (ユーザー/カテゴリ/操作/結果/ホスト/共有/パス/端末/IP/期間)、1 ページ 100 件降順、
先頭/前/次/末尾と総件数を表示。CSVは画面と同じ条件でストリーミング出力する。
ファイル操作 (`READ/WRITE/DELETE/RENAME`) と管理操作 (`ADMIN_*`) の両方。

### 8.10 システム設定
`PasswordExpiryDays` (90) / `PasswordWarningDays` (14) / `AgentMaxConcurrency` (20) /
`SessionIdleMinutes` (30) / `AuditLogRetentionDays` (365) / `MaxFailedLoginAttempts` (15)。

---

## 9. ノードルーティングと Agent (踏み台)

### 9.1 ルーティング (Direct ⇔ Agent)

ホストに紐づく `ExecutionNode` で振分 (`NodeRouter`):

- **`Direct` ノード**: 中央サーバーから直接 SMB
- **`Agent` ノード**: Agent へ HTTP(S) フォワード、Agent が SMB
- **`GatewayNodeId` 付き Agent**: `Server → Gateway Agent → Target Agent → SMB` の **1 段チェーン**
- 認証情報は **POST body または `X-Watashi-Cifs` ヘッダ (Base64 JSON)** で送信 (URL クエリ漏洩回避)
- ストリーミングをリレーで実現 (全段でメモリ展開しない)
- Direct/Gateway の Agent が Unhealthy なら**即 503** (リトライなし)。`EnsureRouteReachable(GatewayNode ?? node)`

### 9.2 Agent (踏み台) の機能

- 中央から HTTP(S) で受けた操作を SMB に変換して実行 (`/agent/files/*`)
- 認証情報はリクエスト毎に JSON body / `X-Watashi-Cifs` ヘッダで受領 (メモリ上のみ、永続化しない)
- 過負荷制御: `MaxConcurrency` (デフォルト 20) 超過で **503 + `Retry-After: 5`** (Interlocked で監視)

### 9.3 Agent の inbound 認証

- **mTLS モード**: 中央のクライアント証明書サムプリントを `Auth:CentralCertificateThumbprint` と照合
- **共有秘密モード**: `X-Watashi-Secret` を `Auth:SharedSecret` と一致確認
- 両方未設定なら全アクセス拒否 (401)

### 9.4 ハートビート

- Agent → 中央 `POST /api/internal/heartbeat` を **30 秒毎** (`PeriodicTimer`)。mTLS 有効時は証明書提示
- 中央は受信して `LastHeartbeatAt` を更新、応答 `HeartbeatAck` で `MaxConcurrency` を返す

### 9.5 ヘルスモニタ (中央側 `NodeHealthMonitor`)

- **15 秒毎**に Agent の `LastHeartbeatAt` をチェック
- **90 秒以上**音信不通 → `Unhealthy`、復活 → `Healthy`
- Gateway 配下で直接 heartbeat できない Agent は `Unknown` のまま、操作時の HTTP 到達性で判定
- 状態変化のあったノードだけ `ExecuteUpdate` (no-op SaveChanges を排除)。Direct は常に `Healthy`

### 9.6 Agent のログバッファ (PendingLogs)

中央が一時的に到達不能でも操作を継続できるよう、Agent のローカル SQLite にバッファ:

- **10 秒毎**に中央 `POST /api/internal/audit-logs/batch` へ送信を試行
- central はレコード単位のaccepted/rejectedを返し、Agentはacceptedだけを削除
- 通信障害ではレコードを破棄せず**指数バックオフ (最大 5 分)**。形式不正のrejectedだけをdead-letterとして保持
- `AuditLog.EventId` を決定的に補完し、応答喪失後の再送を一意制約で冪等化

### 9.7 SMB セッションプール (`Watashi.Shared.Cifs.CifsSessionPool`)

- `(host, port, user, share)` キーの再利用プール
- idle TTL (デフォルト 60 秒) + キー単位上限 (デフォルト 4)
- TCP 接続 + SMB negotiate + Login + TreeConnect のコストを削減
- バックグラウンド evict タイマーで idle セッションを自動破棄

---

## 10. API 一覧

ベース: 中央サーバー。`[認証]` 列は要求される認可。`Bearer` = JWT アクセストークン必須、
`Admin` = 管理者ポリシー、`Agent` = Agent ポリシー (mTLS/共有秘密)、`-` = 認証不要。

### 10.1 認証 `/api/auth`

| Method | Path | 用途 | 認証 |
|---|---|---|---|
| POST | `/api/auth/login` | 手動ログイン | - (レート制限 `login-ip`) |
| POST | `/api/auth/auto-login` | 自動ログイン | - (レート制限 `login-ip`) |
| POST | `/api/auth/trust-device` | 信頼デバイス登録 | Bearer |
| GET/DELETE | `/api/auth/devices[/{id}]` | 自分の信頼端末一覧 / 個別失効 | Bearer |
| POST | `/api/auth/win/sso` | 任意のWindows SSO | Windows認証 |
| POST | `/api/auth/refresh` | トークン更新 (ローテーション) | - (リフレッシュトークン) |
| POST | `/api/auth/logout` | ログアウト (トークン失効) | Bearer |
| POST | `/api/auth/change-password` | パスワード変更 | Bearer |

ログイン応答 (`LoginResponse`): `AccessToken` / `RefreshToken` / `RefreshTokenId` /
`ExpiresIn` / `MustChangePassword` / `PasswordExpiresInDays` / `IdleMinutes`。

### 10.2 ファイル `/api/files` (Bearer)

| Method | Path | 用途 |
|---|---|---|
| GET | `/api/files` | 一覧 (200 件/ページ + ソート) |
| GET | `/api/files/download` | ダウンロード (ストリーミング) |
| POST | `/api/files/upload` | アップロード (ストリーミング) |
| DELETE | `/api/files` | 削除 |
| POST | `/api/files/rename` | リネーム (同一親限定) |
| POST | `/api/files/mkdir` | フォルダ作成 |
| POST | `/api/files/copy` | リモートコピー（移動はしない） |
| GET | `/api/files/incremental` | cursor型増分一覧 |
| POST | `/api/files/search` | 権限内横断検索 |

### 10.3 ホスト `/api/hosts` (Bearer)

| Method | Path | 用途 |
|---|---|---|
| GET | `/api/hosts` | アクセス可能なホスト一覧 |
| GET | `/api/hosts/{hostId}/shares` | 共有一覧 |
| GET | `/api/hosts/{hostId}/shares/{shareId}/locations` | 付与パス (場所) 一覧 |
| GET | `/api/hosts/catalog` | host/share/location を **1 リクエストで集約** (N×M HTTP 解消) |

### 10.4 管理 `/api/admin/*` (Admin)

| グループ | 主なエンドポイント |
|---|---|
| ユーザー `/users` | `GET /`・`POST /`・`PATCH /{id}`・`DELETE /{id}`・`POST /{id}/unlock`・`POST /{id}/reset-password`・`GET /{id}/devices`・`DELETE /{id}/devices`・`GET /export.csv`・`POST /import.csv` |
| ホスト `/hosts` | `GET /`・`POST /`・`PATCH /{id}`・`DELETE /{id}`・`POST /{id}/test` |
| 共有 `/shares` | `GET /?hostId`・`POST /`・`PATCH /{id}`・`DELETE /{id}` |
| テンプレート `/permission-templates` | `GET /`・`POST /`・`PATCH /{id}`・`DELETE /{id}` |
| ユーザー権限 `/user-permissions` | `GET /?userId&shareId`・`POST /`・`DELETE /{id}`・`POST /copy` |
| 権限セット `/permission-bundles` | `GET /`・`GET /{id}`・`POST /`・`PATCH /{id}`・`DELETE /{id}`・`POST /{id}/apply` |
| 実行ノード `/nodes` | `GET /`・`POST /`・`PATCH /{id}`・`DELETE /{id}`・`POST /{id}/regenerate-key`・`GET /{id}/status` |
| システム設定 `/settings` | `GET /`・`GET /{key}`・`PUT /{key}` |
| 操作ログ `/logs` | `GET /`・`GET /export.csv` |
| パスブラウザ `/browse` | `GET /api/admin/browse` |

### 10.5 内部 (Agent ↔ 中央) `/api/internal` (Agent)

| Method | Path | 用途 |
|---|---|---|
| POST | `/api/internal/heartbeat` | Agent → 中央。応答 `HeartbeatAck{MaxConcurrency}` |
| POST | `/api/internal/audit-logs/batch` | バッファした監査ログの一括送信 |

### 10.6 Agent `/agent` (inbound: mTLS/共有秘密)

| Method | Path | 用途 |
|---|---|---|
| POST | `/agent/files/list` | 一覧 |
| POST | `/agent/files/download` | ダウンロード |
| POST | `/agent/files/upload` | アップロード (`X-Watashi-Cifs` ヘッダ対応) |
| POST | `/agent/files/delete` | 削除 |
| POST | `/agent/files/rename` | リネーム |
| POST | `/agent/files/mkdir` | フォルダ作成 |
| POST | `/agent/test-connection` | 接続テスト |
| POST | `/agent/internal/buffer-log` | ログバッファ投入 |
| GET | `/health` | ヘルスチェック |

### 10.7 ヘルス

| Method | Path | 用途 |
|---|---|---|
| GET | `/health` | 中央/Agent の死活確認 (接続テストで使用) |

---

## 11. データモデル

主要エンティティ (SQLite + EF Core)。`[JsonIgnore]` 付きフィールドは API レスポンスから自動除外。

### User
`Id` / `Username` / `PasswordHash` `[JsonIgnore]` / `IsAdmin` / `IsLocked` /
`FailedLoginAttempts` / `MustChangePassword` / `PasswordExpiresAt` / `LastLoginAt` / `CreatedAt` /
`IsPasswordSetupPending` / `PasswordSetupExpiresAt` / `WindowsAccountName`

`PasswordHash` は NOT NULL を維持する。初回設定待ちのユーザーには誰も知り得ないランダム値から
作った bcrypt ハッシュ (使用不能ハッシュ) が入る。NULL 化しないのは、SQLite で NOT NULL 制約を
外す migration がテーブル再構築を伴い、`Users` を Cascade 参照する `UserPermissions` /
`RefreshTokens` / `TrustedDevices` を巻き添えで削除する危険があるため。
判定漏れがあっても照合が必ず false になる fail-closed な構造にもなっている。

### RefreshToken
`Id` / `UserId` / `TokenHash` `[JsonIgnore]` / `ExpiresAt` / `IsRevoked` / `RevokedAt` /
`ReplacedByTokenId` / ファミリー識別子 (ローテーション・盗難検知用) / `CreatedAt`

### TrustedDevice
`Id` / `UserId` / `DeviceTokenHash` `[JsonIgnore]` / `MachineName` / `WindowsUser` /
`IsRevoked` / `RevokedAt` / `RevokeReason` (例 `admin_revoked`) / `CreatedAt` / `LastUsedAt`

### CifsHost
`Id` / `DisplayName` / `HostName` / `Port` (445) / `CredUsername` /
`CredPasswordEnc` `[JsonIgnore]` (AES-256-GCM) / `ExecutionNodeId` / `CreatedAt`

### CifsShare
`Id` / `HostId` / `ShareName` (ホスト内ユニーク) / `DisplayName`

### PermissionTemplate
`Id` / `Name` / `CanRead` / `CanWrite` / `CanDelete` / `CanRename`

### UserPermission
`Id` / `UserId` / `ShareId` / `SubPath` / `PermissionTemplateId` / `DisplayName`

### PermissionBundle / PermissionBundleItem
Bundle: `Id` / `Name` / `Description` / `Items[]`
Item: `Id` / `BundleId` / `ShareId` / `PermissionTemplateId` / `SubPath` / `DisplayName`

### ExecutionNode
`Id` / `Name` / `NodeType` (`Direct`/`Agent`) / `BaseUrl` / 共有秘密 /
`ClientCertificateThumbprint` (mTLS) / `GatewayNodeId` / `MaxConcurrency` /
`HealthStatus` / `LastHeartbeatAt` / `CreatedAt`

### AuditLog
`Id` / `Timestamp` / `UserId` / `Username` / `Operation` / `Host` / `Share` / `Path` /
`TargetPath` / `Result` (`success`/`failure`/`denied`) / `ErrorMessage` / `ClientIp` /
`Bytes` / `DurationMs` / `Protocol` (HTTP/HTTPS) / `ExecutionNodeId` / `UsedPermissionId`
索引: Timestamp 降順 / UserId / HostId

### SystemSetting
`Key` / `Value` (5 キー、§13.3)

### PendingLog (Agent ローカル)
`Id` / バッファした監査ログ内容 / `AttemptCount`（centralが形式不正として拒否した回数。>=50でdead-letter、削除しない）

---

## 12. セキュリティ

### 12.1 通信経路

| 経路 | 保護 |
|---|---|
| Client ↔ Server | HTTP / HTTPS (自動ログインは HTTPS 推奨、`AllowHttpForAutoLogin` で制御) |
| Server ↔ Agent | HTTP / HTTPS + **mTLS (双方向証明書認証)** または共有秘密 |
| Server / Agent → CIFS | SMB2/3 (SMBLibrary) |

### 12.2 データ保護

- **CIFS 資格情報**: AES-256-GCM 暗号化、マスターキーは `appsettings.json` か環境変数
- **JWT 署名**: HMAC-SHA256
- **パスワード**: bcrypt (work factor 11 デフォルト)
- **デバイストークン**: 256bit ランダム → bcrypt 保存、平文は Windows Credential Manager に暗号化保存
- 機微フィールド (`PasswordHash` / `TokenHash` / `DeviceTokenHash` / `CredPasswordEnc`) は
  すべて `[JsonIgnore]` (エンティティを誤って直接返しても secret は流出しない)

### 12.3 起動時シークレット検証

- `Jwt:Secret` / `Encryption:MasterKey` のプレースホルダ (`CHANGE-ME...` / `REPLACE-WITH...`) を検出
- **Production では起動拒否**、Dev/Stg では警告 (`ValidateProductionSecret`)

### 12.4 攻撃シナリオ別対策

| 攻撃 | 対策 |
|---|---|
| クレデンシャル総当たり | bcrypt + 15 回ロック (既定、管理画面で変更可) + IP レート制限 (10/分) |
| ユーザー列挙 | 存在しないユーザーでもロックしない、レート制限は IP 単位 |
| リフレッシュトークン盗難 | ローテーション + 再利用検知でファミリー失効 |
| Agent なりすまし | mTLS でサーバー証明書を Thumbprint レベルで確認 |
| 中央サーバなりすまし | mTLS or 共有秘密で Agent inbound を保護 |
| ディレクトリトラバーサル | 正規化 + `IsPathWithin` の 2 段チェック |
| 平文ログ漏洩 | URL に資格情報を載せない (POST body / ヘッダのみ) |
| 監査ログ偽造 | `/api/internal/*` を mTLS/共有秘密で保護、mTLS 時は AgentId と証明書を相互照合 |
| エンティティ直接シリアライズ | 機微フィールドに `[JsonIgnore]` |
| 内部スタックトレース漏洩 | 例外を `Results.Problem` でマスク |
| 接続先の改ざん | `deployment.json` を ClickOnce がハッシュ検証、末端では編集 UI を持たない |

---

## 13. 設定項目

### 13.1 クライアント (deployment.json — 配布時固定)

| キー | デフォルト/例 | 説明 |
|---|---|---|
| `serverUrl` | `https://watashi.internal` | 接続先中央サーバー (変更には再発行) |
| `updateManifestUrl` | `https://watashi.internal/install/Watashi.Client.application` | 起動時に確認する ClickOnce 配置マニフェスト |
| `enableDragDrop` | `true` | D&D 機能の有効化 |

### 13.2 サーバー (appsettings.json)

| セクション | キー | 説明 |
|---|---|---|
| ConnectionStrings | `Default` | SQLite 接続文字列 |
| Jwt | `Secret` / `Issuer` / `Audience` | JWT 署名鍵 (本番でプレースホルダ拒否) |
| Encryption | `MasterKey` | AES-256-GCM マスターキー (本番でプレースホルダ拒否) |
| Auth | `AllowHttpForAutoLogin` | HTTP 自動ログイン許可の最終判定 |
| Auth | `LoginRateLimit` | ログインレート制限 (デフォルト 10/分) |
| Routing | `UseMtls` | Server↔Agent の mTLS 有効化 |
| Cifs | (プール設定) | セッションプール TTL/上限 |
| Kestrel | (`:8080`) | 待受ポート |
| Serilog | (ログ設定) | 日次ローテーション |

### 13.3 システム設定 (DB / 管理画面で変更可)

| キー | デフォルト | 説明 |
|---|---|---|
| `PasswordExpiryDays` | 90 | パスワード有効期限 (日) |
| `PasswordWarningDays` | 14 | 期限警告開始 (日) |
| `AgentMaxConcurrency` | 20 | Agent 同時接続上限 |
| `SessionIdleMinutes` | 30 | アイドルタイムアウト (分) |
| `AuditLogRetentionDays` | 365 | 監査ログ保持日数 (0 以下で永久保管) |
| `MaxFailedLoginAttempts` | 15 | 連続ログイン失敗で自動ロックするまでの回数 |

### 13.4 Agent (appsettings.json)

`Central:BaseUrl` (中央 URL) / `Auth:SharedSecret` / `Auth:CentralCertificateThumbprint` /
`Concurrency:Max` (デフォルト 20) など。

### 13.5 初期シードデータ (新規 DB)

- システム設定: 90 / 14 / 20 / 30 / 365
- テンプレート: フルアクセス (Id=1) / 読取+書込 / 読取のみ
- 実行ノード: `Direct (Local)`
- 管理者は自動シードしない。サーバー端末で `--bootstrap-admin` を明示実行したときだけ、
  CSPRNG で生成した一時パスワードを標準出力へ一度表示し、`MustChangePassword=true` の管理者を作る
- 未使用 (`LastLoginAt=null`) の bootstrap 管理者は、表示を失った場合に限り再実行で旧値を無効化して再発行できる。
  初回ログイン後または利用可能な管理者が既に存在する場合は拒否する

---

## 14. 運用・デプロイ

### 14.1 Windows サービス

- Server / Agent ともに `WindowsServices` で常駐サービス化
- 登録: `deploy/install-server-service.ps1` / `install-agent-service.ps1`、解除: `deploy/uninstall-service.ps1`
- 異常終了時の自動再起動: 5 秒 → 30 秒 → 60 秒

### 14.2 DB

- 初回起動時に `MigrateAsync()` で自動生成 + 非機密データをシード (`DataSeeder`)
- WAL モード + 推奨 PRAGMA (`busy_timeout`, `cache_size`, `foreign_keys=ON`)
- バックアップ: `SqliteConnection.BackupDatabase()`、日次 7 世代 + 月次 4 世代

### 14.3 ログ・保守

- 監査ログ: `AuditLogPurgeService` (BackgroundService) が起動 30 秒後 + 24 時間毎に
  `AuditLogRetentionDays` より古い行を `ExecuteDeleteAsync` で削除
- Serilog 日次ローテーション、`*.log` は `.gitignore` で除外
- DB は月次 VACUUM

### 14.4 ホスティング

IIS リバースプロキシ構成は [deploy/IIS-HOSTING.md](../deploy/IIS-HOSTING.md) を参照。

---

## 15. 用語集

| 用語 | 意味 |
|---|---|
| **中央サーバー (Server)** | 認証・認可・監査・ルーティングの中枢。クライアントの唯一の接続先 |
| **Agent (踏み台)** | 中央から届かない CIFS へ中継する中間ノード |
| **Direct ノード** | 中央サーバー自身が直接 SMB する実行ノード |
| **共有 (Share)** | CIFS/SMB の共有。権限の基本単位 |
| **サブパス (SubPath)** | 共有内の許可されたディレクトリ。権限のもう 1 つの単位 |
| **テンプレート** | READ/WRITE/DELETE/RENAME の許可集合 |
| **権限セット (Bundle)** | 複数の権限行をまとめた配布単位 |
| **信頼デバイス** | 自動ログインを許可された PC (既定1ユーザー3台、設定1〜20台) |
| **deployment.json** | 配布時に管理者が固定する接続先・機能設定 (利用者変更不可) |
| **ClickOnce** | クライアントの配布・自動更新の仕組み |
| **mTLS** | Server↔Agent 間の双方向 TLS クライアント証明書認証 |
