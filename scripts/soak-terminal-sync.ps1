param(
    [int]$TargetProcessId = 0,
    [string]$Executable = "",
    [int]$DurationSeconds = 1800,
    [int]$WarmupSeconds = 300,
    [int]$SampleIntervalSeconds = 5,
    [string]$RemoteUrl = "",
    [switch]$InjectFailures,
    [switch]$InjectionSelfTest,
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "artifacts\soak"
}

$resolvedOutput = [IO.Path]::GetFullPath($OutputDirectory)
$resolvedRepo = [IO.Path]::GetFullPath($repoRoot) + [IO.Path]::DirectorySeparatorChar
if (-not $resolvedOutput.StartsWith($resolvedRepo, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Soak output must stay inside the repository: $resolvedOutput"
}

New-Item -ItemType Directory -Force -Path $resolvedOutput | Out-Null
$runName = Get-Date -Format "yyyyMMdd-HHmmss"
$samplePath = Join-Path $resolvedOutput "$runName-samples.csv"
$summaryPath = Join-Path $resolvedOutput "$runName-summary.json"

if ($DurationSeconds -lt 900) {
    Write-Warning "A duration below 15 minutes is a diagnostic run, not the 30-minute acceptance soak."
}
if (-not $InjectionSelfTest -and
    ($WarmupSeconds -lt 0 -or $WarmupSeconds + 600 -gt $DurationSeconds)) {
    throw "Duration must leave two five-minute comparison windows after warmup."
}
if ($SampleIntervalSeconds -lt 1 -or $SampleIntervalSeconds -gt 60) {
    throw "SampleIntervalSeconds must be between 1 and 60."
}

$launched = $false
if (-not $InjectionSelfTest -and $TargetProcessId -le 0) {
    if ([string]::IsNullOrWhiteSpace($Executable)) {
        throw "Specify TargetProcessId or an executable inside the repository."
    }

    $resolvedExecutable = [IO.Path]::GetFullPath($Executable)
    if (-not $resolvedExecutable.StartsWith($resolvedRepo, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The launched executable must be inside the repository."
    }
    if (-not (Test-Path -LiteralPath $resolvedExecutable -PathType Leaf)) {
        throw "Executable not found: $resolvedExecutable"
    }

    $appProcess = Start-Process -FilePath $resolvedExecutable `
        -WorkingDirectory (Split-Path -Parent $resolvedExecutable) `
        -PassThru
    $TargetProcessId = $appProcess.Id
    $launched = $true
} elseif (-not $InjectionSelfTest) {
    $appProcess = Get-Process -Id $TargetProcessId -ErrorAction Stop
}

function Get-DescendantProcessRows {
    param([int]$RootProcessId)

    $rows = @(Get-CimInstance Win32_Process |
        Select-Object ProcessId, ParentProcessId, Name, CommandLine)
    $ids = [Collections.Generic.HashSet[int]]::new()
    [void]$ids.Add($RootProcessId)
    $changed = $true
    while ($changed) {
        $changed = $false
        foreach ($row in $rows) {
            if ($ids.Contains([int]$row.ParentProcessId) -and
                $ids.Add([int]$row.ProcessId)) {
                $changed = $true
            }
        }
    }

    return @($rows | Where-Object { $ids.Contains([int]$_.ProcessId) })
}

function Get-Median {
    param([double[]]$Values)

    if ($Values.Count -eq 0) {
        throw "Cannot calculate a median from an empty window."
    }
    $ordered = @($Values | Sort-Object)
    $middle = [int][Math]::Floor($ordered.Count / 2)
    if ($ordered.Count % 2 -eq 1) {
        return [double]$ordered[$middle]
    }
    return ([double]$ordered[$middle - 1] + [double]$ordered[$middle]) / 2
}

function Connect-SoakSocket {
    param([string]$Url)

    $socket = [Net.WebSockets.ClientWebSocket]::new()
    $timeout = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(15))
    try {
        [void]($socket.ConnectAsync(
            [Uri]$Url,
            $timeout.Token).GetAwaiter().GetResult())
        # Unary comma prevents PowerShell from treating the socket as a
        # success-stream collection while still unwrapping to one scalar at the
        # caller. All preceding method results are explicitly suppressed.
        return ,$socket
    } catch {
        $socket.Dispose()
        throw
    } finally {
        $timeout.Dispose()
    }
}

function Close-SoakSocket {
    param([AllowNull()][object]$Socket)

    if ($null -eq $Socket) {
        return
    }

    $socketItems = @($Socket | Where-Object {
        $_ -is [Net.WebSockets.ClientWebSocket]
    })
    foreach ($item in $socketItems) {

        try {
            if ($item.State -eq [Net.WebSockets.WebSocketState]::Open) {
                $timeout = [Threading.CancellationTokenSource]::new(
                    [TimeSpan]::FromSeconds(3))
                try {
                    [void]($item.CloseAsync(
                        [Net.WebSockets.WebSocketCloseStatus]::NormalClosure,
                        "soak injection",
                        $timeout.Token).GetAwaiter().GetResult())
                } finally {
                    $timeout.Dispose()
                }
            }
        } catch {
            try { $item.Abort() } catch { }
        } finally {
            try { $item.Dispose() } catch { }
        }
    }
}

if ($InjectionSelfTest) {
    if ([string]::IsNullOrWhiteSpace($RemoteUrl)) {
        throw "InjectionSelfTest requires RemoteUrl."
    }

    $testSlowSocket = $null
    $testReconnectSocket = $null
    try {
        $connected = @(Connect-SoakSocket -Url $RemoteUrl)
        if ($connected.Count -ne 1 -or
            $connected[0] -isnot [Net.WebSockets.ClientWebSocket]) {
            $types = @($connected | ForEach-Object {
                if ($null -eq $_) { "null" } else { $_.GetType().FullName }
            }) -join ", "
            throw "Connect-SoakSocket returned invalid values: $types"
        }
        $testSlowSocket = $connected[0]

        for ($attempt = 0; $attempt -lt 2; $attempt++) {
            $connected = @(Connect-SoakSocket -Url $RemoteUrl)
            if ($connected.Count -ne 1 -or
                $connected[0] -isnot [Net.WebSockets.ClientWebSocket]) {
                $types = @($connected | ForEach-Object {
                    if ($null -eq $_) { "null" } else { $_.GetType().FullName }
                }) -join ", "
                throw "Reconnect attempt returned invalid values: $types"
            }
            $testReconnectSocket = $connected[0]
            # Exercise the same defensive array cleanup that protects finally
            # from partially assigned or legacy success-stream values.
            Close-SoakSocket -Socket @($testReconnectSocket, $null, "ignored")
            $testReconnectSocket = $null
        }
    } finally {
        Close-SoakSocket -Socket @($testReconnectSocket, $testSlowSocket)
    }

    Write-Host "PASS soak WebSocket injection connect/reconnect/cleanup self-test"
    return
}

$samples = [Collections.Generic.List[object]]::new()
$slowSocket = $null
$reconnectSocket = $null
$injectedRendererCrash = $false
$injectedRemoteClients = $false
$startedAt = Get-Date
$stopwatch = [Diagnostics.Stopwatch]::StartNew()
$failedReason = $null

try {
    while ($stopwatch.Elapsed.TotalSeconds -lt $DurationSeconds) {
        $process = Get-Process -Id $TargetProcessId -ErrorAction SilentlyContinue
        if ($null -eq $process) {
            throw "TrayTerminal process $TargetProcessId exited during soak."
        }

        $descendants = @(Get-DescendantProcessRows -RootProcessId $TargetProcessId)
        $webViewRows = @($descendants | Where-Object {
            $_.Name -ieq "msedgewebview2.exe"
        })
        $measuredIds = @($TargetProcessId) + @($webViewRows.ProcessId)
        $measured = @(foreach ($id in $measuredIds | Select-Object -Unique) {
            Get-Process -Id $id -ErrorAction SilentlyContinue
        })
        $privateBytes = [long](($measured | Measure-Object PrivateMemorySize64 -Sum).Sum)
        $handles = [int](($measured | Measure-Object HandleCount -Sum).Sum)
        $threads = [int](($measured | ForEach-Object { $_.Threads.Count } |
            Measure-Object -Sum).Sum)
        $connections = 0
        try {
            $connections = @(Get-NetTCPConnection -ErrorAction Stop |
                Where-Object { $measuredIds -contains $_.OwningProcess }).Count
        } catch {
            $connections = -1
        }

        $sample = [pscustomobject]@{
            Timestamp = (Get-Date).ToString("O")
            ElapsedSeconds = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 3)
            AppProcessId = $TargetProcessId
            WebViewProcessCount = $webViewRows.Count
            PrivateBytes = $privateBytes
            HandleCount = $handles
            ThreadCount = $threads
            ConnectionCount = $connections
        }
        $samples.Add($sample)
        $samples | Export-Csv -LiteralPath $samplePath -NoTypeInformation -Encoding utf8

        if ($InjectFailures -and
            -not $injectedRendererCrash -and
            $stopwatch.Elapsed.TotalSeconds -ge ($WarmupSeconds + 60)) {
            $renderer = $webViewRows |
                Where-Object { $_.CommandLine -match "--type=renderer" } |
                ForEach-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue } |
                Sort-Object WorkingSet64 |
                Select-Object -First 1
            if ($null -ne $renderer) {
                Stop-Process -Id $renderer.Id -Force
                $injectedRendererCrash = $true
            }
        }

        if ($InjectFailures -and
            -not $injectedRemoteClients -and
            -not [string]::IsNullOrWhiteSpace($RemoteUrl) -and
            $stopwatch.Elapsed.TotalSeconds -ge ($WarmupSeconds + 120)) {
            $slowSocket = Connect-SoakSocket -Url $RemoteUrl
            $reconnectSocket = Connect-SoakSocket -Url $RemoteUrl
            Close-SoakSocket -Socket $reconnectSocket
            $reconnectSocket = $null
            $reconnectSocket = Connect-SoakSocket -Url $RemoteUrl
            Close-SoakSocket -Socket $reconnectSocket
            $reconnectSocket = $null
            $injectedRemoteClients = $true
        }

        $remaining = $DurationSeconds - $stopwatch.Elapsed.TotalSeconds
        if ($remaining -gt 0) {
            Start-Sleep -Seconds ([Math]::Min($SampleIntervalSeconds, $remaining))
        }
    }
} catch {
    $failedReason = $_.Exception.Message
} finally {
    Close-SoakSocket -Socket @($reconnectSocket, $slowSocket)
    $stopwatch.Stop()
    if ($samples.Count -gt 0) {
        $samples | Export-Csv -LiteralPath $samplePath -NoTypeInformation -Encoding utf8
    }
}

if ($null -ne $failedReason) {
    [pscustomobject]@{
        Passed = $false
        Failure = $failedReason
        Samples = $samplePath
        ProcessWasLaunched = $launched
    } | ConvertTo-Json | Set-Content -LiteralPath $summaryPath -Encoding utf8
    throw $failedReason
}

$firstWindow = @($samples | Where-Object {
    $_.ElapsedSeconds -ge $WarmupSeconds -and
    $_.ElapsedSeconds -lt ($WarmupSeconds + 300)
})
$lastWindow = @($samples | Where-Object {
    $_.ElapsedSeconds -ge ($DurationSeconds - 300)
})

$firstMemory = Get-Median -Values @($firstWindow.PrivateBytes)
$lastMemory = Get-Median -Values @($lastWindow.PrivateBytes)
$firstHandles = Get-Median -Values @($firstWindow.HandleCount)
$lastHandles = Get-Median -Values @($lastWindow.HandleCount)
$firstThreads = Get-Median -Values @($firstWindow.ThreadCount)
$lastThreads = Get-Median -Values @($lastWindow.ThreadCount)
$memoryDelta = [long]($lastMemory - $firstMemory)
$handleDelta = [int][Math]::Ceiling($lastHandles - $firstHandles)
$threadDelta = [int][Math]::Ceiling($lastThreads - $firstThreads)
$passed = $memoryDelta -le 64MB -and $handleDelta -le 10 -and $threadDelta -le 5

$summary = [pscustomobject]@{
    Passed = $passed
    DurationSeconds = $DurationSeconds
    WarmupSeconds = $WarmupSeconds
    SampleCount = $samples.Count
    FirstWindowPrivateBytesMedian = [long]$firstMemory
    LastWindowPrivateBytesMedian = [long]$lastMemory
    PrivateBytesDelta = $memoryDelta
    HandleDelta = $handleDelta
    ThreadDelta = $threadDelta
    RendererCrashInjected = $injectedRendererCrash
    RemoteReconnectAndSlowClientInjected = $injectedRemoteClients
    Samples = $samplePath
    StartedAt = $startedAt.ToString("O")
    FinishedAt = (Get-Date).ToString("O")
}
$summary | ConvertTo-Json | Set-Content -LiteralPath $summaryPath -Encoding utf8
$summary | Format-List

if (-not $passed) {
    throw "Soak thresholds failed. See $summaryPath and $samplePath"
}
