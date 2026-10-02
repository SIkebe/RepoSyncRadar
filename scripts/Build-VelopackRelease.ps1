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
    $runtimePackageRoot = [System.IO.Path]::Combine(
        (Get-NuGetPackagesRoot),
        $packageName.ToLowerInvariant(),
        $runtimeVersion)
    $ijwHostCandidateDirectories = @(
        [System.IO.Path]::Combine(
            $runtimePackageRoot,
            'runtimes',
            $Runtime,
            'lib',
            ($frameworkName -split '-')[0]),
        [System.IO.Path]::Combine(
            $runtimePackageRoot,
            'runtimes',
            $Runtime,
            'native')
    )

    foreach ($candidateDirectory in $ijwHostCandidateDirectories) {
        if (-not (Test-Path $candidateDirectory)) {
            continue
        }

        $resolvedPath = Get-ChildItem -Path $candidateDirectory -File |
            Where-Object { [string]::Equals($_.Name, 'ijwhost.dll', [System.StringComparison]::OrdinalIgnoreCase) } |
            Select-Object -ExpandProperty FullName -First 1
        if (-not [string]::IsNullOrWhiteSpace($resolvedPath)) {
            return $resolvedPath
        }
    }

    throw "ijwhost.dll was not found under '$runtimePackageRoot'. Checked: $($ijwHostCandidateDirectories -join ', ')"
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

    $nativeRuntime = Join-Path $publishDir "runtimes\$Runtime\native\copilot_runtime.dll"
    if (-not (Test-Path $nativeRuntime)) {
        throw "The in-process Copilot runtime was not published at '$nativeRuntime'."
    }

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