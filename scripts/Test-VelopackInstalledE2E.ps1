[CmdletBinding()]
param(
    [ValidateSet('win-x64')]
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

function Remove-DirectoryBestEffort {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [int]$Attempts = 3,

        [switch]$ThrowOnFailure
    )

    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        if (-not (Test-Path $Path)) {
            return
        }

        try {
            Remove-Item $Path -Recurse -Force -ErrorAction Stop
            return
        }
        catch {
            if ($attempt -eq $Attempts) {
                $message = "Could not remove '$Path' after $Attempts attempt(s): $($_.Exception.Message)"
                if ($ThrowOnFailure) {
                    throw $message
                }

                Write-Warning $message
                return
            }

            Start-Sleep -Milliseconds (250 * $attempt)
        }
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$e2eProjectPath = [System.IO.Path]::Combine($repoRoot, 'tests', 'RepoSyncRadar.App.E2E.Tests', 'RepoSyncRadar.App.E2E.Tests.csproj')
if ([string]::IsNullOrWhiteSpace($ReleaseDir)) {
    $ReleaseDir = [System.IO.Path]::Combine($repoRoot, 'artifacts', 'release', 'velopack', $Runtime)
}

[array]$setupCandidates = @(Get-ChildItem -Path $ReleaseDir -Filter '*-Setup.exe' -File)
if ($setupCandidates.Count -eq 0) {
    throw "No Velopack setup executable was found under '$ReleaseDir'."
}
if ($setupCandidates.Count -gt 1) {
    $candidateNames = ($setupCandidates | Sort-Object Name | ForEach-Object { $_.Name }) -join ', '
    throw "Multiple Velopack setup executables were found under '$ReleaseDir': $candidateNames. Clean the release directory or pass a directory containing exactly one installer."
}

$setupExe = $setupCandidates[0]

$installRoot = Join-Path $env:LOCALAPPDATA 'SIkebe.RepoSyncRadar'

Get-Process RepoSyncRadar -ErrorAction SilentlyContinue | Stop-Process -Force
if ($CleanInstallRoot -and (Test-Path $installRoot)) {
    Remove-DirectoryBestEffort -Path $installRoot -ThrowOnFailure
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
        $e2eProjectPath,
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
        Remove-DirectoryBestEffort -Path $installRoot -ThrowOnFailure
    }
}
