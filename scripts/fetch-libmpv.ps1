<#
.SYNOPSIS
    Downloads libmpv for Windows into third_party/libmpv/win-x64/.

.DESCRIPTION
    The player needs libmpv at runtime. On Linux and macOS it comes from the
    system package manager (apt install libmpv2 / dnf install mpv-libs /
    brew install mpv), so this script is Windows-only. The build copies the DLL
    next to the app when it is present; the app runs without it, with the
    player pane explaining what's missing.

    The build is pinned, so every machine and CI run plays through the same
    mpv. Bump -Tag/-Asset together to update.

.EXAMPLE
    pwsh scripts/fetch-libmpv.ps1
#>
[CmdletBinding()]
param(
    [string]$Tag   = '20261015',
    [string]$Asset = 'mpv-dev-x86_64-20261015-git-492bae0768.7z',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$dest     = Join-Path $repoRoot 'third_party/libmpv/win-x64'
$dll      = Join-Path $dest 'libmpv-2.dll'

if ((Test-Path $dll) -and -not $Force) {
    Write-Host "libmpv already present: $dll (use -Force to re-download)"
    return
}

# Downloads land in a fixed, git-ignored folder so an interrupted one resumes
# (curl -C -) instead of starting over: the archive is ~30 MB.
$cache = Join-Path $repoRoot 'third_party/libmpv/.download'
New-Item -ItemType Directory -Force $cache | Out-Null
$archive = Join-Path $cache $Asset
$url = "https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/$Tag/$Asset"
Write-Host "Downloading $url"
curl.exe -fL --retry 5 -C - -o $archive $url
if ($LASTEXITCODE -ne 0) { throw "Download failed (curl exit $LASTEXITCODE); run again to resume." }

$extract = Join-Path $cache 'x'
if (Test-Path $extract) { Remove-Item $extract -Recurse -Force }
New-Item -ItemType Directory -Force $extract | Out-Null
try {
    # Windows' bundled tar is bsdtar, which reads .7z. By full path: Git for
    # Windows puts GNU tar, which can't, earlier on PATH in its shells.
    & (Join-Path $env:SystemRoot 'System32\tar.exe') -xf $archive -C $extract
    if ($LASTEXITCODE -ne 0) { throw "tar failed to extract $Asset ($LASTEXITCODE). Delete $archive and run again." }

    $found = Get-ChildItem $extract -Recurse -Filter 'libmpv-2.dll' | Select-Object -First 1
    if (-not $found) { throw "libmpv-2.dll not found in $Asset." }

    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item $found.FullName $dll -Force
    Set-Content -Path (Join-Path $dest 'VERSION') -Value "$Tag $Asset" -Encoding utf8
    Write-Host "Installed $dll"
}
finally {
    Remove-Item $extract -Recurse -Force -ErrorAction SilentlyContinue
}
