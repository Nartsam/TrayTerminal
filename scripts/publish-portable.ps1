param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishRoot = Join-Path $repoRoot "publish"
$hostPublish = Join-Path $publishRoot "host"
$appPublish = Join-Path $publishRoot "TrayTerminal"

$runningProcesses = Get-Process TrayTerminal* -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($appPublish, [System.StringComparison]::OrdinalIgnoreCase) }

if ($runningProcesses) {
    $ids = ($runningProcesses | ForEach-Object { "$($_.ProcessName):$($_.Id)" }) -join ", "
    throw "TrayTerminal is running from $appPublish ($ids). Close it before publishing to this directory."
}

if (Test-Path $hostPublish) {
    Remove-Item -LiteralPath $hostPublish -Recurse -Force
}

if (Test-Path $appPublish) {
    Remove-Item -LiteralPath $appPublish -Recurse -Force
}

dotnet publish (Join-Path $repoRoot "src\TrayTerminal.Host\TrayTerminal.Host.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained false `
    -p:Platform=x64 `
    -o $hostPublish

dotnet publish (Join-Path $repoRoot "src\TrayTerminal.App\TrayTerminal.App.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained false `
    -p:Platform=x64 `
    -o $appPublish

Copy-Item -Path (Join-Path $hostPublish "*") -Destination $appPublish -Recurse -Force

$runtimeConfigPath = Join-Path $appPublish "TrayTerminal.runtimeconfig.json"
$runtimeConfig = Get-Content -Raw $runtimeConfigPath | ConvertFrom-Json
$frameworks = @()
if ($runtimeConfig.runtimeOptions.framework) {
    $frameworks += $runtimeConfig.runtimeOptions.framework
}
if ($runtimeConfig.runtimeOptions.frameworks) {
    $frameworks += $runtimeConfig.runtimeOptions.frameworks
}

$frameworkNames = @($frameworks | ForEach-Object { $_.name })
if ($frameworkNames -contains "Microsoft.AspNetCore.App") {
    throw "Unexpected Microsoft.AspNetCore.App runtime dependency in $runtimeConfigPath. TrayTerminal should only require the .NET Desktop Runtime."
}

Write-Host "Portable build written to $appPublish"
Write-Host "Runtime dependencies: $($frameworkNames -join ', ')"
