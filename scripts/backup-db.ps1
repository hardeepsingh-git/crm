<#
.SYNOPSIS
  Backs up the CRM PostgreSQL database with pg_dump (custom format) and prunes old backups.
.DESCRIPTION
  Connection details come from environment variables (ConnectionStrings__Crm, Database__Password), which is how
  servers are configured, or else from the API's user-secrets on a developer machine. The password is passed to
  pg_dump through PGPASSWORD for this process only and is never written to the console or the command line.
  Restore with: pg_restore --clean --if-exists --no-owner -d <database> <file>
.EXAMPLE
  ./scripts/backup-db.ps1
  ./scripts/backup-db.ps1 -OutputDir D:\Backups\crm -RetentionDays 30
#>
[CmdletBinding()]
param(
    [string]$OutputDir = $(if ($env:CRM_BACKUP_DIR) { $env:CRM_BACKUP_DIR } else { Join-Path $PSScriptRoot '..\backups' }),
    [ValidateRange(1, 3650)][int]$RetentionDays = 14,
    [string]$PgDump = 'pg_dump'
)

$ErrorActionPreference = 'Stop'

$connection = $env:ConnectionStrings__Crm
$password = $env:Database__Password
if (-not $connection) {
    $csproj = Join-Path $PSScriptRoot '..\backend\src\Crm.Api\Crm.Api.csproj'
    $id = ([xml](Get-Content $csproj)).Project.PropertyGroup.UserSecretsId | Where-Object { $_ } | Select-Object -First 1
    $path = Join-Path $env:APPDATA "Microsoft\UserSecrets\$id\secrets.json"
    if (-not (Test-Path $path)) { throw 'No connection configured: set ConnectionStrings__Crm (and Database__Password) or the API user-secrets.' }
    $secrets = Get-Content $path -Raw | ConvertFrom-Json
    $connection = $secrets.'ConnectionStrings:Crm'
    if (-not $password) { $password = $secrets.'Database:Password' }
}

$parts = @{}
foreach ($pair in $connection -split ';' | Where-Object { $_ -match '=' }) {
    $key, $value = $pair -split '=', 2
    $parts[$key.Trim().ToLowerInvariant() -replace '\s', ''] = $value.Trim()
}
$dbHost = $parts['host']; $port = if ($parts['port']) { $parts['port'] } else { '5432' }
$database = $parts['database']; $user = if ($parts['username']) { $parts['username'] } else { $parts['userid'] }
if (-not $password) { $password = $parts['password'] }
if (-not ($dbHost -and $database -and $user)) { throw 'The connection string needs Host, Database and Username.' }

if (-not (Get-Command $PgDump -ErrorAction SilentlyContinue)) {
    throw "pg_dump not found. Install the PostgreSQL client tools (same major version as the server) or pass -PgDump <path>."
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$file = Join-Path $OutputDir ("{0}-{1:yyyyMMdd-HHmmss}.dump" -f $database, (Get-Date))

Write-Host "Backing up '$database' on $($dbHost):$port to $file"
$env:PGPASSWORD = $password
try {
    & $PgDump --format=custom --no-owner --host=$dbHost --port=$port --username=$user --dbname=$database --file=$file
    if ($LASTEXITCODE -ne 0) { throw "pg_dump failed with exit code $LASTEXITCODE." }
} finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
}

if ((Get-Item $file).Length -eq 0) { throw 'The backup file is empty.' }
$pgRestore = Join-Path (Split-Path (Get-Command $PgDump).Source) 'pg_restore'
if (Get-Command $pgRestore -ErrorAction SilentlyContinue) {
    & $pgRestore --list $file | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'The backup archive could not be read back by pg_restore.' }
}
Write-Host ("Backup complete: {0:N1} MB" -f ((Get-Item $file).Length / 1MB)) -ForegroundColor Green

$cutoff = (Get-Date).AddDays(-$RetentionDays)
Get-ChildItem $OutputDir -Filter "$database-*.dump" | Where-Object LastWriteTime -lt $cutoff | ForEach-Object {
    Remove-Item $_.FullName
    Write-Host "Removed old backup $($_.Name)"
}
