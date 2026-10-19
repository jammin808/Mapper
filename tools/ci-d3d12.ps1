#!/usr/bin/env pwsh
# CI's Direct3D 12 on WARP (docs/round3/design/d3d12.md §10; the Windows job of .github/workflows/build.yml). Three steps, one each:
#   -Restore -Into d3d12       downloads Microsoft.Direct3D.WARP and Microsoft.Direct3D.D3D12 (the Agility SDK, its debug layer
#                              included) from nuget.org at their pinned versions, refuses a package whose SHA-256 is not the pin's,
#                              and lays out d3d12/d3d10warp.dll and d3d12/D3D12/{D3D12Core,d3d12SDKLayers}.dll; the SDK version is
#                              read from the package's own d3d12.h (D3D12_SDK_VERSION) into PLAYMAP_AGILITY_SDK.
#   -Beside -From d3d12        copies them beside every test project's Release host and beside dotnet.exe (a test runs in
#                              testhost.exe beside its assembly or in dotnet.exe), and sets PLAYMAP_D3D12_REQUIRED=1 so the Direct3D
#                              12 tests assert the pinned WARP made their devices.
#   -Leg [-Filter <f>]         runs every test project's Backend=D3D12 tests (narrowed by a dotnet test filter when given) with
#                              their own results files (<project>.d3d12.trx), lists each result apart in the job's summary with the
#                              leg's minutes, and fails when one failed or none ran.
# WARP's binaries are CI's only: nothing here reaches a published artifact.
param(
    [switch] $Restore,
    [switch] $Beside,
    [switch] $Leg,
    [string] $Into = 'd3d12',
    [string] $From = 'd3d12',
    [string] $Filter = '',
    [string] $Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest

# The pins: each package's version and the SHA-256 of its .nupkg as nuget.org serves it.
$pins = @(
    [pscustomobject]@{ Id = 'Microsoft.Direct3D.WARP'; Version = '1.0.21'; Sha256 = 'EA44B77EB30EEC14427193E20F40CDB2E4C31ED11AB39CB880538FDF4BAC2681' },
    [pscustomobject]@{ Id = 'Microsoft.Direct3D.D3D12'; Version = '1.619.6'; Sha256 = '08F0489281401AA430FC37322D6C3FC98A8025175AACD714C10D562F4963F1E9' }
)

function Write-Summary([string[]] $lines) {
    $lines | ForEach-Object { Write-Host $_ }
    if ($env:GITHUB_STEP_SUMMARY) { $lines | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8NoBOM }
}

function Set-JobEnv([string] $name, [string] $value) {
    Set-Item -Path "env:$name" -Value $value
    if ($env:GITHUB_ENV) { "$name=$value" | Out-File -FilePath $env:GITHUB_ENV -Append -Encoding utf8NoBOM }
}

if ($Restore) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $work = Join-Path ([System.IO.Path]::GetTempPath()) "playmap-d3d12-$PID"
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    $lines = @('### Direct3D 12 on WARP: the pinned packages', '')
    foreach ($pin in $pins) {
        $lower = $pin.Id.ToLowerInvariant()
        $nupkg = Join-Path $work "$lower.$($pin.Version).nupkg"
        Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/$lower/$($pin.Version)/$lower.$($pin.Version).nupkg" -OutFile $nupkg
        $hash = (Get-FileHash $nupkg -Algorithm SHA256).Hash
        if ($hash -ne $pin.Sha256) { throw "$($pin.Id) $($pin.Version) is not the pinned package ($hash)" }
        [System.IO.Compression.ZipFile]::ExtractToDirectory($nupkg, (Join-Path $work $lower))
        $lines += "- ``$($pin.Id)`` $($pin.Version), SHA-256 ``$hash``"
    }
    $warp = Join-Path $work 'microsoft.direct3d.warp/build/native/bin/x64'
    $agility = Join-Path $work 'microsoft.direct3d.d3d12/build/native/bin/x64'
    $runtime = Join-Path $Into 'D3D12'
    New-Item -ItemType Directory -Force -Path $runtime | Out-Null
    Copy-Item (Join-Path $warp 'd3d10warp.dll') $Into
    foreach ($dll in 'D3D12Core.dll', 'd3d12SDKLayers.dll') { Copy-Item (Join-Path $agility $dll) $runtime }
    $header = Join-Path $work 'microsoft.direct3d.d3d12/build/native/include/d3d12.h'
    $line = Select-String -Path $header -Pattern '^#define\s+D3D12_SDK_VERSION\s+\(\s*(\d+)\s*\)' | Select-Object -First 1
    if (-not $line) { throw "D3D12_SDK_VERSION is not defined in $header" }
    $sdk = $line.Matches[0].Groups[1].Value
    Set-JobEnv 'PLAYMAP_AGILITY_SDK' $sdk
    $lines += "- the Agility SDK's version (``D3D12_SDK_VERSION`` in its d3d12.h): $sdk", ''
    Write-Summary $lines
    Get-ChildItem -Recurse $Into | ForEach-Object { Write-Host $_.FullName }
}

if ($Beside) {
    $files = @('d3d10warp.dll', 'D3D12/D3D12Core.dll', 'D3D12/d3d12SDKLayers.dll') | ForEach-Object {
        $path = Join-Path $From $_
        if (-not (Test-Path $path -PathType Leaf)) { throw "$path is missing: run -Restore first" }
        $_
    }
    $projects = @(Get-ChildItem tests/*/*.csproj)
    if ($projects.Count -eq 0) { throw 'no test project matches tests/*/*.csproj' }
    $hosts = @($projects | ForEach-Object {
        $dir = Join-Path $_.DirectoryName "bin/$Configuration/net10.0"
        if (-not (Test-Path $dir -PathType Container)) { throw "$($_.BaseName) has no $Configuration output at $dir" }
        $dir
    })
    $hosts += Split-Path (Get-Command dotnet).Source
    foreach ($dir in $hosts) {
        New-Item -ItemType Directory -Force -Path (Join-Path $dir 'D3D12') | Out-Null
        foreach ($file in $files) { Copy-Item (Join-Path $From $file) (Join-Path $dir $file) }
    }
    Set-JobEnv 'PLAYMAP_D3D12_REQUIRED' '1'
    Write-Host "WARP and the Agility runtime are beside $($hosts.Count) hosts"
}

if ($Leg) {
    # A filter is a dotnet test expression of names, operators and parentheses only: anything else is refused, never run.
    if ($Filter -and $Filter -notmatch '^[A-Za-z0-9_.,~=!&|() -]{1,200}$') { throw "the Direct3D 12 filter '$Filter' holds a character a test filter never needs" }
    # Its parentheses balance, so it stays one term inside Backend=D3D12&(...): the allowed set holds no backslash, so no parenthesis
    # is escaped and a count is exact.
    if ($Filter) {
        $depth = 0
        foreach ($c in $Filter.ToCharArray()) {
            if ($c -eq '(') { $depth++ }
            elseif ($c -eq ')') { $depth--; if ($depth -lt 0) { break } }
        }
        if ($depth -ne 0) { throw "the Direct3D 12 filter '$Filter' has unbalanced parentheses: it would not stay within Backend=D3D12" }
    }
    $expression = if ($Filter) { "Backend=D3D12&($Filter)" } else { 'Backend=D3D12' }
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $failed = @()
    $projects = @(Get-ChildItem tests/*/*.csproj)
    if ($projects.Count -eq 0) { throw 'no test project matches tests/*/*.csproj' }
    foreach ($project in $projects) {
        $name = $project.BaseName
        Write-Host "::group::$name (Direct3D 12)"
        dotnet test $project.FullName -c $Configuration --no-build --filter $expression --logger "trx;LogFileName=$name.d3d12.trx" `
            --blame-hang --blame-hang-timeout 5m --blame-hang-dump-type none
        if ($LASTEXITCODE -ne 0) { $failed += $name }
        Write-Host '::endgroup::'
    }
    $clock.Stop()
    $minutes = $clock.Elapsed.TotalMinutes.ToString('0.0', [System.Globalization.CultureInfo]::InvariantCulture)
    $invariant = [System.Globalization.CultureInfo]::InvariantCulture
    $uri = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'
    $rows = @()
    $counts = @{ Passed = 0; Failed = 0; Other = 0 }
    foreach ($trx in Get-ChildItem tests/*/TestResults/*.d3d12.trx) {
        foreach ($hit in Select-Xml -Path $trx.FullName -XPath '//t:UnitTestResult' -Namespace @{ t = $uri }) {
            # Attributes are read by name, so a result without one (a test not run has no duration) reads as empty.
            $result = $hit.Node
            $test = $result.GetAttribute('testName')
            $outcome = $result.GetAttribute('outcome')
            if ($outcome -eq 'Passed' -or $outcome -eq 'Failed') { $counts[$outcome]++ } else { $counts.Other++ }
            $duration = [TimeSpan]::Zero
            $seconds = if ([TimeSpan]::TryParse($result.GetAttribute('duration'), $invariant, [ref] $duration)) { $duration.TotalSeconds.ToString('0.00', $invariant) } else { '' }
            $rows += "| ``$($test.Replace('|', '\|'))`` | $outcome | $seconds |"
            if ($outcome -eq 'Failed') {
                $names = [System.Xml.XmlNamespaceManager]::new($result.OwnerDocument.NameTable)
                $names.AddNamespace('t', $uri)
                $node = $result.SelectSingleNode('t:Output/t:ErrorInfo/t:Message', $names)
                $message = if ($node) { ($node.InnerText -split "`n" | Select-Object -First 4) -join ' | ' } else { '' }
                Write-Host "FAILED $($trx.BaseName): $test :: $message"
            }
        }
    }
    $lines = @("### The Direct3D 12 leg (``$expression``)", '',
        "$($counts.Passed + $counts.Failed + $counts.Other) results: $($counts.Passed) passed, $($counts.Failed) failed, $($counts.Other) not run, in $minutes minutes.", '')
    if ($rows.Count -gt 0) { $lines += '| Test | Outcome | Seconds |', '|---|---|---|'; $lines += $rows; $lines += '' }
    Write-Summary $lines
    if ($failed.Count -gt 0) { Write-Host "::error::the Direct3D 12 leg failed in: $($failed -join ' ')"; exit 1 }
    if ($counts.Passed + $counts.Failed -eq 0) { Write-Host "::error::no Direct3D 12 test ran for '$expression'"; exit 1 }
}

if (-not ($Restore -or $Beside -or $Leg)) { throw 'say -Restore, -Beside or -Leg' }
exit 0
