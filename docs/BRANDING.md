# Watashi ブランディング変更ガイド

クライアントに配布する **アプリ名** と **アイコン (鳥居マーク)** を変更したいときに、
「どのファイルの何を直すか」をまとめたガイドです。

> このガイドは**変更箇所の一覧**です。実際の改名作業そのものは含みません。
> 新しい名前・アイコンが決まったら、本書を見ながら該当箇所を差し替えてください。

- [1. 変更箇所の全体像](#1-変更箇所の全体像)
- [2. 画面に表示されるアプリ名](#2-画面に表示されるアプリ名watashi)
- [3. exe ファイル名](#3-exe-ファイル名watashiclientexe)
- [4. 配布物・インストーラの名前 (ClickOnce)](#4-配布物インストーラの名前-clickonce)
- [5. アイコン (鳥居マーク)](#5-アイコン-鳥居マーク)
- [6. (任意) より広範にブランドを変える場合](#6-任意-より広範にブランドを変える場合)
- [7. 変更後の確認手順](#7-変更後の確認手順)

---

## 1. 変更箇所の全体像

| 変えたいもの | 主な対象 | 節 |
|---|---|---|
| 画面に出るアプリ名「Watashi」 | クライアントの XAML / `App.xaml.cs` | [§2](#2-画面に表示されるアプリ名watashi) |
| exe ファイル名 (`Watashi.Client.exe`) | `Watashi.Client.csproj` | [§3](#3-exe-ファイル名watashiclientexe) |
| 配布物・インストーラの名前 | `ClickOnceProfile.pubxml` | [§4](#4-配布物インストーラの名前-clickonce) |
| アイコン (鳥居) | `Watashi.ico` + `Icons.xaml` + `Colors.xaml` | [§5](#5-アイコン-鳥居マーク) |

通常のリブランドは **§2・§4・§5 の 3 つ** を変えれば足ります。サーバー側の名前まで変えたい場合は §6 を参照。

---

## 2. 画面に表示されるアプリ名「Watashi」

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

---

## 4. 配布物・インストーラの名前 (ClickOnce)

`src/Watashi.Client/Properties/PublishProfiles/ClickOnceProfile.pubxml`:

| プロパティ | 現在 | 効果 |
|---|---|---|
| `<ProductName>` | `Watashi` | スタートメニュー名・インストーラ画面・「アプリと機能」の表示名 |
| `<PublisherName>` | `Watashi` | 発行元 (スタートメニューのフォルダ名等) |
| `<SuiteName>` | `Watashi` | スタートメニューのグループ名 |
| `<ApplicationIcon>` | `Watashi.ico` | インストーラ/ショートカットのアイコン ([§5](#5-アイコン-鳥居マーク)) |

> 配布 URL (`<PublishUrl>` / `<InstallUrl>` / `<UpdateUrl>`) のパスに `Watashi` が含まれる場合は、
> 配布サーバ側の都合に合わせて任意に変更できます (ブランド名と一致させる必要はありません)。

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
> **通常のリブランドでは §2 (表示名)・§4 (配布物名)・§5 (アイコン) の変更で十分**です。

---

## 7. 変更後の確認手順

1. クライアントをリビルド: `dotnet build src\Watashi.Client\Watashi.Client.csproj`
2. アイコンを変えた場合は `scripts/Generate-ToriiIcon.ps1` を再実行して `.ico` を更新
3. クライアント起動 → **ログイン画面・メイン画面・管理画面**の表示名を目視確認
4. エクスプローラで `Watashi.Client.exe` (または新 exe 名) のアイコン、タスクバー表示を確認
5. ClickOnce 発行 (`dotnet publish ... -p:PublishProfile=ClickOnceProfile`) →
   インストーラ画面・スタートメニュー・「アプリと機能」の名前/アイコンを確認

---

参考: [deploy/README.md](../deploy/README.md) (アプリケーションアイコンの節) / [docs/DEVELOPMENT.md](DEVELOPMENT.md)
