#!/usr/bin/env pwsh
# The Windows job's test runs (.github/workflows/build.yml), each dotnet test under a deadline the step outlives. Two uses, one each:
#   -Suites       every test project (tests/*/*.csproj) but its Backend=D3D12 tests, one after another, each under its deadline;
#                 a project that ends inside it is judged as before (its exit code, its results file), the failed tests listed from
#                 the results at the end; one that runs out is listed '<name>(deadline)' after its process tree is printed and stopped.
#   -HangProof    the hang guard's proof, run only when the workflow is dispatched with the hangproof input: tools/hang-probe's one test,
#                 which never ends, under the blame collector (a one-minute hang timeout, a mini dump) twice, once with
#                 VSTEST_DUMP_FORCENETDUMP=1 as the windows job sets it and once without, each under a deadline of its own; for each leg,
#                 whether blame ended the host and when, the hang dumps written and their sizes, and whether the pinned dotnet-dump
#                 reads the test's managed stack from them.
# Dot-sourced with neither (tools/ci-d3d12.ps1's leg), it only defines Invoke-DotnetTest and the deadlines.
# A process tree is printed by process id, parent id, name and start time, never by command line: the suites start node with a phone
# page's join ticket among its arguments (tests/PlayMap.Net.Tests/Phone/PageTests.cs, tests/PlayMap.Engine.Tests/Phone/PhoneScanE2ETests.cs).
# The suites run the Release build the job made. Dot-sourced, this script sets no variable its caller names but $Suites and
# $HangProof (its own switches), $TestConfiguration and the two deadline variables.
param(
    [switch] $Suites,
    [switch] $HangProof
)
$ErrorActionPreference = 'Stop'
# A native command's exit code is read on its own line, never thrown, whatever the runner's PowerShell defaults to.
$PSNativeCommandUseErrorActionPreference = $false
$TestConfiguration = 'Release'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest

# Each project's deadline: three times its longest normal run on windows-2025 (runs 478 and 479's second attempt: Engine 10 min 10 s
# and 8 min 30 s; App 1 min 46 s at most; Core, Render and Net under 1 min 35 s; the rest under 30 s), rounded up to five minutes,
# and never under the floor: the blame collector's 5-minute hang timeout, then 5 minutes for its dump and the host's end, so blame
# always has its turn first. A project named nowhere here has the floor.
$TestDeadlineFloor = [TimeSpan]::FromMinutes(10)
$TestDeadlines = @{
    'PlayMap.Engine.Tests' = [TimeSpan]::FromMinutes(30)
}

function Get-TestDeadline([string] $Name) {
    if ($TestDeadlines.ContainsKey($Name)) { return $TestDeadlines[$Name] }
    return $TestDeadlineFloor
}

function Get-UtcStamp() { [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffZ', [Globalization.CultureInfo]::InvariantCulture) }

# The processes under $Root (itself included), each started no earlier than its parent so that a reused process id is never taken
# for a child: rows of a Win32_Process (Row) with their depth (Depth), depth first.
function Get-ProcessTree([int] $Root) {
    $all = @(Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId, Name, CreationDate)
    $byParent = @{}
    foreach ($p in $all) {
        $key = [int]$p.ParentProcessId
        if (-not $byParent.ContainsKey($key)) { $byParent[$key] = [Collections.Generic.List[object]]::new() }
        $byParent[$key].Add($p)
    }
    $rootRow = $all | Where-Object { [int]$_.ProcessId -eq $Root } | Select-Object -First 1
    $tree = [Collections.Generic.List[object]]::new()
    if (-not $rootRow) { return $tree }
    $stack = [Collections.Generic.Stack[object]]::new()
    $stack.Push([pscustomobject]@{ Row = $rootRow; Depth = 0 })
    while ($stack.Count -gt 0) {
        $at = $stack.Pop()
        $tree.Add($at)
        $id = [int]$at.Row.ProcessId
        if ($byParent.ContainsKey($id)) {
            foreach ($child in $byParent[$id]) {
                if ([int]$child.ProcessId -ne $id -and $child.CreationDate -ge $at.Row.CreationDate) {
                    $stack.Push([pscustomobject]@{ Row = $child; Depth = $at.Depth + 1 })
                }
            }
        }
    }
    return $tree
}

function Write-ProcessTree([string] $Name, [int] $Root) {
    try {
        $tree = @(Get-ProcessTree $Root)
        if ($tree.Count -eq 0) { Write-Host "  (process $Root is gone: no tree to print)"; return @() }
        foreach ($at in $tree) {
            $row = $at.Row
            $start = if ($row.CreationDate) { $row.CreationDate.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ', [Globalization.CultureInfo]::InvariantCulture) } else { '?' }
            Write-Host ("  {0}pid {1} parent {2} {3} started {4}" -f ('  ' * $at.Depth), $row.ProcessId, $row.ParentProcessId, $row.Name, $start)
        }
        return @($tree | ForEach-Object { [pscustomobject]@{ Id = [int]$_.Row.ProcessId; Start = $_.Row.CreationDate } })
    }
    catch {
        Write-Host "::warning::$Name's process tree could not be listed: $($_.Exception.Message)"
        return @()
    }
}

# Runs `dotnet test <Arguments>` as a process the caller outlives. It ends inside $Deadline: Ended, its ExitCode. It runs out: its
# process tree is printed and stopped, Ended is false. Its start and end are printed either way.
function Invoke-DotnetTest {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [string[]] $Arguments,
        [Parameter(Mandatory)] [TimeSpan] $Deadline
    )
    $dotnet = (Get-Command dotnet).Source
    $info = [Diagnostics.ProcessStartInfo]::new($dotnet)
    $info.UseShellExecute = $false
    $info.ArgumentList.Add('test')
    foreach ($a in $Arguments) { $info.ArgumentList.Add($a) }
    $minutes = $Deadline.TotalMinutes.ToString('0', [Globalization.CultureInfo]::InvariantCulture)
    Write-Host "$Name started at $(Get-UtcStamp) (deadline $minutes min)"
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($info)
    try {
        $ended = $process.WaitForExit([int]$Deadline.TotalMilliseconds)
        if ($ended) {
            $process.WaitForExit()
            $code = $process.ExitCode
            $seconds = $clock.Elapsed.TotalSeconds.ToString('0.0', [Globalization.CultureInfo]::InvariantCulture)
            Write-Host "$Name ended at $(Get-UtcStamp) after $seconds s (exit $code)"
            return [pscustomobject]@{ Ended = $true; ExitCode = $code; Seconds = $clock.Elapsed.TotalSeconds }
        }
        Write-Host "::error::$Name ran past its deadline of $minutes min (at $(Get-UtcStamp)); the process tree under its dotnet test (pid $($process.Id)), then stopped:"
        $listed = @(Write-ProcessTree $Name $process.Id)
        try { $process.Kill($true) } catch { Write-Host "::warning::$Name's tree could not be stopped whole: $($_.Exception.Message)" }
        if (-not $process.WaitForExit(30000)) { Write-Host "::warning::$Name's dotnet test (pid $($process.Id)) still runs 30 s after it was stopped" }
        # A process of the printed tree still running (the same id and the same start, never a reused id) is stopped on its own.
        foreach ($item in $listed) {
            $left = Get-Process -Id $item.Id -ErrorAction SilentlyContinue
            $same = $false
            if ($left -and $item.Start) {
                try { $same = [math]::Abs(($left.StartTime.ToUniversalTime() - $item.Start.ToUniversalTime()).TotalSeconds) -lt 1 } catch { $same = $false }
            }
            if ($same) {
                Write-Host "  still running after the stop: pid $($item.Id) $($left.ProcessName); stopping it alone"
                Stop-Process -Id $item.Id -Force -ErrorAction SilentlyContinue
            }
        }
        Write-Host "$Name stopped at $(Get-UtcStamp) after its deadline"
        return [pscustomobject]@{ Ended = $false; ExitCode = $null; Seconds = $clock.Elapsed.TotalSeconds }
    }
    finally {
        $process.Dispose()
    }
}

if ($Suites -and $HangProof) { throw 'say -Suites or -HangProof, not both' }

if ($Suites) {
    # No test project fails the step, and so does a suite that answers 0 but leaves no results file, or one that runs past its
    # deadline. The Backend=D3D12 tests run in the Direct3D 12 leg, so each result is counted once.
    $failed = @()
    $projects = @(Get-ChildItem tests/*/*.csproj)
    if ($projects.Count -eq 0) { Write-Host "::error::no test project matches tests/*/*.csproj"; exit 1 }
    foreach ($project in $projects) {
        $name = $project.BaseName
        Write-Host "::group::$name"
        $run = Invoke-DotnetTest -Name $name -Deadline (Get-TestDeadline $name) -Arguments @(
            $project.FullName, '-c', $TestConfiguration, '--no-build', '--filter', 'Backend!=D3D12', '--logger', "trx;LogFileName=$name.trx",
            '--blame-hang', '--blame-hang-timeout', '5m', '--blame-hang-dump-type', 'mini')
        if (-not $run.Ended) { $failed += "$name(deadline)" }
        elseif ($run.ExitCode -ne 0) { $failed += $name }
        elseif (-not (Test-Path (Join-Path $project.DirectoryName "TestResults/$name.trx"))) { $failed += "$name(no results)" }
        Write-Host "::endgroup::"
    }
    # Every failed test with the first lines of its message, read from the results, so the whole list is at the log's end. Elements
    # and attributes are read by name, so a result without a message reads as empty under strict mode.
    $uri = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'
    foreach ($trx in Get-ChildItem tests/*/TestResults/*.trx -Exclude *.d3d12.trx) {
        foreach ($hit in Select-Xml -Path $trx.FullName -XPath "//t:UnitTestResult[@outcome='Failed']" -Namespace @{ t = $uri }) {
            $result = $hit.Node
            $names = [System.Xml.XmlNamespaceManager]::new($result.OwnerDocument.NameTable)
            $names.AddNamespace('t', $uri)
            $node = $result.SelectSingleNode('t:Output/t:ErrorInfo/t:Message', $names)
            $message = if ($node) { ($node.InnerText -split "`n" | Select-Object -First 4) -join ' | ' } else { '' }
            Write-Host "FAILED $($trx.BaseName): $($result.GetAttribute('testName')) :: $message"
        }
    }
    if ($failed.Count -gt 0) { Write-Host "::error::failed: $($failed -join ' ')"; exit 1 }
    exit 0
}

if ($HangProof) {
    if ($env:D3D12_FILTER) { throw 'the run was dispatched with both hangproof and d3d12: say one' }
    # The probe's project file is kept as PlayMap.HangProbe.csproj.in so no *.csproj search of the tree meets it; it is built from
    # a copy outside the checkout, where the tree's Directory.Build.props does not reach.
    $probeDir = Join-Path $env:RUNNER_TEMP 'hang-probe'
    if (Test-Path $probeDir) { Remove-Item $probeDir -Recurse -Force }
    New-Item -ItemType Directory -Path $probeDir | Out-Null
    Copy-Item tools/hang-probe/NeverEndsTests.cs -Destination $probeDir
    Copy-Item tools/hang-probe/PlayMap.HangProbe.csproj.in -Destination (Join-Path $probeDir 'PlayMap.HangProbe.csproj')
    $project = (Resolve-Path (Join-Path $probeDir 'PlayMap.HangProbe.csproj')).Path
    dotnet build $project -c $TestConfiguration
    if ($LASTEXITCODE -ne 0) { throw "the hang probe did not build ($LASTEXITCODE)" }

    # dotnet-dump 10.0.750501 from nuget.org, the .nupkg held to its SHA-256 as the windows job's stacks step holds it.
    $nupkg = Join-Path $env:RUNNER_TEMP 'dotnet-dump.zip'
    Invoke-WebRequest -Uri 'https://api.nuget.org/v3-flatcontainer/dotnet-dump/10.0.750501/dotnet-dump.10.0.750501.nupkg' -OutFile $nupkg
    $hash = (Get-FileHash $nupkg -Algorithm SHA256).Hash
    if ($hash -ne 'AE70C9F37548E1E006467893E21AE41A005FEFAF2270E29642B415DF053324A7') { throw "dotnet-dump.zip is not the pinned package ($hash)" }
    $toolDir = Join-Path $env:RUNNER_TEMP 'dotnet-dump'
    Expand-Archive $nupkg -DestinationPath $toolDir -Force
    $tool = (Resolve-Path (Join-Path $toolDir 'tools/net8.0/any/dotnet-dump.dll')).Path

    $legDeadline = [TimeSpan]::FromMinutes(6)
    $method = 'NeverEndsTests.WaitsOnAnEventNobodySets'
    $summary = @('### The hang guard on Windows (blame on a test that never ends)', '',
        '| Leg | Blame ended the host | dotnet test exit | Hang dumps | Managed stack read |', '|---|---|---|---|---|')
    $broken = @()
    foreach ($leg in @(
            [pscustomobject]@{ Label = 'with VSTEST_DUMP_FORCENETDUMP=1'; Folder = 'netdump'; Force = $true },
            [pscustomobject]@{ Label = 'without VSTEST_DUMP_FORCENETDUMP'; Folder = 'blamedump'; Force = $false })) {
        $results = Join-Path $env:RUNNER_TEMP "hangproof-$($leg.Folder)"
        if (Test-Path $results) { Remove-Item $results -Recurse -Force }
        if ($leg.Force) { $env:VSTEST_DUMP_FORCENETDUMP = '1' } else { Remove-Item env:VSTEST_DUMP_FORCENETDUMP -ErrorAction SilentlyContinue }
        Write-Host "::group::the hang probe $($leg.Label)"
        $run = Invoke-DotnetTest -Name "the hang probe $($leg.Label)" -Deadline $legDeadline -Arguments @(
            $project, '-c', $TestConfiguration, '--no-build', '--results-directory', $results, '--logger', 'trx;LogFileName=hangproof.trx',
            '--blame-hang', '--blame-hang-timeout', '1m', '--blame-hang-dump-type', 'mini')
        Write-Host '::endgroup::'
        $seconds = $run.Seconds.ToString('0', [Globalization.CultureInfo]::InvariantCulture)
        $ended = if ($run.Ended) { "yes, dotnet test returned after $seconds s" } else { "no: the step's $($legDeadline.TotalMinutes) min deadline ended it ($seconds s)" }
        if ($run.Ended -and $run.ExitCode -eq 0) { $broken += "$($leg.Label): the test that never ends passed" }
        if (-not $run.Ended) { $broken += "$($leg.Label): blame did not end the host within $($legDeadline.TotalMinutes) min" }
        $dumps = @(Get-ChildItem $results -Recurse -Filter '*_hangdump.dmp' -ErrorAction SilentlyContinue)
        $dumpWords = if ($dumps.Count -eq 0) { 'none' } else { ($dumps | ForEach-Object { "$($_.Name) ($([math]::Round($_.Length / 1MB, 1)) MB)" }) -join '; ' }
        $read = @()
        foreach ($dump in $dumps) {
            $out = Join-Path $results "$($dump.BaseName).stacks.txt"
            Write-Host "::group::stacks of $($dump.Name) ($($leg.Label))"
            dotnet $tool analyze $dump.FullName -c 'clrstack -all' -c 'exit' *> $out
            $code = $LASTEXITCODE
            Get-Content $out | Write-Host
            Write-Host '::endgroup::'
            $named = Select-String -Path $out -Pattern $method -SimpleMatch -Quiet
            $missing = if ($named) { '' } else { ", no frame names $method" }
            # A tool that could not start (a runtime it lacks) says so in its first line, so it is never read as an unreadable dump.
            $said = if ($code -ne 0) { ", its first line: '$("$(Get-Content $out -TotalCount 1)".Replace('|', '/'))'" } else { '' }
            $word = if ($code -eq 0 -and $named) { "yes ($($dump.Name) names $method)" } else { "no ($($dump.Name): dotnet-dump answered $code$said$missing)" }
            $read += $word
        }
        $readWords = if ($read.Count -eq 0) { 'no dump to read' } else { $read -join '; ' }
        $exit = if ($run.Ended) { "$($run.ExitCode)" } else { '-' }
        $line = "| $($leg.Label) | $ended | $exit | $dumpWords | $readWords |"
        Write-Host $line
        $summary += $line
    }
    $summary += ''
    if ($env:GITHUB_STEP_SUMMARY) { $summary | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8NoBOM }
    if ($broken.Count -gt 0) { Write-Host "::error::$($broken -join '; ')"; exit 1 }
    exit 0
}
