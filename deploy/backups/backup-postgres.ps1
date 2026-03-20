$ErrorActionPreference = "Stop"

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backupDir = Join-Path $PSScriptRoot "exports"
New-Item -ItemType Directory -Force -Path $backupDir | Out-Null

if (-not $env:POSTGRES_PASSWORD) {
    $env:POSTGRES_PASSWORD = "postgres"
}

$postgresUser = if ($env:POSTGRES_USER) { $env:POSTGRES_USER } else { "postgres" }
$postgresDb = if ($env:POSTGRES_DB) { $env:POSTGRES_DB } else { "cashlane" }

$file = Join-Path $backupDir "cashlane-$timestamp.dump"

& "C:\Program Files\PostgreSQL\18\bin\pg_dump.exe" `
  -h localhost `
  -p 5432 `
  -U $postgresUser `
  -d $postgresDb `
  -F c `
  -f $file

Write-Output "Backup created at $file"
