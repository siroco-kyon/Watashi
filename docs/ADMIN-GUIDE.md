# Watashi 管理者ガイド

管理者向けの運用ガイド。
管理機能の使い方と典型的なワークフローを順に説明。

- [管理画面の開き方](#管理画面の開き方)
- [初期セットアップフロー](#初期セットアップフロー)
- [タブ別の使い方](#タブ別の使い方)
  - [ユーザー](#ユーザー)
  - [ホスト (CIFS ファイルサーバー)](#ホスト-cifs-ファイルサーバー)
  - [共有](#共有)
  - [テンプレート](#テンプレート-権限テンプレート)
  - [ユーザー権限](#ユーザー権限-サーバー--共有--パス-パスブラウザ付き)
  - [権限セット (PermissionBundle)](#権限セット-permissionbundle)
  - [信頼デバイス](#信頼デバイス)
  - [実行ノード](#実行ノード)
  - [操作ログ](#操作ログ)
  - [システム設定](#システム設定)
- [認証まわりの仕組み (運用上の挙動)](#認証まわりの仕組み-運用上の挙動)
- [トラブル対応](#トラブル対応)

---

## 管理画面の開き方

1. `IsAdmin = true` のユーザーでログイン
2. メイン画面右上の **[管理]** ボタン (朱色) をクリック (旧版のメニューバーから現行の Header ボタンに変更済み)
3. AdminWindow がタブ表示で開く

> 一般ユーザーには「管理」ボタン自体が表示されません(非管理者はリモートペインでも空状態案内のみ)。
> 管理画面は別ウィンドウなので、X で閉じればメインに戻ります。Esc / 「戻る」ボタンは廃止しました。

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
8. ユーザーに通知 (Windows 統合認証を有効にした環境は URL のみ。その他は初期 PW も安全に通知)
```

---

## タブ別の使い方

### ユーザー

ユーザーアカウントの追加 / 削除 / 管理者フラグ変更 / ロック解除 / パスワード関連操作 / CSV 一括取込・出力。

> 💡 **Windows 統合認証を有効にした環境では、初期パスワードの配布は不要です。** ユーザーを作成すると「初回設定待ち」の状態になり、本人が初回ログイン時に自分でパスワードを決めます。本人確認は Windows 統合認証 (ログオン中の Windows ユーザー名 = GID = Watashi ユーザー名) で行われます。無効な環境では「初期 PW 発行」を使います。
>
> 詳細は「[初回パスワード設定の流れ](#初回パスワード設定の流れ)」を参照。

| 操作 | 説明 | 監査ログ |
|---|---|---|
| 新規ユーザー作成 | 右パネルに Username (GID) / 管理者チェック → 「作成」。パスワード欄はありません | `ADMIN_USER_CREATE` |
| 削除 | 行選択 → 「🗑 削除」 (確認ダイアログ) | `ADMIN_USER_DELETE` |
| ロック解除 | 連続失敗 (既定 15 回) でロックされたユーザーを行選択 → 「🔓 ロック解除」 | `ADMIN_USER_UNLOCK` |
| 管理者フラグ変更 | PATCH 経由 | `ADMIN_USER_UPDATE` |
| 初期 PW 発行 | 行選択 → 「🔑 初期PW発行」。**Windows 認証が使えない端末向けの代替手段**。従来どおり管理者がパスワードを決め、初回ログインで強制変更させる | `ADMIN_USER_RESET_PW` |
| 初回設定に戻す | 行選択 → 「↩ 初回設定に戻す」。パスワードを破棄し、本人に再度設定させる | `ADMIN_USER_REQUIRE_SETUP` |
| 全デバイス失効 | 信頼デバイスタブから | `ADMIN_USER_REVOKE_DEVICES` |
| **CSV エクスポート** | 「📤 CSV出力」→ SaveFileDialog で保存先指定 | `ADMIN_USER_EXPORT` |
| **CSV インポート** | 「📥 CSV取込」→ ファイル選択 → モード選択 | `ADMIN_USER_IMPORT` |

> 管理者フラグを変更すると、対象ユーザーの発行済み access/refresh token は即時失効します。
> 昇格・降格後の権限を確実に反映するため、対象ユーザーは再ログインが必要です。アカウントが自動ロックされた場合も、既存 access token は次の API 呼び出しから拒否されます。

一覧の「PW状態」列で各ユーザーの状態がわかります。

| 表示 | 意味 |
|---|---|
| 初回設定待ち | まだパスワードが決まっていない。この状態ではログインできない |
| 有効 | 通常 |
| 要変更 | 管理者が初期 PW を発行した直後など。次回ログインで変更を強制される |
| 期限切れ | `PasswordExpiryDays` を過ぎた。次回ログインで変更を強制される |

> ⚠️ **管理者ロックアウト防止 (サーバ強制)** — 次の操作はサーバが `400 Bad Request` で拒否します:
> - **自分自身を削除する** / **自分自身の管理者フラグを外す** / **自分自身を初回設定待ちに戻す**
> - **他にアクティブな管理者がいない状況で、最後の管理者を削除する / 降格する / 初回設定待ちに戻す**
>
> 「アクティブな管理者」は**ロック中のユーザーと初回設定待ちのユーザーを除外して**カウントします。どちらもログインできないため、数えてしまうと実際に使える最後の管理者を消せてしまうためです。ロック中の管理者しか居ない状態でその管理者を削除しようとした場合も拒否されるので、先に対象をロック解除してから操作してください。
>
> どうしても締め出された場合の最終手段は「[admin がロックアウトされた / パスワード忘れた (最悪のケース)](#admin-がロックアウトされた--パスワード忘れた-最悪のケース)」を参照。

#### 初回パスワード設定の流れ

```
管理者: ユーザーを作成 (パスワード入力なし)
        │
        ├─ ドメイン参加端末 ─────────────────────────────
        │   利用者: Watashi を起動し、GID を入力して「次へ」
        │   → Windows のログオン情報で本人確認 (自動)
        │   → 「初回パスワードの設定」画面が出る
        │   → 本人がパスワードを決めて「設定してログイン」
        │   ★ 管理者が伝えることは「GID でログインしてください」だけ
        │
        └─ ドメイン非参加端末 / Windows 認証が使えない ────
            利用者: GID を入力 →「次へ」→ 通常のパスワード入力欄が出る
            → まだパスワードが無いのでログインできない
            → 管理者に連絡
            → 管理者が「🔑 初期PW発行」で初期パスワードを決めて本人に伝える
            → 従来どおり初回ログイン時に強制変更
```

本人確認は「Windows のログオンユーザー名 = Watashi のユーザー名 (GID)」という運用ルールに依存します。この前提が崩れる環境 (共用 PC など) では下側の経路を使ってください。

複数の独立した Windows ドメインを同時に許可する場合、GID は全許可ドメインを通して一意である必要があります。
同じ GID が複数ドメインに存在し得る場合も、下側の管理者発行経路を使用してください。

ユーザー名の英字は大文字小文字を区別しません。`alice` が存在する状態で `ALICE` は作成できず、ログイン・CSV上書きでも同一ユーザーとして扱われます。既存 DB の更新時にこの組み合わせが残っていると安全のため migration が停止するため、先にアカウントと権限を統合してください。

サーバー側で Windows 統合認証を有効にする手順は [deploy/IIS-HOSTING.md](../deploy/IIS-HOSTING.md) の「Windows 統合認証を有効にする」を参照。**未設定の場合は常に下側の経路 (管理者が初期 PW を発行) になります。**

パスワードを忘れたユーザーへの対応は 2 通りあります。

| 方法 | 使う場面 |
|---|---|
| 「↩ 初回設定に戻す」 | ドメイン参加端末の利用者。本人が自分で決め直せる。パスワードを伝える必要がない |
| 「🔑 初期PW発行」 | それ以外。管理者が決めたパスワードを本人に安全な方法で伝える |

「初回設定に戻す」は、その場でログインできない状態になります。**ログイン中のセッションと「このPCを記憶」も同時に失効させます** — 記憶済み端末を残すと自動ログインでパスワード無しに入れてしまうためです。

#### CSV フォーマット (インポート)

UTF-8 (BOM 推奨)、1 行目はヘッダー必須。**パスワードは CSV に書きません。**

```csv
Username,IsAdmin
G012345,false
G012346,true
G012347,false
```

- 必須列: `Username`
- 任意列: `IsAdmin` (省略時は false)
- 列順は問わない (ヘッダー名で判定)
- 値にカンマ / 改行 / ダブルクォートを含む場合は `"..."` で囲む (RFC4180 風)
- 全行を 1 件ずつ処理。途中で失敗しても他の行は完了する
- 取り込まれた新規ユーザーは**初回設定待ち**になる

> 旧形式 (`Username,Password,IsAdmin`) の CSV もそのまま取り込めます。`Password` 列は**無視され**、完了ダイアログにその旨が表示されます。CSV に書かれたパスワードではログインできないので、手元の古い CSV を作り直す必要はありません。

#### インポートのモード

| モード | 動作 |
|---|---|
| 新規追加のみ (デフォルト) | 既存ユーザー名と衝突する行はスキップ。**「毎回全件更新したくない、追加する人だけ取り込みたい」用途**。 |
| 上書き | 既存ユーザーの `IsAdmin` を更新する。**パスワードには一切触れない**。 |

> ⚠️ 以前の「上書き」は既存ユーザーのパスワードも再設定していましたが、現在はしません。CSV を再取り込みしただけで全員が締め出される事故を防ぐためです。パスワードの再設定が必要な場合は「🔑 初期PW発行」か「↩ 初回設定に戻す」を明示的に使ってください。

完了ダイアログに `追加 N / 更新 N / スキップ N / 失敗 N` の結果と、失敗行の理由(行番号 + ユーザー名 + メッセージ)が表示される。

#### CSV エクスポート

`watashi-users-YYYYMMDD-HHmmss.csv` で保存。
列: `Username, IsAdmin, IsLocked, PasswordStatus, PasswordExpiresAt, LastLoginAt, CreatedAt`
パスワード列は含まれない (DB に平文無いため)。バックアップ・棚卸し用。
`PasswordStatus` は `PendingSetup` / `Active` / `MustChange` / `Expired` のいずれか。

**ユーザー作成時の挙動**:
- パスワードは設定されない (`初回設定待ち`)。DB には誰も知り得ないランダム値が入るので、この状態ではどんなパスワードでもログインできない
- `PasswordExpiresAt = 今 + PasswordExpiryDays` で計算 (本人が設定した時点で再計算される)
- `PasswordSetupExpiryDays` が 0 以外なら、初回設定の受付期限が設定される (既定 0 = 無期限)

**運用 tips**:
- 退職者: 削除でも良いが、監査ログとの紐付けが切れるので「全デバイス失効」+「↩ 初回設定に戻す」で無効化推奨 (パスワードが無効化され、端末とセッションも失効する)

### ホスト (CIFS ファイルサーバー)

中央サーバー / Agent から接続する CIFS サーバーの登録。

**入力項目**:
- 表示名 (例: `経理部ファイルサーバ`)
- ホスト名/IP (例: `fileserver01.corp.local`)
- ポート (デフォルト 445)
- CIFS ユーザー (例: `WATASHI\svc-cifs`)
- CIFS パスワード (目玉アイコンで表示確認可、AES-256-GCM で即座に暗号化される、平文 DB 保存なし)
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

初期 seed (新規 DB ではフルアクセスが Id=1):
| 名前 | Read | Write | Delete | Rename |
|---|:-:|:-:|:-:|:-:|
| フルアクセス | ✓ | ✓ | ✓ | ✓ |
| 読取+書込 | ✓ | ✓ |  |  |
| 読取のみ | ✓ |  |  |  |

#### 各権限でできること

権限テンプレートのチェックは、Watashi クライアントの操作に次のように対応します。

| 権限 | できること | できないこと / 注意 |
|---|---|---|
| Read | リモート一覧表示、フォルダ移動、ダウンロード | Read が無いと一覧取得・ダウンロードは拒否される |
| Write | アップロード、新規フォルダ作成、リネーム先パスへの書き込み確認 | Write だけでは削除やリネーム元の改名はできない |
| Delete | ファイル/フォルダ削除 | 許可パスのルートそのものは Delete があっても削除不可 |
| Rename | 同じ親フォルダ内でのリネーム | 実行には Rename に加えて新しい名前のパスに Write も必要。フォルダを跨ぐ移動は不可 |

よく使うテンプレートの挙動:

| テンプレート例 | 利用者ができること |
|---|---|
| 読取のみ | 一覧表示、フォルダ移動、ダウンロードのみ。アップロード、新規フォルダ、削除、リネームは権限エラー |
| 読取+書込 | 一覧表示、ダウンロード、アップロード、新規フォルダ作成。削除とリネームは不可 |
| 読取+書込+Rename | ファイル名変更まで許可。削除は不可 |
| フルアクセス | 許可パス配下の読み取り、書き込み、削除、リネームすべて可。ただし許可パスのルート自体の削除/リネームは禁止 |

> **表示順** は権限の強さスコア (Read+Write+Delete+Rename の合計) **降順** にクライアント側でソートされるため、既存 DB でもフルアクセスが先頭に来る。
> ユーザー権限の「権限テンプレート」ドロップダウンのデフォルト選択もフルアクセス。

業務に合わせて追加可能 (例: 「読書込+リネームのみ」など)。
使用中のテンプレートは削除不可 (SQL の FK 制約で守られる、削除時にサーバーエラー)。
削除には確認ダイアログあり。
監査ログ: `ADMIN_TEMPLATE_CREATE` / `_UPDATE` / `_DELETE`

### ユーザー権限 (サーバー → 共有 → パス、パスブラウザ付き)

ユーザーへの権限割当。**Watashi の核心機能**。
2026-05 のリリースで **左サイドバーのユーザー一覧 + 右の権限詳細 + 折りたたみ可能な追加カード** に再設計済み。

```
┌──── ユーザー [14] ─┬── 👤 tanaka  「パスを追加しました。」 [🗑 選択を削除] ──┐
│ 🔍 [絞り込み...]   │ ▼ 付与済みのパス  4 件                                  │
├────────────────────┤  ホスト   │ 共有        │ パス             │ テンプレ │
│ admin    1件 (管)  │  test     │ watashitest │ /                │フル..    │
│ tanaka   4件       │  test     │ watashitest │ /新しいフォルダ  │読取のみ  │
│ suzuki   1件       │  test     │ watashitest │ /プロジェ.../長..│フル..    │
│ ito      0件       │  test2... │ ステ.. 共有 │ /                │読書込    │
│ ...                │                                                       │
│ [🔄 再読み込み]    │ ▼ ＋ パスを追加  (Expander, 折りたためる)              │
│                    │  サーバー: [test ▼]      共有: [watashitest ▼]         │
│                    │  権限テンプレート: [フルアクセス ▼]                   │
│                    │  許可パス: [/dept-A]     表示名(任意): [経理部 2026]   │
│                    │  ブラウズ: [/dept-A]               [開く]              │
│                    │  ┌ サブフォルダ一覧 ──────────┐                       │
│                    │  │ 2025  /  2026  /  archive    │                       │
│                    │  └─────────────────────────────┘                       │
│                    │  [ ＋ このパスを追加 ]                                  │
└────────────────────┴────────────────────────────────────────────────────────┘
```

**追加手順** (新フロー):
1. 左サイドバーで対象ユーザーをクリック (必要なら検索ボックスで絞り込み)
2. 右下「＋ パスを追加」セクションで:
   - **サーバー** を選ぶ (test など)
   - **共有** が自動的にそのサーバー配下に絞られる
   - **権限テンプレート** を選ぶ (デフォルトはフルアクセスが先頭)
   - **許可パス** を入力 (例: `/dept-A`、共有全体なら `/`)
   - (任意) **表示名** (例: `経理部 2026 年度`)
3. (任意) **ブラウズ** で実在ディレクトリを開いて、サブフォルダをクリック → 許可パスに自動反映
4. **＋ このパスを追加** を押す

**ユーザーリストの件数バッジ** (例: `tanaka 4件`) は、付与済みパス数を表示。0 件ユーザーも区別できる。

**1 ユーザー × 1 共有に複数のパス権限を持たせられる**:
- 例: `watashitest` 共有について `/dept-A` はフルアクセス、`/shared/templates` は読取のみ

**特殊な制約**:
- 許可パスのルート (例: `/dept-A` 自体) は削除/リネーム不可 (誤削除防止)
- リネームは同じ親ディレクトリ内のみ (フォルダ間移動は禁止)

**削除は確認ダイアログあり**: 「`alice` の `test / watashitest / /dept-A` の権限を削除しますか？」

監査ログ: `ADMIN_PERMISSION_CREATE` (Path に `perm:user=42,share=3,path=/dept-A` 形式で詳細を保持) / `ADMIN_PERMISSION_DELETE`

#### 📦 セットから一括適用 / 📋 他ユーザーから全件コピー

「付与済みのパス」セクションの上にある **クイック追加カード** から、複数行をまとめて付与できます。
- **セットから一括適用**: ドロップダウンで「権限セット」を選び 「適用」ボタン押下
  - 確認ダイアログ: `[はい] 既存重複も上書き  [いいえ] 重複はスキップ (推奨)  [キャンセル]`
  - 結果: 「セット適用: 追加 N / 更新 N / スキップ N」がステータスに表示
  - 監査ログ: `ADMIN_BUNDLE_APPLY` (Path に `bundle:X,user:Y,created:N,...`)
- **他ユーザーから全件コピー**: ドロップダウンでコピー元ユーザーを選び 「コピー」ボタン押下
  - 同じ確認ダイアログ
  - 結果: 「権限コピー: 追加 N / 更新 N / スキップ N」
  - 監査ログ: `ADMIN_PERMISSION_COPY` (Path に `from:X,to:Y,copied:N,...`)

「権限セット」の作成方法は次のセクション参照。

### 権限セット (PermissionBundle)

「経理部標準」「営業部標準」のように **複数の権限行をひとまとめにした再利用可能セット**。
1 ユーザーに繰り返し同じパス群を入力する手間を解消します。

```
┌── 権限セット [3] ──┬── セット内容                          [💾 保存] [🗑 セット削除]
│  📦 経理部標準セット  │  セット名: 経理部標準セット
│  📦 営業部標準セット  │  説明:    経理部員向けの標準権限セット
│  📦 開発部標準セット  │
│                     │  ホスト │ 共有       │ パス           │ テンプレ  │ 表示名
│                     │  test   │ watashitest│ /経理         │ フル...   │ 経理 (フル)
│                     │  test   │ watashitest│ /共通         │ 読取のみ   │ 経理 (参照)
│                     │  arch   │ 2026       │ /             │ 読取のみ   │ アーカイブ
│ [+ 新規セット]      │
│ [🔄 再読み込み]     │  ＋ セットに行を追加: [共有▼] [テンプレ▼] [パス] [表示名] [追加]
└─────────────────────┴────────────────────────────────────────────────────────────
```

**ワークフロー**:
1. **[+ 新規セット]** で名前を入力 → 空のセットが作成される
2. 左でセットを選択して右に詳細表示
3. **+ セットに行を追加** で 共有 / テンプレート / 許可パス / 表示名 を入力 → 「+ 行を追加」
4. すべての行を入れたら 上部の **[💾 保存]** で確定
5. **ユーザー権限**タブに移動して 各ユーザーへ「セットから一括適用」

**特性**:
- 1 セットあたり何行でも持てる
- セットの編集は **「全件置換」** (シンプル運用、差分編集ではない)
- セット自体を削除しても、既に適用済みの UserPermission は残る (リンクではなくスナップショット展開)
- 監査ログ: `ADMIN_BUNDLE_CREATE` / `_UPDATE` / `_DELETE` / `_APPLY`

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
| 経由 Agent | 任意。1段チェーン時に入口 Agent を選ぶ (例: `bastion-a`) |
| ClientCertificateThumbprint | mTLS モード時、Agent が提示するクライアント証明書サムプリント |
| MaxConcurrency | Agent の同時接続上限 (デフォルト 20) |

**HealthStatus**:
- `Healthy`: ハートビート受信済み (Agent) / 起動中 (Direct)
- `Unhealthy`: 90 秒以上ハートビートなし (Agent のみ)
- `Unknown`: まだ一度もハートビート受信していない (新規登録 Agent)

**1段チェーン**:
- `Server → Agent A → Agent B → CIFS` の 1段だけ対応
- Agent A/B 間は HTTP + `Auth:SharedSecret` 前提。mTLS チェーンと 2段以上のチェーンは非対応
- 中央から直接届かない Agent B は、ExecutionNode では Agent B の `Endpoint` を登録し、`経由 Agent` に Agent A を指定する
- Agent B が中央へ heartbeat できない構成では `HealthStatus=Unknown` のままでも操作可能。実際の到達性は操作時に Agent A → Agent B の HTTP 結果で判定される

中央サーバーは Direct Agent / 経由 Agent が Unhealthy の場合、アクセスを即座に 503 で返す。
監査ログ: `ADMIN_NODE_CREATE` / `_UPDATE` / `_DELETE` / `_REGEN_KEY`

### 操作ログ

全ファイル操作と管理者操作の監査ログ。

**フィルタ**:
- User: ユーザー名で完全一致
- Op: ファイル操作（`READ` / `WRITE` / `DELETE` / `RENAME`）または管理者操作（`ADMIN_*`）
- From / To: UTC 期間指定
- 検索ボタンでクエリ実行

**カラム**:
- Timestamp / Username / Op / Result / **ホスト / 共有** / Path / Bytes / Dur(ms) / Err
- **「ホスト / 共有」列** はホスト名 (1行目) と共有の表示名 (2行目) の 2 行レイアウト。**Path 列にホバーすると**「ホスト / 共有 :: パス」のフル表記がツールチップで表示される (例: `経理部FS / share-keiri :: /dept-A/file.txt`)
- ログ取得後にホスト/共有が削除された場合は `(削除済 host#42)` / `(削除済 share#99)` と表示される (ログそのものは保全)

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
- BOM 付き UTF-8、Excel でそのまま開ける (数式インジェクション対策済み)
- 列: `Id, Timestamp, Username, Operation, **Location**, HostId, **HostName**, ShareId, **ShareName**, Path, TargetPath, Result, Error, ClientIp, Bytes, DurationMs, Protocol, NodeId, PermId`
  - **Location** 列は「ホスト / 共有 :: パス」の文字列で、Excel での目視確認に最適
  - HostId/ShareId/Path も従来通り残してあるので BI 連携の後方互換あり
- サーバ側は `AsNoTracking + Select` で射影し、500 件ずつバッチフラッシュ（大規模監査テーブルでも OOM しない）

**保管期間**:
- デフォルト 365 日 (`SystemSettings.AuditLogRetentionDays` で変更可能。0 以下にすれば永久保管)
- 日次バッチ (`AuditLogPurgeService`) で自動削除
- 月次 VACUUM で DB サイズ圧縮

### システム設定

`SystemSettings` テーブルの編集。

| キー | 用途 |
|---|---|
| `PasswordExpiryDays` | パスワード有効期限 (デフォルト 90) |
| `PasswordWarningDays` | 期限警告を出す日数 (デフォルト 14) |
| `AgentMaxConcurrency` | Agent 新規登録時のデフォルト (デフォルト 20) |
| `SessionIdleMinutes` | クライアントアイドルタイムアウト (デフォルト 30) ※ログイン時にクライアントへ配信される |
| `AuditLogRetentionDays` | **監査ログ保管日数 (デフォルト 365)**。`AuditLogPurgeService` が日次で古いログを自動削除する。`0` 以下を指定すると削除しない (永久保管) |
| `MaxFailedLoginAttempts` | **連続ログイン失敗で自動ロックするまでの回数 (デフォルト 15)**。値を小さくするほど総当たり耐性は上がるが、誤入力でのロックも起きやすくなる |

行選択 → 編集欄で値を変更 → 「保存」。
監査ログ: `ADMIN_SETTING_UPDATE`（Path に `setting:PasswordExpiryDays` 形式で対象キーを保持）

> `PasswordExpiryDays` を変更しても **既存ユーザーの `PasswordExpiresAt` は再計算されない**。
> 次回パスワード変更時から新しい期限が適用される。
> `SessionIdleMinutes` を変更した場合、**ログイン中のクライアントには次回ログインまで反映されない**。
> `AuditLogRetentionDays` は **次の日次パージ実行時に反映**される (`AuditLogPurgeService` は起動 30 秒後 + 以降 24 時間毎に実行)。即時反映したい場合はサーバ再起動 + 30 秒待ち。

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
  - mTLS モードでは、Agent が提示するクライアント証明書サムプリントを `ExecutionNode.ClientCertificateThumbprint` と照合
  - 共有秘密モードでは、Agent の `Auth:SharedSecret` と Server の `Routing:SharedSecret` を同じ値にし、`X-Watashi-Secret` で照合
  - mTLS 時は、ハートビートのリクエストボディに含まれる AgentId と、証明書から取得した AgentId が一致しないと 403

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
- `token_reuse_detected`: 複数端末や古い Credential Manager の token が再提示された可能性。対象ユーザーに「全デバイス失効」→ 再度信頼デバイス登録を案内
- `password_changed`: パスワード変更・管理者権限変更・初回設定への差し戻しで資格情報世代が変わった。意図した操作なら再ログインを案内
- `account_locked`: 連続失敗でロック中。管理画面でロック解除してから再ログインを案内
- `device_revoked`: 記憶済み端末が管理者または別端末登録で失効。必要なら再登録を案内
- 古い API 応答や refresh が後から届いても、新しく成立したログインは失効しないようクライアント側で session generation を照合する

### Agent が突然 Unhealthy になった
1. Agent サーバーで `Get-Service Watashi.Agent` で稼働確認
2. Agent ログ (`C:\ProgramData\WatashiAgent\logs\`) でエラー確認
3. Agent → 中央のネットワーク疎通 (`Test-NetConnection central.internal 8443`)
4. 中央 DB の `ExecutionNodes.Name` と Agent 側 `appsettings.json` の `Agent:AgentId` が一致しているか
5. (mTLS モード) Agent クライアント証明書の有効期限・サムプリント不一致を確認

### Agent から 401/403 が頻発
- (mTLS) `Auth:CentralCertificateThumbprint` と中央が Agent へ提示しているクライアント証明書が違う
- (mTLS) 証明書チェーン・ストアにルートが入っていない
- (HTTP) `Auth:SharedSecret` が両側で違う

### admin がロックアウトされた / パスワード忘れた (最悪のケース)

別の管理者ユーザーがいれば、その人が管理画面 → ユーザータブから対象を選んで「🔓 ロック解除」「🔑 PWリセット」で復旧可能。**他に管理者がいない** 場合は DB 直接更新。

#### ロック解除だけしたい (パスワードは覚えている)

`sqlite3.exe` が入っていれば:
```powershell
sqlite3.exe C:\ProgramData\Watashi\watashi.db
sqlite> UPDATE Users SET IsLocked=0, FailedLoginCount=0 WHERE Username='admin';
sqlite> .quit
```

sqlite3.exe が無くても publish フォルダの `Microsoft.Data.Sqlite.dll` 経由で PowerShell から叩ける (下の復旧スクリプトを `UPDATE` 文だけに簡略化して流用)。

#### ロック + パスワード忘れの両方 (=完全復旧)

サーバ publish フォルダの `BCrypt.Net-Next.dll` で新ハッシュを生成し、`Microsoft.Data.Sqlite.dll` で DB に直接 UPDATE する。`sqlite3.exe` の追加インストール不要。

```powershell
# === 環境に合わせて書き換え ===
$publishDir  = "C:\Program Files\Watashi\Server"   # Watashi.Server.exe があるフォルダ
$dbPath      = "C:\ProgramData\Watashi\watashi.db"
$newPassword = "RecoverMe2026!@#"                  # 12 字 + 大小数記号

# 0. サーバ停止 (SQLite 排他ロック解放)
Stop-Service Watashi.Server -ErrorAction SilentlyContinue

Push-Location $publishDir
try {
    # 1. BCrypt ハッシュ生成
    Add-Type -Path ".\BCrypt.Net-Next.dll"
    $hash = [BCrypt.Net.BCrypt]::HashPassword($newPassword)
    Write-Host "★ 仮パスワード: $newPassword`n  ハッシュ: $hash"

    # 2. SQLite に UPDATE
    Add-Type -Path ".\SQLitePCLRaw.batteries_v2.dll"
    [SQLitePCL.Batteries_V2]::Init()
    Add-Type -Path ".\Microsoft.Data.Sqlite.dll"
    $conn = New-Object Microsoft.Data.Sqlite.SqliteConnection("Data Source=$dbPath")
    $conn.Open()
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = @"
UPDATE Users
SET PasswordHash = @h,
    IsLocked = 0,
    FailedLoginCount = 0,
    MustChangePassword = 1,
    PasswordChangedAt = @now,
    PasswordExpiresAt = @exp
WHERE Username = 'admin'
"@
    $p1=$cmd.CreateParameter(); $p1.ParameterName="@h"  ; $p1.Value=$hash
    $p2=$cmd.CreateParameter(); $p2.ParameterName="@now"; $p2.Value=[DateTime]::UtcNow.ToString("o")
    $p3=$cmd.CreateParameter(); $p3.ParameterName="@exp"; $p3.Value=[DateTime]::UtcNow.AddDays(90).ToString("o")
    $cmd.Parameters.Add($p1)|Out-Null; $cmd.Parameters.Add($p2)|Out-Null; $cmd.Parameters.Add($p3)|Out-Null
    $rows = $cmd.ExecuteNonQuery()
    $conn.Close()

    if ($rows -eq 0) { Write-Warning "admin が見つからない。$dbPath を確認" }
    else { Write-Host "`n✓ リセット完了 ($rows 行)。サーバ起動して '$newPassword' でログイン → 即パスワード変更" -ForegroundColor Green }
}
finally { Pop-Location }

# 3. サーバ再起動
Start-Service Watashi.Server
```

このスクリプトの効果:
- `PasswordHash` 上書き → 仮パスでログイン可
- `IsLocked=0` / `FailedLoginCount=0` → ロック解除
- `MustChangePassword=1` → 次ログインで強制変更画面が出る
- `PasswordChangedAt` 更新 → 既存 refresh token を自動失効 (横取り防止)
- `PasswordExpiresAt` 再設定

実行後は `admin` + 仮パスでログイン → 強制変更画面で本番用パスワードに置き換え → **パスマネ等に保管**して二度と忘れないこと。

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
- まず確認: システム設定の `AuditLogRetentionDays` が意図通りか (0 だと永久保管でテーブルが増え続ける仕様)。サーバー内蔵の `AuditLogPurgeService` が起動30秒後+以降24時間毎に、この設定日数を超えたログを自動削除する (appsettings.json ではなく管理画面/`PUT /api/admin/settings/AuditLogRetentionDays` で変更、再起動不要)
- 保管日数を短くしても即座に空き容量を確保したい場合のみ、手動で以下を実行 (自動パージを待てない緊急時向け):
  ```sql
  DELETE FROM AuditLogs WHERE Timestamp < datetime('now', '-180 days');
  VACUUM;
  ```
- これを日次の外部タスクとして恒常的に回すことは避ける。`AuditLogRetentionDays` の設定と食い違う保管期間になり、特に永久保管(0)設定時に矛盾する

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
