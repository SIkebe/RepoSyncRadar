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

    dotnet tool restore

    dotnet publish src/RepoSyncRadar.App/RepoSyncRadar.App.csproj `
        -c $Configuration `
        -r $Runtime `
        --self-contained false `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=embedded `
        -p:RepoSyncRadarVersion=$Version `
        -o $publishDir

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

    dotnet tool run vpk --yes @packArgs
}
finally {
    Pop-Location
}