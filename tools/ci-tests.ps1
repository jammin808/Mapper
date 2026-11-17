#!/usr/bin/env pwsh
# The Windows job's test runs (.github/workflows/build.yml), each dotnet test under a deadline the step outlives. Two uses, one each:
#   -Suites       every test project (tests/*/*.csproj) but its Backend=D3D12 tests, one after another, each under its deadline;
#                 a project that ends inside it is judged as before (its exit code, its results file), the failed tests listed from
#                 the results at the end; one that runs out is listed '<name>(deadline)' after its process tree is printed and stopped.
#   -HangProof    the hang guard's proof, run only when the workflow is dispatched with the hangproof input: tools/hang-probe's one test,
#                 which never ends, under the blame collector (a one-minute hang timeout, a mini dump) twice, once with
#                 VSTEST_DUMP_FORCENETDUMP=1 as the windows job sets it and once without, each under a deadline of its own; for each leg,
#                 whether blame ended the host and when, the hang dumps written and their sizes, and whether the pinned dotnet-dump
#                 reads the test's managed stack from them, each dump said once (blame's copy beside the results names it twice).
#   -Sample <csv> -Parent <pid> -Counters <a|b>
#                 the machine sampler -Suites starts as a process of its own (never run by hand): one row a second of the named
#                 performance counters to <csv> until its parent is gone or 75 minutes have passed. A measurement, never a gate.
# Dot-sourced with none of them (tools/ci-d3d12.ps1's leg), it only defines Invoke-DotnetTest and the deadlines.
# A process tree is printed by process id, parent id, name and start time, never by command line: the suites start node with a phone
# page's join ticket among its arguments (tests/PlayMap.Net.Tests/Phone/PageTests.cs, tests/PlayMap.Engine.Tests/Phone/PhoneScanE2ETests.cs).
# The suites run the Release build the job made. Dot-sourced, this script sets no variable its caller names but $Suites,
# $HangProof, $Sample, $Parent and $Counters (its own parameters), $TestConfiguration, $MachineCounters and the two deadline variables.
param(
    [switch] $Suites,
    [switch] $HangProof,
    [string] $Sample,
    [int] $Parent,
    [string] $Counters
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

if (@(@($Suites.IsPresent, $HangProof.IsPresent, [bool]$Sample) | Where-Object { $_ }).Count -gt 1) { throw 'say one of -Suites, -HangProof or -Sample' }

# The machine sampler's loop, in a process of its own: one row a second, each the counters one Get-Counter call read (it waits a
# second between its two collections, so the rates are over that second), the varying instances as name=value lists: every
# physical disk's queue length and seconds per transfer, and the processor time and process id of each process named testhost,
# dotnet, MsMpEng or PlayMap* (a process's processor time is in one processor's percent, so up to 100 times the processor count).
# Get-Counter, not typeperf: each call expands \Process(*) afresh, so a test host started after the sampler is read (typeperf
# expands a wildcard once, at its start: recalled, not run here). A call that fails writes its words in the row's note and the
# loop goes on; sample_ms is how long the call took (about 1000: much more is the sampler itself held). It ends when its parent is
# gone or after 75 minutes, whichever is first, and its parent stops it after the last project in any case.
if ($Sample) {
    $ErrorActionPreference = 'Continue'
    $paths = @($Counters -split '\|' | Where-Object { $_ })
    $named = '^(testhost|dotnet|msmpeng|playmap[^#]*)(#\d+)?$'
    $invariant = [Globalization.CultureInfo]::InvariantCulture
    $end = [DateTime]::UtcNow.AddMinutes(75)
    $stream = [IO.FileStream]::new($Sample, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
    $writer.AutoFlush = $true
    $writer.WriteLine('utc,available_mb,pages_input_per_s,processor_pct,disk_queue,disk_sec_per_transfer,process_pct,process_pid,sample_ms,note')
    try {
        while ([DateTime]::UtcNow -lt $end -and (Get-Process -Id $Parent -ErrorAction SilentlyContinue)) {
            $clock = [Diagnostics.Stopwatch]::StartNew()
            $available = ''; $pages = ''; $processor = ''; $note = ''
            $queue = [Collections.Generic.List[string]]::new(); $transfer = [Collections.Generic.List[string]]::new()
            $percent = [Collections.Generic.List[string]]::new(); $ids = [Collections.Generic.List[string]]::new()
            try {
                $problems = $null
                $read = Get-Counter -Counter $paths -SampleInterval 1 -MaxSamples 1 -ErrorAction SilentlyContinue -ErrorVariable problems
                if ($problems) { $note = "$($problems[0].Exception.Message)" }
                $samples = if ($read) { @($read.CounterSamples) } else { @() }
                # Not $sample: names ignore case, and a loop variable named as -Sample would take its [string] type.
                foreach ($point in $samples) {
                    if ($point.Status -ne 0) { continue }
                    $path = "$($point.Path)".ToLowerInvariant()
                    $value = ([double]$point.CookedValue).ToString('G6', $invariant)
                    $instance = "$($point.InstanceName)"
                    if ($path.EndsWith('\memory\available mbytes')) { $available = $value }
                    elseif ($path.EndsWith('\memory\pages input/sec')) { $pages = $value }
                    elseif ($path.Contains('\processor(') -and $path.EndsWith('\% processor time')) { $processor = $value }
                    elseif ($path.EndsWith('\current disk queue length')) { $queue.Add("$instance=$value") }
                    elseif ($path.EndsWith('\avg. disk sec/transfer')) { $transfer.Add("$instance=$value") }
                    elseif ($path.Contains('\process(') -and $instance -match $named) {
                        if ($path.EndsWith('\% processor time')) { $percent.Add("$instance=$value") }
                        elseif ($path.EndsWith('\id process')) { $ids.Add("$instance=$value") }
                    }
                }
            }
            catch { $note = $_.Exception.Message }
            $note = ($note -replace '[,\r\n]+', ' ').Trim()
            $took = $clock.Elapsed.TotalMilliseconds.ToString('0', $invariant)
            $writer.WriteLine((@([DateTime]::UtcNow.ToString('o', $invariant), $available, $pages, $processor, ($queue -join ';'),
                        ($transfer -join ';'), ($percent -join ';'), ($ids -join ';'), $took, $note) -join ','))
            $left = 1000 - $clock.ElapsedMilliseconds
            if ($left -gt 0) { Start-Sleep -Milliseconds $left }
        }
    }
    finally {
        $writer.Dispose()
    }
    exit 0
}

# The counters the sampler reads, each checked against the sets this runner lists first, since one path Get-Counter does not know can
# make the whole call refuse: a set or a counter the runner lacks is left out with a warning, and the rest are read.
$MachineCounters = @(
    '\Memory\Available MBytes', '\Memory\Pages Input/sec',
    '\PhysicalDisk(*)\Current Disk Queue Length', '\PhysicalDisk(*)\Avg. Disk sec/Transfer',
    '\Processor(_Total)\% Processor Time',
    '\Process(*)\% Processor Time', '\Process(*)\ID Process')

# Starts the machine sampler as a process of its own beside the suites, writing to $Csv: its parent is this script, never a test's
# dotnet test, so no deadline's tree stop (Get-ProcessTree from a dotnet test's id) takes it. It returns the process, or $null with a
# warning when it could not start; nothing here fails the step or waits on the sampler.
function Start-MachineSampler([string] $Csv) {
    try {
        if (-not (Get-Command Get-Counter -ErrorAction SilentlyContinue)) {
            Write-Host '::warning::the machine sampler did not start: Get-Counter is not on this runner (a measurement, never a gate)'
            return $null
        }
        $sets = @{}
        $paths = [Collections.Generic.List[string]]::new()
        foreach ($path in $MachineCounters) {
            $parts = $path -split '\\'
            $set = $parts[1] -replace '\(.*\)$', ''
            if (-not $sets.ContainsKey($set)) {
                $sets[$set] = @()
                try {
                    $listed = Get-Counter -ListSet $set -ErrorAction Stop
                    foreach ($name in 'Paths', 'Counter') {
                        if ($listed.PSObject.Properties[$name]) { $sets[$set] = @($listed.$name); break }
                    }
                    if ($sets[$set].Count -eq 0) { Write-Host "::warning::the machine sampler leaves out the counter set $set`: the runner listed no counters for it" }
                }
                catch { Write-Host "::warning::the machine sampler leaves out the counter set $set ($($_.Exception.Message))" }
            }
            if (@($sets[$set] | Where-Object { "$_".EndsWith('\' + $parts[-1], [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0) { $paths.Add($path) }
            elseif ($sets[$set].Count -gt 0) { Write-Host "::warning::the machine sampler leaves out $path`: this runner lists no such counter" }
        }
        if ($paths.Count -eq 0) { Write-Host '::warning::the machine sampler did not start: none of its counters is listed on this runner'; return $null }
        New-Item -ItemType Directory -Path (Split-Path $Csv) -Force | Out-Null
        $info = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
        $info.UseShellExecute = $false
        # Its output and errors are its own pipes, drained and dropped (it writes nothing on its normal path), never the step's: a
        # sampler that outlived its stop could otherwise hold the step's output open until its own parent check ended it.
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        foreach ($a in @('-NoProfile', '-NonInteractive', '-File', $PSCommandPath, '-Sample', $Csv, '-Parent', "$PID", '-Counters', ($paths -join '|'))) {
            $info.ArgumentList.Add($a)
        }
        $sampler = [Diagnostics.Process]::Start($info)
        $sampler.BeginOutputReadLine()
        $sampler.BeginErrorReadLine()
        Write-Host "the machine sampler started at $(Get-UtcStamp) (pid $($sampler.Id)), one row a second to $Csv`: $($paths -join ', ')"
        return $sampler
    }
    catch {
        Write-Host "::warning::the machine sampler did not start: $($_.Exception.Message)"
        return $null
    }
}

# Stops the sampler by its own process (never a tree: it starts none) and says what it wrote; a sampler that ended early or wrote no
# row is said with a warning, never failed on.
function Stop-MachineSampler($Sampler, [string] $Csv) {
    if (-not $Sampler) { return }
    try {
        if ($Sampler.HasExited) { Write-Host "::warning::the machine sampler (pid $($Sampler.Id)) ended before the last project, exit $($Sampler.ExitCode)" }
        else {
            $Sampler.Kill()
            if (-not $Sampler.WaitForExit(5000)) { Write-Host "::warning::the machine sampler (pid $($Sampler.Id)) still runs 5 s after it was stopped" }
        }
        $rows = if (Test-Path $Csv) { [math]::Max(0, @(Get-Content $Csv).Count - 1) } else { 0 }
        if ($rows -eq 0) { Write-Host "::warning::the machine sampler wrote no row to $Csv" }
        else { Write-Host "the machine sampler stopped at $(Get-UtcStamp) after $rows rows in $Csv" }
    }
    catch { Write-Host "::warning::the machine sampler could not be stopped or read: $($_.Exception.Message)" }
    finally { $Sampler.Dispose() }
}

# Defender as the runner reports it, read once before the suites: a reading, nothing is changed.
function Write-DefenderReading {
    $words = @()
    foreach ($pair in @(
            @('Get-MpComputerStatus', @('AMServiceEnabled', 'AntivirusEnabled', 'RealTimeProtectionEnabled', 'OnAccessProtectionEnabled',
                    'IoavProtectionEnabled', 'BehaviorMonitorEnabled', 'NISEnabled', 'IsTamperProtected', 'AMRunningMode')),
            @('Get-MpPreference', @('DisableRealtimeMonitoring', 'MAPSReporting', 'SubmitSamplesConsent', 'CloudBlockLevel', 'CloudExtendedTimeout')))) {
        $command = $pair[0]
        $fields = $pair[1]
        if (-not (Get-Command $command -ErrorAction SilentlyContinue)) { $words += "$command is not on this runner"; continue }
        try {
            $read = & $command -ErrorAction Stop
            $words += "$command`: " + (($fields | ForEach-Object {
                        $field = $read.PSObject.Properties[$_]
                        "$_=$(if ($field) { $field.Value } else { '(not given)' })"
                    }) -join ' ')
        }
        catch { $words += "$command answered: $($_.Exception.Message)" }
    }
    Write-Host "Defender as this runner reports it (a reading; nothing is changed): $($words -join '; ')"
}

if ($Suites) {
    # No test project fails the step, and so does a suite that answers 0 but leaves no results file, or one that runs past its
    # deadline. The Backend=D3D12 tests run in the Direct3D 12 leg, so each result is counted once.
    $failed = @()
    $projects = @(Get-ChildItem tests/*/*.csproj)
    if ($projects.Count -eq 0) { Write-Host "::error::no test project matches tests/*/*.csproj"; exit 1 }
    # The machine beside the tests, measured, never a gate: Defender's reading once, and the sampler from before the first project to
    # after the last, its rows uploaded with the probe's (tests/TestResults/*.sampler.csv).
    try { Write-DefenderReading } catch { Write-Host "::warning::Defender's reading failed: $($_.Exception.Message)" }
    $samplerCsv = Join-Path (Get-Location) "tests/TestResults/machine-$([DateTime]::UtcNow.ToString('yyyyMMddTHHmmss', [Globalization.CultureInfo]::InvariantCulture)).sampler.csv"
    $sampler = Start-MachineSampler $samplerCsv
    try {
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
    }
    finally {
        Stop-MachineSampler $sampler $samplerCsv
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
        # Blame writes each dump in a folder of its own and copies it beside the results as their attachment: one name, said once.
        $dumps = @(Get-ChildItem $results -Recurse -Filter '*_hangdump.dmp' -ErrorAction SilentlyContinue | Sort-Object Name -Unique)
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
