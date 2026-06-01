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

function New-Shortcut {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ShortcutPath,

        [Parameter(Mandatory = $true)]
        [string]$TargetPath,

        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory,

        [Parameter(Mandatory = $true)]
        [string]$IconLocation
    )

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $null
    try {
        $shortcut = $shell.CreateShortcut($ShortcutPath)
        $shortcut.TargetPath = $TargetPath
        $shortcut.WorkingDirectory = $WorkingDirectory
        $shortcut.IconLocation = $IconLocation
        $shortcut.Save()
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

    $startMenuRoot = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::Programs)
    $installRootPrefix = Get-DirectoryPrefix -Path $InstallRoot
    $expectedShortcutPath = Join-Path $startMenuRoot 'RepoSyncRadar.lnk'
    if (-not (Test-Path $expectedShortcutPath)) {
        throw "Installed RepoSyncRadar Start Menu shortcut was not found at '$expectedShortcutPath'."
    }

    $expectedTargetPath = Get-ShortcutTargetPath -ShortcutPath $expectedShortcutPath
    if ([string]::IsNullOrWhiteSpace($expectedTargetPath) -or -not (Test-Path $expectedTargetPath)) {
        throw "Installed RepoSyncRadar Start Menu shortcut target was not found: '$expectedShortcutPath' -> '$expectedTargetPath'."
    }

    $expectedTargetFullPath = [System.IO.Path]::GetFullPath($expectedTargetPath)
    if (-not $expectedTargetFullPath.StartsWith($installRootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Installed RepoSyncRadar Start Menu shortcut does not target '$InstallRoot': '$expectedShortcutPath' -> '$expectedTargetPath'."
    }

    $staleShortcutCandidates = @(Get-ChildItem -Path $startMenuRoot -Filter '*RepoSyncRadar*.lnk' -File -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -ne $expectedShortcutPath })
    $staleShortcuts = @(foreach ($shortcut in $staleShortcutCandidates) {
        try {
            $targetPath = Get-ShortcutTargetPath -ShortcutPath $shortcut.FullName
            if ([string]::IsNullOrWhiteSpace($targetPath)) {
                if ($shortcut.Name.StartsWith('SIkebe.RepoSyncRadar', [System.StringComparison]::OrdinalIgnoreCase)) {
                    $shortcut.FullName
                }

                continue
            }

            $targetFullPath = [System.IO.Path]::GetFullPath($targetPath)
            if ($targetFullPath.StartsWith($installRootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                $shortcut.FullName
            }
        }
        catch {
            if ($shortcut.Name.StartsWith('SIkebe.RepoSyncRadar', [System.StringComparison]::OrdinalIgnoreCase)) {
                $shortcut.FullName
            }
        }
    })

    if ($staleShortcuts.Count -gt 0) {
        $staleShortcutList = ($staleShortcuts | Sort-Object) -join ', '
        throw "Stale RepoSyncRadar Start Menu shortcut(s) were found: $staleShortcutList"
    }
}

function Remove-StartMenuShortcutsBestEffort {
    param(
        [Parameter(Mandatory = $true)]
        [string]$InstallRoot
    )

    $startMenuRoot = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::Programs)
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

function Add-StaleStartMenuShortcutForSmoke {
    param(
        [Parameter(Mandatory = $true)]
        [string]$InstallRoot,

        [Parameter(Mandatory = $true)]
        [string]$Runtime
    )

    $startMenuRoot = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::Programs)
    New-Item -ItemType Directory -Path $startMenuRoot -Force | Out-Null
    $staleShortcutPath = Join-Path $startMenuRoot "SIkebe.RepoSyncRadar-$Runtime-stable-Setup.lnk"
    $staleShortcutFolder = Join-Path $startMenuRoot 'RepoSyncRadar Old'
    $sameNameStaleShortcutPath = Join-Path $staleShortcutFolder 'RepoSyncRadar.lnk'
    $targetPath = Join-Path $InstallRoot 'RepoSyncRadar.exe'
    New-Shortcut `
        -ShortcutPath $staleShortcutPath `
        -TargetPath $targetPath `
        -WorkingDirectory $InstallRoot `
        -IconLocation $targetPath

    New-Item -ItemType Directory -Path $staleShortcutFolder -Force | Out-Null
    New-Shortcut `
        -ShortcutPath $sameNameStaleShortcutPath `
        -TargetPath $targetPath `
        -WorkingDirectory $InstallRoot `
        -IconLocation $targetPath

    return @($staleShortcutPath, $sameNameStaleShortcutPath)
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
$seededShortcutPaths = @()
if ($CleanInstallRoot) {
    $seededShortcutPaths = @(Add-StaleStartMenuShortcutForSmoke -InstallRoot $installRoot -Runtime $Runtime)
}

$previousAppExe = $env:REPOSYNCRADAR_E2E_APP_EXE_PATH
try {
    Invoke-ProcessCommand -FilePath $setupExe.FullName -ArgumentList @('--silent')
    Get-Process RepoSyncRadar -ErrorAction SilentlyContinue | Stop-Process -Force

    $installedExe = Join-Path $installRoot 'current\RepoSyncRadar.exe'
    if (-not (Test-Path $installedExe)) {
        throw "Installed RepoSyncRadar.exe was not found at '$installedExe'."
    }

    Assert-StartMenuShortcut -InstallRoot $installRoot

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

    foreach ($seededShortcutPath in $seededShortcutPaths) {
        Remove-Item $seededShortcutPath -Force -ErrorAction SilentlyContinue
    }

    if ($CleanInstallRoot -and (Test-Path $installRoot)) {
        Remove-StartMenuShortcutsBestEffort -InstallRoot $installRoot
        Remove-DirectoryBestEffort -Path $installRoot -ThrowOnFailure
    }
}
