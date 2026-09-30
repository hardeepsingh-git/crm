<#
.SYNOPSIS
  Starts the CRM API and UI for local development, each in its own PowerShell window.
.DESCRIPTION
  API: http://localhost:5290 (API docs at /scalar). UI: http://localhost:4200.
  Close the windows (or press Ctrl+C in them) to stop.
#>
$root = Split-Path $PSScriptRoot -Parent
$shell = (Get-Process -Id $PID).Path

Start-Process $shell -ArgumentList '-NoExit', '-Command', "`$Host.UI.RawUI.WindowTitle = 'CRM API'; dotnet run --project '$root\backend\src\Crm.Api' --launch-profile http"
Start-Process $shell -ArgumentList '-NoExit', '-Command', "`$Host.UI.RawUI.WindowTitle = 'CRM UI'; Set-Location '$root\frontend'; npm start"

Write-Host 'Starting API (http://localhost:5290) and UI (http://localhost:4200) in new windows...'
Write-Host 'Waiting for the API to become ready' -NoNewline
for ($i = 0; $i -lt 90; $i++) {
    try {
        if ((Invoke-WebRequest http://localhost:5290/health/ready -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200) {
            Write-Host "`nAPI is ready. Open http://localhost:4200" -ForegroundColor Green
            return
        }
    } catch { }
    Write-Host '.' -NoNewline
    Start-Sleep -Seconds 2
}
Write-Warning "`nAPI did not report ready within 3 minutes. Check the 'CRM API' window for errors."
