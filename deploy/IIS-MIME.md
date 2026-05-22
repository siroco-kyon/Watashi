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

# 既存削除（多重登録回避）
@(".application", ".manifest", ".deploy") | ForEach-Object {
    Get-WebConfigurationProperty -Filter "system.webServer/staticContent" -Name . -PSPath $path |
        Select-Object -ExpandProperty Collection |
        Where-Object { $_.fileExtension -eq $_ } |
        ForEach-Object { Remove-WebConfigurationProperty -Filter "system.webServer/staticContent" -Name . -AtElement @{ fileExtension = $_.fileExtension } -PSPath $path }
}

Add-WebConfigurationProperty -Filter "system.webServer/staticContent" -Name . -Value @{ fileExtension = ".application"; mimeType = "application/x-ms-application" } -PSPath $path
Add-WebConfigurationProperty -Filter "system.webServer/staticContent" -Name . -Value @{ fileExtension = ".manifest";    mimeType = "application/x-ms-manifest" }    -PSPath $path
Add-WebConfigurationProperty -Filter "system.webServer/staticContent" -Name . -Value @{ fileExtension = ".deploy";      mimeType = "application/octet-stream" }     -PSPath $path
```

## 配布先構成例

```
\\fileserver\share\Watashi\
├── Watashi.Client.application
├── Application Files\
│   └── Watashi.Client_1_0_0_1\
│       ├── Watashi.Client.exe.manifest
│       ├── Watashi.Client.exe.deploy
│       ├── (依存 DLL).deploy
│       └── appsettings.json.deploy
```

クライアントは `https://watashi.internal/install/Watashi.Client.application` を開くだけでインストール開始。
更新は `UpdateInterval=1 Day` で自動チェックされる。

## 証明書

- 発行: 社内 CA から Code Signing 証明書
- 配布: グループポリシーでクライアント PC の「信頼されたルート証明機関」に社内 CA 証明書を配布
- ClickOnceProfile.pubxml の `ManifestCertificateThumbprint` に Code Signing 証明書のサムプリントを設定
