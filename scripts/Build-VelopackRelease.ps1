[CmdletBinding()]
param(
    [string]$Version = '',

    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    [string]$Channel = '',

    [string]$Configuration = 'Release',

    [ValidateSet('FrameworkDependent', 'SelfContainedPartialTrim')]
    [string]$PublishMode = 'SelfContainedPartialTrim',

    [string]$OutputRoot = 'artifacts/release',

    [switch]$Force,

    [switch]$NoPortable,

    [switch]$NoLegacyManifest,

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

function Test-CopilotCliBinary {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BinaryPath,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedVersion,

        [Parameter(Mandatory = $true)]
        [string]$Platform
    )

    if (-not (Test-Path $BinaryPath)) {
        return $false
    }

    $fileInfo = Get-Item $BinaryPath
    if ($fileInfo.Length -lt 1MB) {
        return $false
    }

    $expectedMachine = switch ($Platform) {
        'win32-x64' { 0x8664 }
        'win32-arm64' { 0xAA64 }
        default { throw "Unsupported Copilot CLI platform '$Platform'." }
    }

    $stream = [System.IO.File]::OpenRead($BinaryPath)
    try {
        if ($stream.Length -lt 0x40) {
            return $false
        }

        $reader = [System.IO.BinaryReader]::new($stream)
        try {
            $stream.Position = 0x3C
            $peHeaderOffset = $reader.ReadInt32()
            if ($peHeaderOffset -lt 0 -or $stream.Length -lt ($peHeaderOffset + 6)) {
                return $false
            }

            $stream.Position = $peHeaderOffset
            $signature = $reader.ReadUInt32()
            if ($signature -ne 0x00004550) {
                return $false
            }

            $machine = $reader.ReadUInt16()
            if ($machine -ne $expectedMachine) {
                return $false
            }
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }

    $hostPlatform = switch ([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture) {
        'X64' { 'win32-x64' }
        'Arm64' { 'win32-arm64' }
        default { '' }
    }

    if ($hostPlatform -ne $Platform) {
        return $true
    }

    $expectedVersionPrefix = $ExpectedVersion.Split('-')[0]
    try {
        $versionOutput = & $BinaryPath --version 2>&1
        if ($LASTEXITCODE -ne 0) {
            return $false
        }
    }
    catch {
        return $false
    }

    $versionText = $versionOutput -join [System.Environment]::NewLine
    return $versionText.Contains("GitHub Copilot CLI $expectedVersionPrefix", [System.StringComparison]::Ordinal)
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
    $archivePath = Join-Path $cacheDir 'copilot.zip'
    $binaryPath = Join-Path $cacheDir $packageInfo.BinaryName

    if (Test-CopilotCliBinary -BinaryPath $binaryPath -ExpectedVersion $packageInfo.Version -Platform $packageInfo.Platform) {
        return $binaryPath
    }

    if (Test-Path $cacheDir) {
        Write-Warning "Discarding invalid cached Copilot CLI at '$cacheDir'."
        Remove-Item $cacheDir -Recurse -Force
    }

    $downloadUrl = "https://github.com/github/copilot-cli/releases/download/v$($packageInfo.Version)/copilot-$($packageInfo.Platform).zip"
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        Remove-Item $cacheDir -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null

        try {
            Write-Host "Downloading Copilot CLI $($packageInfo.Version) for $($packageInfo.Platform) (attempt $attempt of 3)."
            Invoke-WebRequest -Uri $downloadUrl -OutFile $archivePath -TimeoutSec 600
            Invoke-NativeCommand -FilePath 'tar' -ArgumentList @('-xf', $archivePath, '-C', $cacheDir)

            if (Test-CopilotCliBinary -BinaryPath $binaryPath -ExpectedVersion $packageInfo.Version -Platform $packageInfo.Platform) {
                return $binaryPath
            }

            throw "Copilot CLI binary was not extracted or failed validation at '$binaryPath'."
        }
        catch {
            Remove-Item $cacheDir -Recurse -Force -ErrorAction SilentlyContinue
            if ($attempt -eq 3) {
                throw
            }

            Write-Warning "Copilot CLI download failed on attempt $attempt of 3: $($_.Exception.Message)"
        }
    }

    throw "Copilot CLI binary was not found at '$binaryPath'."
}

function Get-NuGetPackagesRoot {
    if ([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
        return (Join-Path $HOME '.nuget/packages')
    }

    return $env:NUGET_PACKAGES
}

function Resolve-IjwHostBinary {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot,

        [Parameter(Mandatory = $true)]
        [string]$Runtime
    )

    $assetsPath = Join-Path $RepoRoot 'src/RepoSyncRadar.App/obj/project.assets.json'
    if (-not (Test-Path $assetsPath)) {
        throw "Project assets file not found at '$assetsPath'. Run dotnet restore before resolving ijwhost.dll."
    }

    $assets = Get-Content $assetsPath -Raw | ConvertFrom-Json
    $targetName = $assets.targets.PSObject.Properties.Name |
        Where-Object { $_.EndsWith("/$Runtime", [System.StringComparison]::OrdinalIgnoreCase) } |
        Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($targetName)) {
        throw "Project assets file '$assetsPath' does not contain a target for runtime '$Runtime'."
    }

    $frameworkName = $assets.project.frameworks.PSObject.Properties.Name |
        Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($frameworkName)) {
        throw "Project assets file '$assetsPath' does not contain project framework metadata."
    }

    $packageName = "Microsoft.WindowsDesktop.App.Runtime.$Runtime"
    $runtimeDependency = $assets.project.frameworks.$frameworkName.downloadDependencies |
        Where-Object { [string]::Equals($_.name, $packageName, [System.StringComparison]::OrdinalIgnoreCase) } |
        Select-Object -First 1
    if ($null -eq $runtimeDependency) {
        throw "Project assets file '$assetsPath' does not contain '$packageName'."
    }

    $runtimeVersion = [string]$runtimeDependency.version
    $runtimeVersion = $runtimeVersion.Trim('[', ']')
    $runtimeVersion = ($runtimeVersion -split ',')[0].Trim()
    $ijwHostPath = [System.IO.Path]::Combine(
        (Get-NuGetPackagesRoot),
        $packageName.ToLowerInvariant(),
        $runtimeVersion,
        'runtimes',
        $Runtime,
        'lib',
        ($frameworkName -split '-')[0],
        'Ijwhost.dll')
    if (-not (Test-Path $ijwHostPath)) {
        throw "ijwhost.dll was not found at '$ijwHostPath'."
    }

    return $ijwHostPath
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$publishDir = [System.IO.Path]::Combine($repoRoot, $OutputRoot, 'publish', $Runtime)
$releaseDir = [System.IO.Path]::Combine($repoRoot, $OutputRoot, 'velopack', $Runtime)
$iconPath = [System.IO.Path]::Combine($repoRoot, 'src', 'RepoSyncRadar.App', 'Assets', 'AppIcon.ico')

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

$isSelfContainedPartialTrim = $PublishMode -eq 'SelfContainedPartialTrim'

if ($isSelfContainedPartialTrim) {
    $framework = 'webview2'
}
else {
    $framework = switch ($Runtime) {
        'win-x64' { 'net11.0-x64-desktop,webview2' }
        'win-arm64' { 'net11.0-arm64-desktop,webview2' }
    }
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
    if (-not (Test-Path $iconPath)) {
        throw "Application icon was not found at '$iconPath'."
    }

    $publishArgs = @(
        'publish',
        'src/RepoSyncRadar.App/RepoSyncRadar.App.csproj',
        '--no-restore',
        '-c', $Configuration,
        '-r', $Runtime,
        '--self-contained', $isSelfContainedPartialTrim.ToString().ToLowerInvariant(),
        '-p:DebugType=embedded',
        "-p:CopilotCliBinaryPath=$copilotCliBinaryPath",
        "-p:RepoSyncRadarVersion=$Version",
        '-o', $publishDir
    )

    if ($isSelfContainedPartialTrim) {
        $publishArgs += @(
            '-p:PublishTrimmed=true',
            '-p:IsTrimmable=false',
            '-p:TrimMode=partial',
            '-p:_SuppressWpfTrimError=true',
            '-p:BuiltInComInteropSupport=true'
        )
    }
    Invoke-NativeCommand -FilePath 'dotnet' -ArgumentList $publishArgs

    if ($isSelfContainedPartialTrim) {
        $ijwHostPath = Resolve-IjwHostBinary -RepoRoot $repoRoot -Runtime $Runtime
        Copy-Item $ijwHostPath -Destination (Join-Path $publishDir 'ijwhost.dll') -Force
    }

    $packArgs = @(
        'pack',
        '--packId', 'SIkebe.RepoSyncRadar',
        '--packTitle', 'RepoSyncRadar',
        '--packVersion', $Version,
        '--packDir', $publishDir,
        '--mainExe', 'RepoSyncRadar.exe',
        '--icon', $iconPath,
        '--runtime', $Runtime,
        '--channel', $Channel,
        '--framework', $framework,
        '--shortcuts', 'StartMenuRoot',
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

    if ($NoLegacyManifest) {
        $legacyManifestPath = Join-Path $releaseDir "RELEASES-$Channel"
        Remove-Item $legacyManifestPath -Force -ErrorAction SilentlyContinue
    }
}
finally {
    Pop-Location
}