#!/usr/bin/env pwsh
<#
.SYNOPSIS
    RepoSyncRadar のローカル検証スクリプト。CI が無い段階の代替。

.DESCRIPTION
    `dotnet build -warnaserror` と `dotnet test --timeout 10m -- --filter-not-trait Category=Manual` を順に走らせ、
    どちらかが赤なら non-zero で終了する。実装プラン §0.4 の完了判定に対応。
#>

[CmdletBinding()]
param(
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    Write-Host '==> dotnet build -warnaserror' -ForegroundColor Cyan
    dotnet build -warnaserror --configuration $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE"
    }

    Write-Host '==> dotnet test --no-build --timeout 10m -- --filter-not-trait Category=Manual' -ForegroundColor Cyan
    dotnet test --no-build --configuration $Configuration --timeout 10m -- --filter-not-trait Category=Manual
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet test failed with exit code $LASTEXITCODE"
    }

    Write-Host 'OK: build green, tests green.' -ForegroundColor Green
}
finally {
    Pop-Location
}
