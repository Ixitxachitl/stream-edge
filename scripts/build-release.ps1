<#
.SYNOPSIS
    Builds a self-contained StreamEdge release for the host platform and
    packages it under dist/.

.DESCRIPTION
    Runs on Windows, Linux and macOS under PowerShell 7 (pwsh).
      1. Publishes the app self-contained for the host RID (no .NET install
         needed on the target machine).
      2. Packages the staged folder: .zip on Windows/macOS, .tar.gz on Linux.

    The version defaults to <VersionPrefix> in Directory.Build.props. Pass -Tag
    to also create an annotated git tag (v<version>). -Arch x86 builds the
    32-bit Windows release on a 64-bit Windows machine.

.EXAMPLE
    pwsh scripts/build-release.ps1
    pwsh scripts/build-release.ps1 -Arch x86
    pwsh scripts/build-release.ps1 -Version 0.2.0 -Tag
#>
[CmdletBinding()]
param(
    [string]$Version,
    # The host's architecture unless given; x86 is for Windows only.
    [ValidateSet('', 'x64', 'x86', 'arm64')]
    [string]$Arch = '',
    [switch]$Tag
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $repoRoot

$arch = if ($Arch) { $Arch }
        elseif ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' }
        else { 'x64' }
if ($IsWindows -or $PSVersionTable.PSVersion.Major -le 5) { $platform = 'windows'; $rid = "win-$arch";   $archive = 'zip' }
elseif ($IsLinux)                                        { $platform = 'linux';   $rid = "linux-$arch"; $archive = 'tar.gz' }
elseif ($IsMacOS)                                        { $platform = 'macos';   $rid = "osx-$arch";   $archive = 'zip' }
else { throw 'Unsupported host platform.' }
if ($arch -eq 'x86' -and $platform -ne 'windows') { throw 'x86 builds are for Windows only.' }

if (-not $Version) {
    $Version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.VersionPrefix |
               Where-Object { $_ } | Select-Object -First 1
}

$name    = "StreamEdge-$Version-$platform-$arch"
$staging = Join-Path $repoRoot "dist/$name"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }

# The player needs libmpv. On Windows it ships beside the executable;
# elsewhere it is a system package the user installs (see README).
if ($platform -eq 'windows') {
    & (Join-Path $PSScriptRoot 'fetch-libmpv.ps1') -Arch $arch
}

Write-Host "Publishing $name ($rid)..."
dotnet publish app/StreamEdge.App.Avalonia/StreamEdge.App.Avalonia.csproj `
    -c Release -r $rid --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:VersionPrefix=$Version -o $staging --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

Copy-Item LICENSE, README.md $staging

$out = Join-Path $repoRoot "dist/$name.$archive"
if (Test-Path $out) { Remove-Item $out -Force }
if ($archive -eq 'zip') { Compress-Archive -Path "$staging/*" -DestinationPath $out }
else { tar -czf $out -C (Join-Path $repoRoot 'dist') $name }
Write-Host "Wrote $out"

if ($Tag) {
    git tag -a "v$Version" -m "StreamEdge v$Version"
    Write-Host "Tagged v$Version (push with: git push origin v$Version)"
}
