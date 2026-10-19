#!/usr/bin/env pwsh
# Builds PlayMap's installer from a numbered build (installer/PlayMap.iss; docs/round3/design/installer.md): the published exe's
# folder, FFmpeg's LGPL shared libraries under ffmpeg\ with their licence texts (the build's LICENSE.txt, the LGPL version 3, and the
# GPL version 3 it incorporates), the Agility SDK's D3D12Core.dll under D3D12\ (its debug layer left out), libvmx beside the exe with
# its licence, the publish's notices (notices.ps1: THIRD-PARTY-NOTICES.md, OpenAL Soft's licence beside soft_oal.dll, and licenses\
# with every package's own licence and notice files), licence.txt (the terms the licence page shows and the install keeps:
# installer/licence.txt, byte for byte), and the supervisor under Supervisor\; then the
# payload's SHA256SUMS of every file it lays down, shipped with it; compiled by Inno Setup's ISCC with the version Version.props gives
# this run. -Ffmpeg is the LGPL build's extracted folder (bin\ and LICENSE.txt). A local build is refused with words: its installer
# would read as MAJOR.MINOR.0 and could not be upgraded in order.
# -Version builds the same files under another product version (the build before this one, when this one is build 1 of a raised
# MAJOR or MINOR: MsiVersionRule.Previous).
#   installer/build-installer.ps1 -Run 412 -Commit <sha> -Publish publish/PlayMap -Supervisor publish/supervisor -Ffmpeg lgpl/ffmpeg-n9.0.2-…
#       -D3D12 d3d12/D3D12 -Vmx vmx/libvmx.dll -Iscc <ISCC.exe> -Out out [-Version 0.1.0]
param(
    [Parameter(Mandatory)] [string] $Run,
    [string] $Version = '',
    [Parameter(Mandatory)] [string] $Commit,
    [Parameter(Mandatory)] [string] $Publish,
    [Parameter(Mandatory)] [string] $Supervisor,
    [Parameter(Mandatory)] [string] $Ffmpeg,
    [Parameter(Mandatory)] [string] $D3D12,
    [Parameter(Mandatory)] [string] $Vmx,
    [Parameter(Mandatory)] [string] $Iscc,
    [Parameter(Mandatory)] [string] $Out
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path $PSScriptRoot -Parent
$core = Join-Path $repo 'src/PlayMap.Core/PlayMap.Core.csproj'
$env:DOTNET_NOLOGO = '1'
$versionArgument = @(if ($Version) { "-p:PlayMapVersion=$Version" })
$said = (& dotnet msbuild $core -nologo "-p:PlayMapRun=$Run" "-p:PlayMapCommit=$Commit" @versionArgument '-getProperty:PlayMapVersion' '-getProperty:PlayMapMsiVersion' '-getProperty:FileVersion' '-getProperty:_PlayMapBuildNumber') -join "`n"
if ($LASTEXITCODE -ne 0) { throw "Version.props could not be read: $said" }
$props = ($said.Substring($said.IndexOf('{')) | ConvertFrom-Json).Properties
if (-not $props.PlayMapMsiVersion) {
    throw 'An installer is made from a numbered build only (PlayMapRun and PlayMapCommit from CI): a local build''s would read as MAJOR.MINOR.0 and could not be upgraded in order.'
}

$payload = Join-Path ([System.IO.Path]::GetTempPath()) ("playmap-payload-" + $props.FileVersion)
if (Test-Path $payload) { Remove-Item $payload -Recurse -Force }
New-Item -ItemType Directory -Force -Path $payload, (Join-Path $payload 'ffmpeg'), (Join-Path $payload 'D3D12'), (Join-Path $payload 'Supervisor') | Out-Null
Copy-Item (Join-Path $Publish '*') $payload -Recurse
$licences = Join-Path $PSScriptRoot 'licenses'
if (-not (Test-Path (Join-Path $Ffmpeg 'LICENSE.txt'))) { throw "$Ffmpeg holds no LICENSE.txt: -Ffmpeg is the LGPL build's extracted folder" }
Copy-Item (Join-Path $Ffmpeg 'bin\*.dll') (Join-Path $payload 'ffmpeg')
Copy-Item (Join-Path $Ffmpeg 'LICENSE.txt') (Join-Path $payload 'ffmpeg\LICENSE.txt')
Copy-Item (Join-Path $licences 'GPL-3.0.txt') (Join-Path $payload 'ffmpeg\GPL-3.0.txt')
Copy-Item (Join-Path $D3D12 'D3D12Core.dll') (Join-Path $payload 'D3D12')
Copy-Item $Vmx (Join-Path $payload 'libvmx.dll')
Copy-Item (Join-Path $repo 'src/ThirdParty/libvmx/LICENSE.txt') (Join-Path $payload 'libvmx-LICENSE.txt')
& (Join-Path $PSScriptRoot 'notices.ps1') -Folder $payload
Copy-Item (Join-Path $PSScriptRoot 'licence.txt') (Join-Path $payload 'licence.txt')
Copy-Item (Join-Path $Supervisor 'PlayMap.Supervisor.exe') (Join-Path $payload 'Supervisor')
if (Get-ChildItem $payload -Recurse -Filter 'D3D12SDKLayers.dll') { throw 'the Agility SDK''s debug layer is in the payload: releases leave it out' }
# The sums of every file laid down, shipped with them ({app}\SHA256SUMS); any sums the publish carried are replaced.
& (Join-Path $PSScriptRoot 'sums.ps1') -Folder $payload

New-Item -ItemType Directory -Force -Path $Out | Out-Null
$commit7 = $Commit.Substring(0, 7).ToLowerInvariant()
& $Iscc /Q "/DSemVer=$($props.PlayMapVersion)" "/DBuild=$($props._PlayMapBuildNumber)" "/DCommit=$commit7" "/DMsiVersion=$($props.PlayMapMsiVersion)" `
    "/DFileVersion=$($props.FileVersion)" "/DPayload=$payload" "/DOutputDir=$((Resolve-Path $Out).Path)" (Join-Path $PSScriptRoot 'PlayMap.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }
$exe = Join-Path $Out "PlayMap-$($props.FileVersion)-win-x64-unsigned.exe"
if (-not (Test-Path $exe)) { throw "ISCC wrote no $exe" }
Write-Host "the installer: $exe (installer version $($props.PlayMapMsiVersion), payload $payload)"
if ($env:GITHUB_OUTPUT) {
    "installer=$((Resolve-Path $exe).Path)", "version=$($props.PlayMapMsiVersion)", "file=$($props.FileVersion)", "payload=$payload" |
        Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8NoBOM
}
