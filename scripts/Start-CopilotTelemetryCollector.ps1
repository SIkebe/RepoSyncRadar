#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Starts a local OpenTelemetry Collector container for VS Code GitHub Copilot telemetry.

.DESCRIPTION
    Creates or updates a git-ignored collector config under artifacts/copilot-grafana,
    then starts the pinned OpenTelemetry Collector contrib Docker image with OTLP HTTP/gRPC
    ports published to host localhost.
#>

[CmdletBinding()]
param(
    [string]$ConnectionString = $env:APPLICATIONINSIGHTS_CONNECTION_STRING,

    [string]$ResourceGroup,

    [string]$AppInsightsName,

    [string]$SubscriptionId,

    [string]$ConfigPath = 'artifacts/copilot-grafana/otel-collector-config.yaml',

    [string]$ContainerName = 'otel-collector',

    [string]$Image = 'otel/opentelemetry-collector-contrib:0.153.0',

    [switch]$Restart,

    [switch]$PrepareOnly
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$repoRoot = Split-Path -Parent $PSScriptRoot
$templatePath = Join-Path $repoRoot 'infra/copilot-grafana/otel-collector-config.docker.sample.yaml'
$placeholderConnectionString = 'InstrumentationKey=<YOUR-KEY>;IngestionEndpoint=https://<region>.in.applicationinsights.azure.com/;LiveEndpoint=https://<region>.livediagnostics.monitor.azure.com/;ApplicationId=<YOUR-APP-ID>'

if ([string]::IsNullOrWhiteSpace($ConnectionString) -and $ResourceGroup -and $AppInsightsName) {
    if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
        throw 'Azure CLI (az) was not found on PATH. Install Azure CLI or pass -ConnectionString directly.'
    }

    Write-Host "Resolving Application Insights connection string from Azure (RG=$ResourceGroup, name=$AppInsightsName)..." -ForegroundColor Cyan

    $azArgs = @(
        'resource', 'show',
        '--resource-group', $ResourceGroup,
        '--resource-type', 'microsoft.insights/components',
        '--name', $AppInsightsName,
        '--query', 'properties.ConnectionString',
        '--output', 'tsv'
    )
    if ($SubscriptionId) {
        $azArgs += @('--subscription', $SubscriptionId)
    }

    $ConnectionString = (& az @azArgs).Trim()

    if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
        throw "Failed to resolve connection string for Application Insights '$AppInsightsName' in resource group '$ResourceGroup'."
    }
}

if ([System.IO.Path]::IsPathRooted($ConfigPath)) {
    $resolvedConfigPath = [System.IO.Path]::GetFullPath($ConfigPath)
}
else {
    $resolvedConfigPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $ConfigPath))
}

Push-Location $repoRoot
try {
    $configDirectory = Split-Path -Parent $resolvedConfigPath
    New-Item -ItemType Directory -Force $configDirectory | Out-Null

    if (-not (Test-Path $resolvedConfigPath) -or -not [string]::IsNullOrWhiteSpace($ConnectionString)) {
        if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
            throw "Collector config '$resolvedConfigPath' does not exist. Pass -ConnectionString or set APPLICATIONINSIGHTS_CONNECTION_STRING."
        }

        $configContent = (Get-Content $templatePath -Raw).Replace($placeholderConnectionString, $ConnectionString)
        $utf8NoBom = New-Object System.Text.UTF8Encoding -ArgumentList $false
        [System.IO.File]::WriteAllText($resolvedConfigPath, $configContent, $utf8NoBom)
        Write-Host "Prepared collector config: $resolvedConfigPath" -ForegroundColor Green
    }
    else {
        Write-Host "Using existing collector config: $resolvedConfigPath" -ForegroundColor Cyan
    }

    if ($PrepareOnly) {
        Write-Host 'Prepared config only; collector was not started.' -ForegroundColor Yellow
        return
    }

    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        throw 'Docker was not found on PATH. Install or start Docker Desktop, then retry.'
    }

    Write-Host 'Checking Docker daemon...' -ForegroundColor Cyan
    docker info --format 'Docker Server {{.ServerVersion}}' | Out-Host

    $existingContainerIds = @(docker ps -a --filter "name=^/$ContainerName$" --format '{{.ID}}')
    $runningContainerIds = @(docker ps --filter "name=^/$ContainerName$" --format '{{.ID}}')

    if ($runningContainerIds.Count -gt 0 -and -not $Restart) {
        Write-Host "Collector container '$ContainerName' is already running. Use -Restart to recreate it." -ForegroundColor Green
        docker ps --filter "name=$ContainerName" --format 'table {{.Names}}\t{{.Status}}\t{{.Ports}}' | Out-Host
        return
    }

    if ($runningContainerIds.Count -gt 0) {
        Write-Host "Stopping existing collector container '$ContainerName'..." -ForegroundColor Cyan
        docker stop $ContainerName | Out-Host
    }

    # The container is started with --rm, so `docker stop` typically also removes it.
    # Re-check before attempting to remove to avoid a spurious failure.
    $existingContainerIds = @(docker ps -a --filter "name=^/$ContainerName$" --format '{{.ID}}')
    if ($existingContainerIds.Count -gt 0) {
        Write-Host "Removing existing collector container '$ContainerName'..." -ForegroundColor Cyan
        docker rm $ContainerName | Out-Host
    }

    $collectorConfigMount = "${resolvedConfigPath}:/etc/otelcol-contrib/config.yaml"

    Write-Host "Starting collector container '$ContainerName'..." -ForegroundColor Cyan
    docker run --rm -d --name $ContainerName `
        -p 127.0.0.1:4318:4318 `
        -p 127.0.0.1:4317:4317 `
        -v $collectorConfigMount `
        $Image | Out-Host

    docker ps --filter "name=$ContainerName" --format 'table {{.Names}}\t{{.Status}}\t{{.Ports}}' | Out-Host
    Write-Host 'OTLP HTTP endpoint for VS Code: http://localhost:4318' -ForegroundColor Green
}
finally {
    Pop-Location
}
