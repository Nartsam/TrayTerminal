param(
    [string]$Executable = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Executable)) {
    $Executable = Join-Path $repoRoot `
        "src\TrayTerminal.App\bin\x64\Debug\net10.0-windows\TrayTerminal.exe"
}

$resolvedExecutable = [IO.Path]::GetFullPath($Executable)
$resolvedRepo = [IO.Path]::GetFullPath($repoRoot) + [IO.Path]::DirectorySeparatorChar
if (-not $resolvedExecutable.StartsWith(
        $resolvedRepo,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Authority probe executable must stay inside the repository."
}
if (-not (Test-Path -LiteralPath $resolvedExecutable -PathType Leaf)) {
    throw "Build TrayTerminal first; executable not found: $resolvedExecutable"
}

$existing = @(Get-CimInstance Win32_Process | Where-Object {
    $_.ExecutablePath -and
    [IO.Path]::GetFullPath($_.ExecutablePath).Equals(
        $resolvedExecutable,
        [StringComparison]::OrdinalIgnoreCase)
})
if ($existing.Count -ne 0) {
    throw "Close the existing TrayTerminal process before running the isolated probe."
}

$process = Start-Process `
    -FilePath $resolvedExecutable `
    -ArgumentList "--authority-probe" `
    -WorkingDirectory (Split-Path -Parent $resolvedExecutable) `
    -PassThru
if (-not $process.WaitForExit(60000)) {
    try { $process.Kill($true) } catch { Stop-Process -Id $process.Id -Force }
    throw "Real WebView2 authority probe exceeded 60 seconds."
}
if ($process.ExitCode -ne 0) {
    throw "Real WebView2 authority probe failed with exit code $($process.ExitCode). Check Data\Logs."
}

Write-Host "PASS real WebView2 authority create/write/checkpoint/dispose probe"
