[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+([\-+][0-9A-Za-z\-.+]+)?$')]
    [string]$Version,

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
$toolDir = [System.IO.Path]::Combine($repoRoot, 'artifacts', 'tools', 'vpk')
$vpkExe = Join-Path $toolDir 'vpk.exe'

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

    New-Item -ItemType Directory -Path $publishDir, $releaseDir, $toolDir -Force | Out-Null

    dotnet publish src/RepoSyncRadar.App/RepoSyncRadar.App.csproj `
        -c $Configuration `
        -r $Runtime `
        --self-contained false `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=embedded `
        -o $publishDir

    if (Test-Path $vpkExe) {
        dotnet tool update vpk --version 1.0.1 --tool-path $toolDir
    }
    else {
        dotnet tool install vpk --version 1.0.1 --tool-path $toolDir
    }

    $packArgs = @(
        'pack',
        '--packId', 'RepoSyncRadar',
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

    & $vpkExe --yes @packArgs
}
finally {
    Pop-Location
}