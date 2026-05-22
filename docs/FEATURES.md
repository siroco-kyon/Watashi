# Watashi 機能カタログ

「何ができるか」をひとまとめにした機能一覧。
それぞれの設定方法・操作方法は [SETUP.md](SETUP.md) / [USER-GUIDE.md](USER-GUIDE.md) / [ADMIN-GUIDE.md](ADMIN-GUIDE.md) を参照。

---

## エンドユーザー機能 (WPF クライアント)

### ファイル操作 (FFFTP 風 2 ペイン)
- **ローカルペイン**: PC のフォルダを System.IO で直接ブラウズ
- **リモートペイン**: 中央サーバー越しに CIFS 共有をブラウズ
- 一覧ソート (名前 / 日付 / サイズ、昇順/降順)
- 1 ページ 200 件、ページング対応
- 親フォルダへ移動 (`..`) — 権限ルートでは `..` がグレー

### 転送
- **アップロード** (ローカル → リモート): ストリーミング 4 MB チャンク、進捗バー
- **ダウンロード** (リモート → ローカル): ストリーミング、SaveFileDialog で保存先指定
- ファイルサイズ無制限
- 転送中の進捗 % とファイル名がステータスバーに表示

### その他のファイル操作
| UI | 動作 |
|---|---|
| 削除ボタン | リモート/ローカル両ペインで削除 (確認ダイアログ) |
| F2 / 新規フォルダ | リモート: API 経由 / ローカル: System.IO |
| ダブルクリック | フォルダ展開、ファイルは未操作 |
| Enter キー (パス欄) | パス直接入力で移動 |

### 認証
- **手動ログイン**: ユーザー名 / パスワード
- **自動ログイン (デバイス記憶)**: PC 名 + Windows ユーザー名 + デバイストークン
  - HTTPS 接続時のみ利用可能
  - チェックボックスで有効化、Credential Manager に保存
  - 1 ユーザー = 1 デバイスのみ (新規登録で旧トークン失効)
- **パスワード変更**: 期限切れ/初回ログインで強制画面
- **アイドルタイムアウト**: 30 分操作なしで自動ログアウト
- **ログアウト**: メニューから手動。Credential Manager もクリア

### 接続
- 接続設定画面でサーバー URL とプロトコル (HTTP/HTTPS) を選択
- 接続テストボタンで `/health` を叩いて疎通確認
- 設定は `%LocalAppData%\Watashi\settings.json` に保存

---

## 管理者機能 (Admin タブ)

`IsAdmin=true` のユーザーがログインすると、メニューの「管理」が表示される。
タブ構成 (TabControl):

### 1. ユーザー
- 一覧 / 追加 / 削除 / 管理者フラグ変更
- ロック解除 (5 回連続失敗の解除)
- パスワード強制リセット (12 字 + 大小数記号ポリシー検証)
- すべての新規/リセットユーザーは初回 `MustChangePassword=true`

### 2. ホスト (CIFS ファイルサーバー)
- 一覧 / 追加 / 削除
- 表示名、ホスト名/IP、ポート (デフォルト 445)、CIFS 資格情報、実行ノード
- 接続テスト (登録済み共有を 1 つ使って SMB セッション確立を試す)
- 資格情報は AES-256-GCM で暗号化して DB 保存

### 3. 共有
- ホストに紐づく SMB 共有名と表示名
- 同一ホストで `ShareName` ユニーク制約

### 4. テンプレート (権限テンプレート)
- 名前 + READ/WRITE/DELETE/RENAME のチェックボックス
- 初期シード: 「読取のみ」「読取+書込」「フルアクセス」
- 使用中テンプレートは削除不可

### 5. ユーザー権限 (パスブラウザ付き)
- 一覧 / 追加 / 削除
- 1 ユーザー × 1 共有 に複数のサブパス権限を付与可能
- パスブラウザで実際のディレクトリツリーから許可パスを選択
- テンプレート選択で操作種別を制御

### 6. 信頼デバイス
- ユーザーごとの自動ログインデバイスを一覧
- 「全デバイス失効」で対象ユーザーの自動ログイン無効化 (退職時等)
- 失効理由 (`admin_revoked`) と失効日時を記録

### 7. 実行ノード
- 一覧 / 追加 / 削除
- NodeType = `Direct` (中央サーバー自身) / `Agent` (踏み台)
- HealthStatus 自動切替: 90 秒以上ハートビートなしで `Unhealthy`
- MaxConcurrency (Agent の同時接続上限、デフォルト 20)
- 削除前にホストでの使用チェック

### 8. 操作ログ
- フィルタ: ユーザー名 / 操作種別 / 期間
- 1 ページ 100 件、降順
- CSV エクスポート (BOM 付き UTF-8)
- 1 年経過分は日次バッチで自動削除

### 9. システム設定
- `PasswordExpiryDays` (デフォルト 90)
- `PasswordWarningDays` (デフォルト 14)
- `AgentMaxConcurrency` (デフォルト 20)
- `SessionIdleMinutes` (デフォルト 30)

---

## サーバー機能

### 認証
- bcrypt によるパスワードハッシュ (ソルト自動付与)
- JWT (HS256) アクセストークン 15 分
- リフレッシュトークン 30 日 (DB 検証付き)
- リフレッシュ毎に DB 状態確認:
  - User.IsLocked → 401
  - 紐づく TrustedDevice.IsRevoked → 401
  - User.MustChangePassword → レスポンスにフラグ付与
- 5 回連続失敗で自動アカウントロック

### 権限モデル
- 粒度: **(共有, サブパス)** 単位
- パス正規化 (`..` 展開、ディレクトリトラバーサル防御)
- 1 ユーザー × 1 共有 に複数の許可パスを持てる
- 許可パスのルート自体は DELETE / RENAME 不可 (誤削除防止)
- RENAME は同一親ディレクトリ内に限定 (隠れ MOVE 防止)

### ファイル操作 API
| Method | Path | 用途 |
|---|---|---|
| GET | `/api/files` | 一覧 (200 件/ページ + ソート) |
| GET | `/api/files/download` | ダウンロード (ストリーミング) |
| POST | `/api/files/upload` | アップロード (ストリーミング) |
| DELETE | `/api/files` | 削除 |
| POST | `/api/files/rename` | リネーム (同一親限定) |
| POST | `/api/files/mkdir` | フォルダ作成 |

### ストリーミング
- ダウンロード: SMB → クライアントへ 4 MB バッファでパススルー
- アップロード: クライアント → SMB へ 4 MB バッファでパススルー
- Agent 経由時も `HttpCompletionOption.ResponseHeadersRead` で全件メモリ展開を回避

### 監査ログ
- 全ファイル操作の前後で記録
- 記録項目: Timestamp / UserId / Username / Operation / Host/Share/Path / TargetPath / Result(success|failure) / ErrorMessage / ClientIp / Bytes / DurationMs / Protocol(HTTP|HTTPS) / ExecutionNodeId / UsedPermissionId
- 索引: Timestamp 降順 / UserId / HostId

### ノードルーティング (Direct ⇔ Agent)
- ホストに紐づく ExecutionNode で振分
- `Direct` ノード → 中央サーバーから直接 SMB
- `Agent` ノード → エージェントへ HTTP(S) フォワード、エージェントが SMB
- ストリーミング転送をリレーで実現 (全段でメモリ展開しない)
- Unhealthy ノードは即座に 503 を返す (リトライなし)

### ヘルスモニタ
- バックグラウンドで 15 秒毎に Agent ノードの LastHeartbeatAt をチェック
- 90 秒以上音信不通 → `Unhealthy`、復活 → `Healthy`
- Direct ノードは常に `Healthy`

---

## エージェント機能 (踏み台)

### CIFS 中継
- 中央サーバーから HTTP(S) で受け取った操作を SMB に変換して実行
- 認証情報は中央からリクエスト毎にクエリ/ボディで受領 (メモリ上のみ、永続化しない)
- ストリーミング転送対応

### ハートビート
- 30 秒毎に中央へ `/api/internal/heartbeat` を POST
- 中央側はこれを受けて `LastHeartbeatAt` を更新

### ログバッファ
- 中央が一時的に到達不能でも操作を継続できるよう、ローカル SQLite (`PendingLogs`) に監査ログをバッファ可能
- 10 秒毎に中央の `/api/internal/audit-logs/batch` へ送信を試行
- 成功で削除、失敗で `AttemptCount` をインクリメント

### 過負荷制御
- `MaxConcurrency` (デフォルト 20) を超えると 503 + `Retry-After: 5`
- スレッド数を Interlocked で監視

---

## セキュリティ

### 通信
- Client ↔ Server: HTTP / HTTPS 選択式 (自動ログインは HTTPS 必須)
- Server ↔ Agent: HTTP / HTTPS、mTLS は設定で ON/OFF
- Server / Agent → CIFS: SMB2/3 (SMBLibrary)

### データ
- CIFS 資格情報: AES-256-GCM 暗号化、マスターキーは `appsettings.json` または環境変数
- JWT 署名: HMAC-SHA256
- パスワード: bcrypt (work factor 11 デフォルト)
- デバイストークン: 256 bit ランダム → bcrypt 保存、平文は Windows Credential Manager にユーザー単位で暗号化保存

### パスワードポリシー
- 12 文字以上
- 英大文字 + 英小文字 + 数字 + 記号 各 1 文字以上
- 履歴チェックなし (再利用 OK、運用ポリシーで補完想定)
- 期限: デフォルト 90 日 (設定変更可)

### ディレクトリトラバーサル防御
1. ユーザー入力パスを `NormalizePath()` で正規化 (`..` 展開)
2. `IsPathWithin(allowedPath, normalizedPath)` で許可範囲内か確認
3. 範囲外なら 403、操作ログに `denied` を記録

---

## 運用機能

- DB ファイル自動生成 (初回起動時 `MigrateAsync()`)
- WAL モード + 推奨 PRAGMA (busy_timeout, cache_size, foreign_keys=ON)
- バックアップ: `SqliteConnection.BackupDatabase()` 利用、日次タスクで 7 世代 + 月次 4 世代
- ログ削除: 1 年経過分を日次 DELETE、月次 VACUUM
- ClickOnce 配布で自動アップデート (操作中は強制再起動しない)
