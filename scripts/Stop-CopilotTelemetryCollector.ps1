#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Stops the local OpenTelemetry Collector container started by Start-CopilotTelemetryCollector.ps1.

.DESCRIPTION
    Stops (and optionally removes) the named OpenTelemetry Collector contrib Docker container
    used to forward VS Code GitHub Copilot telemetry to Application Insights.

    Because the container is started with --rm, a clean `docker stop` already removes it.
    Use -Force to also remove a leftover container that exited abnormally.
#>

[CmdletBinding()]
param(
    [string]$ContainerName = 'otel-collector',

    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'Docker was not found on PATH. Install or start Docker Desktop, then retry.'
}

$runningContainerIds = @(docker ps --filter "name=^/$ContainerName$" --format '{{.ID}}')
$existingContainerIds = @(docker ps -a --filter "name=^/$ContainerName$" --format '{{.ID}}')

if ($runningContainerIds.Count -eq 0 -and $existingContainerIds.Count -eq 0) {
    Write-Host "Collector container '$ContainerName' is not present." -ForegroundColor Yellow
    return
}

if ($runningContainerIds.Count -gt 0) {
    Write-Host "Stopping collector container '$ContainerName'..." -ForegroundColor Cyan
    docker stop $ContainerName | Out-Host
}
else {
    Write-Host "Collector container '$ContainerName' is not running." -ForegroundColor Yellow
}

if ($Force) {
    $remaining = @(docker ps -a --filter "name=^/$ContainerName$" --format '{{.ID}}')
    if ($remaining.Count -gt 0) {
        Write-Host "Removing leftover collector container '$ContainerName'..." -ForegroundColor Cyan
        docker rm -f $ContainerName | Out-Host
    }
}

Write-Host 'Collector stopped.' -ForegroundColor Green
