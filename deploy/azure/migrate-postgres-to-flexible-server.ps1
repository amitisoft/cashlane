param(
    [Parameter(Mandatory)]
    [string]$TargetServerName,

    [Parameter(Mandatory)]
    [string]$TargetAdminUser,

    [Parameter(Mandatory)]
    [SecureString]$TargetAdminPassword,

    [string]$TargetDatabaseName = "cashlane",
    [string]$SourceHost = "localhost",
    [int]$SourcePort = 5432,
    [string]$SourceDatabaseName = "cashlane",
    [string]$SourceDatabaseUser = "postgres",
    [string]$SourceDatabasePassword = "postgres",
    [string]$SourceApiContainerName = "cashlane-api",
    [string]$SourceWebContainerName = "cashlane-web",
    [string]$BackupDirectory = (Join-Path $PSScriptRoot "..\backups\exports"),
    [switch]$KeepSourceStopped,
    [switch]$SkipSourceFreeze
)

$ErrorActionPreference = "Stop"

function Get-PlainText([SecureString]$Value) {
    return [System.Net.NetworkCredential]::new("", $Value).Password
}

function Resolve-PostgresToolPath([string]$ToolName) {
    $command = Get-Command $ToolName -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $candidate = Get-ChildItem "C:\Program Files\PostgreSQL" -Filter $ToolName -Recurse -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending |
        Select-Object -First 1

    if ($candidate) {
        return $candidate.FullName
    }

    throw "Unable to locate $ToolName. Install PostgreSQL client tools or add them to PATH."
}

function Get-ContainerRunningState([string]$ContainerName) {
    $state = & podman inspect --format '{{.State.Running}}' $ContainerName 2>$null
    if ($LASTEXITCODE -ne 0) {
        return $false
    }

    return $state.Trim().ToLowerInvariant() -eq "true"
}

function Stop-ContainerIfRunning([string]$ContainerName) {
    if (Get-ContainerRunningState $ContainerName) {
        Write-Output "Stopping $ContainerName to freeze writes..."
        & podman stop $ContainerName | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to stop $ContainerName."
        }
        return $true
    }

    return $false
}

function Start-ContainerIfNeeded([string]$ContainerName, [bool]$ShouldStart) {
    if ($ShouldStart) {
        Write-Output "Starting $ContainerName..."
        & podman start $ContainerName | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to start $ContainerName."
        }
    }
}

$pgDumpPath = Resolve-PostgresToolPath "pg_dump.exe"
$pgRestorePath = Resolve-PostgresToolPath "pg_restore.exe"
$targetPassword = Get-PlainText $TargetAdminPassword

New-Item -ItemType Directory -Force -Path $BackupDirectory | Out-Null

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$dumpFile = Join-Path $BackupDirectory "cashlane-azure-$timestamp.dump"
$apiWasStopped = $false
$webWasStopped = $false
$previousPgPassword = $env:PGPASSWORD
$previousPgSslMode = $env:PGSSLMODE

try {
    if (-not $SkipSourceFreeze) {
        $webWasStopped = Stop-ContainerIfRunning $SourceWebContainerName
        $apiWasStopped = Stop-ContainerIfRunning $SourceApiContainerName
    }

    Write-Output "Creating local PostgreSQL dump at $dumpFile..."
    $env:PGPASSWORD = $SourceDatabasePassword
    & $pgDumpPath `
        -h $SourceHost `
        -p $SourcePort `
        -U $SourceDatabaseUser `
        -d $SourceDatabaseName `
        -F c `
        -f $dumpFile

    if ($LASTEXITCODE -ne 0) {
        throw "pg_dump failed."
    }

    Write-Output "Restoring dump into Azure PostgreSQL Flexible Server..."
    $env:PGPASSWORD = $targetPassword
    $env:PGSSLMODE = "require"
    & $pgRestorePath `
        --clean `
        --if-exists `
        --no-owner `
        --no-privileges `
        -h "$TargetServerName.postgres.database.azure.com" `
        -p 5432 `
        -U $TargetAdminUser `
        -d $TargetDatabaseName `
        $dumpFile

    if ($LASTEXITCODE -ne 0) {
        throw "pg_restore failed."
    }

    Write-Output "Database migration completed successfully."
}
finally {
    if ($null -eq $previousPgPassword) {
        Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    }
    else {
        $env:PGPASSWORD = $previousPgPassword
    }

    if ($null -eq $previousPgSslMode) {
        Remove-Item Env:PGSSLMODE -ErrorAction SilentlyContinue
    }
    else {
        $env:PGSSLMODE = $previousPgSslMode
    }

    if (-not $KeepSourceStopped) {
        Start-ContainerIfNeeded $SourceApiContainerName $apiWasStopped
        Start-ContainerIfNeeded $SourceWebContainerName $webWasStopped
    }
}
