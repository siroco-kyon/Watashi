<#
.SYNOPSIS
  Rename the on-screen application name (default "Watashi") across the WPF
  client in one shot, without touching namespaces / class names.

.DESCRIPTION
  Replaces only the UI-facing brand strings documented in docs/BRANDING.md
  sections 2 (display name) and 4 (ClickOnce product name), using anchored
  patterns so internal identifiers like the "Watashi.Client" namespace are
  never modified:

    XAML  : Text="OLD"          -> Text="NEW"
            Title="OLD          -> Title="NEW    (keeps any suffix, e.g. " - login")
    C#    : "OLD -              -> "NEW -         (dialog / About titles)
    pubxml: <ProductName>OLD</ProductName> and PublisherName / SuiteName

  It does NOT change:
    - namespaces / class names / x:Class (they are internal, not shown on screen)
    - the exe/assembly name (BRANDING.md section 3 - opt-in, edit the csproj)
    - the subtitle text ("CIFS file management" etc.)
    - the icon (BRANDING.md section 5 - see Generate-ToriiIcon.ps1)
    - publish/install URLs in the pubxml

  Encoding: each file is read and rewritten as UTF-8 with its original BOM
  state preserved, so Japanese titles (e.g. "OLD - kanri") are not corrupted.

  This script is intentionally ASCII-only: Windows PowerShell 5.1 reads a
  BOM-less .ps1 in the system ANSI codepage, which would garble non-ASCII
  source. All messages are therefore in English.

.PARAMETER NewName
  The new brand name to show on screen (required).

.PARAMETER OldName
  The current brand name to replace. Default: "Watashi".

.PARAMETER ClientDir
  Path to the WPF client project. Default: <repo>/src/Watashi.Client

.PARAMETER WhatIf
  Preview only. Lists every file and line that WOULD change, writes nothing.

.EXAMPLE
  # Preview first (nothing is written):
  powershell -NoProfile -File scripts/Rename-Brand.ps1 -NewName "Torii" -WhatIf

.EXAMPLE
  # Apply:
  powershell -NoProfile -File scripts/Rename-Brand.ps1 -NewName "Torii"
#>
param(
    [Parameter(Mandatory = $true)][string]$NewName,
    [string]$OldName = 'Watashi',
    [string]$ClientDir,
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($NewName)) { throw 'NewName must not be empty.' }
if ($NewName -eq $OldName) { throw "NewName equals OldName ('$OldName'); nothing to do." }

if (-not $ClientDir) {
    $repo = Split-Path -Parent $PSScriptRoot
    $ClientDir = Join-Path $repo 'src\Watashi.Client'
}
if (-not (Test-Path $ClientDir)) { throw "ClientDir not found: $ClientDir" }
$ClientDir = (Resolve-Path $ClientDir).Path

# Escape the old name for use inside regex patterns (e.g. a dot in the name).
$old = [regex]::Escape($OldName)

# Anchored replacement rules. Each rule is (regex pattern, replacement).
# The patterns are deliberately narrow so that namespace tokens such as
# "OldName.Client" (which is "OldName." not "OldName\"" / "OldName - ") never match.
$xamlRules = @(
    @{ Pattern = ('Text="{0}"' -f $old);  Replace = ('Text="{0}"' -f $NewName) },
    @{ Pattern = ('Title="{0}' -f $old);  Replace = ('Title="{0}' -f $NewName) }
)
$csRules = @(
    @{ Pattern = ('"{0} - ' -f $old);     Replace = ('"{0} - ' -f $NewName) }
)
$pubxmlRules = @(
    @{ Pattern = ('<ProductName>{0}</ProductName>' -f $old);     Replace = ('<ProductName>{0}</ProductName>' -f $NewName) },
    @{ Pattern = ('<PublisherName>{0}</PublisherName>' -f $old); Replace = ('<PublisherName>{0}</PublisherName>' -f $NewName) },
    @{ Pattern = ('<SuiteName>{0}</SuiteName>' -f $old);         Replace = ('<SuiteName>{0}</SuiteName>' -f $NewName) }
)

function Read-TextPreserveBom([string]$path, [ref]$hadBom) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        $hadBom.Value = $true
        return [System.Text.Encoding]::UTF8.GetString($bytes, 3, $bytes.Length - 3)
    }
    $hadBom.Value = $false
    return [System.Text.Encoding]::UTF8.GetString($bytes)
}

function Write-TextPreserveBom([string]$path, [string]$text, [bool]$withBom) {
    $enc = New-Object System.Text.UTF8Encoding($withBom)
    [System.IO.File]::WriteAllText($path, $text, $enc)
}

$totalFiles = 0
$totalHits = 0

function Invoke-FileRules([string]$path, [array]$rules) {
    $hadBom = $false
    $text = Read-TextPreserveBom $path ([ref]$hadBom)
    $orig = $text
    $fileHits = 0
    $lineHits = New-Object System.Collections.ArrayList

    foreach ($rule in $rules) {
        $rx = [regex]$rule.Pattern
        $m = $rx.Matches($text)
        if ($m.Count -gt 0) {
            $fileHits += $m.Count
            foreach ($hit in $m) {
                # Report the 1-based line number of each hit for the preview.
                $lineNo = ($text.Substring(0, $hit.Index) -split "`n").Count
                [void]$lineHits.Add("L$lineNo  $($hit.Value)")
            }
            $text = $rx.Replace($text, $rule.Replace)
        }
    }

    if ($fileHits -eq 0) { return 0 }

    $rel = $path.Substring($ClientDir.Length).TrimStart('\', '/')
    Write-Host ("  {0}  ({1} hit(s))" -f $rel, $fileHits) -ForegroundColor Cyan
    foreach ($l in $lineHits) { Write-Host "      $l" }

    if (-not $WhatIf -and $text -ne $orig) {
        Write-TextPreserveBom $path $text $hadBom
    }
    return $fileHits
}

Write-Host ""
if ($WhatIf) {
    Write-Host ("PREVIEW (no files written): '{0}' -> '{1}'" -f $OldName, $NewName) -ForegroundColor Yellow
} else {
    Write-Host ("Renaming brand: '{0}' -> '{1}'" -f $OldName, $NewName) -ForegroundColor Green
}
Write-Host ("Client dir: {0}" -f $ClientDir)
Write-Host ""

# --- XAML (excluding build output) ---
Write-Host "XAML:"
Get-ChildItem $ClientDir -Recurse -Include *.xaml |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    Sort-Object FullName |
    ForEach-Object {
        $h = Invoke-FileRules $_.FullName $xamlRules
        if ($h -gt 0) { $script:totalFiles++; $script:totalHits += $h }
    }

# --- C# (excluding build output) ---
Write-Host ""
Write-Host "C#:"
Get-ChildItem $ClientDir -Recurse -Include *.cs |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    Sort-Object FullName |
    ForEach-Object {
        $h = Invoke-FileRules $_.FullName $csRules
        if ($h -gt 0) { $script:totalFiles++; $script:totalHits += $h }
    }

# --- ClickOnce publish profile ---
Write-Host ""
Write-Host "ClickOnce (pubxml):"
$pubxml = Join-Path $ClientDir 'Properties\PublishProfiles\ClickOnceProfile.pubxml'
if (Test-Path $pubxml) {
    $h = Invoke-FileRules $pubxml $pubxmlRules
    if ($h -gt 0) { $totalFiles++; $totalHits += $h }
} else {
    Write-Host "  (ClickOnceProfile.pubxml not found - skipped)"
}

Write-Host ""
Write-Host ("Total: {0} replacement(s) across {1} file(s)." -f $totalHits, $totalFiles)
if ($WhatIf) {
    Write-Host "This was a preview. Re-run without -WhatIf to apply." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Not handled by this script (see docs/BRANDING.md):"
Write-Host "  - exe/assembly name        (section 3: add AssemblyName to the csproj)"
Write-Host "  - subtitle 'CIFS ...' text (section 2 note: optional)"
Write-Host "  - icon / brand color       (section 5: Generate-ToriiIcon.ps1, Colors.xaml)"
Write-Host "  - server-side names        (section 6: services, data paths, JWT issuer)"
Write-Host ""
Write-Host "After applying, rebuild and verify:"
Write-Host "  dotnet build src\Watashi.Client\Watashi.Client.csproj"
