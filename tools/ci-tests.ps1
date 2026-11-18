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
#   -NodeHostAB   the node host measurement, run only when the workflow is dispatched with the nodehostab input: NodeHostTests' PING
#                 test alone (on the Engine tests' Release build, with Mesa beside their host and dotnet.exe and the scheduling probe
#                 on, as the nodehostab job prepares them), three times, each leg with the machine sampler running and its files in a folder of its own
#                 under tests/TestResults/nodehostab: A with the child drawing the default project's 1080p output at the default
#                 class, B the same with the child's home at process class normal, C as the suite runs it (the output not enabled);
#                 each held 5 s after it is seen up. Per leg, the child's priority
#                 class, renderer, first output enabled (the leg's project, from STATE), processors used over the hold, the probe's
#                 largest gap and beat gap in the hold and from the child's up stamp to the kill, the child's up to the test's seeing
#                 it, the sampler's longest sample, and the test host's own priority class as it started the child and before the
#                 kill, in the log and the summary. The readings are never a pass mark: it fails only
#                 when a leg's test failed for another reason than a wait the starvation ran out, ran past its deadline, wrote no
#                 files, or ran on another project than its leg's (STATE's first output enabled in A and B, not enabled in C).
#   -PriorityCost the priority cost's measurement, run only when the workflow is dispatched with the prioritycost input: tools/priority-probe
#                 built from a copy outside the checkout (as -HangProof builds its probe), its frame's work calibrated once on the
#                 quiet runner, then six legs one after another, each a process of the probe's own with the machine sampler running and
#                 its files in a folder of its own under tests/TestResults/prioritycost: a victim (a desk's kind of work at Normal, set by
#                 its own hand) alone, then beside a burner whose class the product's own platform claims, HIGH or NORMAL, on every
#                 processor and on cores less one, and last HIGH on every processor beside a victim left at the runner's own class; then
#                 the probe's reader makes the table, in the log and the summary. A reading is never a pass mark: it fails only when a leg
#                 wrote no files, a process did not start or did not read back the class it was told, or a leg ran past its deadline.
#   -Sample <csv> -Parent <pid> -Counters <a|b>
#                 the machine sampler -Suites starts as a process of its own (never run by hand): one row a second of the named
#                 performance counters to <csv> until its parent is gone or 75 minutes have passed. A measurement, never a gate.
# Dot-sourced with none of them (tools/ci-d3d12.ps1's leg), it only defines Invoke-DotnetTest and the deadlines.
# A process tree is printed by process id, parent id, name and start time, never by command line: the suites start node with a phone
# page's join ticket among its arguments (tests/PlayMap.Net.Tests/Phone/PageTests.cs, tests/PlayMap.Engine.Tests/Phone/PhoneScanE2ETests.cs).
# The suites run the Release build the job made. Dot-sourced, this script sets no variable its caller names but $Suites,
# $HangProof, $NodeHostAB, $PriorityCost, $Sample, $Parent and $Counters (its own parameters), $TestConfiguration, $MachineCounters and
# the two deadline variables.
param(
    [switch] $Suites,
    [switch] $HangProof,
    [switch] $NodeHostAB,
    [switch] $PriorityCost,
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
    return Invoke-WithDeadline -Name $Name -FileName (Get-Command dotnet).Source -Arguments (@('test') + $Arguments) -Deadline $Deadline
}

# Runs <FileName> <Arguments> as a process the caller outlives, as Invoke-DotnetTest runs dotnet test: it ends inside $Deadline (Ended,
# its ExitCode), or its process tree is printed and stopped (Ended false). Its start and end are printed either way.
function Invoke-WithDeadline {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [string] $FileName,
        [Parameter(Mandatory)] [string[]] $Arguments,
        [Parameter(Mandatory)] [TimeSpan] $Deadline
    )
    $info = [Diagnostics.ProcessStartInfo]::new($FileName)
    $info.UseShellExecute = $false
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
        Write-Host "::error::$Name ran past its deadline of $minutes min (at $(Get-UtcStamp)); the process tree under it (pid $($process.Id)), then stopped:"
        $listed = @(Write-ProcessTree $Name $process.Id)
        try { $process.Kill($true) } catch { Write-Host "::warning::$Name's tree could not be stopped whole: $($_.Exception.Message)" }
        if (-not $process.WaitForExit(30000)) { Write-Host "::warning::$Name (pid $($process.Id)) still runs 30 s after it was stopped" }
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

if (@(@($Suites.IsPresent, $HangProof.IsPresent, $NodeHostAB.IsPresent, $PriorityCost.IsPresent, [bool]$Sample) | Where-Object { $_ }).Count -gt 1) {
    throw 'say one of -Suites, -HangProof, -NodeHostAB, -PriorityCost or -Sample'
}

# The machine sampler's loop, in a process of its own: one row a second, each the counters one Get-Counter call read (it waits a
# second between its two collections, so the rates are over that second), the varying instances as name=value lists: every
# physical disk's queue length and seconds per transfer, and the processor time and process id of each process named testhost,
# dotnet, MsMpEng, pwsh (the step's own and this sampler's) or PlayMap* (a process's processor time is in one processor's percent,
# so up to 100 times the processor count); after the note, at the row's end, the base priority of the same processes (6 below
# normal class, 8 normal, 13 high) and the system's processor queue length (threads ready and waiting for a processor). A process
# is named as its counter path names it, with the counter's own index for a second of one name (dotnet, dotnet#1, ...), so the
# three process lists of one row pair by name; the lists' orders can differ (a sample that does not read is left out of its own
# list only), so they never pair by position. A name holds within one row only: the index is the counter's at that sample and
# moves when a process of the same name exits, so across rows a process is followed by its pid (the process_pid list).
# Get-Counter, not typeperf: each call expands \Process(*) afresh, so a test host started after the sampler is read (typeperf
# expands a wildcard once, at its start: recalled, not run here). A call that fails writes its words in the row's note and the
# loop goes on; sample_ms is how long the call took (about 1000: much more is the sampler itself held). It ends when its parent is
# gone or after 75 minutes, whichever is first, and its parent stops it after the last project in any case.
if ($Sample) {
    $ErrorActionPreference = 'Continue'
    $paths = @($Counters -split '\|' | Where-Object { $_ })
    $named = '^(testhost|dotnet|msmpeng|pwsh|playmap[^#]*)(#\d+)?$'
    $invariant = [Globalization.CultureInfo]::InvariantCulture
    $end = [DateTime]::UtcNow.AddMinutes(75)
    $stream = [IO.FileStream]::new($Sample, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
    $writer.AutoFlush = $true
    $writer.WriteLine('utc,available_mb,pages_input_per_s,processor_pct,disk_queue,disk_sec_per_transfer,process_pct,process_pid,sample_ms,note,process_priority_base,processor_queue_length')
    try {
        while ([DateTime]::UtcNow -lt $end -and (Get-Process -Id $Parent -ErrorAction SilentlyContinue)) {
            $clock = [Diagnostics.Stopwatch]::StartNew()
            $available = ''; $pages = ''; $processor = ''; $note = ''; $ready = ''
            $queue = [Collections.Generic.List[string]]::new(); $transfer = [Collections.Generic.List[string]]::new()
            $percent = [Collections.Generic.List[string]]::new(); $ids = [Collections.Generic.List[string]]::new()
            $bases = [Collections.Generic.List[string]]::new()
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
                    # A process's name from its path keeps the index that tells two of one name apart (InstanceName drops it).
                    if ($path -match '\\process\(([^)]*)\)\\') { $instance = $Matches[1] }
                    if ($path.EndsWith('\memory\available mbytes')) { $available = $value }
                    elseif ($path.EndsWith('\memory\pages input/sec')) { $pages = $value }
                    elseif ($path.Contains('\processor(') -and $path.EndsWith('\% processor time')) { $processor = $value }
                    elseif ($path.EndsWith('\current disk queue length')) { $queue.Add("$instance=$value") }
                    elseif ($path.EndsWith('\avg. disk sec/transfer')) { $transfer.Add("$instance=$value") }
                    elseif ($path.EndsWith('\system\processor queue length')) { $ready = $value }
                    elseif ($path.Contains('\process(') -and $instance -match $named) {
                        if ($path.EndsWith('\% processor time')) { $percent.Add("$instance=$value") }
                        elseif ($path.EndsWith('\id process')) { $ids.Add("$instance=$value") }
                        elseif ($path.EndsWith('\priority base')) { $bases.Add("$instance=$value") }
                    }
                }
            }
            catch { $note = $_.Exception.Message }
            $note = ($note -replace '[,\r\n]+', ' ').Trim()
            $took = $clock.Elapsed.TotalMilliseconds.ToString('0', $invariant)
            $writer.WriteLine((@([DateTime]::UtcNow.ToString('o', $invariant), $available, $pages, $processor, ($queue -join ';'),
                        ($transfer -join ';'), ($percent -join ';'), ($ids -join ';'), $took, $note, ($bases -join ';'), $ready) -join ','))
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
    '\Process(*)\% Processor Time', '\Process(*)\ID Process',
    '\Process(*)\Priority Base', '\System\Processor Queue Length')

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

# The priority class of this step's own pwsh and of its parent, read once at the top of a run: a reading, nothing is set. A process
# started with no class named takes its creator's when that is below normal (Windows' rule, recalled), and the stall a saturating
# child makes needs its test host below the child's class, so where the runner's below-normal class comes from is read here. A read that fails is a warning, never a failure; the parent is named
# only when it started before this pwsh (a reused process id is never taken for it).
function Write-StepPriority {
    try {
        $self = Get-CimInstance Win32_Process -Filter "ProcessId=$PID" -ErrorAction Stop
        $rows = @([pscustomobject]@{ Who = "this step's pwsh"; Row = $self })
        $parent = Get-CimInstance Win32_Process -Filter "ProcessId=$([int]$self.ParentProcessId)" -ErrorAction SilentlyContinue
        if ($parent -and $parent.CreationDate -le $self.CreationDate) { $rows += [pscustomobject]@{ Who = 'its parent'; Row = $parent } }
        else { Write-Host "::warning::the parent of this step's pwsh (pid $($self.ParentProcessId)) is gone or not the one that started it" }
        $words = @()
        foreach ($at in $rows) {
            $id = [int]$at.Row.ProcessId
            $class = try { "$((Get-Process -Id $id -ErrorAction Stop).PriorityClass)" } catch { "(not read: $($_.Exception.Message))" }
            $words += "$($at.Who) $($at.Row.Name) (pid $id) class $class, base $($at.Row.Priority)"
        }
        Write-Host "Priority as this runner reports it (a reading; nothing is set): $($words -join '; ')"
    }
    catch { Write-Host "::warning::this step's priority could not be read: $($_.Exception.Message)" }
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
    # The machine beside the tests, measured, never a gate: the step's priority and Defender's reading once, and the sampler from
    # before the first project to after the last, its rows uploaded with the probe's (tests/TestResults/*.sampler.csv).
    Write-StepPriority
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

# Sets a leg's variable for the dotnet test it starts (its test host and the child engine inherit it), or removes it when the leg
# names no value, so an unset leg variable is unset as everywhere else, never empty.
function Set-LegVariable([string] $Name, [string] $Value) {
    if ($Value) { Set-Item -Path "env:$Name" -Value $Value } else { Remove-Item "env:$Name" -ErrorAction SilentlyContinue }
}

# The probe's rows inside a window (the hold, from the child seen up to the hold's end; or the whole, from the child's own up stamp to
# the kill asked) and the first row after it, so a gap that began in the window and ended after it is counted: the largest gap between two rows and the largest beat gap a row carries, in milliseconds, or $null
# each when no row falls there. A row that does not read (a torn last line) is left out.
function Read-HoldWindow([string] $Latency, [DateTime] $From, [DateTime] $To) {
    $invariant = [Globalization.CultureInfo]::InvariantCulture
    $lines = @(Get-Content $Latency | Where-Object { $_ -and -not $_.StartsWith('#') })
    if ($lines.Count -lt 2) { return [pscustomobject]@{ Gap = $null; Beat = $null; Rows = 0 } }
    $columns = $lines[0] -split ','
    $beatAt = [Array]::IndexOf($columns, 'beat_gap_ms')
    $previous = $null; $gap = $null; $beat = $null; $rows = 0
    foreach ($line in ($lines | Select-Object -Skip 1)) {
        $cells = $line -split ','
        if ($cells.Count -ne $columns.Count) { continue }
        $at = [DateTimeOffset]::MinValue
        if (-not [DateTimeOffset]::TryParse($cells[0], $invariant, [Globalization.DateTimeStyles]::AssumeUniversal, [ref] $at)) { continue }
        $utc = $at.UtcDateTime
        if ($utc -le $From) { $previous = $utc; continue }
        $rows++
        if ($null -ne $previous) { $gap = [math]::Max([double]$(if ($null -eq $gap) { 0 } else { $gap }), ($utc - $previous).TotalMilliseconds) }
        $value = 0.0
        if ($beatAt -ge 0 -and [double]::TryParse($cells[$beatAt], [Globalization.NumberStyles]::Float, $invariant, [ref] $value)) {
            $beat = [math]::Max([double]$(if ($null -eq $beat) { 0 } else { $beat }), $value)
        }
        $previous = $utc
        if ($utc -gt $To) { break }
    }
    return [pscustomobject]@{ Gap = $gap; Beat = $beat; Rows = $rows }
}

if ($NodeHostAB) {
    if ($env:D3D12_FILTER -or $env:HANGPROOF -eq 'true') { throw 'the run was dispatched with nodehostab and another input (d3d12 or hangproof): say one' }
    if ($env:PLAYMAP_SCHEDULING_PROBE -ne '1') { throw 'the node host legs run with the scheduling probe on: PLAYMAP_SCHEDULING_PROBE=1' }
    Write-StepPriority
    $invariant = [Globalization.CultureInfo]::InvariantCulture
    $project = (Resolve-Path tests/PlayMap.Engine.Tests/PlayMap.Engine.Tests.csproj).Path
    # The probe and the node host row are written to the project's own TestResults (tests/Shared/SchedulingProbe.cs), emptied before
    # each leg and moved into the leg's folder after it.
    $projectResults = Join-Path (Split-Path $project) 'TestResults'
    $test = 'PlayMap.Engine.Tests.Spine.NodeHostTests.TheNodeHostRunsAnEngineThatAnswersPing'
    $root = Join-Path (Get-Location) 'tests/TestResults/nodehostab'
    if (Test-Path $root) { Remove-Item $root -Recurse -Force }
    $holdMs = '5000'
    $uri = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'
    # A and B draw the default project's 1080p output (PLAYMAP_NODEHOST_OUTPUT=on), the saturating case the suite no longer runs;
    # C is the suite's own test, nothing set but the hold.
    $legs = @(
        [pscustomobject]@{ Name = 'A'; Label = 'its output drawn, the default class'; Process = ''; Output = 'on'; Enabled = 'true' },
        [pscustomobject]@{ Name = 'B'; Label = "its output drawn, the home's process class normal"; Process = 'normal'; Output = 'on'; Enabled = 'true' },
        [pscustomobject]@{ Name = 'C'; Label = 'as the suite runs it (its output not enabled)'; Process = ''; Output = ''; Enabled = 'false' })
    $summary = @('### The node host test held 5 s after its child is up, in three legs (a measurement, never a pass mark)', '',
        ('| Leg | Test | Priority class | Renderer | First output enabled | Processors over the hold | Probe''s largest gap in the hold | ' +
            'Largest beat gap in the hold | Probe''s largest gap, child up to kill | Largest beat gap, child up to kill | Child up to seen | ' +
            'Sampler''s longest sample | Test host''s class, child started / before the kill |'),
        '|---|---|---|---|---|---|---|---|---|---|---|---|---|')
    $broken = @()
    foreach ($leg in $legs) {
        $dir = Join-Path $root "leg-$($leg.Name)"
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        if (Test-Path $projectResults) { Remove-Item $projectResults -Recurse -Force }
        $csv = Join-Path $dir 'machine.sampler.csv'
        Set-LegVariable 'PLAYMAP_NODEHOST_HOLD_MS' $holdMs
        Set-LegVariable 'PLAYMAP_NODEHOST_PROCESS' $leg.Process
        Set-LegVariable 'PLAYMAP_NODEHOST_OUTPUT' $leg.Output
        $sampler = Start-MachineSampler $csv
        Write-Host "::group::leg $($leg.Name), $($leg.Label)"
        try {
            $run = Invoke-DotnetTest -Name "leg $($leg.Name)" -Deadline $TestDeadlineFloor -Arguments @(
                $project, '-c', $TestConfiguration, '--no-build', '--filter', "FullyQualifiedName=$test", '--results-directory', $dir,
                '--logger', 'trx;LogFileName=leg.trx', '--blame-hang', '--blame-hang-timeout', '5m', '--blame-hang-dump-type', 'mini')
        }
        finally {
            Stop-MachineSampler $sampler $csv
            foreach ($name in 'PLAYMAP_NODEHOST_HOLD_MS', 'PLAYMAP_NODEHOST_PROCESS', 'PLAYMAP_NODEHOST_OUTPUT') { Set-LegVariable $name '' }
        }
        Write-Host '::endgroup::'
        if (Test-Path $projectResults) {
            Get-ChildItem $projectResults -File | Where-Object { $_.Name -like '*.latency.csv' -or $_.Name -like '*.nodehost.csv' } |
                Move-Item -Destination $dir
        }

        # The test's outcome: a failure whose words are a wait that ran out (the starvation measured) is said, never failed on.
        $outcome = '-'
        $trx = Join-Path $dir 'leg.trx'
        if (-not $run.Ended) { $outcome = 'past its deadline'; $broken += "leg $($leg.Name) ran past its deadline" }
        elseif (-not (Test-Path $trx)) { $outcome = 'no results'; $broken += "leg $($leg.Name) left no results (exit $($run.ExitCode))" }
        else {
            $results = @(Select-Xml -Path $trx -XPath '//t:UnitTestResult' -Namespace @{ t = $uri })
            if ($results.Count -ne 1) { $outcome = "$($results.Count) tests"; $broken += "leg $($leg.Name) ran $($results.Count) tests, not the one" }
            else {
                $result = $results[0].Node
                $outcome = $result.GetAttribute('outcome')
                if ($outcome -eq 'Failed') {
                    $names = [System.Xml.XmlNamespaceManager]::new($result.OwnerDocument.NameTable)
                    $names.AddNamespace('t', $uri)
                    $node = $result.SelectSingleNode('t:Output/t:ErrorInfo/t:Message', $names)
                    $message = if ($node) { ($node.InnerText -split "`n" | Select-Object -First 3) -join ' | ' } else { '' }
                    if ($message -match 'TimeoutException|the node did not come up') {
                        $outcome = 'Failed: a wait ran out'
                        Write-Host "::warning::leg $($leg.Name)'s test failed as the starvation measured does (a wait ran out): $message"
                    }
                    else { $broken += "leg $($leg.Name)'s test failed: $message" }
                }
                elseif ($outcome -ne 'Passed') { $broken += "leg $($leg.Name)'s test ended $outcome" }
            }
        }

        # The readings: the node host row, the probe's rows inside the hold, the sampler's longest sample.
        $nodehost = @(Get-ChildItem $dir -Filter *.nodehost.csv)
        $latency = @(Get-ChildItem $dir -Filter *.latency.csv)
        if ($nodehost.Count -eq 0 -or $latency.Count -eq 0) {
            $broken += "leg $($leg.Name) wrote no files: node host rows $($nodehost.Count), probe files $($latency.Count)"
        }
        $class = '-'; $renderer = '-'; $enabled = '-'; $processors = '-'; $gapWords = '-'; $beatWords = '-'; $sampleWords = '-'
        $wholeGapWords = '-'; $wholeBeatWords = '-'; $seenWords = '-'; $hostWords = '-'
        if ($nodehost.Count -gt 0) {
            $row = @(Import-Csv $nodehost[0].FullName) | Select-Object -First 1
            if ($row) {
                $class = if ($row.child_priority_class) { $row.child_priority_class } else { '(not read)' }
                $renderer = if ($row.child_renderer) { $row.child_renderer } else { '(not named)' }
                # The test host's own class: the stall needs it below the child's (docs/round2/CARRYOVER.md, fix-ci-nodehost-mend).
                $hostWords = "$(if ($row.host_priority_class_start) { $row.host_priority_class_start } else { '(not read)' }) / " +
                    "$(if ($row.host_priority_class_kill) { $row.host_priority_class_kill } else { '(not read)' })"
                # The project the leg ran on, from the STATE the test read: a cell that is not the leg's means the leg did not run as
                # set (a project that would not open leaves the engine on its default, whose output is enabled), never a reading.
                $enabled = if ($row.state_output_enabled) { $row.state_output_enabled } else { '(not read)' }
                if ($row.state_output_enabled -and $row.state_output_enabled -ne $leg.Enabled) {
                    $broken += "leg $($leg.Name) ran with its first output enabled $($row.state_output_enabled), not $($leg.Enabled): not the leg it was set to be"
                }
                $from = [DateTimeOffset]::MinValue; $to = [DateTimeOffset]::MinValue
                $styles = [Globalization.DateTimeStyles]::AssumeUniversal
                # The whole window, from the child's own up stamp to the kill asked: a stall before the test saw the child up (run
                # 488's child was seen 17 s after it was up) falls outside the hold and inside this.
                $up = [DateTimeOffset]::MinValue; $kill = [DateTimeOffset]::MinValue; $seen = [DateTimeOffset]::MinValue
                if ([DateTimeOffset]::TryParse($row.child_up_utc, $invariant, $styles, [ref] $up)) {
                    if ([DateTimeOffset]::TryParse($row.test_saw_up_utc, $invariant, $styles, [ref] $seen)) {
                        $seenWords = "$(($seen - $up).TotalMilliseconds.ToString('0', $invariant)) ms"
                    }
                    if ($latency.Count -gt 0 -and [DateTimeOffset]::TryParse($row.kill_asked_utc, $invariant, $styles, [ref] $kill)) {
                        $whole = Read-HoldWindow $latency[0].FullName $up.UtcDateTime $kill.UtcDateTime
                        $wholeGapWords = if ($null -ne $whole.Gap) { "$($whole.Gap.ToString('0', $invariant)) ms ($($whole.Rows) rows)" } else { '(no row)' }
                        $wholeBeatWords = if ($null -ne $whole.Beat) { "$($whole.Beat.ToString('0', $invariant)) ms" } else { '(no row)' }
                    }
                }
                if ([DateTimeOffset]::TryParse($row.child_up_read_utc, $invariant, $styles, [ref] $from) -and
                    [DateTimeOffset]::TryParse($row.hold_end_utc, $invariant, $styles, [ref] $to)) {
                    $seconds = ($to - $from).TotalSeconds.ToString('0.0', $invariant)
                    $processors = if ($row.hold_processors) { "$($row.hold_processors) (over $seconds s)" } else { "(not read; hold $seconds s)" }
                    if ($latency.Count -gt 0) {
                        $window = Read-HoldWindow $latency[0].FullName $from.UtcDateTime $to.UtcDateTime
                        $gapWords = if ($null -ne $window.Gap) { "$($window.Gap.ToString('0', $invariant)) ms ($($window.Rows) rows)" } else { '(no row)' }
                        $beatWords = if ($null -ne $window.Beat) { "$($window.Beat.ToString('0', $invariant)) ms" } else { '(no row)' }
                    }
                }
            }
        }
        if (Test-Path $csv) {
            $longest = @(Import-Csv $csv | ForEach-Object { $v = 0.0; if ([double]::TryParse($_.sample_ms, [Globalization.NumberStyles]::Float, $invariant, [ref] $v)) { $v } }) |
                Measure-Object -Maximum
            if ($longest.Count -gt 0) { $sampleWords = "$($longest.Maximum.ToString('0', $invariant)) ms" }
        }
        $line = ("| $($leg.Name), $($leg.Label) | $outcome | $class | $($renderer.Replace('|', '/')) | $enabled | $processors | $gapWords | " +
            "$beatWords | $wholeGapWords | $wholeBeatWords | $seenWords | $sampleWords | $hostWords |")
        Write-Host $line
        $summary += $line
    }
    $summary += '', ('Read beside one another: A against C is what the drawn output demands (the processors over the hold); A against B ' +
        'is who loses at each class, never how much the child burns; the stall needs the test host below the child''s class (the last ' +
        'column). Processors over the hold near 4 is all four of the runner''s; a probe gap or beat gap of a second or more, or a ' +
        'sample far over 1,000 ms, is the starvation runs 487, 488 and 490 recorded; C quiet is the suite as it now runs ' +
        '(docs/round2/CARRYOVER.md, fix-ci-nodehost-measure and fix-ci-nodehost-mend).'), ''
    if ($env:GITHUB_STEP_SUMMARY) { $summary | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8NoBOM }
    if ($broken.Count -gt 0) { Write-Host "::error::$($broken -join '; ')"; exit 1 }
    exit 0
}

if ($PriorityCost) {
    if ($env:D3D12_FILTER -or $env:HANGPROOF -eq 'true' -or $env:NODEHOSTAB -eq 'true') {
        throw 'the run was dispatched with prioritycost and another input (d3d12, hangproof or nodehostab): say one'
    }
    Write-StepPriority
    # The probe's project file is kept as PlayMap.PriorityProbe.csproj.in so no *.csproj search of the tree meets it; it is built from a
    # copy outside the checkout, where the tree's Directory.Build.props does not reach, and references this checkout's Core and
    # Platform.Windows by PlayMapCheckout (ending in a separator: '/', which MSBuild reads on Windows too and which no quoting of the
    # argument can take for an escape), so the class the burner claims is the product's own claim.
    $probeDir = Join-Path $env:RUNNER_TEMP 'priority-probe'
    if (Test-Path $probeDir) { Remove-Item $probeDir -Recurse -Force }
    New-Item -ItemType Directory -Path $probeDir | Out-Null
    Copy-Item tools/priority-probe/*.cs -Destination $probeDir
    Copy-Item tools/priority-probe/PlayMap.PriorityProbe.csproj.in -Destination (Join-Path $probeDir 'PlayMap.PriorityProbe.csproj')
    $project = (Resolve-Path (Join-Path $probeDir 'PlayMap.PriorityProbe.csproj')).Path
    $checkout = (Get-Location).Path.TrimEnd('\', '/') + '/'
    dotnet build $project -c $TestConfiguration "-p:PlayMapCheckout=$checkout"
    if ($LASTEXITCODE -ne 0) { throw "the priority probe did not build ($LASTEXITCODE)" }
    # Run as its own exe, so the machine sampler names its processes PlayMap.PriorityProbe (followed by pid, as every row is).
    $exe = Join-Path $probeDir "bin/$TestConfiguration/net10.0/PlayMap.PriorityProbe.exe"
    if (-not (Test-Path $exe)) { throw "the priority probe built no $exe" }

    $root = Join-Path (Get-Location) 'tests/TestResults/prioritycost'
    if (Test-Path $root) { Remove-Item $root -Recurse -Force }
    New-Item -ItemType Directory -Path $root | Out-Null
    $broken = @()
    # Each leg's window is 20 s after a 3 s lead; the probe bounds a leg at 60 s for each ready file and 90 s after the window, so a
    # leg the runner holds longer than that is stopped and said; this deadline (5 min) is past all of it, for a probe that hangs.
    $legDeadline = [TimeSpan]::FromMinutes(5)
    $run = Invoke-WithDeadline -Name 'the calibration' -FileName $exe -Arguments @('calibrate', '--out', $root) -Deadline ([TimeSpan]::FromMinutes(2))
    if (-not $run.Ended -or $run.ExitCode -ne 0) { $broken += "the calibration (exit $($run.ExitCode))" }
    else {
        foreach ($leg in 'baseline', 'high-n', 'normal-n', 'high-n1', 'normal-n1', 'high-n-runner') {
            $csv = Join-Path $root "leg-$leg/machine.sampler.csv"
            $sampler = Start-MachineSampler $csv
            Write-Host "::group::leg $leg"
            try {
                $run = Invoke-WithDeadline -Name "leg $leg" -FileName $exe -Deadline $legDeadline -Arguments @(
                    'leg', '--out', $root, '--name', $leg, '--seconds', '20', '--lead', '3')
            }
            finally {
                Stop-MachineSampler $sampler $csv
            }
            Write-Host '::endgroup::'
            # The leg's own row says why it is broken (no files, a process that did not start or did not read back its class, a
            # deadline); exit 1 is that, any other end is the probe's own failure.
            if (-not $run.Ended) { $broken += "leg $leg ran past its deadline" }
            elseif ($run.ExitCode -ne 0) {
                $legCsv = Join-Path $root "leg-$leg/leg.csv"
                $row = if (Test-Path $legCsv) { @(Import-Csv $legCsv) | Select-Object -First 1 } else { $null }
                $words = if ($row) { $row.words } else { "no leg row (exit $($run.ExitCode))" }
                $broken += "leg $leg`: $words"
            }
        }
    }
    $read = Invoke-WithDeadline -Name 'the reader' -FileName $exe -Arguments @('read', '--out', $root) -Deadline ([TimeSpan]::FromMinutes(2))
    if (-not $read.Ended -or $read.ExitCode -ne 0) { $broken += "the reader (exit $($read.ExitCode))" }
    $summary = Join-Path $root 'summary.md'
    if ($env:GITHUB_STEP_SUMMARY -and (Test-Path $summary)) {
        Get-Content $summary | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8NoBOM
    }
    if ($broken.Count -gt 0) { Write-Host "::error::$($broken -join '; ')"; exit 1 }
    exit 0
}
