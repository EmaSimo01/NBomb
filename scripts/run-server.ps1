<#
.SYNOPSIS
    Starts one NBomb server on Windows and uses logman to collect data for the duration of the test.

.DESCRIPTION
    Launches the server executable (not via `dotnet run` which wraps the server in
    a parent process and would point the counter collector at the wrong PID), then records
    counters for as long as the test lasts.
    Three complementary sources are used because none alone is complete:
      1. Windows Performance Monitor driven through logman: CPU, memory, threads, handles, I/O
         and the bytes transmitted on the network.
      2. The in-process sampler in NBombShared (enabled via the NBOMB_SAMPLER_CSV environment
         variable). Covers garbage collection activity and thread-pool queue
         depth. The .NET CLR * PerfMon categories are a .NET Framework feature so they still
         appear in PerfMon on Windows 11 because .NET Framework registers them but a .NET 10
         process publishes nothing to them.
      3. _env.txt, which records the facts about the machine that change how to read a measurement and are
         not recoverable from the CSVs afterwards.
    Reading the CPU counter `% Tempo processore` is expressed relative to a single
    core, so on an N-core machine it saturates at N*100.

.PARAMETER Protocol
    Which protocol to serve: http1, http2, websocket or grpc.

.PARAMETER Tls
    Transport security: on or off.

.PARAMETER RecordSeconds
    How long to record. It is generous, and it can be because the client writes the window of its
    measured phase into run-info.txt. Recording
    too much costs only a few kilobytes; recording too little loses test data.

.PARAMETER OutputDir
    Directory where to store the counter CSVs. Created if missing.

.PARAMETER Label
    Tag embedded in the created files' names, e.g. "R1-http1-n16-tls". Use the same label on the
    client side so the halves of a test can be paired.

.PARAMETER SampleIntervalSeconds
    Seconds between samples for all collectors.

.PARAMETER Affinity
    Processor affinity mask for the server process, hex (0xFFFF) or decimal. Used on the loopback
    benchmark to give the server and the client independent cores. Omit on a multiple machine benchmark.

.PARAMETER NetworkAdapter
    PerfMon instance name of the network interface to record. Autodetected from the fastest
    active physical adapter when omitted.
    Pass 'none' to record no network counter at all (intended behaviour for loopback benchmarks).

.PARAMETER CertPath
    PFX file the server should use for a TLS run, exported through NBOMB_CERT_PATH. If left empty
    the server falls back to its own default of C:\CA\server.pfx.

.PARAMETER CertPassword
    Password for the PFX exported through NBOMB_CERT_PASSWORD. If left empty the server falls
    back to its own default.

.EXAMPLE
    .\run-server.ps1 -Protocol grpc -RecordSeconds 120 -Label R1-grpc-plain

.EXAMPLE
    .\run-server.ps1 -Protocol websocket -Tls on -RecordSeconds 120 -Label R1-wss

.EXAMPLE
    .\run-server.ps1 -Protocol http2 -Tls on -Affinity 0x0000FFFF -Label R1-http2-tls

.NOTES
    Run the shell as an Administrator because logman counter collection requires Administrator privilege.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('http1', 'http2', 'websocket', 'grpc')]
    [string]$Protocol,

    [ValidateSet('on', 'off')]
    [string]$Tls = 'off',

    [int]$RecordSeconds = 300,
    [string]$OutputDir = "$PSScriptRoot\..\counters",
    [string]$Label = 'run',
    [int]$SampleIntervalSeconds = 1,
    [string]$Affinity = '',
    [string]$NetworkAdapter = '',
    [string]$Configuration = 'Release',
    [string]$CertPath = '',
    [string]$CertPassword = ''
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------------------------
# COUNTER NAMES ARE LOCALISED
#
# logman resolves counter paths through PDH, which on a localised Windows knows only the
# localised names.
# Test-CounterPath below validates every path against the live machine before logman is asked to
# create anything, this way a wrong name names itself instead of failing some steps later.
# ---------------------------------------------------------------------------------------------

# Fail if this shell is not elevated.
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal(
    [Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "This shell is not running as Administrator: logman counter collection requires an elevated shell."
}

# Runs logman and captures its output, this way a failure can be analysed. $ErrorActionPreference is
# lowered locally because with it set to 'Stop' a stderr line from a native command merged in via
# 2>&1 would otherwise abort the script here before the exit code can be inspected.
function Invoke-Logman {
    param([string[]]$LogmanArgs)
    $ErrorActionPreference = 'Continue'
    $output = (& logman @LogmanArgs 2>&1 | Out-String).Trim()
    [PSCustomObject]@{ ExitCode = $LASTEXITCODE; Output = $output }
}

# Returns $true when PDH can resolve a counter path on the machine.
function Test-CounterPath {
    param([string]$Path)
    try {
        Get-Counter -Counter $Path -MaxSamples 1 -ErrorAction Stop | Out-Null
        return $true
    }
    catch {
        return $false
    }
}

# Parses an affinity mask written as hex (0xFF) or decimal. Returns $null when absent.
function ConvertTo-AffinityMask {
    param([string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }
    $trimmed = $Text.Trim()
    try {
        if ($trimmed.StartsWith('0x', [StringComparison]::OrdinalIgnoreCase)) {
            return [Convert]::ToInt64($trimmed.Substring(2), 16)
        }
        return [Convert]::ToInt64($trimmed, 10)
    }
    catch {
        throw "Invalid -Affinity value '$Text'. Expected hex (0xFFFF) or decimal."
    }
}

# PerfMon renames adapters when it publishes them as counter instances: parentheses become
# square brackets and a handful of other characters become underscores. This mirrors that change so an
# adapter description can be matched against the instance list.
function ConvertTo-CounterInstanceName {
    param([string]$AdapterDescription)
    $name = $AdapterDescription
    $name = $name.Replace('(', '[').Replace(')', ']')
    $name = $name.Replace('#', '_').Replace('\', '_').Replace('/', '_')
    return $name
}

# ---------------------------------------------------------------------------------------------
# PROTOCOL -> PROJECT
#
# There are four protocols but only three projects because http1 and http2 are served by the same executable.
# This is why the http server is the only one that takes the --protocol parameter. This is the only script that needs
# to know.
# ---------------------------------------------------------------------------------------------
$projectMap = @{
    'http1'     = 'NBombedHttpServer'
    'http2'     = 'NBombedHttpServer'
    'websocket' = 'NBombedWebsocketServer'
    'grpc'      = 'NBombedGrpcServer'
}
$projectName = $projectMap[$Protocol]
$repoRoot = Resolve-Path "$PSScriptRoot\.."
$exePath = Join-Path $repoRoot "$projectName\bin\$Configuration\net10.0\$projectName.exe"

if (-not (Test-Path $exePath)) {
    throw "Server executable not found at '$exePath'. Run: dotnet build -c $Configuration"
}

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

# Same timestamp the client uses for its report folder.
$stamp = Get-Date -Format 'yyyy-MM-dd_HH-mm-ss'
$perfmonCsv = Join-Path $OutputDir "$stamp`_$Label`_perfmon.csv"
$runtimeCsv = Join-Path $OutputDir "$stamp`_$Label`_runtime.csv"
$envTxt     = Join-Path $OutputDir "$stamp`_$Label`_env.txt"
$collectorName = "NBomb_$Label"

# Point the sampler at its own file. Set before launching so the server can read it.
$env:NBOMB_SAMPLER_CSV = $runtimeCsv
$env:NBOMB_SAMPLER_INTERVAL_MS = ($SampleIntervalSeconds * 1000).ToString()

# Certificate location exported if overridden. If left unset the server falls back to its own default.
if ($CertPath)     { $env:NBOMB_CERT_PATH = $CertPath }
if ($CertPassword) { $env:NBOMB_CERT_PASSWORD = $CertPassword }

# Server arguments.
$serverArgs = @()
if ($projectName -eq 'NBombedHttpServer') { $serverArgs += @('--protocol', $Protocol) }
$serverArgs += @('--tls', $Tls)

Write-Host "Starting $projectName (--protocol $Protocol --tls $Tls)..." -ForegroundColor Cyan

$process = Start-Process -FilePath $exePath -ArgumentList $serverArgs -PassThru
Write-Host "  PID: $($process.Id)" -ForegroundColor DarkGray

# Affinity setted when the loopback benchmark asked for it. Applied before the load starts so every thread
# the server creates from here on inherits the restricted set.
$affinityMask = ConvertTo-AffinityMask $Affinity
if ($null -ne $affinityMask) {
    try {
        $process.ProcessorAffinity = [IntPtr]$affinityMask
        Write-Host ("  Affinity: 0x{0:X}" -f $affinityMask) -ForegroundColor DarkGray
    }
    catch {
        Write-Warning "Could not apply affinity 0x$('{0:X}' -f $affinityMask): $_"
    }
}

# Give Kestrel the time to bind its listeners before starting the collector.
Start-Sleep -Seconds 2

if ($process.HasExited) {
    throw "Server exited immediately with code $($process.ExitCode). Check the server console window."
}

# ---------------------------------------------------------------------------------------------
# COUNTERS
# ---------------------------------------------------------------------------------------------

# PerfMon identifies processes by executable name without the extension. With one server running
# at a time there is no suffix disambiguation.
$instance = $projectName

# Network interface instance. The explicit 'none' is needed to
# say the counter is not wanted and a loopback test is where this value is wanted, because the
# traffic never reaches an adapter and the resulting column of random data is only noise.
$networkCounterDisabled = $NetworkAdapter -and
    [string]::Equals($NetworkAdapter, 'none', [StringComparison]::OrdinalIgnoreCase)

if ($networkCounterDisabled) {
    $NetworkAdapter = ''
}
elseif (-not $NetworkAdapter) {
    # -not Virtual is what excludes VPN and Hyper-V adapters.
    $adapter = Get-NetAdapter |
        Where-Object { $_.Status -eq 'Up' -and -not $_.Virtual } |
        Sort-Object -Property Speed -Descending |
        Select-Object -First 1
    if ($adapter) {
        $NetworkAdapter = ConvertTo-CounterInstanceName $adapter.InterfaceDescription
    }
}

$counters = @(
    # Process
    "\Processo($instance)\% Tempo processore"
    "\Processo($instance)\Working set - Privato"
    "\Processo($instance)\Working set"
    "\Processo($instance)\Byte privati"
    # OS thread count. Thread-pool threads are OS threads. A sustained growth here is the
    # signature of a starved pool under blocking work.
    "\Processo($instance)\Conteggio thread"
    "\Processo($instance)\Conteggio degli handle"
    "\Processo($instance)\Byte letti IO/sec"
    "\Processo($instance)\Byte scritti IO/sec"

    # Machine context: useful to analyse a test that hit some limit.
    "\Processore(_Total)\% Tempo processore"
    "\Memoria\MByte disponibili"

    # On a wired link this should stay at zero for the whole test.
    # Anything above zero means the test's result became a network problem.
    "\TCPv4\Segmenti ritrasmessi/sec"
)

if ($NetworkAdapter) {
    # Bytes transmitted on the network. This is the only measurement of framing cost the system
    # has: the size NBomber reports is the application payload which is the same for all four
    # protocols by construction and therefore says nothing about how efficiently each one frames
    # it.
    $counters += "\Interfaccia di rete($NetworkAdapter)\Totale byte/sec"
}

# Connection level counters. Left out because they are machine wide rather than process specific.
#   "\TCPv4\Connessioni stabilite"
#   "\TCPv4\Connessioni attive"

# Validate before starting logman so a wrong counter name says which one it is.
$missing = @()
foreach ($counter in $counters) {
    if (-not (Test-CounterPath $counter)) { $missing += $counter }
}
if ($missing.Count -gt 0) {
    Stop-Process -Id $process.Id -Force -Confirm:$false
    throw ("These counter paths do not resolve on this machine:`n  " + ($missing -join "`n  ") +
           "`n`nCounter names are localised. List the existing ones with, for example:`n" +
           "  Get-Counter -ListSet 'Processo'`n" +
           "and substitute them in the `$counters array of this script.")
}

Write-Host "Recording counters for ${RecordSeconds}s" -ForegroundColor Cyan
Write-Host "  PerfMon : $perfmonCsv" -ForegroundColor DarkGray
Write-Host "  Runtime : $runtimeCsv" -ForegroundColor DarkGray
Write-Host "  Env     : $envTxt" -ForegroundColor DarkGray

# ---------------------------------------------------------------------------------------------
# ENVIRONMENT SNAPSHOT
#
# Recorded every test because a number is not readable months later without the context: the thread pool's
# behaviour depends on the core count, the GC counters depend on the GC mode, and
# the achievable rate depends on the negotiated link speed.
#
# The UTC offset is recorded once here.
# Logman writes its file in local time through PDH and is not configurable, so
# local is the base every file of a test uses; this line is what keeps the absolute instant
# recoverable and what makes a timezone mismatch between two benchmarking machines detectable.
# ---------------------------------------------------------------------------------------------
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
$os = Get-CimInstance Win32_OperatingSystem
$totalRamGb = [math]::Round(((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB), 1)
$utcOffset = [System.TimeZoneInfo]::Local.GetUtcOffset((Get-Date))
$adapterLine = 'none detected'
if ($networkCounterDisabled) {
    # Distinguished from 'none detected': one says the machine has no active adapter,
    # the other says it has one and the counter was declined. Reading a CSV months later this information is usefull
    # to distinguish two different facts about the test.
    $adapterLine = 'none, requested with -NetworkAdapter none'
}
elseif ($NetworkAdapter) {
    $live = Get-NetAdapter | Where-Object { (ConvertTo-CounterInstanceName $_.InterfaceDescription) -eq $NetworkAdapter }
    if ($live) { $adapterLine = "$($live.InterfaceDescription) @ $($live.LinkSpeed)" }
    else { $adapterLine = $NetworkAdapter }
}

# The number of logical processors the affinity mask leaves the process is advised rather than
# left to be counted manually by the user. PerfMon expresses
# % Tempo processore relative to a single core, so a single process figure saturates at 100 times
# the number of the cores available.
$affinityLine = "not set (all $($cpu.NumberOfLogicalProcessors) logical CPUs available)"
if ($null -ne $affinityMask) {
    # PowerShell's -shr on a signed 64-bit integer is arithmetic, so a mask with the top bit set
    # would keep producing 1s and the loop would never end.
    $availableCpus = 0
    for ($bit = 0; $bit -lt 64; $bit++) {
        if ((($affinityMask -shr $bit) -band 1) -eq 1) { $availableCpus++ }
    }
    $affinityLine = ('0x{0:X} ({1} logical CPUs available)' -f $affinityMask, $availableCpus)
}

$envLines = @(
    "NBomb server environment"
    ""
    "  Label            : $Label"
    "  Protocol         : $Protocol"
    "  TLS              : $Tls"
    "  Executable       : $projectName ($Configuration)"
    "  Arguments        : $($serverArgs -join ' ')"
    "  PID              : $($process.Id)"
    "  Affinity         : $affinityLine"
    ""
    "  Machine          : $env:COMPUTERNAME"
    "  CPU              : $($cpu.Name.Trim())"
    "  Cores / logical  : $($cpu.NumberOfCores) / $($cpu.NumberOfLogicalProcessors)"
    "  RAM              : $totalRamGb GB"
    "  OS               : $($os.Caption) build $($os.BuildNumber)"
    "  Network adapter  : $adapterLine"
    "  UTC offset       : $utcOffset  (every timestamp in this system is LOCAL; this maps them back)"
    ""
    "  Record seconds   : $RecordSeconds"
    "  Sample interval  : ${SampleIntervalSeconds}s"
    "  Started (local)  : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff')"
)
$envLines | Out-File -FilePath $envTxt -Encoding utf8

# Remove any collector possibly left behind by a previous test (an interrupted one or a
# collision on the default -Label 'test'). A data collector set left "Running" cannot be
# deleted directly so it must be stopped first, otherwise it would remain active and make the
# create below fail with an exit code indistinguishable from a permission problem.
$existingCollector = Invoke-Logman @('query', $collectorName)
if ($existingCollector.ExitCode -eq 0) {
    if ($existingCollector.Output -match 'Running') {
        Invoke-Logman @('stop', $collectorName) | Out-Null
    }
    $deleteExisting = Invoke-Logman @('delete', $collectorName)
    if ($deleteExisting.ExitCode -ne 0) {
        Stop-Process -Id $process.Id -Force -Confirm:$false
        throw "Could not remove the leftover data collector set '$collectorName' from a previous run:`n$($deleteExisting.Output)"
    }
}

# logman accepts one '-c' followed by the list of counter paths.
$createArgs = @(
    'create', 'counter', $collectorName,
    '-f', 'csv',
    '-o', $perfmonCsv,
    '-si', $SampleIntervalSeconds.ToString(),
    '-rf', $RecordSeconds.ToString(),
    '-ow',
    '-c'
) + $counters

$createResult = Invoke-Logman $createArgs
if ($createResult.ExitCode -ne 0) {
    Stop-Process -Id $process.Id -Force -Confirm:$false
    throw "logman create failed with exit code $($createResult.ExitCode):`n$($createResult.Output)`n`n" +
          "This shell is already confirmed elevated and every counter path was validated, so this is " +
          "likely neither a privilege nor a name problem. Check whether a data collector set named " +
          "'$collectorName' already exists (logman query $collectorName) or whether '$OutputDir' is writable."
}

# Everything from here on must be undone whether the test finishes, throws, or is stopped with
# Ctrl+C. PowerShell runs a finally block on a pipeline stop, so Ctrl+C is safe; force closing
# the terminal window is impossible to cover and it leaves both the collector and the
# server process active. To clean up manually after a user closed window:
#   logman stop NBomb_<label>; logman delete NBomb_<label>
#   Stop-Process -Name <project name> -Force
try {
    $startResult = Invoke-Logman @('start', $collectorName)
    if ($startResult.ExitCode -ne 0) {
        throw "logman start failed with exit code $($startResult.ExitCode):`n$($startResult.Output)"
    }

    Write-Host "Recording. Press Ctrl+C to stop early; cleanup still extecutes." -ForegroundColor DarkGray

    # Sleep one second rather than longer periods so that Ctrl+C is promptly handled.
    # -rf stops the collector at the duration, the extra seconds let the final samples
    # reach the disk.
    $deadline = (Get-Date).AddSeconds($RecordSeconds + 3)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 1
        if ($process.HasExited) {
            Write-Warning "The server process exited with code $($process.ExitCode)."
            break
        }
    }
}
finally {
    Invoke-Logman @('stop', $collectorName) | Out-Null
    Invoke-Logman @('delete', $collectorName) | Out-Null

    Write-Host "Stopping server (PID $($process.Id))..." -ForegroundColor Cyan
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -Confirm:$false
    }

    # -------------------------------------------------------------------------------------
    # RENAME THE PERFMON COLUMNS
    #
    # logman writes headers like "\\<MACHINE>\Processo(NBombedHttpServer)\% Tempo processore",
    # one per column, which is difficult to read in a spreadsheet and does not match the snake_case the
    # runtime sampler writes. Rewriting the header here means both CSVs of a test are read in the same way.
    #
    # The first data row is deleted because a rate counter has no previous sample to differentiate against.
    # -------------------------------------------------------------------------------------
    if (Test-Path $perfmonCsv) {
        try {
            # Ordered: the longest patterns must be tested first or "Working set" would also
            # match "Working set - Privato".
            $columnMap = @(
                @{ Pattern = '\\Working set - Privato$';        Name = 'working_set_private_bytes' }
                @{ Pattern = '\\% Tempo processore$';           Name = 'cpu_percent' }
                @{ Pattern = '\\Working set$';                  Name = 'working_set_bytes' }
                @{ Pattern = '\\Byte privati$';                 Name = 'private_bytes' }
                @{ Pattern = '\\Conteggio thread$';             Name = 'thread_count' }
                @{ Pattern = '\\Conteggio degli handle$';       Name = 'handle_count' }
                @{ Pattern = '\\Byte letti IO/sec$';            Name = 'io_read_bytes_sec' }
                @{ Pattern = '\\Byte scritti IO/sec$';          Name = 'io_write_bytes_sec' }
                @{ Pattern = '\\MByte disponibili$';            Name = 'mem_available_mb' }
                @{ Pattern = '\\Totale byte/sec$';              Name = 'net_bytes_total_sec' }
                @{ Pattern = '\\Segmenti ritrasmessi/sec$';     Name = 'tcp_segments_retransmitted_sec' }
            )

            $lines = Get-Content -Path $perfmonCsv
            if ($lines.Count -ge 2) {
                $headerCells = $lines[0] -split '","' | ForEach-Object { $_.Trim('"') }

                for ($i = 0; $i -lt $headerCells.Count; $i++) {
                    if ($i -eq 0) {
                        $headerCells[$i] = 'timestamp_local'
                        continue
                    }

                    # A column that matches nothing keeps its original name.
                    foreach ($entry in $columnMap) {
                        if ($headerCells[$i] -match $entry.Pattern) {
                            if ($entry.Name -eq 'cpu_percent' -and $headerCells[$i] -match '\\Processore\(') {
                                $headerCells[$i] = 'machine_cpu_percent'
                            }
                            else {
                                $headerCells[$i] = $entry.Name
                            }
                            break
                        }
                    }
                }

                $rewritten = @('"' + ($headerCells -join '","') + '"')
                # Skip the header and the first data row.
                $rewritten += $lines[2..($lines.Count - 1)]
                $rewritten | Set-Content -Path $perfmonCsv -Encoding utf8
            }
        }
        catch {
            Write-Warning "Could not rename the perfmon CSV columns: $_"
        }
    }

    Remove-Item Env:\NBOMB_SAMPLER_CSV -ErrorAction SilentlyContinue
    Remove-Item Env:\NBOMB_SAMPLER_INTERVAL_MS -ErrorAction SilentlyContinue
    if ($CertPath)     { Remove-Item Env:\NBOMB_CERT_PATH -ErrorAction SilentlyContinue }
    if ($CertPassword) { Remove-Item Env:\NBOMB_CERT_PASSWORD -ErrorAction SilentlyContinue }
}

Write-Host "Done." -ForegroundColor Green
Write-Host "  $perfmonCsv"
Write-Host "  $runtimeCsv"
Write-Host "  $envTxt"