#!/usr/bin/env pwsh
# Lays down, beside a publish of PlayMap, the notices its third-party files ask for (THIRD-PARTY-NOTICES.md's wp3-installer heading
# says what each covers): THIRD-PARTY-NOTICES.md itself; OpenAL Soft's licence beside soft_oal.dll; the Apache License 2.0 (the MCP
# SDK's) and Inter's SIL Open Font License under licenses\; and, under licenses\<package>\, the licence and notice files of every
# package the publish's PlayMap.deps.json names with files in the folder (the .NET and ASP.NET Core runtime packs among them), and of
# every package with files that the restore's project.assets.json of another program the folder will carry lists (-Assets: the
# supervisor's, a single-file exe whose deps.json is inside it), copied from the restore as the package holds them. A package the
# publish or such a program carries that THIRD-PARTY-NOTICES.md does not name in backticks
# (its id, or a family as `Prefix.*`), or one the restore does not hold, stops it with words before anything is laid down; so does a
# native library in the folder that no library of PlayMap.deps.json lists and THIRD-PARTY-NOTICES.md does not name by its file name
# in backticks (the shader worker's from the engine's build, DXC's compiler and SPIRV-Tools'; in the installer's payload FFmpeg's,
# the Agility SDK's runtime and libvmx too), and a dxcompiler.dll without DXC's licence and notices in licenses\DXC\ beside it (the
# engine's build lays them down; this script leaves them in place). The portable folder's step and build-installer.ps1 (for the
# installer's payload) run it before the sums; -Summary adds its line to the job's summary.
#   installer/notices.ps1 -Folder publish/PlayMap [-Assets src/PlayMap.Supervisor/obj/project.assets.json] [-Summary]
param(
    [Parameter(Mandatory)] [string] $Folder,
    [string[]] $Assets = @(),
    [switch] $Summary
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path $PSScriptRoot -Parent
$root = (Resolve-Path $Folder).Path
$notices = Join-Path $repo 'THIRD-PARTY-NOTICES.md'
$named = [System.IO.File]::ReadAllText($notices)
$deps = Join-Path $root 'PlayMap.deps.json'
if (-not (Test-Path -LiteralPath $deps)) { throw "$Folder holds no PlayMap.deps.json: -Folder is the app's publish" }
if (-not (Test-Path -LiteralPath (Join-Path $root 'soft_oal.dll'))) { throw "$Folder carries no soft_oal.dll: OpenAL Soft's licence has nothing to sit beside" }

# Where the restore put the packages, and the SDK's own packs (a runtime pack the SDK carries is read from there).
$said = (& dotnet nuget locals global-packages --list) -join "`n"
if ($LASTEXITCODE -ne 0 -or $said -notmatch 'global-packages:\s*(?<path>\S.*)') { throw "the restore's package folder could not be read: $said" }
$packages = $Matches['path'].Trim()
$packs = Join-Path (Split-Path (Get-Command dotnet).Source) 'packs'

# THIRD-PARTY-NOTICES.md names a package by its id in backticks, or by a family of ids in backticks as `Prefix.*`.
function Test-Named([string] $id) {
    if ($named.Contains('`' + $id + '`')) { return $true }
    $parts = $id.Split('.')
    for ($n = $parts.Length; $n -ge 1; $n--) {
        if ($named.Contains('`' + ($parts[0..($n - 1)] -join '.') + '.*`')) { return $true }
    }
    return $false
}

$json = Get-Content -LiteralPath $deps -Raw | ConvertFrom-Json
$target = $json.targets.PSObject.Properties[$json.runtimeTarget.name].Value
$carried = [System.Collections.Generic.List[object]]::new()
$unnamed = [System.Collections.Generic.List[string]]::new()
foreach ($library in $json.libraries.PSObject.Properties) {
    if ($library.Value.type -ne 'package' -and $library.Value.type -ne 'runtimepack') { continue }
    $entry = $target.PSObject.Properties[$library.Name]
    if ($null -eq $entry) { continue }
    # Only a package with files in the folder: its managed assemblies, its native libraries or its satellite assemblies.
    $files = @($entry.Value.PSObject.Properties | Where-Object { $_.Name -in 'runtime', 'native', 'resources' -and @($_.Value.PSObject.Properties).Count -gt 0 })
    if ($files.Count -eq 0) { continue }
    $name, $version = $library.Name.Split('/')
    $id = $name -replace '^runtimepack\.', ''
    if (-not (Test-Named $id)) { $unnamed.Add("$id $version"); continue }
    $dir = @((Join-Path $packages "$($id.ToLowerInvariant())/$version"), (Join-Path $packs "$id/$version")) |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $dir) { throw "the restore holds no $id $version (looked in $packages and $packs): its licence and notices cannot be laid down" }
    $carried.Add([pscustomobject]@{ Id = $id; Dir = $dir })
}
# The packages of each other program the folder carries, from its restore's project.assets.json: each with runtime or native files
# (a placeholder _._ is none) is held to the same rule as the publish's.
foreach ($file in $Assets) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "$file is not there: the restore of the program it lists writes it" }
    $restore = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
    foreach ($target in $restore.targets.PSObject.Properties) {
        foreach ($entry in $target.Value.PSObject.Properties) {
            if ($entry.Value.type -ne 'package') { continue }
            $files = @($entry.Value.PSObject.Properties | Where-Object { $_.Name -in 'runtime', 'native', 'runtimeTargets' } |
                ForEach-Object { @($_.Value.PSObject.Properties | ForEach-Object { $_.Name }) } | Where-Object { -not $_.EndsWith('/_._') })
            if ($files.Count -eq 0) { continue }
            $id, $version = $entry.Name.Split('/')
            if (@($carried | Where-Object { $_.Id -eq $id }).Count -gt 0 -or $unnamed.Contains("$id $version")) { continue }
            if (-not (Test-Named $id)) { $unnamed.Add("$id $version"); continue }
            $dir = Join-Path $packages "$($id.ToLowerInvariant())/$version"
            if (-not (Test-Path -LiteralPath $dir)) { throw "the restore holds no $id $version (looked in $packages): its licence and notices cannot be laid down" }
            $carried.Add([pscustomobject]@{ Id = $id; Dir = $dir })
        }
    }
}
if ($unnamed.Count -gt 0) {
    throw "the publish carries $($unnamed -join ', '), which THIRD-PARTY-NOTICES.md does not name: name each under its package's heading with its licence and copyright"
}
# The native libraries no library of PlayMap.deps.json lists (its packages', its runtime packs' and PlayMap's own files, by name):
# each is named in THIRD-PARTY-NOTICES.md by its file name in backticks, and DXC's compiler has its licence and notices beside it.
$listed = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $target.PSObject.Properties) {
    foreach ($kind in @($entry.Value.PSObject.Properties | Where-Object { $_.Name -in 'runtime', 'native', 'resources' })) {
        foreach ($asset in $kind.Value.PSObject.Properties) { [void]$listed.Add([System.IO.Path]::GetFileName($asset.Name)) }
    }
}
$natives = @(Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object { $_.Extension -eq '.dll' -and -not $listed.Contains($_.Name) } |
    ForEach-Object { [System.IO.Path]::GetRelativePath($root, $_.FullName).Replace('\', '/') } |
    Sort-Object -Culture ([System.Globalization.CultureInfo]::InvariantCulture))
$bareNatives = @($natives | Where-Object { -not $named.Contains('`' + [System.IO.Path]::GetFileName($_) + '`') })
if ($bareNatives.Count -gt 0) {
    throw "the folder carries $($bareNatives -join ', '), which no package of PlayMap.deps.json carries and THIRD-PARTY-NOTICES.md does not name: name each file in backticks under the heading of what ships it, with its licence and copyright"
}
$dxcNotices = 'LICENSE-LLVM.txt', 'LICENSE.TXT', 'ThirdPartyNotices.txt'
$compilers = @(Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object { $_.Name -eq 'dxcompiler.dll' })
foreach ($dxc in $compilers) {
    $beside = Join-Path $dxc.DirectoryName 'licenses/DXC'
    $lacking = @($dxcNotices | Where-Object { -not (Test-Path -LiteralPath (Join-Path $beside $_) -PathType Leaf) })
    if ($lacking.Count -gt 0) {
        throw "$([System.IO.Path]::GetRelativePath($root, $dxc.FullName)) has no $($lacking -join ', ') in licenses/DXC/ beside it: the engine's build lays DXC's licence and notices down with its compiler"
    }
}

Copy-Item -LiteralPath $notices (Join-Path $root 'THIRD-PARTY-NOTICES.md')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses/LGPL-2.0.txt') (Join-Path $root 'soft_oal-LICENSE.txt')
$licences = Join-Path $root 'licenses'
New-Item -ItemType Directory -Force -Path $licences | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses/Apache-2.0.txt') (Join-Path $licences 'Apache-2.0.txt')
Copy-Item -LiteralPath (Join-Path $repo 'src/PlayMap.Render/Assets/Inter-LICENSE-OFL.txt') (Join-Path $licences 'Inter-OFL.txt')
$laid = [System.Collections.Generic.List[string]]::new()
$bare = [System.Collections.Generic.List[string]]::new()
foreach ($package in ($carried | Sort-Object Id -Culture ([System.Globalization.CultureInfo]::InvariantCulture))) {
    $own = @(Get-ChildItem -LiteralPath $package.Dir -File | Where-Object { $_.Name -match '^(LICEN[CS]E|THIRD-PARTY-NOTICES|ThirdPartyNotices|NOTICE)(\.(txt|md))?$' })
    if ($own.Count -eq 0) { $bare.Add($package.Id); continue }
    $into = Join-Path $licences $package.Id
    New-Item -ItemType Directory -Force -Path $into | Out-Null
    foreach ($file in $own) {
        Copy-Item -LiteralPath $file.FullName (Join-Path $into $file.Name)
        $laid.Add("$($package.Id)/$($file.Name)")
    }
}
$line = "- the notices beside the files: THIRD-PARTY-NOTICES.md, soft_oal-LICENSE.txt, licenses/Apache-2.0.txt, licenses/Inter-OFL.txt and " +
    "$($laid.Count) licence and notice files of $($carried.Count - $bare.Count) packages ($($laid -join ', ')); " +
    "THIRD-PARTY-NOTICES.md alone carries the licences of $($bare.Count) packages that hold no such file ($($bare -join ', ')); " +
    "it names the $($natives.Count) native libraries no package carries ($($natives -join ', ')); DXC's licence and notices lie in licenses/DXC/ beside each dxcompiler.dll ($($compilers.Count))"
Write-Host $line
if ($Summary -and $env:GITHUB_STEP_SUMMARY) { $line | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8NoBOM }
