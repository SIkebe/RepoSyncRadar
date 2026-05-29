[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    [string]$ReleaseDir = '',

    [switch]$CleanInstallRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-NativeCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$ArgumentList
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE."
    }
}

function Invoke-ProcessCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$ArgumentList
    )

    $process = Start-Process -FilePath $FilePath -ArgumentList $ArgumentList -NoNewWindow -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "$FilePath failed with exit code $($process.ExitCode)."
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($ReleaseDir)) {
    $ReleaseDir = [System.IO.Path]::Combine($repoRoot, 'artifacts', 'release', 'velopack', $Runtime)
}

if ($Runtime -ne 'win-x64') {
    throw "Installed E2E smoke currently requires an x64 Windows runner; '$Runtime' packages cannot be executed here."
}

$setupExe = Get-ChildItem -Path $ReleaseDir -Filter '*-Setup.exe' -File |
    Sort-Object Name |
    Select-Object -First 1

if ($null -eq $setupExe) {
    throw "No Velopack setup executable was found under '$ReleaseDir'."
}

$installRoot = Join-Path $env:LOCALAPPDATA 'SIkebe.RepoSyncRadar'

Get-Process RepoSyncRadar -ErrorAction SilentlyContinue | Stop-Process -Force
if ($CleanInstallRoot -and (Test-Path $installRoot)) {
    Remove-Item $installRoot -Recurse -Force
}

Invoke-ProcessCommand -FilePath $setupExe.FullName -ArgumentList @('--silent')
Get-Process RepoSyncRadar -ErrorAction SilentlyContinue | Stop-Process -Force

$installedExe = Join-Path $installRoot 'current\RepoSyncRadar.exe'
if (-not (Test-Path $installedExe)) {
    throw "Installed RepoSyncRadar.exe was not found at '$installedExe'."
}

$previousAppExe = $env:REPOSYNCRADAR_E2E_APP_EXE_PATH
try {
    $env:REPOSYNCRADAR_E2E_APP_EXE_PATH = $installedExe
    Invoke-NativeCommand -FilePath 'dotnet' -ArgumentList @(
        'test',
        'tests/RepoSyncRadar.App.E2E.Tests/RepoSyncRadar.App.E2E.Tests.csproj',
        '--',
        '--filter-trait',
        'Category=E2E',
        '--output',
        'detailed'
    )
}
finally {
    if ($null -eq $previousAppExe) {
        Remove-Item Env:REPOSYNCRADAR_E2E_APP_EXE_PATH -ErrorAction SilentlyContinue
    }
    else {
        $env:REPOSYNCRADAR_E2E_APP_EXE_PATH = $previousAppExe
    }

    Get-Process RepoSyncRadar -ErrorAction SilentlyContinue | Stop-Process -Force

    if ($CleanInstallRoot -and (Test-Path $installRoot)) {
        Remove-Item $installRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}