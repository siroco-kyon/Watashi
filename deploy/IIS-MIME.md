# ClickOnce 配布サーバー (IIS) MIME 設定

ClickOnce マニフェスト/アプリケーションをブラウザがダウンロードして起動するには、IIS で以下の MIME タイプを登録する必要があります。

| 拡張子 | MIME タイプ |
|---|---|
| `.application` | `application/x-ms-application` |
| `.manifest`    | `application/x-ms-manifest` |
| `.deploy`      | `application/octet-stream` |

## PowerShell で一括設定

```powershell
Import-Module WebAdministration

$site = "Default Web Site"
$path = "IIS:\Sites\$site"

# 既存削除（多重登録回避）。外側ループ変数は $ext に明示する。
# （ForEach-Object の $_ で書くと Where-Object 内の $_ と衝突し、削除条件が常に false になる）
foreach ($ext in @(".application", ".manifest", ".deploy")) {
    Get-WebConfigurationProperty -Filter "system.webServer/staticContent" -Name . -PSPath $path |
        Select-Object -ExpandProperty Collection |
        Where-Object { $_.fileExtension -eq $ext } |
        ForEach-Object { Remove-WebConfigurationProperty -Filter "system.webServer/staticContent" -Name . -AtElement @{ fileExtension = $_.fileExtension } -PSPath $path }
}

Add-WebConfigurationProperty -Filter "system.webServer/staticContent" -Name . -Value @{ fileExtension = ".application"; mimeType = "application/x-ms-application" } -PSPath $path
Add-WebConfigurationProperty -Filter "system.webServer/staticContent" -Name . -Value @{ fileExtension = ".manifest";    mimeType = "application/x-ms-manifest" }    -PSPath $path
Add-WebConfigurationProperty -Filter "system.webServer/staticContent" -Name . -Value @{ fileExtension = ".deploy";      mimeType = "application/octet-stream" }     -PSPath $path
```

## 設定確認

登録された MIME が正しく入っているか確認:

```powershell
Get-WebConfigurationProperty -PSPath $path -Filter "system.webServer/staticContent" -Name Collection |
    Where-Object { $_.fileExtension -in ".application", ".manifest", ".deploy" } |
    Select-Object fileExtension, mimeType
```

期待される出力 (3 行):

```
fileExtension mimeType
------------- --------
.application  application/x-ms-application
.manifest     application/x-ms-manifest
.deploy       application/octet-stream
```

## 配布先構成例

発行物 (`bin\publish\` の中身) を、そのまま IIS の install ディレクトリへ展開する。
`publish.htm` だけは ClickOnce が生成しないので、リポジトリの `deploy/install/publish.htm` を一緒に置く。

```
\\fileserver\share\Watashi\            (= https://watashi.internal/install/)
├── publish.htm                         ← 利用者が開くインストールページ (deploy/install/ から手動コピー)
├── Watashi.Client.application          ← デプロイ マニフェスト (インストールの入口)
├── Launcher.exe
└── Application Files\
    └── Watashi.Client_1_26_0612_1217\
        ├── Watashi.Client.dll.manifest
        ├── Watashi.Client.dll.deploy
        ├── (依存 DLL).deploy
        └── deployment.json.deploy
```

- 利用者には **`https://watashi.internal/install/publish.htm`** を案内する (インストールページ)。
- `publish.htm` は隣の `Watashi.Client.application` からバージョンを自動表示するため、再発行ごとの編集は不要。
- ページを介さず `https://watashi.internal/install/Watashi.Client.application` を直接開いてもインストールは始まる。
- 更新は発行プロファイルの `UpdateMode=Foreground` 設定で、ショートカット起動のたびに自動チェックされる ([README.md](README.md) の「更新ポリシー」参照)。

### ポータル + マニュアルも一緒に置く (推奨)

インストールページに加えて、**ポータル (玄関) ページ**と**マニュアル**もサイトのルートへ置くと、利用者には URL を 1 つ案内するだけで済む。リンクは相対パスなので、下のレイアウトどおりに配置すればそのままつながる。

```
\\fileserver\share\Watashi\            (= https://watashi.internal/  サイトのルート)
├── index.html                          ← ポータル (deploy/site/index.html を配置 / IIS 既定ドキュメント)
├── install/                            ← 上記のインストール一式 (publish.htm + ClickOnce 発行物)
│   ├── publish.htm
│   ├── Watashi.Client.application
│   └── Application Files\ ...
└── manual/                             ← docs/manual/ の中身をそのままコピー
    ├── index.html                       (利用者マニュアル)
    ├── admin.html                       (管理者マニュアル)
    └── images\ ...
```

- 利用者には **`https://watashi.internal/`** (ポータル) を案内すればよい。そこから「インストール / 利用者マニュアル / 管理者マニュアル」へ 1 クリックで到達できる。
- `.htm` / `.html` は IIS の既定 MIME なので、マニュアルとポータルに追加の MIME 設定は不要 (ClickOnce 用の `.application` / `.manifest` / `.deploy` の MIME 設定は従来どおり必要)。
- リンク先のフォルダ名 (`install` / `manual`) を変える場合は、`index.html` の `<a href>` を合わせて直す。

## 証明書

- 発行: 社内 CA から Code Signing 証明書
- 配布: グループポリシーでクライアント PC の「信頼されたルート証明機関」に社内 CA 証明書を配布
- ClickOnceProfile.pubxml の `ManifestCertificateThumbprint` に Code Signing 証明書のサムプリントを設定
