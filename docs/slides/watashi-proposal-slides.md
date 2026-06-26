---
marp: true
theme: default
paginate: true
size: 16:9
header: ""
footer: "⛩ Watashi 導入提案 — 社内ファイル共有ツールの刷新"
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
    --ng: #CF222E;
  }
  section {
    background: var(--bg);
    color: var(--text);
    font-family: "Yu Gothic UI", "Meiryo", "Segoe UI", sans-serif;
    font-size: 23px;
    padding: 54px 64px;
    line-height: 1.5;
  }
  section h1 { color: var(--header); font-size: 1.5em; }
  section h2 {
    color: var(--header);
    border-bottom: 4px solid var(--accent);
    padding-bottom: 6px;
    font-size: 1.2em;
    margin-bottom: 14px;
  }
  section h3 { color: var(--accent); font-size: 1.0em; margin: 2px 0 6px; }
  section strong { color: var(--accent); }
  section footer { color: var(--muted); font-size: 0.5em; }
  section table { font-size: 0.82em; margin: 6px auto; border-collapse: collapse; }
  section th { background: var(--header); color: #fff; padding: 5px 10px; }
  section td { padding: 5px 10px; }
  section code { background: #EAEEF3; color: var(--header); padding: 1px 6px; border-radius: 4px; }
  section ul, section ol { margin: 4px 0; padding-left: 22px; }
  section li { margin: 3px 0; }
  blockquote {
    border-left: 5px solid var(--accent);
    background: #fff;
    margin: 12px 0 0;
    padding: 8px 16px;
    color: var(--header);
    font-size: 0.92em;
    border-radius: 0 8px 8px 0;
  }

  /* タイトルスライド・章扉 */
  section.lead {
    background: linear-gradient(135deg, var(--header) 0%, #243B5C 100%);
    color: #fff;
    text-align: center;
  }
  section.lead h1 { color: #fff; font-size: 2.0em; }
  section.lead h2 { color: var(--accent-soft); border: none; }
  section.lead h3 { color: #fff; }
  section.lead p, section.lead li { color: #D5D9DE; }
  section.lead footer, section.lead header { display: none; }
  section.lead::after { color: #9CA3AF; }
  section.lead blockquote { background: rgba(255,255,255,.08); border-left-color: var(--accent-soft); color: #fff; }

  /* 章扉 (朱色) */
  section.chapter {
    background: linear-gradient(135deg, var(--accent) 0%, #8C2911 100%);
    color: #fff;
    text-align: center;
    display: flex;
    flex-direction: column;
    justify-content: center;
  }
  section.chapter h1 { color: #fff; font-size: 1.8em; }
  section.chapter p { color: #F9E5E0; font-size: 1.05em; }
  section.chapter footer, section.chapter header { display: none; }

  /* カラムレイアウト */
  .cols { display: flex; gap: 22px; align-items: stretch; }
  .cols > div { flex: 1; }
  .cols3 { display: flex; gap: 16px; align-items: stretch; }
  .cols3 > div { flex: 1; }

  /* カード */
  .card {
    background: #fff;
    border: 1px solid #D5D9DE;
    border-radius: 12px;
    padding: 12px 18px;
    margin: 8px 0;
    box-shadow: 0 2px 6px rgba(27,42,65,.06);
  }
  .card.hi { border-color: var(--accent); border-width: 2px; background: #fff; }
  .card h3 { margin-top: 0; }
  .card ul { padding-left: 20px; }

  .ok { color: var(--ok); font-weight: bold; }
  .ng { color: var(--ng); font-weight: bold; }
  .small { font-size: 0.8em; color: var(--muted); }
  .center { text-align: center; }
  .badge {
    display: inline-block; background: var(--accent); color: #fff;
    border-radius: 999px; padding: 2px 14px; font-size: 0.78em; font-weight: bold;
  }

  /* フロー図 (構成図用) */
  .flow { display: flex; align-items: center; justify-content: center; gap: 8px; margin: 12px 0; }
  .flow .node {
    background: #fff; border: 2px solid var(--header); border-radius: 10px;
    padding: 9px 14px; text-align: center; font-weight: bold; color: var(--header);
    font-size: 0.82em; line-height: 1.3; box-shadow: 0 2px 6px rgba(27,42,65,.08);
  }
  .flow .node.good { border-color: var(--ok); }
  .flow .node small { display: block; font-weight: normal; color: var(--muted); font-size: 0.78em; }
  .flow .arrow { text-align: center; color: var(--accent); font-weight: bold; font-size: 0.6em; line-height: 1.25; white-space: nowrap; }
  .flow .arrow .line { display: block; font-size: 2.0em; line-height: 1; }

  /* 大きな数字・キーメッセージ */
  .key { background: var(--accent-soft); border: 2px solid var(--accent); border-radius: 12px; padding: 12px 20px; color: var(--header); font-size: 1.02em; text-align: center; margin: 6px 0 12px; }
  .key b { color: var(--accent); }
---

<!-- _class: lead -->

# 社内ファイル共有ツール 刷新のご提案

## 現行ツールの課題を解決する後継ツール「⛩ Watashi」

意思決定者向け 説明資料

<!--
表紙です。発表日・部署名・発表者名を下に追記してください。
例: 2026年6月◯日 / 情報システム部 ◯◯
-->

---

## ご提案の要旨（結論から）

<div class="card hi">

1. 現行ツールは **通信が暗号化されていない（HTTP）** ため、セキュリティ要件を満たせない
2. 「**HTTPS にすべき**」という要望に、Watashi なら **大きなインフラ投資なし**で応えられる
3. 「**ユーザーごとに権限を分けたい**」要望にも対応 — しかも **管理の手間はむしろ減る**

</div>

> まずは小規模な **試験導入（PoC）** から始めるのが、安全で確実です。

---

## なぜ今、見直すのか — 現行ツールの課題

<div class="cols3">
<div class="card">

### 🔒 セキュリティ

- 通信が **HTTP（平文）**
- ユーザー別に **権限を分けられない**

</div>
<div class="card">

### 🧾 日々の運用

- ユーザー追加が大変
- 許可フォルダは **1つずつ入力**
- ログは **DB を直接**見るしかない
- 設定変更が大変

</div>
<div class="card">

### 🧱 保守・構成

- **MySQL** の脆弱性対応・立上げが大変
- **IIS** 必須の構成
- 名称が他製品と重複

</div>
</div>

> どれも“使いにくい”だけでは済まず、**情報漏えい**や**運用コスト**につながっています。

---

<!-- _class: chapter -->

# ① セキュリティ（最優先）

「HTTPS にしないといけない」に応える

---

## 現行は「平文 HTTP」— ここが最大の問題

<div class="cols">
<div class="card">

### 🔓 今（HTTP）

- 通信が **平文**のまま。途中で **パスワードもファイルも見られかねません**
- 「HTTPS にすべき」という **要望・要件**が出ている

<span class="ng">✘ 盗聴 ／ ✘ 改ざん ／ ✘ なりすまし</span>

</div>
<div class="card">

### 🔒 Watashi（HTTPS）

- 通信を **暗号化**。途中で覗いても **意味不明な暗号**にしか見えない

<span class="ok">✔ 中身を守る ／ ✔ 改ざん検知 ／ ✔ 接続先を確認</span>

</div>
</div>

```text
HTTP  : PC ─「給与.xlsx」「PW: abc」─▶ サーバー   誰でも読める
HTTPS : PC ─「x8#kQ…zR2$」(暗号文) ─▶ サーバー   読めない 🔒
```

---

## 「HTTPS 化」を、最小の投資で実現できる

<div class="key">
HTTPS にする“だけ”のために、<b>大掛かりなインフラは要りません。</b>
</div>

<div class="cols">
<div class="card">

### Watashi のシンプル構成

- サーバー **1 台**で稼働（Windows サービス）
- **専用 DB（MySQL）不要** … 内蔵DBが自動生成
- **IIS 必須でない** … 既存資産は活かせる

</div>
<div class="card">

### だから、こんな利点

- HTTPS 要件を満たしつつ **サーバー・ライセンス・構築工数を抑制**
- 検証環境も **すぐ用意**できる
- **守る対象（脆弱性対応）が減る**

</div>
</div>

> HTTPS にするという要件を、**サーバーを増やさずに**満たせます。

---

## セキュリティは「通信」だけではない

| 守るもの | しくみ |
|---|---|
| ログインパスワード | 強力なハッシュ化で保存（**管理者でも元に戻せない**） |
| サーバーが預かる接続情報 | **AES-256 で暗号化**して保管 |
| パスワード強度 | **12 文字 + 英大小・数字・記号**を全社で強制・期限あり |
| 不正ログイン | 連続失敗で **自動ロック** + 試行回数制限 |
| サーバー間の通信 | 相互認証（mTLS）で **なりすましを防止** |

> 暗号化は入口にすぎません。**保存データ・ログイン・操作記録**もまとめて守ります。

---

<!-- _class: chapter -->

# ② ユーザーごとの権限

「人によって触れる範囲を分けたい」に応える

---

## 「権限を分けたい」要望に応えられる

現行は **全員が同じ範囲**。Watashi は **人 × フォルダ × 操作**で制御します。

| 例 | 見る | 書く | 削除 | 改名 |
|---|:-:|:-:|:-:|:-:|
| 営業Aさん × `営業\見積` | ✔ | ✔ | ✔ | ✔ |
| 営業Aさん × `経理\決算` | — | — | — | — |
| 監査Cさん × `経理` 全体 | ✔ | — | — | — |

> 許可のないフォルダは **一覧に出ません**（“見えるけど開けない”ではなく、そもそも見えない）。

---

## 権限設定はラク → 管理工数は増えない

<div class="cols">
<div class="card">

### 選ぶだけ

- フォルダは画面の **ツリーから選択**（手入力不要）
- 「読取のみ／読取+書込／フル」を **テンプレから選ぶ**

</div>
<div class="card">

### 大人数でもラクに回る

- 📦 **権限セット**を 1 クリックで一括適用
- 📋 他ユーザーから **まるごとコピー**
- 📥 **CSV で一括登録**（異動・新年度も一気に）

</div>
</div>

> 権限を細かくしても、**設定の手間はむしろ減ります**。

---

<!-- _class: chapter -->

# ③ 管理工数の削減

毎日の運用の手間をできるだけ減らす

---

## 管理コスト — Before / After

| 作業 | これまで | Watashi |
|---|---|---|
| アプリの配布・更新 | 1 台ずつ手作業 | **自動配布・自動更新** |
| 新入社員の権限設定 | 個別に設定・依頼 | **権限セットを 1 クリック** |
| 大量のユーザー登録 | 1 人ずつ手入力 | **CSV 一括取込** |
| 退職者の処理 | 漏れがち | **削除＋失効をワンクリック** |
| 「誰が消した？」調査 | ほぼ不可能 | **ログ検索 → CSV 提出** |
| サーバー障害 | 気づいたら再起動 | **自動再起動＋異常の自動検知** |

---

## ログも設定も「画面ひとつ」で

<div class="cols">
<div class="card">

### 📝 監査ログ

- ファイル操作も管理操作も **自動記録**
- **絞り込み + CSV 出力**（DB を直接見る必要なし）
- 古いログは **自動で削除**（保管期間は設定可）

</div>
<div class="card">

### ⚙ 設定・保守

- パスワード期限・タイムアウト等も **画面から変更**
- DB は **自動生成・自動バックアップ**
- サーバーは **常駐サービス**（再起動後も自動復帰）

</div>
</div>

> 現行の「ログは DB を直接見る」「設定変更が大変」も、これで解決できます。

---

## まとめ：課題 → 解決 を一望

| 観点 | 現行ツール | Watashi |
|---|---|---|
| 🔒 セキュリティ | HTTP のみ・権限を分けられない | **HTTPS＋暗号化・人ごとの権限** |
| 🧾 運用 | 追加/設定が手作業・ログは DB | **GUI・CSV 一括・ログ画面＋CSV** |
| 🧱 保守・構成 | MySQL/IIS 必須・構築が大変 | **DB 内蔵・IIS 不要・自動生成** |

<div class="key">
いただいた課題は、<b>ひと通り解決できます</b>。あとから対策を足したのではなく、もともと「安全・簡単・軽い」を考えて作られたツールです。
</div>

---

## 導入の進め方（ご提案）

<div class="cols">
<div class="card">

### 進め方（段階導入）

1. **試験導入（PoC）**：1 部署・少人数で実運用に近い形で
2. **評価**：安全性・運用性・保守性を現行と比較
3. **本番展開**：問題なければ順次切替

</div>
<div class="card">

### 必要なもの

- 社内に **中央サーバー 1 台**（Windows）
- 必要に応じて **踏み台サーバー**
- 社員 PC へは **自動配布**（個別作業なし）

</div>
</div>

> ご判断のお願い：まず **PoC 実施のご承認**を。実際の画面と数字でご報告します。

---

<!-- _class: lead -->

# ご検討よろしくお願いします

### 質疑応答

詳細資料：**利用者ガイド** ／ **管理者ガイド** ／ **導入提案書（HTML）** をご用意しています

<!--
問い合わせ先（担当者・連絡先）をここに追記してください。
-->
