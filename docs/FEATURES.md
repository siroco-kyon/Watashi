# Watashi 機能カタログ

「何ができるか」をひとまとめにした機能一覧。
それぞれの設定方法・操作方法は [SETUP.md](SETUP.md) / [USER-GUIDE.md](USER-GUIDE.md) / [ADMIN-GUIDE.md](ADMIN-GUIDE.md) を参照。

---

## エンドユーザー機能 (WPF クライアント)

### UI / デザイン
- **モダンテーマ**: 朱色 (鳥居) アクセント、Card レイアウト、丸み・余白・ホバーフィードバック
- **鳥居 (Torii) アイコン**: ベクター (XAML Path) で実装、ウィンドウアイコン・ヘッダー・ログイン画面・リモート空状態の視覚マーク
- **ツールチップ**: 長いファイル名/パス/ホスト名は `TextTrimming="CharacterEllipsis"` + ホバーで全文表示
- **空状態のヒント**: ユーザー 0 件・付与パス 0 件・リモート場所無し・フィルタ無一致 すべてに親切な案内文
- **ListBox/ListView 仮想化** で大量ユーザー時のスクロール軽量化

### ファイル操作 (FFFTP 風 2 ペイン)
- **ローカルペイン**: PC のフォルダを `EnumerateDirectories/Files` で増分ブラウズ（大量ファイルでも UI が固まらない）
- **リモートペイン**: 中央サーバー越しに CIFS 共有をブラウズ
- 一覧ソート (名前 / 日付 / サイズ、昇順/降順)
- 1 ページ 200 件、ページング対応
- 親フォルダへ移動 (`..`) — 権限ルートでは `..` がグレー
- ローカル I/O は全て `Task.Run` でバックグラウンド実行（UI スレッドブロックなし）

### 転送
- **アップロード** (ローカル → リモート): ストリーミング 4 MB チャンク、進捗バー
- **ダウンロード** (リモート → ローカル): ストリーミング、SaveFileDialog で保存先指定
- ファイルサイズ無制限
- 転送中の進捗 % とファイル名がステータスバーに表示
- `ArrayPool` 採用により、大容量転送でも LOH (Large Object Heap) アロケーションを排除

### その他のファイル操作
| UI | 動作 |
|---|---|
| 削除ボタン | リモート/ローカル両ペインで削除 (確認ダイアログ「本当に削除しますか？」付き) |
| Delete キー | リスト上で押すと削除 (確認ダイアログ付き) |
| F2 / 新規フォルダ | リモート/ローカル ともに PromptDialog でフォルダ名を入力 |
| F5 | 全体更新 (両ペイン同時) |
| Backspace (リスト) | 親フォルダへ |
| Alt + ← / → / ↑ | 戻る / 進む / 上へ |
| ダブルクリック | フォルダ展開、ファイルは未操作 |
| Enter キー (パス欄) | パス直接入力で移動 |

### 認証
- **手動ログイン**: ユーザー名 / パスワード
  - **パスワード可視化トグル (👁 目玉アイコン)** — 入力欄右端のボタンで PasswordBox (マスク) と TextBox (平文) を一発切替。打ち間違い確認に便利
- **自動ログイン (デバイス記憶)**: PC 名 + Windows ユーザー名 + デバイストークン
  - **HTTP/HTTPS どちらでも利用可** (HTTP は平文なので警告ツールチップ付き、サーバー側 `Auth:AllowHttpForAutoLogin` で最終判定)
  - チェックボックスで有効化、Credential Manager に保存
  - 1 ユーザー = 1 デバイスのみ (新規登録で旧トークン失効)
- **パスワード変更**: 期限切れ/初回ログインで強制画面、12 字 + 大小数記号ポリシーヒント表示。3 つの入力欄 (現在 / 新規 / 確認) すべてに目玉アイコン
- **アイドルタイムアウト**: サーバ `SystemSettings.SessionIdleMinutes` 由来（デフォルト 30 分、設定変更可）
- **ログアウト**: ヘッダー右上の ⏻ ボタン (確認ダイアログ付き)。Credential Manager もクリア

### 接続
- **接続先サーバ・更新確認先・D&D 可否はアプリ同梱の `deployment.json` で配布時に固定** — 管理者が発行前に編集し、利用者 (クライアント) からは変更できない。exe と同じ場所に置く読み取り専用設定で、`settings.json` より優先される (`DeploymentConfig` が起動時に上書き適用)
- ログイン画面の **接続テスト** で、固定された接続先に `/health` を叩いて疎通確認のみ可能（`IHttpClientFactory` 経由でソケット使い回し）。接続先の編集 UI は持たない
- 利用者ごとの可変設定 `%LocalAppData%\Watashi\settings.json` には最終ローカルパス等のみ保存（接続先は保存しない）
- deployment.json 欠落 / `serverUrl` または `updateManifestUrl` 未設定時は「配布設定エラー」を表示して終了（接続先を末端で変更させない設計のため、設定画面は出さない）
- 起動時の自動ログインは 8 秒タイムアウトで UI フリーズを防ぐ
- **`IsHttps` は `ServerUrl` のスキームから判定**。表示と通信の整合性を保証

---

## 管理者機能 (Admin タブ)

`IsAdmin=true` のユーザーがログインすると、メニューの「管理」が表示される。
タブ構成 (TabControl) — 全 admin 操作は監査ログ（`ADMIN_*` プレフィックス）に記録される:

### 1. ユーザー
- 一覧 / 追加 / 削除 / 管理者フラグ変更
- ロック解除 (連続失敗ロックの解除)
- **新規ユーザーはパスワード未設定で作成**。本人が初回ログイン時に Windows 統合認証で本人確認され、自分で決める (初期パスワードの配布が不要)
- 初期 PW 発行 (12 字 + 大小数記号ポリシー検証) — Windows 認証が使えない端末向けの第二経路。`MustChangePassword=true`
- 初回設定に戻す — パスワードを破棄し本人に再設定させる。セッションと信頼済み端末も失効
- **CSV インポート (一括登録)**: `Username, IsAdmin` 列の CSV を選んで一括登録 (新規は未設定で作成)。**新規追加のみ** (重複スキップ) と **上書き** (`IsAdmin` のみ更新) の 2 モード。旧形式の `Password` 列は無視。行単位エラー詳細表示
- **CSV エクスポート (棚卸し用)**: 現ユーザー一覧を `Username, IsAdmin, IsLocked, PasswordStatus, PasswordExpiresAt, LastLoginAt, CreatedAt` 形式の CSV (BOM 付き UTF-8) で保存

### 2. ホスト (CIFS ファイルサーバー)
- 一覧 / 追加 / 削除
- 表示名、ホスト名/IP、ポート (デフォルト 445)、CIFS 資格情報、実行ノード
- 接続テスト (登録済み共有を 1 つ使って SMB セッション確立を試す)
- 資格情報は AES-256-GCM で暗号化して DB 保存
- `CredPasswordEnc` は `[JsonIgnore]` で API レスポンスから自動除外

### 3. 共有
- ホストに紐づく SMB 共有名と表示名
- 同一ホストで `ShareName` ユニーク制約

### 4. テンプレート (権限テンプレート)
- 名前 + READ/WRITE/DELETE/RENAME のチェックボックス
- 初期シード (新規 DB): 「フルアクセス」「読取+書込」「読取のみ」 (フルアクセスが Id=1)
- 表示はクライアント側で **権限スコア降順** にソート (既存 DB でもフルアクセスが先頭)
- 使用中テンプレートは削除不可

### 5. ユーザー権限 (パスブラウザ付き、サーバー → 共有 → パス階層)
- **左サイドバー**: ユーザー一覧 (絞り込み検索 + 件数バッジ)、ListBox 仮想化で大量ユーザー対応
- **右パネル**: 選択ユーザーの付与済みパス + 「＋ パスを追加」Expander
- **サーバー → 共有** のドロップダウン階層 (共有が増えても探しやすい)
- **権限テンプレート**: 強い順に表示、デフォルト選択は **フルアクセス**
- パスブラウザで実際のディレクトリツリーから許可パスを選択 → サブフォルダクリックで自動反映
- 1 ユーザー × 1 共有 に複数のサブパス権限を付与可能
- 付与済み一覧は ホスト > 共有 > パス でソート、行ホバーで全情報ツールチップ表示
- 削除は確認ダイアログ付き
- **📦 セットから一括適用**: 「権限セット」を選んで 1 クリック適用 (重複行はスキップ or 上書き)
- **📋 他ユーザーから全件コピー**: 既存ユーザーの全権限をワンクリック複製 (重複行はスキップ or 上書き)

### 5b. 権限セット (PermissionBundle)
- 部署/役割ごとに複数の権限行をひとまとめにして登録
- セットを編集してユーザー権限から「セット適用」で一括付与
- セット = (名前, 説明, [(共有, テンプレ, パス, 表示名)…])
- セットを削除しても既に適用済みのユーザー権限は残る (スナップショット展開方式)
- API: GET/POST/PATCH/DELETE `/api/admin/permission-bundles`、POST `/api/admin/permission-bundles/{id}/apply`

### 6. 信頼デバイス
- ユーザーごとの自動ログインデバイスを一覧
- 「全デバイス失効」で対象ユーザーの自動ログイン無効化 (退職時等)
- 失効処理は `ExecuteUpdateAsync` でアトミック実行
- 失効理由 (`admin_revoked`) と失効日時を記録

### 7. 実行ノード
- 一覧 / 追加 / 削除
- NodeType = `Direct` (中央サーバー自身) / `Agent` (踏み台)
- HealthStatus 自動切替: 90 秒以上ハートビートなしで `Unhealthy`、状態変化のあったノードだけ `ExecuteUpdate`
- MaxConcurrency (Agent の同時接続上限、デフォルト 20)
- 削除前にホストでの使用チェック
- mTLS モードでは `ClientCertificateThumbprint` で Agent を識別

### 8. 操作ログ
- フィルタ: ユーザー名 / 操作種別 / 期間
- 1 ページ 100 件、降順
- CSV エクスポート (BOM 付き UTF-8、AsNoTracking + Select 射影で大規模テーブルでも安全)
- 1 年経過分は日次バッチで自動削除
- ファイル操作 (`READ/WRITE/DELETE/RENAME`) と管理者操作 (`ADMIN_*`) の両方を記録

### 9. システム設定
- `PasswordExpiryDays` (デフォルト 90)
- `PasswordWarningDays` (デフォルト 14)
- `AgentMaxConcurrency` (デフォルト 20)
- `SessionIdleMinutes` (デフォルト 30、ログイン応答経由でクライアントに反映)
- `AuditLogRetentionDays` (デフォルト 365)。`AuditLogPurgeService` (BackgroundService) が起動 30 秒後 + 24 時間毎にこの設定値より古い AuditLog を `ExecuteDeleteAsync` で削除。`0` 以下で永久保管
- `MaxFailedLoginAttempts` (デフォルト 15)。連続ログイン失敗がこの回数に達すると自動でアカウントロック。管理画面から変更可

---

## サーバー機能

### 認証
- bcrypt によるパスワードハッシュ (ソルト自動付与、`PasswordHash` は `[JsonIgnore]`)
- JWT (HS256) アクセストークン 15 分
- **リフレッシュトークンのローテーション**:
  - 30 日間有効、リフレッシュ毎に新トークン発行 + 旧トークン即失効
  - 失効済みトークンの再提示 → ファミリー全失効（盗難検知）
  - 漏洩リスクの大幅低減
- リフレッシュ毎に DB 状態確認:
  - User.IsLocked → 401
  - 紐づく TrustedDevice.IsRevoked → 401
  - User.MustChangePassword → レスポンスにフラグ付与
- 連続失敗で自動アカウントロック (既定 15 回、`MaxFailedLoginAttempts` で変更可)
- **ログインレート制限**: `/api/auth/login` `/api/auth/auto-login` に IP 単位固定ウィンドウ (デフォルト 10/分)

### 機微フィールドの API 漏洩防止
- `User.PasswordHash`, `RefreshToken.TokenHash`, `TrustedDevice.DeviceTokenHash`, `CifsHost.CredPasswordEnc` は全て `[JsonIgnore]` 付き
- エンドポイントが entity を誤って直接返した場合でも secret は流出しない（defense in depth）

### 権限モデル
- 粒度: **(共有, サブパス)** 単位
- パス正規化 (`..` 展開、ディレクトリトラバーサル防御)
- 1 ユーザー × 1 共有 に複数の許可パスを持てる
- 許可パスのルート自体は DELETE / RENAME 不可 (誤削除防止)
- RENAME は同一親ディレクトリ内に限定 (隠れ MOVE 防止)
- **PermissionService の結果は per-request メモ化** (`HttpContext.Items`)
- 同一リクエスト内で何度 `CanPerformAsync` を呼んでも DB 1 回のみ

### ファイル操作 API
| Method | Path | 用途 |
|---|---|---|
| GET | `/api/files` | 一覧 (200 件/ページ + ソート) |
| GET | `/api/files/download` | ダウンロード (ストリーミング) |
| POST | `/api/files/upload` | アップロード (ストリーミング) |
| DELETE | `/api/files` | 削除 |
| POST | `/api/files/rename` | リネーム (同一親限定) |
| POST | `/api/files/mkdir` | フォルダ作成 |
| GET | `/api/hosts/catalog` | host/share/location を 1 リクエストで集約取得 (N×M HTTP の解消) |

`ExecuteAsync` 共通ヘルパーで認可 → 実行 → 監査ログ → エラーマップを統一処理。
内部例外メッセージは `Results.Problem` でマスクし、クライアントには汎用メッセージのみ返す。

### ストリーミング
- ダウンロード: SMB → クライアントへ 4 MB バッファでパススルー
- アップロード: クライアント → SMB へ 4 MB バッファでパススルー
- Agent 経由時も `HttpCompletionOption.ResponseHeadersRead` で全件メモリ展開を回避
- `SmbWriteStream` は `ArrayPool<byte>.Shared` を利用、LOH 圧迫なし

### 監査ログ
- 全ファイル操作の前後で記録
- 全 admin 操作 (`ADMIN_USER_*`, `ADMIN_HOST_*`, `ADMIN_SHARE_*`, `ADMIN_TEMPLATE_*`, `ADMIN_PERMISSION_*`, `ADMIN_NODE_*`, `ADMIN_SETTING_UPDATE`) も記録
- 記録項目: Timestamp / UserId / Username / Operation / Host/Share/Path / TargetPath / Result(success|failure) / ErrorMessage / ClientIp / Bytes / DurationMs / Protocol(HTTP|HTTPS) / ExecutionNodeId / UsedPermissionId
- 索引: Timestamp 降順 / UserId / HostId

### ノードルーティング (Direct ⇔ Agent)
- ホストに紐づく ExecutionNode で振分
- `Direct` ノード → 中央サーバーから直接 SMB
- `Agent` ノード → エージェントへ HTTP(S) フォワード、エージェントが SMB
- `GatewayNodeId` 付き Agent ノード → `Server → Gateway Agent → Target Agent → SMB` の 1段チェーン
- 認証情報は **POST body または X-Watashi-Cifs ヘッダ (Base64 JSON)** で送信（URL クエリ漏洩を回避）
- ストリーミング転送をリレーで実現 (全段でメモリ展開しない)
- Direct Agent / Gateway Agent が Unhealthy の場合は即座に 503 を返す (リトライなし)

### ヘルスモニタ
- バックグラウンドで 15 秒毎に Agent ノードの LastHeartbeatAt をチェック（`PeriodicTimer`）
- 90 秒以上音信不通 → `Unhealthy`、復活 → `Healthy`
- Gateway 配下で中央へ直接 heartbeat できない Agent は `Unknown` のまま操作時の HTTP 到達性で判定
- 状態変化があったノードだけを `ExecuteUpdate` で書き込み（no-op SaveChanges を排除）
- Direct ノードは常に `Healthy`

### SMB セッションプール
- `Watashi.Shared.Cifs.CifsSessionPool` で `(host, port, user, share)` キーの再利用プール
- idle TTL（デフォルト 60 秒）+ キー単位上限（デフォルト 4）
- 操作毎の TCP 接続 + SMB negotiate + Login + TreeConnect のコストを削減
- バックグラウンド evict タイマーで idle セッションを自動破棄

### EF Core 最適化
- 全 read-only クエリに `AsNoTracking`
- 監査ログ CSV エクスポートは `Select` 射影 + `AsAsyncEnumerable` でストリーミング出力
- バッチ更新は `ExecuteUpdate` / `ExecuteDelete`（TrustedDevice 失効、PendingLog 削除など）

---

## エージェント機能 (踏み台)

### CIFS 中継
- 中央サーバーから HTTP(S) で受け取った操作を SMB に変換して実行
- 認証情報は中央からリクエスト毎に **JSON body または X-Watashi-Cifs ヘッダ** で受領（メモリ上のみ、永続化しない）
- ストリーミング転送対応
- 共有 SMB セッションプールにより、複数操作で TCP/SMB セッションを再利用

### 認証 (inbound)
- mTLS モード: 中央サーバの **クライアント証明書サムプリント** を `Auth:CentralCertificateThumbprint` と照合
- 共有秘密モード: `X-Watashi-Secret` ヘッダの値を `Auth:SharedSecret` と一致確認
- 両方とも未設定だと全アクセス拒否（401）

### ハートビート
- 30 秒毎に中央へ `/api/internal/heartbeat` を POST（`PeriodicTimer`）
- mTLS が有効ならクライアント証明書を提示
- 中央側はこれを受けて `LastHeartbeatAt` を更新

### ログバッファ
- 中央が一時的に到達不能でも操作を継続できるよう、ローカル SQLite (`PendingLogs`) に監査ログをバッファ可能
- 10 秒毎に中央の `/api/internal/audit-logs/batch` へ送信を試行
- 成功で `ExecuteDelete` で一括削除、失敗で `ExecuteUpdate` で `AttemptCount` をインクリメント
- 連続失敗時は指数バックオフ（最大 5 分）、`AttemptCount >= 50` で送信対象外（dead-letter 扱い）

### 過負荷制御
- `MaxConcurrency` (デフォルト 20) を超えると 503 + `Retry-After: 5`
- スレッド数を Interlocked で監視

---

## デプロイ・運用機能

### Windows Service
- Server / Agent ともに `Microsoft.Extensions.Hosting.WindowsServices` で常駐サービス化
- `deploy/install-server-service.ps1` / `install-agent-service.ps1` でワンコマンド登録
- 異常終了時の自動再起動: 5 秒 → 30 秒 → 60 秒
- アンインストールは `deploy/uninstall-service.ps1`

### 起動時シークレット検証
- `Jwt:Secret` のプレースホルダ (`CHANGE-ME...`) 検出 → Production で起動拒否、Dev/Stg で警告
- `Encryption:MasterKey` の同様検証

### DB
- DB ファイル自動生成 (初回起動時 `MigrateAsync()`)
- WAL モード + 推奨 PRAGMA (busy_timeout, cache_size, foreign_keys=ON)
- バックアップ: `SqliteConnection.BackupDatabase()` 利用、日次タスクで 7 世代 + 月次 4 世代

### ログ運用
- ログ削除: 1 年経過分を日次 DELETE、月次 VACUUM
- Serilog の日次ローテーション (`rollingInterval=Day`)
- `*.log` は `.gitignore` で除外（誤コミット防止）

### ClickOnce
- 配布で自動アップデート (操作中は強制再起動しない)

---

## セキュリティ

### 通信
- Client ↔ Server: HTTP / HTTPS 選択式 (自動ログインは HTTPS 必須)
- Server ↔ Agent: HTTP / HTTPS、**mTLS で双方向の証明書認証**
- Server / Agent → CIFS: SMB2/3 (SMBLibrary)

### データ
- CIFS 資格情報: AES-256-GCM 暗号化、マスターキーは `appsettings.json` または環境変数
- JWT 署名: HMAC-SHA256
- パスワード: bcrypt (work factor 11 デフォルト)
- デバイストークン: 256 bit ランダム → bcrypt 保存、平文は Windows Credential Manager にユーザー単位で暗号化保存
- 機微フィールドは `[JsonIgnore]` で API 漏洩防止

### パスワードポリシー
- 12 文字以上
- 英大文字 + 英小文字 + 数字 + 記号 各 1 文字以上
- 履歴チェックなし (再利用 OK、運用ポリシーで補完想定)
- 期限: デフォルト 90 日 (設定変更可)

### ディレクトリトラバーサル防御
1. ユーザー入力パスを `NormalizePath()` で正規化 (`..` 展開)
2. `IsPathWithin(allowedPath, normalizedPath)` で許可範囲内か確認
3. 範囲外なら 403、操作ログに `denied` を記録

### 攻撃シナリオ別対策

| 攻撃 | 対策 |
|---|---|
| クレデンシャル総当たり | bcrypt + 15 回ロック (既定、管理画面で変更可) + IP レート制限 (10/分) |
| ユーザー列挙 | 存在しないユーザーでもロックしない、login レート制限が IP 単位 |
| リフレッシュトークン盗難 | ローテーション + 再利用検知でファミリー失効 |
| Agent なりすまし | mTLS でサーバー証明書を Thumbprint レベルで確認 |
| 中央サーバなりすまし | mTLS or SharedSecret で Agent inbound を保護 |
| ディレクトリトラバーサル | 正規化 + IsPathWithin 二段チェック |
| 平文ログ漏洩 | URL に資格情報を載せない（POST body / ヘッダのみ） |
| 監査ログ偽造 | `/api/internal/*` を mTLS または共有秘密で保護。mTLS 時は AgentId と証明書を相互照合 |
| エンティティ直接シリアライズ | 機微フィールドに `[JsonIgnore]` を付与 |
| 内部スタックトレース漏洩 | 例外メッセージは `Results.Problem` でマスク |
