[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$TargetPath
)

$resolvedTargetPath = [System.IO.Path]::GetFullPath($TargetPath)
$processes = Get-CimInstance Win32_Process -Filter "Name = 'RepoSyncRadar.exe'" -ErrorAction SilentlyContinue |
    Where-Object {
        $_.ExecutablePath -and
        ([System.IO.Path]::GetFullPath($_.ExecutablePath) -ieq $resolvedTargetPath)
    }

foreach ($process in $processes) {
    try {
        Stop-Process -Id $process.ProcessId -Force -ErrorAction Stop
        Write-Host "Stopped RepoSyncRadar PID $($process.ProcessId) at $resolvedTargetPath"
    }
    catch {
        Write-Warning "Failed to stop RepoSyncRadar PID $($process.ProcessId): $($_.Exception.Message)"
    }
}