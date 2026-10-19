#!/usr/bin/env pwsh
# M17's probe (docs/round3/PLAN.md §1.5): with the WiX toolset the wix-toolset job built from its source in -Feed, builds the probe
# package (a service, a major upgrade and a firewall rule) with no licence gesture of any kind, and answers in the job's summary:
# where every WixToolset.* package was restored from (the feed only, its hash the feed's), and whether the self-built tool asks for
# the gesture (WIX7015). When it does not ask, the package is installed, upgraded, refused as a downgrade and uninstalled. The probe
# fails only when an answer cannot be given: a package restored from anywhere but the feed, or a build that fails for another reason.
param(
    [Parameter(Mandatory)] [string] $Feed,
    [Parameter(Mandatory)] [string] $Exe
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$probe = Join-Path $PSScriptRoot 'probe'
$sdk = Get-ChildItem $Feed -Filter 'WixToolset.Sdk.*.nupkg' | Select-Object -First 1
if (-not $sdk) { throw "the feed $Feed holds no WixToolset.Sdk package" }
$version = $sdk.Name.Substring('WixToolset.Sdk.'.Length) -replace '\.nupkg$', ''
@{ 'msbuild-sdks' = @{ 'WixToolset.Sdk' = $version } } | ConvertTo-Json | Set-Content (Join-Path $probe 'global.json') -Encoding utf8NoBOM

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('#### M17: the WiX toolset from its source'); $lines.Add('')
$lines.Add("- the feed's packages (version ``$version``):")
foreach ($p in Get-ChildItem $Feed -Filter '*.nupkg') {
    $lines.Add("  - ``$($p.Name)`` sha256 ``$((Get-FileHash $p.FullName -Algorithm SHA256).Hash.ToLowerInvariant())``")
}

function Build-Probe([string] $ProbeVersion, [string] $Out) {
    $log = Join-Path $env:RUNNER_TEMP "probe-$ProbeVersion.log"
    & dotnet build (Join-Path $probe 'Probe.wixproj') -c Release "-p:ProbeVersion=$ProbeVersion" "-p:WixVersion=$version" "-p:ProbeExe=$Exe" `
        -o $Out -nologo 2>&1 | Tee-Object -FilePath $log | Out-Host
    return @{ Exit = $LASTEXITCODE; Log = (Get-Content $log -Raw) }
}

$first = Build-Probe '1.0.1' (Join-Path $env:RUNNER_TEMP 'probe-1')

# Where each package came from: NuGet's metadata beside each restored package names its source.
$cache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME '.nuget/packages' }
$feedFull = (Resolve-Path $Feed).Path.TrimEnd('\', '/')
foreach ($id in 'wixtoolset.sdk', 'wixtoolset.firewall.wixext', 'wixtoolset.util.wixext') {
    $meta = Join-Path $cache "$id/$($version.ToLowerInvariant())/.nupkg.metadata"
    if (-not (Test-Path $meta)) { throw "$id $version was not restored ($meta)" }
    $source = ((Get-Content $meta -Raw | ConvertFrom-Json).source).TrimEnd('\', '/')
    if ($source -ne $feedFull) { throw "$id was restored from $source, not the feed $feedFull" }
    $restored = Get-ChildItem (Split-Path $meta) -Filter '*.nupkg' | Select-Object -First 1
    $built = Get-ChildItem $Feed -Filter "$id.$version.nupkg" | Select-Object -First 1
    if ((Get-FileHash $restored.FullName).Hash -ne (Get-FileHash $built.FullName).Hash) { throw "$id's restored package is not the feed's" }
}
$lines.Add("- restored from the feed only: WixToolset.Sdk, WixToolset.Firewall.wixext and WixToolset.Util.wixext, each the feed's bytes")

if ($first.Exit -ne 0 -and $first.Log -match 'WIX7015') {
    $lines.Add('- **the self-built tool asks for the licence gesture**: the probe built with none fails with WIX7015. PlayMap never passes the gesture; without a patch to the pinned source that answers the check, Inno Setup builds the installer (PLAN.md §9 decision 3).')
}
elseif ($first.Exit -ne 0) {
    throw "the probe failed for a reason other than the licence check ($($first.Exit)); see its log above"
}
else {
    $lines.Add('- the self-built tool does not ask for the licence gesture: the probe built with none')
    $msi1 = Get-ChildItem (Join-Path $env:RUNNER_TEMP 'probe-1') -Filter '*.msi' | Select-Object -First 1
    $second = Build-Probe '1.0.2' (Join-Path $env:RUNNER_TEMP 'probe-2')
    if ($second.Exit -ne 0) { throw 'the probe 1.0.2 did not build' }
    $msi2 = Get-ChildItem (Join-Path $env:RUNNER_TEMP 'probe-2') -Filter '*.msi' | Select-Object -First 1
    foreach ($step in @(@($msi1, 0, 'installed 1.0.1'), @($msi2, 0, 'upgraded to 1.0.2'), @($msi1, 1638, 'refused the downgrade to 1.0.1'))) {
        $p = Start-Process msiexec -ArgumentList '/i', "`"$($step[0].FullName)`"", '/qn', '/norestart' -Wait -PassThru
        if ($p.ExitCode -ne $step[1] -and -not ($step[1] -eq 1638 -and $p.ExitCode -ne 0)) { throw "msiexec $($step[2]) answered $($p.ExitCode)" }
        $lines.Add("- $($step[2]) (msiexec $($p.ExitCode))")
    }
    $p = Start-Process msiexec -ArgumentList '/x', "`"$($msi2.FullName)`"", '/qn', '/norestart' -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "msiexec uninstall answered $($p.ExitCode)" }
    $lines.Add('- uninstalled')
}
$lines.Add('')
$lines | Write-Host
if ($env:GITHUB_STEP_SUMMARY) { $lines | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8NoBOM }
