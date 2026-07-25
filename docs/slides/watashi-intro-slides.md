---
marp: true
theme: default
paginate: true
size: 16:9
header: ""
footer: "⛩ Watashi — 社内 CIFS ファイル管理ツール"
style: |
  /* ===== Watashi ブランドテーマ (朱色 #C73E1D / 紺 #1B2A41) ===== */
  :root {
    --accent: #C73E1D;
    --accent-soft: #F9E5E0;
    --header: #1B2A41;
    --bg: #F6F7F9;
    --text: #1F2328;
    --muted: #6B7280;
    --ok: #1A7F37;
  }
  section {
    background: var(--bg);
    color: var(--text);
    font-family: "Yu Gothic UI", "Meiryo", "Segoe UI", sans-serif;
    font-size: 24px;
    padding: 60px 70px;
    line-height: 1.55;
  }
  section h1 { color: var(--header); font-size: 1.5em; }
  section h2 {
    color: var(--header);
    border-bottom: 4px solid var(--accent);
    padding-bottom: 6px;
    font-size: 1.25em;
  }
  section h3 { color: var(--accent); font-size: 1.05em; }
  section strong { color: var(--accent); }
  section footer { color: var(--muted); font-size: 0.55em; }
  section table { font-size: 0.85em; margin: 0 auto; }
  section th { background: var(--header); color: #fff; }
  section code { background: #EAEEF3; color: var(--header); padding: 1px 6px; border-radius: 4px; }

  /* タイトルスライド・章扉 */
  section.lead {
    background: linear-gradient(135deg, var(--header) 0%, #243B5C 100%);
    color: #fff;
    text-align: center;
  }
  section.lead h1 { color: #fff; font-size: 2.1em; }
  section.lead h2 { color: var(--accent-soft); border: none; }
  section.lead p, section.lead li { color: #D5D9DE; }
  section.lead footer, section.lead header { display: none; }
  section.lead::after { color: #9CA3AF; }

  /* 章扉 (朱色) */
  section.chapter {
    background: linear-gradient(135deg, var(--accent) 0%, #8C2911 100%);
    color: #fff;
    text-align: center;
    display: flex;
    flex-direction: column;
    justify-content: center;
  }
  section.chapter h1 { color: #fff; font-size: 1.9em; }
  section.chapter p { color: #F9E5E0; font-size: 1.1em; }
  section.chapter footer, section.chapter header { display: none; }

  /* 2 カラムレイアウト */
  .cols { display: flex; gap: 32px; align-items: stretch; }
  .cols > div { flex: 1; }

  /* カード */
  .card {
    background: #fff;
    border: 1px solid #D5D9DE;
    border-radius: 12px;
    padding: 14px 20px;
    margin: 8px 0;
    box-shadow: 0 2px 6px rgba(27,42,65,.06);
  }
  .card h3 { margin-top: 0; }

  /* ★ 画像プレースホルダー: スクリーンショット等をここに貼る ★ */
  .img-placeholder {
    border: 3px dashed var(--accent);
    border-radius: 12px;
    background: var(--accent-soft);
    color: var(--accent);
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    text-align: center;
    font-weight: bold;
    min-height: 240px;
    padding: 16px;
  }
  .img-placeholder small { font-weight: normal; color: #A83216; font-size: 0.75em; }
  .img-placeholder.sm { min-height: 80px; font-size: 0.85em; }
  .img-placeholder.tall { min-height: 280px; }
  .center { text-align: center; }

  /* 強調バッジ */
  .badge {
    display: inline-block;
    background: var(--accent);
    color: #fff;
    border-radius: 999px;
    padding: 2px 16px;
    font-size: 0.8em;
    font-weight: bold;
    margin-right: 8px;
  }
  .ok { color: var(--ok); font-weight: bold; }
  .ng { color: #CF222E; font-weight: bold; }
  .small { font-size: 0.8em; color: var(--muted); }

  /* フロー図 (構成図用) */
  .flow { display: flex; align-items: center; justify-content: center; gap: 8px; margin: 14px 0; }
  .flow .node {
    background: #fff; border: 2px solid var(--header); border-radius: 10px;
    padding: 10px 16px; text-align: center; font-weight: bold; color: var(--header);
    font-size: 0.85em; line-height: 1.35; box-shadow: 0 2px 6px rgba(27,42,65,.08);
  }
  .flow .node small { display: block; font-weight: normal; color: var(--muted); font-size: 0.75em; }
  .flow .arrow { text-align: center; color: var(--accent); font-weight: bold; font-size: 0.65em; line-height: 1.3; white-space: nowrap; }
  .flow .arrow .line { display: block; font-size: 2.1em; line-height: 1; }
---

<!-- _class: lead -->

# ⛩ Watashi

## 社内ファイルサーバーを「安全に・カンタンに」使うためのツール

社内向け説明資料

<!--
ここは表紙です。発表者名・部署名・日付を下に追記してください。
例: 2026年6月◯日 / ○○部 ○○
-->

---

## 本日お話しすること

<div class="card">

1. **Watashi とは？** — 何をするツールか、なぜ必要か
2. **画面イメージ** — 実際の使い心地
3. **ポイント① 通信の暗号化 (HTTPS)** — 中身を見られない・改ざんされない
4. **ポイント② ユーザーごとの権限管理** — 必要な人に、必要な範囲だけ
5. **ポイント③ 管理がラク** — 日々の運用負担を最小に
6. **導入の流れ・よくある質問**

</div>

> 専門用語はできるだけ使わずに説明します。詳細仕様は別資料にあります。

---

<!-- _class: chapter -->

# 1. Watashi とは？

---

## Watashi とは — ひとことで

> 社内のファイルサーバー (共有フォルダ) へ、
> **安全に・記録を残しながら** アクセスするための社内専用ツールです。

<div class="cols">
<div class="card">

### こんな道具です

- 画面は「**左に自分の PC / 右に社内サーバー**」の 2 画面構成
- マウス操作でファイルをアップロード / ダウンロード
- 昔ながらのファイル転送ソフト (FFFTP など) を、**より安全で・管理しやすくした後継**

</div>
<div>

<div class="flow">
  <div class="node">🖥 自分の PC<small>専用アプリ (左の画面)</small></div>
  <div class="arrow">ファイル<span class="line">⇄</span></div>
  <div class="node">🗄 社内ファイル<br>サーバー<small>(右の画面)</small></div>
</div>

<div class="img-placeholder">
📷 ここにスクリーンショット<br>メイン画面 (2 ペイン全体)
<small>ログイン後のメイン画面を等倍でキャプチャ</small>
</div>

</div>
</div>

---

## これまでの困りごと → Watashi でこう変わる

| これまでの困りごと | Watashi では |
|---|---|
| 通信の中身が **見られてしまうかも** | <span class="ok">✔</span> **HTTPS で暗号化**、盗み見できない |
| **全員が** あらゆるフォルダを見られる | <span class="ok">✔</span> **人ごと・フォルダごと** に権限を設定 |
| 誰がどのファイルを触ったか分からない | <span class="ok">✔</span> **全操作を自動で記録** (監査ログ) |
| 退職者のアクセスが残り続ける | <span class="ok">✔</span> 管理画面から **ワンクリックで無効化** |
| アプリの配布・更新が手間 | <span class="ok">✔</span> **自動配布・自動更新** (ClickOnce) |
| 直接つなげないサーバーがある (踏み台) | <span class="ok">✔</span> **踏み台越しのアクセスに対応** |

---

## 全体の構成 (イメージ)

<div class="flow">
  <div class="node">🖥 社員の PC<small>専用アプリ</small></div>
  <div class="arrow">🔒 HTTPS<span class="line">⇄</span>暗号化通信</div>
  <div class="node">🏢 中央サーバー<small>社内に 1 台</small></div>
  <div class="arrow">(必要なら)<span class="line">→</span>🔒 HTTPS</div>
  <div class="node">🪜 踏み台<br>サーバー<small>中継役</small></div>
  <div class="arrow"><span class="line">→</span></div>
  <div class="node">🗄 社内ファイル<br>サーバー</div>
</div>

<div class="cols">
<div class="card">

### 中央サーバー (社内に 1 台)

アクセス権の管理・操作記録・ファイル受け渡しの**中継役**。
ここを通ることで「権限チェック」と「記録」が必ず行われます。

</div>
<div class="card">

### 踏み台サーバー (必要な場合のみ)

セキュリティ上、直接つなげない場所にあるサーバーへ**中継してアクセス**するためのもの。
ここも暗号化通信に対応しています。

</div>
</div>

> 💡 データは **すべて社内で完結** します。外部のクラウドには一切出ません。

---

<!-- _class: chapter -->

# 2. 画面イメージ

実際の使い心地を見てみましょう

---

## ログイン → ファイル操作まで

<div class="cols">
<div>

<div class="img-placeholder">
📷 ここにスクリーンショット<br>ログイン画面
<small>鳥居ロゴ + ユーザー名/パスワード入力欄が写るように</small>
</div>

**① ログイン**
- 配布されたアプリを起動してログインするだけ
- 接続先は管理者が設定済み — **利用者の設定作業はゼロ**

</div>
<div>

<div class="img-placeholder">
📷 ここにスクリーンショット<br>ファイル転送中の画面
<small>進捗バー表示中のメイン画面 (アップロード中など)</small>
</div>

**② ファイル操作**
- 左 (自分の PC) ⇄ 右 (サーバー) でアップロード / ダウンロード
- 大きなファイルも OK (サイズ無制限・進捗表示付き)

</div>
</div>

---

## 利用者にやさしい工夫

<div class="cols">
<div class="card">

### 迷わない・間違えない

- Explorer 風の見た目とマウス操作 — **教育コストほぼゼロ**
- 削除前には必ず確認ダイアログ
- ダウンロード途中で失敗しても**不完全なファイルが残らない**
- パスワード入力欄に 👁 ボタン — 打ち間違い確認に便利

</div>
<div class="card">

### 手間がかからない

- **自動ログイン** (PC を記憶) 対応
- 一定時間操作がないと**自動ログアウト** (初期値 30 分)
- 新しい版が出ると**自動で更新** — 利用者の作業不要
- 見えるのは**自分に許可されたフォルダだけ**

</div>
</div>

<p class="small center">📷 (任意) ここに補足スクリーンショットを貼れます: 削除確認ダイアログ / 自動ログインのチェックボックス など</p>

---

<!-- _class: chapter -->

# 3. ポイント① 通信の暗号化

<p>HTTPS — 途中で「見られない・書き換えられない」</p>

---

## なぜ暗号化が必要？

<div class="cols">
<div class="card">

### 🔓 暗号化なし (HTTP) だと…

ネットワークの途中を流れるデータは**ハガキ**と同じ。
経路上で覗かれると、**ファイルの中身もパスワードも丸見え**になりえます。

<span class="ng">✘ 盗聴</span> / <span class="ng">✘ 改ざん</span> / <span class="ng">✘ なりすまし</span>

</div>
<div class="card">

### 🔒 HTTPS だと…

データは**封筒に入れて封をした手紙**。
途中で覗いても**意味不明な暗号にしか見えません**。

<span class="ok">✔ 中身を見られない</span>
<span class="ok">✔ 書き換えを検知できる</span>
<span class="ok">✔ 接続先が本物だと確認できる</span>

</div>
</div>

```text
  HTTP  : PC ── 「給与データ.xlsx」「パスワード: abc」 ──▶ サーバー   ← 誰でも読める
  HTTPS : PC ── 「x8#kQ...zR2$」(暗号文) ─────────────▶ サーバー   ← 読めない 🔒
```

---

## Watashi の暗号化は「全区間」カバー

<div class="flow">
  <div class="node">🖥 社員の PC</div>
  <div class="arrow">区間①<span class="line">⇄</span>🔒 HTTPS</div>
  <div class="node">🏢 中央サーバー<small>預かる接続用パスワードも<br>AES-256 で暗号化して保管</small></div>
  <div class="arrow">区間②<span class="line">⇄</span>🔒 HTTPS + mTLS</div>
  <div class="node">🪜 踏み台<br>サーバー</div>
  <div class="arrow"><span class="line">→</span></div>
  <div class="node">🗄 ファイル<br>サーバー</div>
</div>

<div class="cols">
<div class="card">

### 区間① PC ⇄ 中央サーバー

社員の通信はすべて **HTTPS で暗号化**。
社内の証明書で「接続先が本物のサーバーか」も検証します。

</div>
<div class="card">

### 区間② 中央サーバー ⇄ 踏み台

ここも HTTPS。さらに **mTLS (相互認証)** に対応 —
**お互いに証明書を見せ合って本人確認**してから通信します。
偽サーバーのなりすましを防ぎます。

</div>
</div>

> 💡 証明書は社内の発行サービスから**自動で更新**される仕組みを用意済み。期限切れ事故を防ぎます。

---

## 通信以外の「守り」も多層で

| 守るもの | しくみ |
|---|---|
| ログインパスワード | 強力なハッシュ化 (bcrypt) で保存 — **管理者でも元に戻せない** |
| サーバーが預かる接続情報 | **AES-256 で暗号化**して保管 |
| パスワードの強度 | **12 文字以上 + 英大小・数字・記号** を全員に強制、定期変更あり |
| 不正ログイン | 連続失敗で**自動ロック** + 試行回数制限 (総当たり攻撃対策) |
| ログイン状態の乗っ取り | 認証チケットを短時間で更新、盗まれた形跡があれば**自動で全失効** |

<br>

> 「通信の暗号化」は入口にすぎず、**保存・認証・ログまで一貫して保護**しています。

---

<!-- _class: chapter -->

# 4. ポイント② ユーザーごとの権限管理

<p>必要な人に、必要なフォルダだけ、必要な操作だけ</p>

---

## 「誰が・どこを・何できるか」を細かく決められる

権限は **人 × フォルダ × 操作の種類** の組み合わせで設定します。

| 例 | 見る (READ) | 書く (WRITE) | 削除 (DELETE) | 名前変更 (RENAME) |
|---|:-:|:-:|:-:|:-:|
| 営業 A さん × `営業\見積` | ✔ | ✔ | ✔ | ✔ |
| 営業 A さん × `経理\決算` | — | — | — | — |
| 経理 B さん × `経理\決算` | ✔ | ✔ | — | — |
| 監査 C さん × `経理` 全体 | ✔ | — | — | — |

<div class="cols">
<div class="card">

**フォルダ単位 (サブフォルダ単位) で指定可能**
共有全体ではなく「この共有の、このフォルダ以下だけ」と絞れます。

</div>
<div class="card">

**許可がなければ存在ごと見えない**
権限のないフォルダは一覧に出ません。「見えるけど開けない」ではなく**そもそも見えない**。

</div>
</div>

---

## 権限設定はパズルではなく「選ぶだけ」

<div class="cols">
<div>

<div class="img-placeholder tall">
📷 ここにスクリーンショット<br>管理画面「ユーザー権限」タブ
<small>左のユーザー一覧 + 右の権限付与パネル (パスブラウザ) が写るように</small>
</div>

</div>
<div>

### 設定の流れ (3 ステップ)

1. **ユーザーを選ぶ** (検索で絞り込み)
2. **サーバー → 共有 → フォルダ** を画面上のツリーから選ぶ
   (パスの手入力は不要)
3. **権限テンプレートを選ぶ**
   「読取のみ」「読取+書込」「フルアクセス」など

<br>

> パスを 1 文字ずつ打つ必要はありません。
> **実際のフォルダ構成を見ながらクリックで選択**できます。

</div>
</div>

---

## 大人数でも破綻しない仕組み

<div class="cols">
<div class="card">

### 📦 権限セット (部署テンプレ)

「経理部標準」「営業部標準」のように
**複数の権限をひとまとめに登録** → 新メンバーには **1 クリックで一括適用**。

</div>
<div class="card">

### 📋 他の人からコピー

「B さんと同じ権限にして」→ 既存ユーザーの権限を**ワンクリックで丸ごと複製**。

</div>
</div>

<div class="cols">
<div class="card">

### 📥 CSV で一括登録

人事異動・新年度の大量登録は **CSV ファイルの取り込みで一括処理**。
棚卸し用の **CSV 出力** もあります。

</div>
<div class="card">

### 🚪 退職時もワンクリック

アカウント削除・自動ログインの**全デバイス失効**が管理画面から即座にできます。

</div>
</div>

> 💡 入社 → 異動 → 退職、**ライフサイクル全体を管理画面だけで完結**できます。

---

<!-- _class: chapter -->

# 5. ポイント③ 管理がラク

<p>日々の運用負担を最小にする設計</p>

---

## 管理は「画面ひとつ」で完結

サーバーにログインしてコマンドを打つ…といった作業は**不要**です。
管理者は普段のアプリに出てくる **[管理] ボタン** からすべて操作できます。

<div class="cols">
<div>

<div class="img-placeholder tall">
📷 ここにスクリーンショット<br>管理画面 (タブが並んだ全体)
<small>ユーザー / ホスト / 共有 / 権限 / 操作ログ などタブ構成が分かる画角で</small>
</div>

</div>
<div>

### 管理画面でできること

- 👥 ユーザーの追加・削除・ロック解除・PW リセット
- 🗄 ファイルサーバー・共有フォルダの登録 (**接続テスト付き**)
- 🔑 権限の設定 (前章のとおり)
- 📝 操作ログの閲覧・CSV 出力
- ⚙ パスワード有効期限などの全社設定

<p class="small">※ 一般ユーザーには [管理] ボタン自体が表示されません</p>

</div>
</div>

---

## 「いつ・誰が・何をしたか」が全部残る (監査ログ)

<div class="cols">
<div>

<div class="img-placeholder tall">
📷 ここにスクリーンショット<br>管理画面「操作ログ」タブ
<small>ユーザー名・操作種別・対象パスが並んだ一覧。フィルタ欄も写すと◎</small>
</div>

</div>
<div>

### 自動で記録されるもの

- 📄 **ファイル操作すべて** — 閲覧・アップロード・削除・名前変更 (成功も失敗も)
- 🛠 **管理者の操作も** — ユーザー追加・権限変更なども漏れなく記録
- 場所は「**サーバー名 / 共有名 / パス**」で読みやすく表示

### 運用の手間なし

- ユーザー名・操作種別・期間で**絞り込み検索**
- **CSV 出力** — 監査・報告にそのまま使えます
- 古いログは**自動で削除** (初期値 1 年保管、変更可)

</div>
</div>

---

## 配ったあとも、手がかからない

<div class="cols">
<div class="card">

### 🔄 アプリは自動更新

新しい版を出すと、社員の PC へ**自動で配布・更新** (ClickOnce)。
**1 台ずつインストールして回る作業はありません。**

</div>
<div class="card">

### 📌 接続先は配布時に固定

接続先サーバーは管理者が配布時に設定し、**利用者は変更できません**。
誤接続・設定ミスによる**問い合わせ自体が発生しない**設計です。

</div>
</div>

<div class="cols">
<div class="card">

### 🖥 サーバーは「常駐サービス」

Windows のサービスとして動作し、PC 再起動後も**自動で立ち上がります**。
万一異常終了しても**自動で再起動**。

</div>
<div class="card">

### 💓 不調は自動検知

踏み台サーバーとは常時ヘルスチェック。
応答が途絶えると管理画面に **「異常」と自動表示** — 気づくのが早い。

</div>
</div>

---

## 管理コストまとめ — Before / After

| 作業 | これまで (例: 手作業運用) | Watashi |
|---|---|---|
| アプリ配布・更新 | 1 台ずつ手作業 | **自動配布・自動更新** |
| 新入社員の権限設定 | 個別に設定・依頼 | **権限セットを 1 クリック適用** |
| 大量のユーザー登録 | 1 人ずつ手入力 | **CSV 一括取込** |
| 退職者の処理 | 漏れがち | **削除 + デバイス失効をワンクリック** |
| 「誰が消した？」調査 | ほぼ不可能 | **監査ログを検索 → CSV 提出** |
| ログの掃除 | 手動 or 放置 | **自動削除 (保管期間は設定可)** |
| サーバー障害対応 | 気づいたら再起動 | **自動再起動 + 異常の自動検知** |

---

<!-- _class: chapter -->

# 6. 導入の流れ・FAQ

---

## 導入に必要なもの・進め方

<div class="cols">
<div class="card">

### 必要なもの

- 社内に **中央サーバー 1 台** (Windows)
- 必要に応じて **踏み台サーバー**
- 社員 PC へは**自動配布** — 個別作業なし
- 想定規模: **同時 50 名程度** の社内利用

</div>
<div class="card">

### 初期セットアップ (管理者作業)

1. サーバーをサービスとして登録
2. ファイルサーバー (ホスト・共有) を登録 → **接続テスト**
3. ユーザーを登録 (CSV 一括可)
4. 権限を割り当て (権限セットで一括)
5. 利用者へ案内して完了 🎉

</div>
</div>

<div class="img-placeholder sm">
📅 (任意) ここに導入スケジュール案 — 「検証 ◯週間 → 部分導入 → 全社展開」など
</div>

---

## よくある質問

<div class="card">

**Q. データはクラウドに出ますか？**
　いいえ。**すべて社内で完結**し、外部サービスには一切送信されません。

**Q. 既存のファイルサーバーを置き換えるのですか？**
　いいえ。既存サーバーはそのまま。そこへの「**安全な入口**」を追加するイメージです。

**Q. 社員の教育は大変ですか？**
　2 画面を並べた直感的な UI で、基本はマウス操作。**利用ガイドも用意済み**です。

**Q. 何かあったとき、誰が触ったか分かりますか？**
　はい。**全操作が記録**され、CSV で出力して報告にも使えます。

**Q. パスワード管理が心配です。**
　強度ポリシー (12 文字+記号等) と有効期限を**全社で強制**します。連続失敗は自動ロックします。

</div>

---

## まとめ — Watashi の 3 つのポイント

<div class="cols">
<div class="card center">

### 🔒
### 通信を暗号化

PC ⇄ サーバー ⇄ 踏み台、
**全区間 HTTPS**。
盗聴・改ざん・なりすましを防止

</div>
<div class="card center">

### 🔑
### 人ごとに権限

**人 × フォルダ × 操作**で
細かく制御。
テンプレで設定もラク

</div>
<div class="card center">

### 🛠
### 管理がラク

自動配布・自動更新・
監査ログ・一括登録。
**画面ひとつで運用完結**

</div>
</div>

<br>

> 既存のファイルサーバーはそのままに、「**安全な入口**」と「**見える管理**」を追加するツールです。

---

<!-- _class: lead -->

# ご清聴ありがとうございました

### 質疑応答

<br>

詳細資料のご案内
**利用者向けガイド** / **管理者向けガイド** / **機能仕様書** をご用意しています

<!--
問い合わせ先 (担当者名・メールアドレス・チャットチャンネルなど) をここに追記してください。
-->
