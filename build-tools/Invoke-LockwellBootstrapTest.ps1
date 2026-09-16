# Runs Lockwell bootstrap checks before ship. Never deletes user vault data.
param(
    [Parameter(Mandatory = $true)]
    [string] $ExePath,
    [ValidateSet("Smoke", "Ui")]
    [string] $Mode = "Smoke",
    [int] $UiWaitSeconds = 10
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $ExePath)) {
    throw "Missing exe: $ExePath"
}

Get-Process Lockwell -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

$tempCrashLog = Join-Path $env:TEMP "Lockwell-last-crash.txt"
Remove-Item $tempCrashLog -Force -ErrorAction SilentlyContinue

try {
    if ($Mode -eq "Smoke") {
        $proc = Start-Process -FilePath $ExePath -ArgumentList "--smoke-test" -PassThru -WindowStyle Hidden
        if (-not $proc.WaitForExit(120000)) {
            try { Stop-Process -Id $proc.Id -Force } catch {}
            throw "Lockwell smoke test timed out."
        }

        if ($proc.ExitCode -ne 0) {
            $detail = if (Test-Path $tempCrashLog) { Get-Content $tempCrashLog -Raw } else { "(no crash log)" }
            throw "Lockwell smoke test failed (exit $($proc.ExitCode)).`n$detail"
        }
    } else {
        $proc = Start-Process -FilePath $ExePath -PassThru -WindowStyle Normal
        Start-Sleep -Seconds $UiWaitSeconds

        if ($proc.HasExited -and (Test-Path $tempCrashLog)) {
            $detail = Get-Content $tempCrashLog -Raw
            throw "Lockwell exited during UI bootstrap (code $($proc.ExitCode)).`n$detail"
        }

        if ($proc.HasExited -and $proc.ExitCode -ne 0) {
            $detail = if (Test-Path $tempCrashLog) { Get-Content $tempCrashLog -Raw } else { "(no crash log)" }
            throw "Lockwell exited during UI bootstrap (code $($proc.ExitCode)).`n$detail"
        }

        try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch {}
    }

    Write-Host "Bootstrap OK ($Mode)"
} finally {
    Remove-Item $tempCrashLog -Force -ErrorAction SilentlyContinue
}
