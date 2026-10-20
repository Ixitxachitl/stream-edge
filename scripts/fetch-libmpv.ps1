<#
.SYNOPSIS
    Downloads libmpv for Windows into third_party/libmpv/win-<arch>/.

.DESCRIPTION
    The player needs libmpv at runtime. On Linux and macOS it comes from the
    system package manager (apt install libmpv2 / dnf install mpv-libs /
    brew install mpv), so this script is Windows-only. The build copies the DLL
    next to the app when it is present; the app runs without it, with the
    player pane explaining what's missing.

    x64 gets the slim build made by scripts/build-libmpv.sh (only what
    StreamEdge plays, ~31 MB), from this repo's libmpv-* pre-release and
    checked against its SHA-256. x86 gets shinchiro's general-purpose build.

    Both are pinned, so every machine and CI run plays through the same mpv.
    To update: run the libmpv workflow, then bump -SlimTag and -SlimSha256
    (x64), or -Tag and -Build together (x86).

.EXAMPLE
    pwsh scripts/fetch-libmpv.ps1
    pwsh scripts/fetch-libmpv.ps1 -Arch x86
#>
[CmdletBinding()]
param(
    # x64, or x86 for the 32-bit Windows build.
    [ValidateSet('x64', 'x86')]
    [string]$Arch  = 'x64',
    [string]$Tag   = '20261015',
    [string]$Build = '20261015-git-492bae0768',
    [string]$SlimTag    = 'libmpv-20261020',
    [string]$SlimSha256 = '6C26CDBED458E977B459ECDC66E6E495578286317DB1F86378090705C04F813B',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$Asset    = "mpv-dev-$(if ($Arch -eq 'x86') { 'i686' } else { 'x86_64' })-$Build.7z"
$dest     = Join-Path $repoRoot "third_party/libmpv/win-$Arch"
$dll      = Join-Path $dest 'libmpv-2.dll'

if ((Test-Path $dll) -and -not $Force) {
    Write-Host "libmpv already present: $dll (use -Force to re-download)"
    return
}

if ($Arch -eq 'x64') {
    $url = "https://github.com/Ixitxachitl/stream-edge/releases/download/$SlimTag/libmpv-2.dll"
    $download = Join-Path $env:TEMP "libmpv-2-$SlimTag.dll"
    Write-Host "Downloading $url"
    curl.exe -fL --retry 5 -o $download $url
    if ($LASTEXITCODE -ne 0) { throw "Download failed (curl exit $LASTEXITCODE)." }
    $hash = (Get-FileHash $download -Algorithm SHA256).Hash
    if ($hash -ne $SlimSha256) {
        Remove-Item $download -Force
        throw "libmpv-2.dll from $SlimTag has SHA-256 $hash, expected $SlimSha256."
    }
    New-Item -ItemType Directory -Force $dest | Out-Null
    Move-Item $download $dll -Force
    Set-Content -Path (Join-Path $dest 'VERSION') -Value "$SlimTag slim (scripts/build-libmpv.sh)" -Encoding utf8
    Write-Host "Installed $dll"
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
