[CmdletBinding()]
param(
    [string]$Version = '',

    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    [string]$Channel = '',

    [string]$Configuration = 'Release',

    [string]$OutputRoot = 'artifacts/release',

    [switch]$Force,

    [switch]$NoPortable,

    [string]$SignParams = '',

    [string]$AzureTrustedSignFile = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-NativeCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$ArgumentList,

        [int]$Attempts = 1
    )

    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        & $FilePath @ArgumentList
        if ($LASTEXITCODE -eq 0) {
            return
        }

        if ($attempt -lt $Attempts) {
            Write-Warning "$FilePath failed with exit code $LASTEXITCODE on attempt $attempt of $Attempts; retrying."
            continue
        }

        throw "$FilePath failed with exit code $LASTEXITCODE after $Attempts attempt(s)."
    }
}

function Get-CopilotCliPackageInfo {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot,

        [Parameter(Mandatory = $true)]
        [string]$Runtime
    )

    [xml]$packageProps = Get-Content (Join-Path $RepoRoot 'Directory.Packages.props')
    $sdkVersion = $packageProps.Project.ItemGroup.PackageVersion |
        Where-Object { $_.Include -eq 'GitHub.Copilot.SDK' } |
        ForEach-Object { $_.Version } |
        Select-Object -First 1

    if ([string]::IsNullOrWhiteSpace($sdkVersion)) {
        throw 'Directory.Packages.props must define the GitHub.Copilot.SDK package version.'
    }

    $nugetPackagesRoot = if ([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
        Join-Path $HOME '.nuget/packages'
    }
    else {
        $env:NUGET_PACKAGES
    }

    $sdkPropsPath = Join-Path $nugetPackagesRoot "github.copilot.sdk/$sdkVersion/build/GitHub.Copilot.SDK.props"
    if (-not (Test-Path $sdkPropsPath)) {
        throw "GitHub.Copilot.SDK props file not found at '$sdkPropsPath'. Run dotnet restore before resolving Copilot CLI metadata."
    }

    [xml]$sdkProps = Get-Content $sdkPropsPath
    $cliVersion = $sdkProps.Project.PropertyGroup.CopilotCliVersion
    if ([string]::IsNullOrWhiteSpace($cliVersion)) {
        throw "CopilotCliVersion was not found in '$sdkPropsPath'."
    }

    $platform = switch ($Runtime) {
        'win-x64' { 'win32-x64' }
        'win-arm64' { 'win32-arm64' }
    }

    [pscustomobject]@{
        Version = $cliVersion
        Platform = $platform
        BinaryName = 'copilot.exe'
    }
}

function Resolve-CopilotCliBinary {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot,

        [Parameter(Mandatory = $true)]
        [string]$OutputRoot,

        [Parameter(Mandatory = $true)]
        [string]$Runtime
    )

    $packageInfo = Get-CopilotCliPackageInfo -RepoRoot $RepoRoot -Runtime $Runtime
    $cacheDir = [System.IO.Path]::Combine($RepoRoot, $OutputRoot, 'copilot-cli', $packageInfo.Version, $packageInfo.Platform)
    $archivePath = Join-Path $cacheDir 'copilot.tgz'
    $binaryPath = Join-Path $cacheDir $packageInfo.BinaryName

    if (Test-Path $binaryPath) {
        return $binaryPath
    }

    New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null

    $downloadUrl = "https://registry.npmjs.org/@github/copilot-$($packageInfo.Platform)/-/copilot-$($packageInfo.Platform)-$($packageInfo.Version).tgz"
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        Remove-Item $archivePath -Force -ErrorAction SilentlyContinue

        try {
            Write-Host "Downloading Copilot CLI $($packageInfo.Version) for $($packageInfo.Platform) (attempt $attempt of 3)."
            Invoke-WebRequest -Uri $downloadUrl -OutFile $archivePath -TimeoutSec 600
            Invoke-NativeCommand -FilePath 'tar' -ArgumentList @('-xzf', $archivePath, '--strip-components=1', '-C', $cacheDir)

            if (Test-Path $binaryPath) {
                return $binaryPath
            }

            throw "Copilot CLI binary was not extracted to '$binaryPath'."
        }
        catch {
            Remove-Item $archivePath -Force -ErrorAction SilentlyContinue
            if ($attempt -eq 3) {
                throw
            }

            Write-Warning "Copilot CLI download failed on attempt $attempt of 3: $($_.Exception.Message)"
        }
    }

    throw "Copilot CLI binary was not found at '$binaryPath'."
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$publishDir = [System.IO.Path]::Combine($repoRoot, $OutputRoot, 'publish', $Runtime)
$releaseDir = [System.IO.Path]::Combine($repoRoot, $OutputRoot, 'velopack', $Runtime)

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$buildProps = Get-Content (Join-Path $repoRoot 'Directory.Build.props')
    $Version = $buildProps.Project.PropertyGroup |
    ForEach-Object { $_.RepoSyncRadarVersion.InnerText } |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -First 1
}

if ($Version -notmatch '^\d+\.\d+\.\d+([\-+][0-9A-Za-z\-.+]+)?$') {
    throw "Release version '$Version' must be SemVer like 0.1.0 or 0.1.0-beta.1."
}

if ($Version -match '^0\.0\.0([\-+]|$)') {
    throw "Release version '$Version' must be 0.0.1 or greater for Velopack."
}

if ([string]::IsNullOrWhiteSpace($Channel)) {
    $Channel = "$Runtime-stable"
}

$framework = switch ($Runtime) {
    'win-x64' { 'net10.0-x64-desktop,webview2' }
    'win-arm64' { 'net10.0-arm64-desktop,webview2' }
}

Push-Location $repoRoot
try {
    Remove-Item $publishDir -Recurse -Force -ErrorAction SilentlyContinue
    if ($Force) {
        Remove-Item $releaseDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    New-Item -ItemType Directory -Path $publishDir, $releaseDir -Force | Out-Null

    Invoke-NativeCommand -FilePath 'dotnet' -ArgumentList @('tool', 'restore')
    Invoke-NativeCommand -FilePath 'dotnet' -ArgumentList @('restore', 'src/RepoSyncRadar.App/RepoSyncRadar.App.csproj', '-r', $Runtime)

    $copilotCliBinaryPath = Resolve-CopilotCliBinary -RepoRoot $repoRoot -OutputRoot $OutputRoot -Runtime $Runtime

    Invoke-NativeCommand -FilePath 'dotnet' -ArgumentList @(
        'publish',
        'src/RepoSyncRadar.App/RepoSyncRadar.App.csproj',
        '--no-restore',
        '-c', $Configuration,
        '-r', $Runtime,
        '--self-contained', 'false',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=embedded',
        "-p:CopilotCliBinaryPath=$copilotCliBinaryPath",
        "-p:RepoSyncRadarVersion=$Version",
        '-o', $publishDir
    )

    $packArgs = @(
        'pack',
        '--packId', 'SIkebe.RepoSyncRadar',
        '--packTitle', 'RepoSyncRadar',
        '--packVersion', $Version,
        '--packDir', $publishDir,
        '--mainExe', 'RepoSyncRadar.exe',
        '--runtime', $Runtime,
        '--channel', $Channel,
        '--framework', $framework,
        '--outputDir', $releaseDir
    )

    if ($NoPortable) {
        $packArgs += '--noPortable'
    }

    if (-not [string]::IsNullOrWhiteSpace($SignParams)) {
        $packArgs += @('--signParams', $SignParams)
    }

    if (-not [string]::IsNullOrWhiteSpace($AzureTrustedSignFile)) {
        $packArgs += @('--azureTrustedSignFile', $AzureTrustedSignFile)
    }

    Invoke-NativeCommand -FilePath 'dotnet' -ArgumentList (@('tool', 'run', 'vpk', '--yes') + $packArgs)
}
finally {
    Pop-Location
}