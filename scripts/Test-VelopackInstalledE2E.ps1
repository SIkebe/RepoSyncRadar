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

function Get-ShortcutTargetPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ShortcutPath
    )

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $null
    try {
        $shortcut = $shell.CreateShortcut($ShortcutPath)
        return [string]$shortcut.TargetPath
    }
    finally {
        if ($null -ne $shortcut) {
            [System.Runtime.InteropServices.Marshal]::ReleaseComObject($shortcut) | Out-Null
        }

        [System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null
    }
}

function Get-DirectoryPrefix {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    return $fullPath + [System.IO.Path]::DirectorySeparatorChar
}

function Assert-StartMenuShortcut {
    param(
        [Parameter(Mandatory = $true)]
        [string]$InstallRoot
    )

    $startMenuRoot = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu'
    $shortcutCandidates = @(Get-ChildItem -Path $startMenuRoot -Filter 'RepoSyncRadar.lnk' -File -Recurse -ErrorAction SilentlyContinue)
    if ($shortcutCandidates.Count -eq 0) {
        throw "Installed RepoSyncRadar Start Menu shortcut was not found under '$startMenuRoot'."
    }

    $installRootPrefix = Get-DirectoryPrefix -Path $InstallRoot
    foreach ($shortcut in $shortcutCandidates) {
        $targetPath = Get-ShortcutTargetPath -ShortcutPath $shortcut.FullName
        if ([string]::IsNullOrWhiteSpace($targetPath) -or -not (Test-Path $targetPath)) {
            continue
        }

        $targetFullPath = [System.IO.Path]::GetFullPath($targetPath)
        if ($targetFullPath.StartsWith($installRootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            return
        }
    }

    $candidatePaths = ($shortcutCandidates | Sort-Object FullName | ForEach-Object { $_.FullName }) -join ', '
    throw "RepoSyncRadar Start Menu shortcut was found, but none targets '$InstallRoot'. Candidate shortcut(s): $candidatePaths"
}

function Remove-StartMenuShortcutsBestEffort {
    param(
        [Parameter(Mandatory = $true)]
        [string]$InstallRoot
    )

    $startMenuRoot = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu'
    $installRootPrefix = Get-DirectoryPrefix -Path $InstallRoot
    $shortcutCandidates = @(Get-ChildItem -Path $startMenuRoot -Filter 'RepoSyncRadar.lnk' -File -Recurse -ErrorAction SilentlyContinue)
    foreach ($shortcut in $shortcutCandidates) {
        try {
            $targetPath = Get-ShortcutTargetPath -ShortcutPath $shortcut.FullName
            if ([string]::IsNullOrWhiteSpace($targetPath)) {
                continue
            }

            $targetFullPath = [System.IO.Path]::GetFullPath($targetPath)
            if ($targetFullPath.StartsWith($installRootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                Remove-Item $shortcut.FullName -Force -ErrorAction Stop
            }
        }
        catch {
            Write-Warning "Could not remove Start Menu shortcut '$($shortcut.FullName)': $($_.Exception.Message)"
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

Assert-StartMenuShortcut -InstallRoot $installRoot

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
        Remove-StartMenuShortcutsBestEffort -InstallRoot $installRoot
        Remove-DirectoryBestEffort -Path $installRoot -ThrowOnFailure
    }
}
