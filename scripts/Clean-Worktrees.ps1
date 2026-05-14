#!/usr/bin/env pwsh
<#
.SYNOPSIS
    ローカルプレビュー (Step 19.5) が作成した docs worktree をすべて削除する。

.DESCRIPTION
    `DocsRepository.BareCloneDir` 配下に蓄積された worktree を
    `git worktree remove --force` で順に消し、最後に `git worktree prune` で
    管理メタデータを掃除する。アプリ内の「キャッシュをクリーンアップ」ボタンと
    同じ処理を CLI からも実行できるようにしたもの。

    既定のパスは appsettings(.local).json の DocsRepository:BareCloneDir を
    読み取る。明示指定したい場合は -BareCloneDir で上書き。

.EXAMPLE
    ./scripts/Clean-Worktrees.ps1
    ./scripts/Clean-Worktrees.ps1 -BareCloneDir 'C:\github\.cache\docs.git' -WhatIf
#>

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$BareCloneDir
)

$ErrorActionPreference = 'Stop'

if (-not $BareCloneDir) {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $candidates = @(
        (Join-Path $repoRoot 'src/RepoSyncRadar.App/appsettings.local.json'),
        (Join-Path $repoRoot 'src/RepoSyncRadar.App/appsettings.json')
    )
    foreach ($file in $candidates) {
        if (Test-Path $file) {
            try {
                $json = Get-Content -Raw -Path $file | ConvertFrom-Json
                $value = $json.DocsRepository.BareCloneDir
                if ($value) { $BareCloneDir = $value; break }
            } catch {
                Write-Verbose "Failed to parse $file : $_"
            }
        }
    }
}

if (-not $BareCloneDir) {
    throw 'BareCloneDir is not set. Pass -BareCloneDir or configure DocsRepository:BareCloneDir.'
}

if (-not (Test-Path $BareCloneDir)) {
    Write-Host "BareCloneDir does not exist: $BareCloneDir (nothing to do)." -ForegroundColor Yellow
    return
}

Push-Location $BareCloneDir
try {
    Write-Host "==> git worktree list --porcelain ($BareCloneDir)" -ForegroundColor Cyan
    $porcelain = git worktree list --porcelain
    if ($LASTEXITCODE -ne 0) {
        throw "git worktree list failed with exit code $LASTEXITCODE"
    }

    # Split into blank-line-separated stanzas; ignore the 'bare' entry (BareCloneDir
    # itself), keep only entries that have a 'worktree <path>' line.
    $stanzas = ($porcelain -join "`n") -split "`r?`n`r?`n"
    $paths = foreach ($stanza in $stanzas) {
        # PowerShell -match defaults to single-line mode, so check the 'bare' line
        # explicitly per stanza-line instead of relying on ^ / $ anchors.
        $lines = $stanza -split "`r?`n"
        if ($lines | Where-Object { $_.Trim() -eq 'bare' }) { continue }
        $match = [regex]::Match($stanza, '(?m)^worktree\s+(.+)$')
        if ($match.Success) { $match.Groups[1].Value.Trim() }
    }

    if (-not $paths) {
        Write-Host 'No worktrees to remove.' -ForegroundColor Green
        return
    }

    $removed = 0
    foreach ($p in $paths) {
        if ($PSCmdlet.ShouldProcess($p, 'git worktree remove --force')) {
            git worktree remove --force -- $p
            if ($LASTEXITCODE -eq 0) {
                $removed++
                Write-Host "  removed: $p" -ForegroundColor DarkGray
            } else {
                Write-Warning "  failed (exit $LASTEXITCODE): $p"
            }
        }
    }

    if ($PSCmdlet.ShouldProcess($BareCloneDir, 'git worktree prune')) {
        git worktree prune
    }

    Write-Host "OK: $removed worktree(s) removed." -ForegroundColor Green
}
finally {
    Pop-Location
}
