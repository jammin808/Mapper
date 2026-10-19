#!/usr/bin/env pwsh
# Builds PlayMap's installer from a numbered build (installer/PlayMap.iss; docs/round3/design/installer.md): the published exe's
# folder, FFmpeg's LGPL shared libraries under ffmpeg\, the Agility SDK's D3D12Core.dll under D3D12\ (its debug layer left out),
# libvmx beside the exe and the supervisor under Supervisor\, compiled by Inno Setup's ISCC with the version Version.props gives this
# run. A local build is refused with words: its installer would read as MAJOR.MINOR.0 and could not be upgraded in order.
# -Version builds the same files under another product version (the build before this one, when this one is build 1 of a raised
# MAJOR or MINOR: MsiVersionRule.Previous).
#   installer/build-installer.ps1 -Run 412 -Commit <sha> -Publish publish/PlayMap -Supervisor publish/supervisor -Ffmpeg .ffmpeg/lib
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

$payload = Join-Path ([System.IO.Path]::GetTempPath()) ("playmap-payload-" + $props._PlayMapBuildNumber)
if (Test-Path $payload) { Remove-Item $payload -Recurse -Force }
New-Item -ItemType Directory -Force -Path $payload, (Join-Path $payload 'ffmpeg'), (Join-Path $payload 'D3D12'), (Join-Path $payload 'Supervisor') | Out-Null
Copy-Item (Join-Path $Publish '*') $payload -Recurse
Get-ChildItem $payload -Filter SHA256SUMS -File | Remove-Item
Copy-Item (Join-Path $Ffmpeg '*.dll') (Join-Path $payload 'ffmpeg')
Copy-Item (Join-Path $D3D12 'D3D12Core.dll') (Join-Path $payload 'D3D12')
Copy-Item $Vmx (Join-Path $payload 'libvmx.dll')
Copy-Item (Join-Path $Supervisor 'PlayMap.Supervisor.exe') (Join-Path $payload 'Supervisor')
if (Get-ChildItem $payload -Recurse -Filter 'D3D12SDKLayers.dll') { throw 'the Agility SDK''s debug layer is in the payload: releases leave it out' }

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
