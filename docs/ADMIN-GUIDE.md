# Watashi 管理者ガイド

情シス担当者向けの運用ガイド。
管理機能の使い方と典型的なワークフローを順に説明。

- [管理画面の開き方](#管理画面の開き方)
- [初期セットアップフロー](#初期セットアップフロー)
- [タブ別の使い方](#タブ別の使い方)
  - [ユーザー](#ユーザー)
  - [ホスト (CIFS ファイルサーバー)](#ホスト-cifs-ファイルサーバー)
  - [共有](#共有)
  - [テンプレート](#テンプレート-権限テンプレート)
  - [ユーザー権限](#ユーザー権限-パスブラウザ付き)
  - [信頼デバイス](#信頼デバイス)
  - [実行ノード](#実行ノード)
  - [操作ログ](#操作ログ)
  - [システム設定](#システム設定)
- [認証まわりの仕組み (運用上の挙動)](#認証まわりの仕組み-運用上の挙動)
- [トラブル対応](#トラブル対応)

---

## 管理画面の開き方

1. `IsAdmin = true` のユーザーでログイン
2. メイン画面メニューバーの「**管理(A)**」をクリック
3. AdminWindow がタブ表示で開く

> 一般ユーザーには「管理」メニュー自体が表示されません。

---

## 初期セットアップフロー

新しい CIFS 共有を Watashi 経由で利用させるまでの典型的な流れ:

```
1. 実行ノード登録 (Direct は seed 済み、Agent を追加する場合のみ)
       ↓
2. ホスト登録 (CIFS サーバーの IP + CIFS 資格情報 + 実行ノード)
       ↓
3. ホストの接続テスト (失敗するとここで気付ける)
       ↓
4. 共有登録 (ホストに紐づく SMB 共有名)
       ↓
5. (必要なら) 権限テンプレート追加
       ↓
6. ユーザー登録
       ↓
7. ユーザー権限割当 (ユーザー × 共有 × サブパス × テンプレート)
       ↓
8. ユーザーに通知 (URL / 初期パスワード)
```

---

## タブ別の使い方

### ユーザー

ユーザーアカウントの追加 / 削除 / 管理者フラグ変更 / ロック解除 / パスワードリセット。

| 操作 | 説明 | 監査ログ |
|---|---|---|
| 新規ユーザー作成 | 下部入力欄に Username / Password / 管理者チェック → 「作成」 | `ADMIN_USER_CREATE` |
| 削除 | 行選択 → 「削除」 | `ADMIN_USER_DELETE` |
| ロック解除 | 5 連続失敗でロックされたユーザーを行選択 → 「ロック解除」 | `ADMIN_USER_UNLOCK` |
| 管理者フラグ変更 | PATCH 経由 | `ADMIN_USER_UPDATE` |
| PW リセット | 行選択 → 「PWリセット」 → ダイアログで新パスワード入力 | `ADMIN_USER_RESET_PW` |
| 全デバイス失効 | デバイスタブから | `ADMIN_USER_REVOKE_DEVICES` |

**ユーザー作成時の挙動**:
- パスワードは即座にポリシー検証 (12 字 + 大小数記号)
- 作成と同時に `MustChangePassword=true` (初回ログインで強制変更)
- `PasswordExpiresAt = 今 + PasswordExpiryDays` で計算

**運用 tips**:
- 退職者: 削除でも良いが、監査ログとの紐付けが切れるので「全デバイス失効」+「PWリセット」で無効化推奨

### ホスト (CIFS ファイルサーバー)

中央サーバー / Agent から接続する CIFS サーバーの登録。

**入力項目**:
- 表示名 (例: `経理部ファイルサーバ`)
- ホスト名/IP (例: `fileserver01.corp.local`)
- ポート (デフォルト 445)
- CIFS ユーザー (例: `WATASHI\svc-cifs`)
- CIFS パスワード (AES-256-GCM で即座に暗号化される、平文 DB 保存なし)
- 実行ノード (Direct / Agent から選択)

| 操作 | 監査ログ |
|---|---|
| 追加 | `ADMIN_HOST_CREATE` |
| 更新 | `ADMIN_HOST_UPDATE` |
| 削除 | `ADMIN_HOST_DELETE` |
| 接続テスト | `ADMIN_HOST_TEST` (Result が success/failure に応じる) |

**接続テスト**:
- 行選択 → 「接続テスト」
- このホストに紐づく **共有が 1 つ以上登録されている** ことが前提
- 失敗時は CIFS 資格情報 / ファイアウォール / ノード経路を確認
- 結果（成功/失敗）は監査ログに記録される

### 共有

ホスト × SMB 共有名の対応表。

| 入力 | 内容 |
|---|---|
| ホスト | プルダウンから選択 |
| ShareName | SMB 上の共有名 (例: `documents`) |
| 表示名 | UI 上の名前 (例: `経理部ドキュメント`) |

複数の表示名で同じ共有を登録する用途は想定外 (`(HostId, ShareName)` ユニーク)。
監査ログ: `ADMIN_SHARE_CREATE` / `ADMIN_SHARE_UPDATE` / `ADMIN_SHARE_DELETE`

### テンプレート (権限テンプレート)

操作種別の組み合わせを名前付きで登録。

初期 seed:
| 名前 | Read | Write | Delete | Rename |
|---|:-:|:-:|:-:|:-:|
| 読取のみ | ✓ |  |  |  |
| 読取+書込 | ✓ | ✓ |  |  |
| フルアクセス | ✓ | ✓ | ✓ | ✓ |

業務に合わせて追加可能 (例: 「読書込+リネームのみ」など)。
使用中のテンプレートは削除不可。
監査ログ: `ADMIN_TEMPLATE_CREATE` / `_UPDATE` / `_DELETE`

### ユーザー権限 (パスブラウザ付き)

ユーザーへの権限割当。**Watashi の核心機能**。

```
┌─────────────────────────────────────────────────────┐
│ ユーザー: [tanaka ▼]      [削除] [再読み込み]       │
├─────────────────────────────────────────────────────┤
│ [共有 ▼] [テンプレ ▼] [パス: /dept-A] [表示名] [追加]│
├─────────────────────────────────────────────────────┤
│ ブラウズ: [/dept-A] [開く]                          │
│ ┌─ ディレクトリツリー ─────────────────┐            │
│ │ 📁 2025                             │            │
│ │ 📁 2026                             │            │
│ │ 📁 archive                          │            │
│ └────────────────────────────────────┘            │
├─────────────────────────────────────────────────────┤
│ 共有    │ パス              │ テンプレート            │
│ docs    │ /dept-A           │ フルアクセス            │
│ docs    │ /shared/templates │ 読取のみ                │
│ archives│ /                 │ 読取のみ                │
└─────────────────────────────────────────────────────┘
```

**追加手順**:
1. 一番上のユーザー選択ドロップダウンで対象ユーザーを選ぶ
2. 「共有」「テンプレ」を選ぶ
3. (任意) 「ブラウズ」セクションで実在ディレクトリを開いて確認
4. 「パス」欄に許可したいサブパスを入力 (例: `/dept-A`、共有全体なら `/`)
5. (任意) 「表示名」にユーザー向け説明 (例: `経理部 2026 年度`)
6. 「追加」

**1 ユーザー × 1 共有に複数のパス権限を持たせられる**:
- 例: `documents` 共有について `/dept-A` はフルアクセス、`/shared/templates` は読取のみ

**特殊な制約**:
- 許可パスのルート (例: `/dept-A` 自体) は削除/リネーム不可 (誤削除防止)
- リネームは同じ親ディレクトリ内のみ (フォルダ間移動は禁止)

監査ログ: `ADMIN_PERMISSION_CREATE` (Path に `perm:user=42,share=3,path=/dept-A` 形式で詳細を保持) / `ADMIN_PERMISSION_DELETE`

### 信頼デバイス

ユーザーの自動ログインを管理。

```
ユーザー: [tanaka ▼] [全デバイス失効]
─────────────────────────────────────
ID │ マシン │ Windows ユーザ │ 登録 │ 最終使用 │ 失効
1  │ PC0123 │ tanaka         │ ...  │ ...      │ false
```

- 1 ユーザー = 1 デバイスのみ (DB ユニーク制約)
- 退職者対応: ユーザー選択 → 「全デバイス失効」
- 失効すると `IsRevoked=true` + `RevokedAt`、その後の自動ログインは拒否
- ユーザーは手動ログインに戻る
- 「全デバイス失効」は `ExecuteUpdateAsync` で 1 トランザクションで処理（旧実装の N+1 を解消）

### 実行ノード

CIFS への接続経路。`Direct` = 中央サーバー自身、`Agent` = 踏み台のエージェント。

| 入力 | 内容 |
|---|---|
| 名前 | Agent の場合は AgentId と一致させること (例: `bastion-a`) |
| 種別 | Direct / Agent |
| Endpoint | Agent の URL (例: `http://bastion-a:8081` または `https://bastion-a:8443`) |
| ClientCertificateThumbprint | mTLS モード時、Agent が提示するクライアント証明書サムプリント |
| MaxConcurrency | Agent の同時接続上限 (デフォルト 20) |

**HealthStatus**:
- `Healthy`: ハートビート受信済み (Agent) / 起動中 (Direct)
- `Unhealthy`: 90 秒以上ハートビートなし (Agent のみ)
- `Unknown`: まだ一度もハートビート受信していない (新規登録 Agent)

中央サーバーは Unhealthy ノードへのアクセスを即座に 503 で返す。
監査ログ: `ADMIN_NODE_CREATE` / `_UPDATE` / `_DELETE` / `_REGEN_KEY`

### 操作ログ

全ファイル操作と管理者操作の監査ログ。

**フィルタ**:
- User: ユーザー名で完全一致
- Op: ファイル操作（`READ` / `WRITE` / `DELETE` / `RENAME`）または管理者操作（`ADMIN_*`）
- From / To: UTC 期間指定
- 検索ボタンでクエリ実行

**カラム**:
- Timestamp / Username / Op / Result / Host/Share / Path / Bytes / Dur(ms) / Err

**操作種別の凡例**:
| 操作種別 | 説明 |
|---|---|
| `READ` `WRITE` `DELETE` `RENAME` | ユーザーによるファイル操作 |
| `ADMIN_USER_*` | ユーザー管理（CREATE/UPDATE/DELETE/UNLOCK/RESET_PW/REVOKE_DEVICES） |
| `ADMIN_HOST_*` | ホスト管理（CREATE/UPDATE/DELETE/TEST） |
| `ADMIN_SHARE_*` | 共有管理（CREATE/UPDATE/DELETE） |
| `ADMIN_TEMPLATE_*` | テンプレート管理（CREATE/UPDATE/DELETE） |
| `ADMIN_PERMISSION_*` | 権限管理（CREATE/DELETE） |
| `ADMIN_NODE_*` | ノード管理（CREATE/UPDATE/DELETE/REGEN_KEY） |
| `ADMIN_SETTING_UPDATE` | システム設定変更 |

**CSV エクスポート**:
- 「CSV出力」で現在のフィルタ条件のまま全件 CSV ダウンロード
- BOM 付き UTF-8、Excel でそのまま開ける
- サーバ側は `AsNoTracking + Select` で射影し、500 件ずつバッチフラッシュ（大規模監査テーブルでも OOM しない）

**保管期間**:
- 1 年 (日次バッチで自動削除、設定では変えられない)
- 月次 VACUUM で DB サイズ圧縮

### システム設定

`SystemSettings` テーブルの編集。

| キー | 用途 |
|---|---|
| `PasswordExpiryDays` | パスワード有効期限 (デフォルト 90) |
| `PasswordWarningDays` | 期限警告を出す日数 (デフォルト 14) |
| `AgentMaxConcurrency` | Agent 新規登録時のデフォルト (デフォルト 20) |
| `SessionIdleMinutes` | クライアントアイドルタイムアウト (デフォルト 30) ※ログイン時にクライアントへ配信される |

行選択 → 編集欄で値を変更 → 「保存」。
監査ログ: `ADMIN_SETTING_UPDATE`（Path に `setting:PasswordExpiryDays` 形式で対象キーを保持）

> `PasswordExpiryDays` を変更しても **既存ユーザーの `PasswordExpiresAt` は再計算されない**。
> 次回パスワード変更時から新しい期限が適用される。
> `SessionIdleMinutes` を変更した場合、**ログイン中のクライアントには次回ログインまで反映されない**。

---

## 認証まわりの仕組み (運用上の挙動)

### リフレッシュトークンのローテーション

- 旧来の「リフレッシュ毎に LastUsedAt 更新だけ」から、**毎回新トークンを発行する** 方式に変更
- リフレッシュ成功すると古いトークンは即座に `IsRevoked=true` になり、新しい `RefreshTokenId` と `RefreshToken` がレスポンスに含まれる
- クライアント (`SessionManager`) は自動で新トークンに差し替える
- **失効済みトークンが再度提示された場合は再利用とみなし、当該ユーザーのアクティブなリフレッシュトークン全てを失効** (`token_reuse_detected`)
  - これは盗難検知機構（同じトークンを 2 箇所から使うと両方失効）
  - 該当ユーザーは強制再ログインになる

### ログインのレート制限

- `/api/auth/login` と `/api/auth/auto-login` に IP 単位で固定ウィンドウ制限
- デフォルト 10回/分（`Auth:LoginPerMinutePerIp`）
- 超過すると HTTP 429（Too Many Requests）
- 内部 NAT 等で多数ユーザが同一 IP の場合は値を上げる

### Agent 認証 (mTLS / 共有秘密)

- **Server → Agent (`/agent/*`)**: Agent inbound は `CentralOrSharedSecret` ポリシーで保護
  - mTLS が成立しクライアント証明書サムプリントが `Auth:CentralCertificateThumbprint` と一致する OR
  - `X-Watashi-Secret` ヘッダの値が Agent の `Auth:SharedSecret` と一致する
  - どちらでもなければ 401
- **Agent → Server (`/api/internal/*`)**: Server 側で `Agent` 認可ポリシーが要求
  - クライアント証明書サムプリントを `ExecutionNode.ClientCertificateThumbprint` と照合
  - 一致した ExecutionNode 名（=AgentId）を Claim として持つ
  - ハートビートのリクエストボディに含まれる AgentId と、証明書から取得した AgentId が一致しないと 403

---

## トラブル対応

### あるユーザーだけ全画面でエラーが出る
- 操作ログタブで Username 絞り込みして直近のエラーを確認
- `denied` (権限不足) / `not_found` (ホスト or 共有が削除済) / `parent_changed` (リネーム禁止違反)

### 「だれが何時に何を変えたか」を追跡したい
- 操作ログタブ → Op フィルタに `ADMIN_` プレフィックスを部分一致で指定はできないので、`ADMIN_USER_CREATE` のように完全一致で個別指定
- まとめて見たい場合は CSV エクスポート → Excel フィルタで `ADMIN_` で絞り込み

### ログインの 429 が頻発する
- 同一 IP からの試行が 1 分 10 回を超えている
- 内部 NAT 等で同一 IP 経由ユーザが多い場合は `Auth:LoginPerMinutePerIp` を上げる（例: 50）
- 攻撃の可能性もあるので、まず IP を確認

### あるユーザーが頻繁に「再ログインしてください」になる
- リフレッシュトークン再利用検知（`token_reuse_detected`）の可能性
- 原因として: 複数端末で同一アカウントを使っている、Credential Manager の古いトークンが残っている、自動ログインを跨いで動作している
- 該当ユーザーに「全デバイス失効」→ 再度信頼デバイス登録を案内

### Agent が突然 Unhealthy になった
1. Agent サーバーで `Get-Service Watashi.Agent` で稼働確認
2. Agent ログ (`C:\ProgramData\WatashiAgent\logs\`) でエラー確認
3. Agent → 中央のネットワーク疎通 (`Test-NetConnection central.internal 8443`)
4. 中央 DB の `ExecutionNodes.Name` と Agent 側 `appsettings.json` の `Agent:AgentId` が一致しているか
5. (mTLS モード) Agent クライアント証明書の有効期限・サムプリント不一致を確認

### Agent から 401/403 が頻発
- (mTLS) `Auth:CentralCertificateThumbprint` と中央が実際に提示している証明書が違う
- (mTLS) 証明書チェーン・ストアにルートが入っていない
- (HTTP) `Auth:SharedSecret` が両側で違う

### admin がロックアウトされた (最悪のケース)
DB 直接更新で解除:
```powershell
sqlite3.exe C:\ProgramData\Watashi\watashi.db
sqlite> UPDATE Users SET IsLocked=0, FailedLoginCount=0 WHERE Username='admin';
sqlite> .quit
```

### Encryption MasterKey を変えたくなった
- 既存の暗号化済み CIFS パスワードはすべて復号不能になる
- 全ホストの CIFS パスワードを管理画面から再設定する必要がある
- 手順:
  1. 新キーを別途生成 (旧キーは捨てない)
  2. 旧キーで全ホストの平文パスワードを取得 (運用記録から)
  3. `appsettings.json` の MasterKey を新キーに差し替え
  4. サーバー再起動
  5. 全ホストの編集画面でパスワードを再入力 (新キーで再暗号化される)

### ストレージ容量逼迫
- AuditLogs テーブルが肥大化している可能性
- 即時実行:
  ```sql
  DELETE FROM AuditLogs WHERE Timestamp < datetime('now', '-180 days');
  VACUUM;
  ```
- 日次削除タスクが回っているか確認

### Agent が「503 Service Unavailable」を頻発
- `MaxConcurrency` を超えている → Admin → ノードタブで値を上げる
- 同時転送中のクライアント数を確認
- ハートビート未着で Unhealthy 判定されている可能性も（ノードタブで `HealthStatus` 確認）

### 操作が遅い / SMB 操作毎にハンドシェイクが発生
- 中央 `appsettings.json` の `Cifs:SessionIdleSeconds` を 60 → 120 などに伸ばす（プールアイドル時間が長くなり再接続が減る）
- `Cifs:MaxSessionsPerKey` を 4 → 8 など増やす（同時操作が多い環境向け）
- いずれもサーバ再起動が必要

### ファイル一覧が遅い
- ディレクトリに大量のファイル (10 万件以上) → ページング (200/ページ) が効くが、最初の COUNT 取得で時間がかかる
- 該当ディレクトリの整理 (アーカイブ化) を運用で

### パスワード変更を全ユーザーに強制したい
```sql
UPDATE Users SET MustChangePassword=1, PasswordExpiresAt=datetime('now');
```
次回ログインから強制画面が出る。
