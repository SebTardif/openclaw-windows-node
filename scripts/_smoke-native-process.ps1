Set-StrictMode -Version Latest

function ConvertTo-SmokeNativeArgument {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Value
    )

    $builder = [Text.StringBuilder]::new()
    [void]$builder.Append('"')
    $backslashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') {
            $backslashes++
            continue
        }

        if ($character -eq '"') {
            [void]$builder.Append(('\' * (($backslashes * 2) + 1)))
            [void]$builder.Append('"')
            $backslashes = 0
            continue
        }

        if ($backslashes -gt 0) {
            [void]$builder.Append(('\' * $backslashes))
            $backslashes = 0
        }
        [void]$builder.Append($character)
    }

    if ($backslashes -gt 0) {
        [void]$builder.Append(('\' * ($backslashes * 2)))
    }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Stop-SmokeNativeProcessTree {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateRange(1, [int]::MaxValue)]
        [int]$RootProcessId
    )

    $processes = @(Get-CimInstance Win32_Process -ErrorAction Stop)
    if ($processes.Count -gt 4096) {
        throw "Refusing to inspect an unexpectedly large process inventory."
    }

    $ownedIds = [Collections.Generic.HashSet[int]]::new()
    [void]$ownedIds.Add($RootProcessId)
    $orderedDescendants = New-Object "Collections.Generic.List[int]"
    do {
        $added = $false
        foreach ($process in $processes) {
            $processId = [int]$process.ProcessId
            $parentId = [int]$process.ParentProcessId
            if (
                -not $ownedIds.Contains($processId) -and
                $ownedIds.Contains($parentId)
            ) {
                [void]$ownedIds.Add($processId)
                $orderedDescendants.Add($processId)
                $added = $true
            }
        }
    } while ($added)

    for ($index = $orderedDescendants.Count - 1; $index -ge 0; $index--) {
        Stop-Process -Id $orderedDescendants[$index] -Force -ErrorAction SilentlyContinue
    }
    Stop-Process -Id $RootProcessId -Force -ErrorAction SilentlyContinue
}

function Invoke-SmokeNativeProcess {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Operation,
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [string[]]$ArgumentList = @(),
        [Parameter(Mandatory = $true)]
        [ValidateRange(1, 86400)]
        [int]$TimeoutSeconds,
        [Parameter(Mandatory = $true)]
        [string]$CaptureRoot,
        [Parameter(Mandatory = $true)]
        [ValidatePattern('^[a-z0-9][a-z0-9.-]{0,63}$')]
        [string]$CaptureName,
        [string]$WorkingDirectory = (Get-Location).Path
    )

    $application = Get-Command $FilePath -CommandType Application -ErrorAction Stop
    $resolvedFilePath = [IO.Path]::GetFullPath($application.Source)
    if (-not (Test-Path -LiteralPath $resolvedFilePath -PathType Leaf)) {
        throw "$Operation executable is unavailable: '$resolvedFilePath'."
    }
    $resolvedCaptureRoot = [IO.Path]::GetFullPath($CaptureRoot).TrimEnd('\')
    if (-not (Test-Path -LiteralPath $resolvedCaptureRoot -PathType Container)) {
        throw "$Operation capture root does not exist: '$resolvedCaptureRoot'."
    }
    $captureRootItem = Get-Item -LiteralPath $resolvedCaptureRoot -Force -ErrorAction Stop
    if (($captureRootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Operation capture root must not be a reparse point."
    }
    $resolvedWorkingDirectory = [IO.Path]::GetFullPath($WorkingDirectory)
    if (-not (Test-Path -LiteralPath $resolvedWorkingDirectory -PathType Container)) {
        throw "$Operation working directory does not exist: '$resolvedWorkingDirectory'."
    }

    $stdoutPath = Join-Path $resolvedCaptureRoot "$CaptureName.stdout.log"
    $stderrPath = Join-Path $resolvedCaptureRoot "$CaptureName.stderr.log"
    foreach ($capturePath in @($stdoutPath, $stderrPath)) {
        if (Test-Path -LiteralPath $capturePath) {
            $captureItem = Get-Item -LiteralPath $capturePath -Force -ErrorAction Stop
            if (($captureItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "$Operation capture path must not be a reparse point: '$capturePath'."
            }
        }
    }

    $processStartInfo = [Diagnostics.ProcessStartInfo]::new()
    $processStartInfo.FileName = $resolvedFilePath
    $processStartInfo.Arguments = (
        @($ArgumentList | ForEach-Object {
            ConvertTo-SmokeNativeArgument -Value ([string]$_)
        }) -join ' ')
    $processStartInfo.WorkingDirectory = $resolvedWorkingDirectory
    $processStartInfo.UseShellExecute = $false
    $processStartInfo.CreateNoWindow = $true
    $processStartInfo.RedirectStandardOutput = $true
    $processStartInfo.RedirectStandardError = $true

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $processStartInfo
    $stdoutTask = $null
    $stderrTask = $null
    $timedOut = $false
    $exitCode = $null
    $deadlineMilliseconds = [Int64]$TimeoutSeconds * 1000
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    try {
        if (-not $process.Start()) {
            throw "$Operation did not start."
        }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $readTasks = [Threading.Tasks.Task[]]@($stdoutTask, $stderrTask)
        if (-not $process.WaitForExit([int]$deadlineMilliseconds)) {
            $timedOut = $true
            Stop-SmokeNativeProcessTree -RootProcessId $process.Id
            [void]$process.WaitForExit(5000)
            [void][Threading.Tasks.Task]::WaitAll($readTasks, 5000)
        } else {
            $exitCode = [int]$process.ExitCode
            $remainingMilliseconds = [Math]::Max(
                0,
                $deadlineMilliseconds - $stopwatch.ElapsedMilliseconds)
            if (
                $remainingMilliseconds -le 0 -or
                -not [Threading.Tasks.Task]::WaitAll(
                    $readTasks,
                    [int]$remainingMilliseconds)
            ) {
                $timedOut = $true
                Stop-SmokeNativeProcessTree -RootProcessId $process.Id
                [void][Threading.Tasks.Task]::WaitAll($readTasks, 5000)
            }
        }
    } finally {
        $stopwatch.Stop()
        $stdout = if ($null -ne $stdoutTask -and $stdoutTask.IsCompleted) {
            [string]$stdoutTask.GetAwaiter().GetResult()
        } else {
            "<stdout capture did not drain after process termination>"
        }
        $stderr = if ($null -ne $stderrTask -and $stderrTask.IsCompleted) {
            [string]$stderrTask.GetAwaiter().GetResult()
        } else {
            "<stderr capture did not drain after process termination>"
        }
        [IO.File]::WriteAllText($stdoutPath, $stdout, [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText($stderrPath, $stderr, [Text.UTF8Encoding]::new($false))
        $process.Dispose()
    }

    return [pscustomobject][ordered]@{
        operation = $Operation
        filePath = $resolvedFilePath
        exitCode = $exitCode
        timedOut = $timedOut
        elapsedMilliseconds = [Int64]$stopwatch.ElapsedMilliseconds
        stdoutPath = $stdoutPath
        stderrPath = $stderrPath
    }
}

function Write-SmokeNativeProcessOutput {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Result
    )

    foreach ($stream in @(
            @{ Label = "stdout"; Path = [string]$Result.stdoutPath },
            @{ Label = "stderr"; Path = [string]$Result.stderrPath }
        )) {
        if ((Get-Item -LiteralPath $stream.Path -ErrorAction Stop).Length -gt 0) {
            "$($Result.operation) $($stream.Label):"
            Get-Content -LiteralPath $stream.Path -ErrorAction Stop
        }
    }
}

function Assert-SmokeNativeProcessSucceeded {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Result
    )

    if ([bool]$Result.timedOut) {
        throw (
            "$($Result.operation) timed out after $($Result.elapsedMilliseconds) milliseconds. " +
            "See '$($Result.stdoutPath)' and '$($Result.stderrPath)'.")
    }
    if ([int]$Result.exitCode -ne 0) {
        throw (
            "$($Result.operation) failed with exit code $($Result.exitCode). " +
            "See '$($Result.stdoutPath)' and '$($Result.stderrPath)'.")
    }
}
