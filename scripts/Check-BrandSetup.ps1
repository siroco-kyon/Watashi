<#
.SYNOPSIS
  Diagnose the A/B brand coexistence setup before a ClickOnce publish.

.DESCRIPTION
  Publishing two differently branded builds of the client from one repository
  goes wrong in a few specific, hard-to-read ways. This script checks for them
  and prints what to fix. See docs/BRANDING.md section 7.

  Checks performed:

    1. Repository location
       Cloud-synced folders (OneDrive / Dropbox / Google Drive) create conflict
       copies and lock files mid-build, which breaks publish in confusing ways.

    2. Project files
       A duplicated or conflict-copied .csproj makes NuGet restore fail with
       "Ambiguous project name '<name>'." during publish.

    3. Solution entries
       Two solution entries resolving to the same project name cause the same
       restore failure.

    4. AssemblyName
       NuGet derives the restore project name from AssemblyName. When two files
       declare the same AssemblyName the restore graph becomes ambiguous, and
       A/B also end up sharing one ClickOnce application identity.

    5. BrandId
       Local state (settings, logs, auto-login credential) is separated by
       BrandId. A brand-specific publish profile that sets AssemblyName but not
       BrandId silently falls back to the default, so A/B fight over the same
       Windows credential slot and keep revoking each other's auto-login.

  This script only reads files. It never modifies the repository.

  This script is intentionally ASCII-only: Windows PowerShell 5.1 reads a
  BOM-less .ps1 in the system ANSI codepage, which would garble non-ASCII
  source. All messages are therefore in English.

.PARAMETER RepositoryRoot
  Repository root to inspect. Default: the parent folder of this script.

.EXAMPLE
  powershell -NoProfile -File scripts/Check-BrandSetup.ps1

.EXAMPLE
  # Inspect a clone that lives somewhere else
  powershell -NoProfile -File scripts/Check-BrandSetup.ps1 -RepositoryRoot "C:\src\watashi"

.NOTES
  Exit code 0 = no problems found, 1 = at least one problem reported.
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$RepositoryRoot
)

$ErrorActionPreference = 'Stop'

if (-not $RepositoryRoot) { $RepositoryRoot = Split-Path -Parent $PSScriptRoot }
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path

$script:Problems = @()

function Write-Section { param([string]$Title)
    Write-Host ''
    Write-Host "== $Title ==" -ForegroundColor Cyan
}
function Write-Ok      { param([string]$Message) Write-Host "  [ OK ] $Message" -ForegroundColor Green }
function Write-Problem { param([string]$Message)
    Write-Host "  [WARN] $Message" -ForegroundColor Yellow
    $script:Problems += $Message
}
function Write-Detail  { param([string]$Message) Write-Host "         $Message" -ForegroundColor DarkGray }

function Get-RelativePath {
    param([string]$FullName)
    if ($FullName.StartsWith($RepositoryRoot, [StringComparison]::OrdinalIgnoreCase)) {
        return $FullName.Substring($RepositoryRoot.Length).TrimStart('\')
    }
    return $FullName
}

# Read an MSBuild property out of a project / publish profile without a full
# XML parse, so that a malformed file still yields a useful report.
function Get-MSBuildPropertyValue {
    param([string]$Path, [string]$PropertyName)
    $content = Get-Content -LiteralPath $Path -Raw -ErrorAction SilentlyContinue
    if (-not $content) { return @() }
    $pattern = '<' + $PropertyName + '(?:\s[^>]*)?>\s*([^<]+?)\s*</' + $PropertyName + '>'
    return ([regex]::Matches($content, $pattern) | ForEach-Object { $_.Groups[1].Value })
}

Write-Host ''
Write-Host 'Watashi brand setup check' -ForegroundColor White
Write-Host "Repository: $RepositoryRoot"

# --- 1. Repository location -------------------------------------------------
Write-Section '1. Repository location'
if ($RepositoryRoot -match 'OneDrive|Dropbox|GoogleDrive|Google Drive') {
    Write-Problem 'Repository lives inside a cloud-synced folder.'
    Write-Detail 'Sync can create conflict copies of .csproj and lock files during a build.'
    Write-Detail 'Move the clone to a plain local path, for example C:\src\watashi.'
} else {
    Write-Ok 'Not inside a known cloud-synced folder.'
}

# --- 2. Project files -------------------------------------------------------
Write-Section '2. Project files'
$allFiles = Get-ChildItem -LiteralPath $RepositoryRoot -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\(bin|obj|\.git|\.vs|node_modules)\\' }

$projectFiles = @($allFiles | Where-Object { $_.Extension -eq '.csproj' })

if ($projectFiles.Count -eq 0) {
    Write-Problem 'No .csproj found. Is -RepositoryRoot pointing at the repository?'
} else {
    foreach ($p in ($projectFiles | Sort-Object FullName)) {
        Write-Detail (Get-RelativePath $p.FullName)
    }
    Write-Host "         ($($projectFiles.Count) project file(s))"

    # A conflict copy or a hand-made duplicate normally lands next to the
    # original, so its file name no longer matches its own folder name.
    $mismatched = @($projectFiles | Where-Object { $_.BaseName -ne $_.Directory.Name })
    if ($mismatched.Count -gt 0) {
        Write-Problem 'Project file name does not match its folder name (possible duplicate or conflict copy):'
        foreach ($m in $mismatched) { Write-Detail (Get-RelativePath $m.FullName) }
    }

    $crowded = @($projectFiles | Group-Object { $_.DirectoryName } | Where-Object { $_.Count -gt 1 })
    if ($crowded.Count -gt 0) {
        Write-Problem 'More than one .csproj in the same folder:'
        foreach ($c in $crowded) {
            foreach ($f in $c.Group) { Write-Detail (Get-RelativePath $f.FullName) }
        }
    }

    if ($mismatched.Count -eq 0 -and $crowded.Count -eq 0) {
        Write-Ok 'No duplicated or conflict-copied project files.'
    }
}

# --- 3. Solution entries ----------------------------------------------------
Write-Section '3. Solution entries'
$solutions = @($allFiles | Where-Object { $_.Extension -eq '.sln' })
if ($solutions.Count -eq 0) {
    Write-Detail 'No .sln found (skipped).'
} else {
    foreach ($sln in $solutions) {
        Write-Detail (Get-RelativePath $sln.FullName)
        $content = Get-Content -LiteralPath $sln.FullName -Raw
        # Project("{type-guid}") = "Name", "relative\path.csproj", "{guid}"
        $entries = [regex]::Matches($content, 'Project\("\{[^}]+\}"\)\s*=\s*"([^"]+)",\s*"([^"]+)"') |
            ForEach-Object {
                [pscustomobject]@{ Name = $_.Groups[1].Value; Path = $_.Groups[2].Value }
            }
        $codeProjects = @($entries | Where-Object { $_.Path -match '\.csproj$' })
        foreach ($e in $codeProjects) { Write-Detail "  $($e.Name) -> $($e.Path)" }

        $dupNames = @($codeProjects | Group-Object Name | Where-Object { $_.Count -gt 1 })
        if ($dupNames.Count -gt 0) {
            foreach ($d in $dupNames) {
                Write-Problem "Solution lists the project name '$($d.Name)' $($d.Count) times."
            }
        } else {
            Write-Ok "$((Get-RelativePath $sln.FullName)): project names are unique."
        }
    }
}

# --- 4. AssemblyName --------------------------------------------------------
Write-Section '4. AssemblyName'
$configFiles = @($allFiles | Where-Object { $_.Extension -eq '.csproj' -or $_.Extension -eq '.pubxml' })
$assemblyNames = @()
foreach ($f in ($configFiles | Sort-Object FullName)) {
    foreach ($v in (Get-MSBuildPropertyValue -Path $f.FullName -PropertyName 'AssemblyName')) {
        $assemblyNames += [pscustomobject]@{ File = (Get-RelativePath $f.FullName); Value = $v }
    }
}

if ($assemblyNames.Count -eq 0) {
    Write-Ok 'No explicit AssemblyName. Output names follow the project names.'
    Write-Detail 'Expected when only one brand is published.'
} else {
    foreach ($a in $assemblyNames) { Write-Detail "$($a.Value)   <- $($a.File)" }

    $dupAsm = @($assemblyNames | Group-Object Value | Where-Object { $_.Count -gt 1 })
    if ($dupAsm.Count -gt 0) {
        foreach ($d in $dupAsm) {
            Write-Problem "AssemblyName '$($d.Name)' is declared in $($d.Count) files:"
            foreach ($f in $d.Group) { Write-Detail $f.File }
            Write-Detail 'This is the usual cause of "Ambiguous project name" during publish,'
            Write-Detail 'and it also makes A/B share one ClickOnce application identity.'
            Write-Detail 'Give each brand its own AssemblyName in its own publish profile.'
        }
    } else {
        Write-Ok 'Every declared AssemblyName is unique.'
    }
}

# --- 5. BrandId -------------------------------------------------------------
Write-Section '5. BrandId (local state separation)'
$brandIds = @()
foreach ($f in ($configFiles | Sort-Object FullName)) {
    foreach ($v in (Get-MSBuildPropertyValue -Path $f.FullName -PropertyName 'BrandId')) {
        $brandIds += [pscustomobject]@{
            File      = (Get-RelativePath $f.FullName)
            Value     = $v
            Extension = $f.Extension
        }
    }
}
foreach ($b in ($brandIds | Where-Object { $_.Extension -eq '.pubxml' })) {
    Write-Detail "$($b.Value)   <- $($b.File)"
}

# A publish profile that overrides AssemblyName is brand-specific, so it must
# also carry a BrandId or its local state collides with the default build.
$brandProfiles = @($assemblyNames | Where-Object { $_.File -match '\.pubxml$' })
$missingBrandId = @()
foreach ($p in $brandProfiles) {
    $hasBrandId = @($brandIds | Where-Object { $_.File -eq $p.File }).Count -gt 0
    if (-not $hasBrandId) { $missingBrandId += $p.File }
}
if ($missingBrandId.Count -gt 0) {
    Write-Problem 'Publish profile sets AssemblyName but not BrandId:'
    foreach ($m in $missingBrandId) { Write-Detail $m }
    Write-Detail 'Local state falls back to the default "Watashi", so this build shares'
    Write-Detail 'settings, logs and the auto-login credential with the other brand.'
    Write-Detail 'See docs/BRANDING.md section 7-4.'
}

$pubxmlBrandIds = @($brandIds | Where-Object { $_.Extension -eq '.pubxml' })
$dupBrand = @($pubxmlBrandIds | Group-Object Value | Where-Object { $_.Count -gt 1 })
if ($dupBrand.Count -gt 0) {
    foreach ($d in $dupBrand) {
        Write-Problem "BrandId '$($d.Name)' is used by $($d.Count) publish profiles:"
        foreach ($f in $d.Group) { Write-Detail $f.File }
        Write-Detail 'Those builds share local state. Give each brand a distinct BrandId.'
    }
}
if ($missingBrandId.Count -eq 0 -and $dupBrand.Count -eq 0) {
    if ($brandProfiles.Count -eq 0) {
        Write-Ok 'No brand-specific publish profile. Local state uses the default "Watashi".'
    } else {
        Write-Ok 'Every brand-specific publish profile carries a distinct BrandId.'
    }
}

# --- Summary ----------------------------------------------------------------
Write-Section 'Summary'
if ($script:Problems.Count -eq 0) {
    Write-Host '  No problems found.' -ForegroundColor Green
    exit 0
}
Write-Host "  $($script:Problems.Count) problem(s) found:" -ForegroundColor Yellow
foreach ($p in $script:Problems) { Write-Host "    - $p" -ForegroundColor Yellow }
Write-Host ''
Write-Host '  See docs/BRANDING.md section 7 for the full A/B coexistence checklist.'
exit 1
