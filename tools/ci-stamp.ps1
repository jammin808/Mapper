#!/usr/bin/env pwsh
# CI's check of a numbered build (Version.props; README, "Versions"). Reads the stamp back from a built file's version
# information (its Windows version resource on Windows, its assembly attributes elsewhere) and fails unless it is this run's:
# build number GITHUB_RUN_NUMBER less PlayMapRunBase as MSBuild reads it, and commit GITHUB_SHA. Writes what it read to the
# step's summary (with -Heading, under a heading that names the build) and to the step's outputs: file (the four-part file
# version), build, and artifact when -Artifact gives a name with {0} for the file version.
#   tools/ci-stamp.ps1 -File src/PlayMap.Core/bin/Release/net10.0/PlayMap.Core.dll -Label Linux -Heading
#   tools/ci-stamp.ps1 -File publish/PlayMap/PlayMap.exe -Label 'The published exe' -Artifact 'PlayMap-{0}-win-x64'
param(
    [Parameter(Mandatory)] [string] $File,
    [Parameter(Mandatory)] [string] $Label,
    [switch] $Heading,
    [string] $Artifact = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($env:GITHUB_RUN_NUMBER -notmatch '^[1-9][0-9]{0,8}$') { throw "GITHUB_RUN_NUMBER '$env:GITHUB_RUN_NUMBER' is not a run number" }
if ($env:GITHUB_SHA -notmatch '^[0-9a-fA-F]{7,64}$') { throw "GITHUB_SHA '$env:GITHUB_SHA' is not a commit" }
$run = [long]$env:GITHUB_RUN_NUMBER
$short = $env:GITHUB_SHA.Substring(0, 7).ToLowerInvariant()

# The base as the build read it: MSBuild's evaluation of a project under the root's Directory.Build.props, whose answer is the
# value alone on the last line.
$env:DOTNET_NOLOGO = '1'
$project = Join-Path (Split-Path $PSScriptRoot -Parent) 'src/PlayMap.Core/PlayMap.Core.csproj'
$answer = @(& dotnet msbuild $project -nologo '-getProperty:PlayMapRunBase')
$said = "$($answer | Where-Object { "$_".Trim() } | Select-Object -Last 1)".Trim()
if ($LASTEXITCODE -ne 0 -or $said -notmatch '^(0|[1-9][0-9]{0,8})$') { throw "PlayMapRunBase could not be read from Version.props: $($answer -join ' ')" }
$base = [long]$said
$build = $run - $base

$path = (Resolve-Path $File).Path
$name = Split-Path $path -Leaf
$info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($path)
if ("$($info.FileVersion)" -notmatch '^(\d+\.\d+\.\d+)\.(\d+)$') { throw "$name's file version '$($info.FileVersion)' is not four parts" }
$version = $Matches[1]
if ([long]$Matches[2] -ne $build) { throw "$name's file version $($info.FileVersion) is not build $build (run $run less PlayMapRunBase $base)" }
$product = "$version+$build.$short"
if ("$($info.ProductVersion)" -cne $product) { throw "$name's product version '$($info.ProductVersion)' is not $product" }

$words = "${Label}: ``$name`` carries file version ``$($info.FileVersion)`` and product version ``$($info.ProductVersion)`` (run $run less PlayMapRunBase $base)"
$outputs = @("file=$($info.FileVersion)", "build=$build")
if ($Artifact) {
    $artifactName = [string]::Format([System.Globalization.CultureInfo]::InvariantCulture, $Artifact, $info.FileVersion)
    $words += "; the artifact is ``$artifactName``"
    $outputs += "artifact=$artifactName"
}
$summary = @()
if ($Heading) { $summary += "### PlayMap $version (build $build, $short)", '' }
$summary += "$words.", ''
Write-Host "$words."
if ($env:GITHUB_STEP_SUMMARY) { $summary | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8NoBOM }
if ($env:GITHUB_OUTPUT) { $outputs | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8NoBOM }
