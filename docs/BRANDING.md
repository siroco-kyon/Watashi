# Watashi ブランディング変更ガイド

クライアントに配布する **アプリ名** と **アイコン (鳥居マーク)** を変更したいときに、
「どのファイルの何を直すか」をまとめたガイドです。

> このガイドは**変更箇所の一覧**です。実際の改名作業そのものは含みません。
> 新しい名前・アイコンが決まったら、本書を見ながら該当箇所を差し替えてください。

> 📘 表やチェックリストを画面で読みやすくした [HTML 版](BRANDING.html) もあります。

- [1. 変更箇所の全体像](#1-変更箇所の全体像)
- [2. 画面に表示されるアプリ名](#2-画面に表示されるアプリ名watashi)
- [3. exe ファイル名](#3-exe-ファイル名watashiclientexe)
- [4. 配布物・インストーラの名前 (ClickOnce)](#4-配布物インストーラの名前-clickonce)
- [5. アイコン (鳥居マーク)](#5-アイコン-鳥居マーク)
- [6. (任意) より広範にブランドを変える場合](#6-任意-より広範にブランドを変える場合)
- [7. A/B 版を同じ PC で共存させる場合](#7-ab-版を同じ-pc-で共存させる場合)
- [8. 変更後の確認手順](#8-変更後の確認手順)

> 🔎 A/B 共存の設定は **`scripts/Check-BrandSetup.ps1`** で自動点検できます ([§7-7](#7-7-設定を自動で点検する))。

---

## 1. 変更箇所の全体像

| 変えたいもの | 主な対象 | 節 |
|---|---|---|
| 画面に出るアプリ名「Watashi」 | クライアントの XAML / `App.xaml.cs` | [§2](#2-画面に表示されるアプリ名watashi) |
| exe ファイル名 (`Watashi.Client.exe`) | `Watashi.Client.csproj` | [§3](#3-exe-ファイル名watashiclientexe) |
| 配布物・インストーラの名前 | `ClickOnceProfile.pubxml` | [§4](#4-配布物インストーラの名前-clickonce) |
| アイコン (鳥居) | `Watashi.ico` + `Icons.xaml` + `Colors.xaml` | [§5](#5-アイコン-鳥居マーク) |
| A/B を別アプリとして同時利用 | ClickOnce ID・配布 URL・ローカル保存先 | [§7](#7-ab-版を同じ-pc-で共存させる場合) |

1 種類だけを配布する通常のリブランドは **§2・§4・§5 の 3 つ**を変えれば足ります。
ただし、**A 版と B 版を同じ PC にインストールして同時利用する場合は §7 まで必須**です。
サーバー側の名前まで変えたい場合は §6 を参照してください。

---

## 2. 画面に表示されるアプリ名「Watashi」

> ### 🚀 一括置換スクリプト (推奨)
>
> 本節と §4 (ClickOnce の製品名) の置換は、**`scripts/Rename-Brand.ps1`** で一括実行できます。
> 名前空間・クラス名 (`Watashi.Client` 等) を巻き込まないアンカー付きパターンで置換し、
> 日本語タイトルの文字コード (UTF-8 / BOM) も保持します。
>
> ```powershell
> # まずプレビュー (何も書き換えません。変更予定の全箇所を一覧表示)
> powershell -NoProfile -File scripts/Rename-Brand.ps1 -NewName "新名称" -WhatIf
>
> # 問題なければ適用
> powershell -NoProfile -File scripts/Rename-Brand.ps1 -NewName "新名称"
> ```
>
> - 置換対象: 本節の XAML/コードの表示名 + [§4](#4-配布物インストーラの名前-clickonce) の `ProductName`/`PublisherName`/`SuiteName`
> - 置換**しない**もの (別途手動、下記各節参照): exe 名 ([§3](#3-exe-ファイル名watashiclientexe))・補足文字「CIFS ファイル管理」・アイコン/色 ([§5](#5-アイコン-鳥居マーク))・サーバー側の名前 ([§6](#6-任意-より広範にブランドを変える場合))
> - 適用後は必ずリビルドで確認: `dotnet build src\Watashi.Client\Watashi.Client.csproj`
> - 既定の旧名は `Watashi`。過去に一度改名済みで再度変える場合は `-OldName "現在の名前"` を指定
>
> 以下の表は、スクリプトが実際に書き換える箇所の内訳です (手動で直す場合の参照用)。

UI 文字列としての「Watashi」は以下に直書きされています。新名称へ置換してください。

| ファイル | 箇所 | 現在の値 |
|---|---|---|
| `src/Watashi.Client/Views/SplashWindow.xaml` | ウィンドウタイトル (タスクバー表示) | `Title="Watashi - 起動中"` |
| 〃 | スプラッシュのロゴ文字 | `<TextBlock Text="Watashi" Style="{StaticResource H1}"/>` |
| `src/Watashi.Client/Views/LoginWindow.xaml` | ウィンドウタイトル | `Title="Watashi - ログイン"` |
| 〃 | ログイン画面の大見出し | `<TextBlock Text="Watashi" Style="{StaticResource H1}" .../>` |
| `src/Watashi.Client/Views/MainWindow.xaml` | ウィンドウタイトル | `Title="Watashi"` |
| 〃 | ヘッダーのロゴ文字 | `<TextBlock Text="Watashi" Style="{StaticResource H3}"/>` |
| `src/Watashi.Client/Views/Admin/AdminWindow.xaml` | ウィンドウタイトル | `Title="Watashi - 管理"` |
| 〃 | ヘッダーの文字 | `<TextBlock Text="Watashi" Style="{StaticResource H3}" .../>` |
| `src/Watashi.Client/Themes/Icons.xaml` | ロゴテンプレート ×2 (`ToriiLogo` / `ToriiLogoLight`) | `<TextBlock Text="Watashi" .../>` |
| `src/Watashi.Client/App.xaml.cs` | 各種ダイアログのタイトル (配布設定エラー / 致命エラー / PC 記憶) | `"Watashi - 配布設定エラー"` / `$"Watashi - {title}"` / `"Watashi - PC 記憶"` |
| `src/Watashi.Client/Views/MainWindow.xaml.cs` | バージョン情報 (About) ダイアログ | `"Watashi - 社内 CIFS ファイル管理ツール\nバージョン ..."` |

> ロゴ直下の補足文字「CIFS ファイル管理」も、必要なら合わせて変更してください。
> 出現箇所は 3 ファイル: `MainWindow.xaml` (ヘッダー) / `LoginWindow.xaml` (「社内 CIFS ファイル管理」) / `SplashWindow.xaml` (スプラッシュ)。

**置換の目安** (UI に出る文字列のみ対象、大文字小文字を区別):
- `Text="Watashi"` → `Text="新名称"`
- `Title="Watashi` → `Title="新名称`
- コード内のダイアログタイトル `"Watashi - …` (`App.xaml.cs` / `MainWindow.xaml.cs`) → `"新名称 - …`

> 漏れ防止には、`src/Watashi.Client` 配下を `Text="Watashi"` と `"Watashi - ` の 2 パターンで全文検索して洗い出すのが確実です。

> ⚠ **名前空間・クラス名の `Watashi.*` (例: `Watashi.Client.Views`) は変更しないこと。**
> これは内部識別子で画面には一切出ません。書き換えるとビルドが壊れます。

> `Window.Title` はタスクバーのホバー表示にも使われますが、タイトルを変えるだけでは
> A/B のタスクバーグループは分離されません。`Watashi (2)` のようにまとめられる場合は
> [§7](#7-ab-版を同じ-pc-で共存させる場合) の ClickOnce ID 分離が必要です。

---

## 3. exe ファイル名 (`Watashi.Client.exe`)

現在 `src/Watashi.Client/Watashi.Client.csproj` には `AssemblyName` 等が未設定のため、
出力 exe はプロジェクト名どおり `Watashi.Client.exe` になります。
変えたい場合は `<PropertyGroup>` に以下を追加します。

```xml
<AssemblyName>NewName.Client</AssemblyName>   <!-- 出力 exe 名 -->
<Product>NewName</Product>                    <!-- exe のプロパティ→詳細「製品名」 -->
<AssemblyTitle>NewName</AssemblyTitle>        <!-- 〃「説明」 -->
```

通常のリブランドでは exe 名の変更は任意です。一方、A/B を同じ PC で別アプリとして
共存させる場合は、ClickOnce のアプリケーション ID を分けるため、
本ガイドの共存手順では `AssemblyName` を A/B で別名にします。具体例は [§7-2](#7-2-a-版と-b-版で変更する値) を参照してください。

---

## 4. 配布物・インストーラの名前 (ClickOnce)

`src/Watashi.Client/Properties/PublishProfiles/ClickOnceProfile.pubxml`:

| プロパティ | 現在 | 効果 |
|---|---|---|
| `<ProductName>` | `Watashi` | スタートメニュー名・インストーラ画面・「アプリと機能」の表示名 |
| `<PublisherName>` | `Watashi` | 発行元 (スタートメニューのフォルダ名等) |
| `<SuiteName>` | `Watashi` | スタートメニューのグループ名 |
| `<ApplicationIcon>` | `Watashi.ico` | インストーラ/ショートカットのアイコン ([§5](#5-アイコン-鳥居マーク)) |

> 上表の 3 プロパティは [§2 の一括置換スクリプト](#2-画面に表示されるアプリ名watashi) が自動で書き換えます。
> `<ApplicationIcon>` とアイコン実体はアイコン差し替え ([§5](#5-アイコン-鳥居マーク)) 側で扱います。

> 配布 URL (`<PublishUrl>` / `<InstallUrl>` / `<UpdateUrl>`) のパスに `Watashi` が含まれる場合は、
> 配布サーバ側の都合に合わせて任意に変更できます (ブランド名と一致させる必要はありません)。
> スクリプトは URL を書き換えません (アンカーを `<ProductName>` 等の要素に限定しているため)。

> `ProductName` は主に人に見せる名前です。A/B を ClickOnce 上の別アプリとして共存させるには、
> `ProductName` の変更だけでは不十分です。`AssemblyName`、生成マニフェストの
> `assemblyIdentity`、配布・更新 URL も分離してください。詳細は [§7](#7-ab-版を同じ-pc-で共存させる場合) を参照してください。

---

## 5. アイコン (鳥居マーク)

アイコンは **「Win32/ClickOnce 用の .ico」** と **「アプリ画面内で描くベクター」** の 2 系統があります。
ブランドを揃えるには両方を変更します。

### 5-1. exe・タスクバー・インストーラのアイコン (.ico)
- 実体: `src/Watashi.Client/Watashi.ico` (16/24/32/48/64/128/256 px のマルチサイズ)
- `Watashi.Client.csproj` と `ClickOnceProfile.pubxml` の `<ApplicationIcon>` が参照
- 別画像に差し替えるなら、同名で `.ico` を上書きするか、ファイル名を変えて両方の `<ApplicationIcon>` を更新

### 5-2. 鳥居デザインを描き直す (推奨ワークフロー)
鳥居の意匠は `scripts/Generate-ToriiIcon.ps1` が `System.Drawing` で描画して `.ico` を生成しています。
デザインを変えるならこのスクリプトを編集 → 再実行するだけで `.ico` が再生成されます。

```powershell
powershell.exe -NoProfile -File scripts/Generate-ToriiIcon.ps1
```

### 5-3. アプリ画面内のロゴ (ベクター)
ウィンドウ内に出るロゴは `.ico` ではなく XAML のベクター画像です。
`src/Watashi.Client/Themes/Icons.xaml` の以下を差し替えます。
- `ToriiIcon` / `ToriiIconLight` (鳥居本体の `DrawingImage` ジオメトリ)
- まったく別の画像にするなら、`DrawingImage` を `BitmapImage` 参照などに置き換える

> `ToriiIcon` はスプラッシュ (`SplashWindow.xaml`)・ログイン画面・メイン/管理画面ヘッダー・
> 各ウィンドウの `Icon` 属性から `StaticResource` 参照されているため、
> Icons.xaml を 1 箇所差し替えれば全画面に反映されます (個別の修正は不要)。

### 5-4. 色 (朱色アクセント)
ブランドカラーは `src/Watashi.Client/Themes/Colors.xaml`:
- `AccentColor` = `#C73E1D` (朱色 / 鳥居の色)
- `AccentDeepColor` = `#5C1A0B` (濃い朱)

色を変える場合、**`Generate-ToriiIcon.ps1` の `$ACCENT` / `$ACCENT_DEEP` も同じ値に合わせてください**
(`.ico` とアプリ内ロゴの色を一致させるため)。現在のスクリプト値:

```powershell
$ACCENT      = [System.Drawing.Color]::FromArgb(0xFF, 0xC7, 0x3E, 0x1D)   # = #C73E1D
$ACCENT_DEEP = [System.Drawing.Color]::FromArgb(0xFF, 0x5C, 0x1A, 0x0B)   # = #5C1A0B
```

### 5-5. 色違いアイコン (接続先 DB / サーバごとの区別用)

複数の中央サーバ (= 接続先 DB) に向けたクライアントを併用する運用では、
配布物ごとにアイコンの色を変えると「どの環境のクライアントか」がタスクバーだけで見分けられます。
生成済みの色違い `.ico` (既定の朱を含む 11 色、同ジオメトリ・マルチサイズ) を `assets/icons/` に同梱しています。

| ファイル | 参考名 | アクセント | 濃色 (島木) |
|---|---|---|---|
| `assets/icons/Watashi-shu.ico`     | **朱 (しゅ) — 既定** | `#C73E1D` | `#5C1A0B` |
| `assets/icons/Watashi-orange.ico`  | 橙 (だいだい) | `#C9731D` | `#5B330B` |
| `assets/icons/Watashi-gold.ico`    | 山吹 (やまぶき) | `#C9A61D` | `#5B4B0B` |
| `assets/icons/Watashi-lime.ico`    | 若草 (わかくさ) | `#73C91D` | `#335B0B` |
| `assets/icons/Watashi-green.ico`   | 常磐 (ときわ) | `#1DC956` | `#0B5B26` |
| `assets/icons/Watashi-teal.ico`    | 青碧 (せいへき) | `#1DC9AC` | `#0B5B4E` |
| `assets/icons/Watashi-cyan.ico`    | 浅葱 (あさぎ) | `#1D8FC9` | `#0B405B` |
| `assets/icons/Watashi-blue.ico`    | 瑠璃 (るり) | `#1D48C9` | `#0B1F5B` |
| `assets/icons/Watashi-violet.ico`  | 菫 (すみれ) | `#5C1DC9` | `#280B5B` |
| `assets/icons/Watashi-magenta.ico` | 紫 (むらさき) | `#C91DC9` | `#5B0B5B` |
| `assets/icons/Watashi-rose.ico`    | 躑躅 (つつじ) | `#C91D64` | `#5B0B2C` |

`Watashi-shu.ico` は既定の `src/Watashi.Client/Watashi.ico` と同一内容のコピーです。
他の 10 色は朱と同じ導出規則 (アクセント = HSL(色相, 0.75, 0.45) / 濃色 = HSL(色相, 0.79, 0.20)) で
色相だけを変えているため、明度・彩度のトーンは既定と揃います。11 環境まで区別できます。

**配布への適用手順 (環境ごと):**
1. `src/Watashi.Client/deployment.json` の `serverUrl` をその環境の中央サーバに設定
2. 選んだ色の `.ico` で `src/Watashi.Client/Watashi.ico` を上書き
   (`Copy-Item assets\icons\Watashi-blue.ico src\Watashi.Client\Watashi.ico -Force`)
3. ClickOnce を再発行 ([§4](#4-配布物インストーラの名前-clickonce)。マニフェストがハッシュ検証するため再発行は必須)
4. 発行が終わったら既定の朱に戻す:
   `Copy-Item assets\icons\Watashi-shu.ico src\Watashi.Client\Watashi.ico -Force`
   (Git が使えるなら `git restore src/Watashi.Client/Watashi.ico` でも同じ)

> ⚠ **手順 2 の上書きは発行作業中だけの一時変更です。コミットしないでください。**
> コミットするとリポジトリの既定アイコンがその色に置き換わってしまいます。
> 万一上書きしたまま分からなくなっても、`Watashi-shu.ico` からのコピー /
> `git restore` / `scripts/Generate-ToriiIcon.ps1` の再実行のどれでも朱色に復元できます。

> `.ico` の差し替えで変わるのは exe・タスクバー・インストーラ・スタートメニューのアイコンです。
> アプリ画面内のロゴ (ベクター) の色も揃えたい場合は、[§5-4](#5-4-色-朱色アクセント) の
> `Colors.xaml` (`AccentColor` / `AccentDeepColor`) を上表の 2 色に合わせて変更してください。

> アイコンを色分けしても、Windows 上のアプリ ID は分かれません。
> A/B が `Watashi (2)` と同じタスクバーグループに入る場合は [§7](#7-ab-版を同じ-pc-で共存させる場合) の対応が必要です。

**再生成・色の追加:**
`scripts/Generate-ToriiIconVariants.ps1` が生成元です。色を追加したい場合は
スクリプト内の `$variants` に名前と色相 (Hue) を足して再実行します。

```powershell
powershell.exe -NoProfile -File scripts/Generate-ToriiIconVariants.ps1
```

---

## 6. (任意) より広範にブランドを変える場合

クライアントの見た目だけでなく、サーバー/運用名まで変えたい場合の参考です。

| 対象 | 箇所 | 注意 |
|---|---|---|
| クライアントのデータ保存フォルダ | `src/Watashi.Client/Services/AppSettings.cs` の `"Watashi"` (`%LocalAppData%\Watashi\settings.json`) | 既存ユーザーの設定パスが変わる |
| Windows サービス名 | `deploy/install-server-service.ps1` / `install-agent-service.ps1` (`Watashi.Server` / `Watashi.Agent`) | 運用コマンド・監視も追従が必要 |
| サーバーデータパス | `C:\ProgramData\Watashi` (`appsettings.json` の接続文字列・ログパス) | 既存 DB の移行を伴う |
| JWT Issuer / Audience | `appsettings.json` の `Jwt:Issuer` / `Jwt:Audience` (`Watashi`) | Client/Server 双方の整合が必要 |

> これらは「見た目の名前」ではなく運用上の識別子です。
> **1 種類だけを配布する通常のリブランドでは §2 (表示名)・§4 (配布物名)・§5 (アイコン) の変更で十分**です。
> A/B を同じ PC で共存させる場合は、クライアント側の保存先も含めて §7 を参照してください。

---

## 7. A/B 版を同じ PC で共存させる場合

### 7-1. なぜ表示名だけでは足りないのか

Windows と ClickOnce には、見た目の名前とは別にアプリケーションを識別する値があります。
たとえばショートカットを `Watashi-a` / `Watashi-b` に改名しても、内部 ID が同じなら
Windows は同じアプリの 2 ウィンドウと判断し、タスクバーに `Watashi (2)` と表示します。

| 層 | 役割 | A/B での扱い |
|---|---|---|
| `Window.Title` | タスクバーのホバー、各画面のタイトル | **別にする** |
| `ProductName` | スタートメニュー、インストーラ、「アプリと機能」 | **別にする** |
| `AssemblyName` | exe 名、ClickOnce アプリケーションマニフェスト ID の基礎 | **別にする** |
| ClickOnce `assemblyIdentity` | ClickOnce がアプリを識別する内部 ID | **発行後に別値か確認** |
| 配布・更新 URL | インストール元と更新先 | **完全に分ける** |
| `serverUrl` | 接続する中央サーバ | 環境ごとに設定 |
| ローカル設定・資格情報・ログ | 同じ Windows ユーザー内の保存先 | **`BrandId` で別にする ([§7-4](#7-4-ローカル設定自動ログインログも分離する-brandid))** |
| アイコン | 人が見分けるための補助 | 色分けを推奨。ただし ID 分離にはならない |

### 7-2. A 版と B 版で変更する値

以下は例です。内部 ID には全角の `ｂ` を使わず、ASCII の `A` / `B` を使用してください。
画面表示は `Watashi-a` / `Watashi-b` のような任意の表記で構いません。

| 設定 | A 版の例 | B 版の例 |
|---|---|---|
| 画面表示名 | `Watashi-a` | `Watashi-b` |
| `AssemblyName` | `WatashiA.Client` | `WatashiB.Client` |
| `Product` / `AssemblyTitle` | `Watashi-a` | `Watashi-b` |
| ClickOnce `ProductName` | `Watashi-a` | `Watashi-b` |
| 発行フォルダ | `\\fileserver\share\Watashi-a\` | `\\fileserver\share\Watashi-b\` |
| インストール・更新 URL | `https://host/install/watashi-a/` | `https://host/install/watashi-b/` |
| 更新マニフェスト | `WatashiA.Client.application` | `WatashiB.Client.application` |
| `serverUrl` | A 環境の中央サーバ | B 環境の中央サーバ |
| `BrandId` (`.pubxml` または `/p:`) | 既定のまま (指定しない) | `Watashi-b` |
| 設定・ログフォルダ | `%LOCALAPPDATA%\Watashi\` | `%LOCALAPPDATA%\Watashi-b\` |
| 資格情報ターゲット | `Watashi/AutoLogin` | `Watashi-b/AutoLogin` |

#### `AssemblyName` をどこに書くか

**発行プロファイルを A/B で分ける運用では、`AssemblyName` は各 `.pubxml` に書きます。**
csproj に書くと発行のたびに手で書き換えることになります。

```xml
<!-- MSAClickOnceProfile.pubxml -->
<AssemblyName>WatashiA.Client</AssemblyName>
<BrandId>Watashi-a</BrandId>
```

```xml
<!-- kmtClickOnceProfile.pubxml -->
<AssemblyName>WatashiB.Client</AssemblyName>
<BrandId>Watashi-b</BrandId>
```

> ⚠ **csproj 側で `$(BrandId)` を条件にして `AssemblyName` を切り替えることはできません。**
>
> ```xml
> <!-- これは効きません -->
> <AssemblyName Condition="'$(BrandId)' != 'Watashi'">$(BrandId).Client</AssemblyName>
> ```
>
> MSBuild は**プロパティを記述順に評価**し、`.pubxml` は csproj 本文より**後**に import されます。
> そのため csproj のプロパティは `.pubxml` の値を参照できず、条件が常に偽になります。
>
> 一方**項目 (ItemGroup) は全プロパティの評価後**に処理されるため、
> `<AssemblyMetadata Include="BrandId" Value="$(BrandId)" />` は `.pubxml` の値を拾えます。
> これが「`BrandId` は `.pubxml` で効くのに、`AssemblyName` の条件分岐は効かない」理由です。

`Product` / `AssemblyTitle` (exe のプロパティに出る表示名) も同じく `.pubxml` に置けます。

```xml
<Product>Watashi-a</Product>
<AssemblyTitle>Watashi-a</AssemblyTitle>
```

#### ⚠ 他プロジェクトは `AssemblyName` の汚染から守る (対応済み)

**Visual Studio の「発行」は、プロファイルの `AssemblyName` をソリューション全体の復元へ波及させます。**
無防備だと 5 つのプロジェクト全部が同じ名前になり、NuGet の復元がこう失敗します。

```
error : Ambiguous project name 'Watashi_KmtMSA.Client'. [Watashi.sln]
```

コマンドラインからプロジェクトを指定して発行した場合は復元範囲が狭いため**再現しません**。
「VS の発行だけ失敗し、コマンドラインなら通る」という切り分けになったらこれを疑ってください。

対策として、`Watashi.Client` **以外**の 4 プロジェクトに自分の名前を守る指定を入れてあります。

```xml
<Project Sdk="Microsoft.NET.Sdk" TreatAsLocalProperty="AssemblyName">
  <PropertyGroup>
    <AssemblyName>$(MSBuildProjectName)</AssemblyName>
```

`TreatAsLocalProperty` は、外から渡されたグローバル値をプロジェクト側で上書き可能にする
MSBuild の機能です。新しいプロジェクトをソリューションに追加する場合も、同じ指定を入れてください
([§7-7](#7-7-設定を自動で点検する) のスクリプトが検出します)。

#### `ClickOnceProfile.pubxml`

`ProductName` に加えて、発行先と更新先を A/B で分けます。
`PublisherName` は会社・発行組織名なので共通でも構いません。
`SuiteName` も同じスタートメニューフォルダにまとめたい場合は共通にできます。

```xml
<!-- A 版の例 -->
<PublishUrl>\\fileserver\share\Watashi-a\</PublishUrl>
<InstallUrl>https://host/install/watashi-a/</InstallUrl>
<UpdateUrl>https://host/install/watashi-a/</UpdateUrl>
<ProductName>Watashi-a</ProductName>
```

```xml
<!-- B 版の例 -->
<PublishUrl>\\fileserver\share\Watashi-b\</PublishUrl>
<InstallUrl>https://host/install/watashi-b/</InstallUrl>
<UpdateUrl>https://host/install/watashi-b/</UpdateUrl>
<ProductName>Watashi-b</ProductName>
```

#### `deployment.json`

`serverUrl` と `updateManifestUrl` は必ず同じ版の組み合わせにします。
A 版から B 版のマニフェストを参照させてはいけません。

```json
{
  "serverUrl": "https://server-a.internal",
  "updateManifestUrl": "https://host/install/watashi-a/WatashiA.Client.application"
}
```

```json
{
  "serverUrl": "https://server-b.internal",
  "updateManifestUrl": "https://host/install/watashi-b/WatashiB.Client.application"
}
```

### 7-3. タスクバー ID は ClickOnce に管理させる

Windows は AppUserModelID を使ってタスクバーのウィンドウをグループ化します。
ClickOnce で起動するアプリについては、ClickOnce が AppUserModelID を割り当てます。

そのため、このプロジェクトのような ClickOnce 配布では
`SetCurrentProcessExplicitAppUserModelID` をアプリ側から呼ばないでください。
Microsoft も、ClickOnce 管理アプリが明示的な AppUserModelID を設定すると
ClickOnce が割り当てる ID と競合し、予期しない結果になると説明しています。

- [Microsoft Learn: Application User Model IDs](https://learn.microsoft.com/windows/win32/shell/appids)
- [Microsoft Learn: ClickOnce application manifest](https://learn.microsoft.com/visualstudio/deployment/clickonce-application-manifest)

タスクバーグループを分けるには、明示的な AppUserModelID をコードへ追加するのではなく、
A/B の `AssemblyName`、ClickOnce マニフェスト ID、配布 URL を分離し、
ClickOnce から別アプリとしてインストールしてください。

### 7-4. ローカル設定・自動ログイン・ログも分離する (`BrandId`)

ローカル状態の保存先は、ビルド時プロパティ **`BrandId`** で版ごとに分離します。
`Services/Brand.cs` がアセンブリに焼き込まれた値を読み、次の 3 つの保存先を決めます。

| 対象 | 保存先 | ファイル |
|---|---|---|
| ユーザー設定 | `%LOCALAPPDATA%\{BrandId}\settings.json` | `Services/AppSettings.cs` |
| クライアントログ | `%LOCALAPPDATA%\{BrandId}\logs\` | `Services/AppLog.cs` |
| 自動ログイン資格情報 | `{BrandId}/AutoLogin` | `Services/CredentialStore.cs` |

**既定値は `Watashi`** です。`BrandId` を指定しなければ保存先は従来と同じなので、
1 種類だけを配布する通常のリブランドでは何もする必要はありません。

#### なぜ分離が必須か

資格情報ターゲットは Windows 資格情報マネージャーの **1 スロットを奪い合います**。
デバイストークンは各中央サーバの DB (`TrustedDevices`) に個別に保存されるため、
**A 版のトークンは B 版のサーバでは決して検証を通りません**。共通のままだと:

1. A で「この PC を記憶」→ トークン T_A を保存
2. B で「この PC を記憶」→ 上書きされ T_A 消滅
3. A を起動 → T_B を A のサーバへ送り 401
4. `App.xaml.cs` が `ClearDeviceToken()` を実行し **T_B も消える**
5. 以降 A/B が互いの自動ログインを潰し合い、毎回手動ログインになる

利用者からは原因の分からない「勝手に自動ログインが切れる」不具合として現れます。

#### 発行時の指定

**方法 1: 発行プロファイルに書く (Visual Studio から発行する場合はこちら)**

`AssemblyName` と同じ `.pubxml` に併記します。ブランド固有の値が 1 ファイルに揃うため、
「URL は B に変えたのに `BrandId` を付け忘れた」という取り違えが起きません。

```xml
<BrandId>Watashi-b</BrandId>
```

VS の「発行」画面でプロファイルを選ぶだけで反映されます。コマンドは不要です。

**方法 2: コマンドラインで渡す**

```powershell
MSBuild.exe src\Watashi.Client\Watashi.Client.csproj /t:Publish /p:Configuration=Release /p:PublishProfile=ClickOnceProfile /p:BrandId=Watashi-b
```

`/p:` はグローバルプロパティとして最優先で効き、プロファイルの記述も上書きします。

> ⚠ **既存配布の `BrandId` は変更しないでください。**
> 現行ツールは `BrandId` 未指定 (= `Watashi`) のまま発行し、**新ブランド側にだけ**新しい値を与えます。
> 現行側も `Watashi-a` などに変えると、**既存利用者全員が設定と自動ログインを 1 回失います**
> (再ログインで復旧しますが問い合わせの原因になります)。
>
> | | `BrandId` | 保存先 |
> |---|---|---|
> | 現行 (既存利用者) | **指定しない** | `%LOCALAPPDATA%\Watashi\` (現状維持) |
> | 新ブランド | `Watashi-b` 等 | `%LOCALAPPDATA%\Watashi-b\` |

#### 確認方法

A/B 双方で「この PC を記憶」した後、資格情報が 2 件に分かれていることを確認します。

```powershell
cmdkey /list | findstr /i AutoLogin
```

`Watashi/AutoLogin` と `Watashi-b/AutoLogin` の 2 件が出れば成功です。
あわせて `%LOCALAPPDATA%` に 2 フォルダができ、A/B を交互に起動しても
自動ログインが切れないことを確認してください。

### 7-5. 発行後にマニフェストを確認する

生成された `.application` と `Application Files` 内の `*.manifest` を開き、
最上位の `assemblyIdentity` が A/B で異なることを確認します。

期待例:

```xml
<!-- A 版 -->
<assemblyIdentity name="WatashiA.Client.application" ... />
```

```xml
<!-- B 版 -->
<assemblyIdentity name="WatashiB.Client.application" ... />
```

アプリケーションマニフェスト側も `WatashiA.Client.exe` / `WatashiB.Client.exe` のように
別名になっていることを確認してください。生成済みマニフェストを手作業で改名・編集すると
署名とハッシュが壊れるため、設定を直して必ず再発行します。

### 7-6. 既に同じアプリとして入っている PC での再確認

古い ClickOnce インストールやタスクバーのピン留めが残っていると、新しい発行物を正しく評価できません。

1. Windows の「インストールされているアプリ」で、旧 `Watashi` / 試作 A/B 版をアンインストール
2. 旧アイコンをタスクバーにピン留めしている場合はピン留めを外す
3. A 版を A 専用 URL からインストール
4. B 版を B 専用 URL からインストール
5. A/B を同時起動し、タスクバーが 2 グループに分かれることを確認
6. ホバー表示が `Watashi-a` / `Watashi-b` になることを確認
7. それぞれが自分の更新 URL と中央サーバだけを参照することを確認
8. A/B 双方で自動ログインを設定し、再起動後も互いの資格情報を上書きしないことを確認

### 7-7. 設定を自動で点検する

発行前の確認は **`scripts/Check-BrandSetup.ps1`** で自動化できます。
読み取り専用で、リポジトリを書き換えません。

```powershell
powershell -NoProfile -File scripts/Check-BrandSetup.ps1
```

点検項目:

| # | 内容 |
|---|---|
| 1 | リポジトリがクラウド同期フォルダ (OneDrive 等) 配下にないか |
| 2 | `.csproj` の重複・競合コピーがないか |
| 3 | `.sln` にプロジェクト名の重複がないか |
| 4 | `AssemblyName` が複数ファイルで重複していないか |
| 5 | ブランド別プロファイルに `BrandId` が漏れていないか / 重複していないか |
| 6 | 他プロジェクトに `TreatAsLocalProperty` の防御が入っているか |

問題があれば終了コード 1 を返すので、発行手順に組み込めます。

### 7-8. 症状別チェック

| 症状 | 主な確認箇所 |
|---|---|
| **VS の発行でだけ `Ambiguous project name '<名前>'`** (コマンドラインは通る) | **他プロジェクトの `TreatAsLocalProperty` 防御が抜けている ([§7-2](#7-2-a-版と-b-版で変更する値))。当面はコマンドラインから発行すれば回避できる** |
| コマンドラインでも `Ambiguous project name` | 同じ `AssemblyName` を宣言したファイルが 2 つ以上ある。`.csproj` の重複・競合コピーも疑う |
| `Watashi (2)` と表示される | A/B の `assemblyIdentity`、`AssemblyName`、旧ピン留め |
| A/B の片方がもう片方として更新される | `UpdateUrl`、`updateManifestUrl`、配布マニフェスト名 |
| 片方を入れるともう片方が置き換わる | ClickOnce `assemblyIdentity` と配布 URL |
| ホバー名だけ `Watashi` のまま | 各 XAML の `Window.Title`、古いビルド成果物 |
| 自動ログインが突然解除される | 発行時の `BrandId` 指定漏れ ([§7-4](#7-4-ローカル設定自動ログインログも分離する-brandid)) |
| 設定やログが混ざる | 発行時の `BrandId` 指定漏れ ([§7-4](#7-4-ローカル設定自動ログインログも分離する-brandid)) |
| ビルドは通るのに `XDG0008 名前 "○○View" は名前空間に存在しません` | XAML デザイナー由来。クリーン後にビルドすれば消える。エラー一覧のフィルタを「ビルドのみ」にすると実害の有無が分かる |

---

## 8. 変更後の確認手順

1. クライアントをリビルド: `dotnet build src\Watashi.Client\Watashi.Client.csproj`
2. アイコンを変えた場合は `scripts/Generate-ToriiIcon.ps1` を再実行して `.ico` を更新
3. クライアント起動 → **ログイン画面・メイン画面・管理画面**の表示名を目視確認
4. エクスプローラで `Watashi.Client.exe` (または新 exe 名) のアイコン、タスクバー表示を確認
5. ClickOnce 発行 (`MSBuild.exe src\Watashi.Client\Watashi.Client.csproj /t:Publish /p:Configuration=Release /p:PublishProfile=ClickOnceProfile`) →
   インストーラ画面・スタートメニュー・「アプリと機能」の名前/アイコンを確認
6. A/B 共存が必要な場合は [§7-5](#7-5-発行後にマニフェストを確認する) と
   [§7-6](#7-6-既に同じアプリとして入っている-pc-での再確認) の確認も実施

---

参考: [deploy/README.md](../deploy/README.md) (アプリケーションアイコンの節) / [docs/DEVELOPMENT.md](DEVELOPMENT.md)
