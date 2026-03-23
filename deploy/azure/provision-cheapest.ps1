param(
    [Parameter(Mandatory)]
    [string]$ResourceGroupName,

    [Parameter(Mandatory)]
    [string]$PostgresServerName,

    [Parameter(Mandatory)]
    [string]$WebAppName,

    [Parameter(Mandatory)]
    [string]$StaticWebAppName,

    [Parameter(Mandatory)]
    [string]$PostgresAdminUser,

    [Parameter(Mandatory)]
    [SecureString]$PostgresAdminPassword,

    [string]$Location = "centralindia",
    [string]$PostgresDatabaseName = "cashlane",
    [string]$AppServicePlanName = "cashlane-prod-plan",
    [string]$ClientIpAddress,
    [string[]]$Tags = @(
        "environment=production",
        "application=cashlane",
        "region=centralindia",
        "cost-profile=cheapest"
    )
)

$ErrorActionPreference = "Stop"

function Get-PlainText([SecureString]$Value) {
    return [System.Net.NetworkCredential]::new("", $Value).Password
}

function Invoke-AzCli {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    & az @Arguments

    if ($LASTEXITCODE -ne 0) {
        throw "Azure CLI command failed: az $($Arguments -join ' ')"
    }
}

function Get-PublicIpAddress {
    try {
        return (Invoke-RestMethod -Uri "https://api.ipify.org?format=json" -TimeoutSec 15).ip
    }
    catch {
        Write-Warning "Unable to detect the current public IP address automatically."
        return $null
    }
}

$null = az account show --output none
if ($LASTEXITCODE -ne 0) {
    throw "Run 'az login' before provisioning Azure resources."
}

$postgresPassword = Get-PlainText $PostgresAdminPassword
$effectiveClientIp = if ($ClientIpAddress) { $ClientIpAddress } else { Get-PublicIpAddress }

Write-Output "Creating resource group $ResourceGroupName in $Location..."
Invoke-AzCli -Arguments (@(
    "group", "create",
    "--name", $ResourceGroupName,
    "--location", $Location,
    "--tags"
) + $Tags + @(
    "--output", "none"
))

Write-Output "Creating PostgreSQL Flexible Server $PostgresServerName..."
Invoke-AzCli -Arguments (@(
    "postgres", "flexible-server", "create",
    "--resource-group", $ResourceGroupName,
    "--name", $PostgresServerName,
    "--location", $Location,
    "--admin-user", $PostgresAdminUser,
    "--admin-password", $postgresPassword,
    "--tier", "Burstable",
    "--sku-name", "Standard_B1ms",
    "--storage-size", "32",
    "--storage-type", "Premium_LRS",
    "--backup-retention", "7",
    "--storage-auto-grow", "Disabled",
    "--zonal-resiliency", "Disabled",
    "--geo-redundant-backup", "Disabled",
    "--public-access", "None",
    "--version", "16",
    "--tags"
) + $Tags + @(
    "--output", "none",
    "--yes"
))

Write-Output "Creating PostgreSQL database $PostgresDatabaseName..."
Invoke-AzCli -Arguments @(
    "postgres", "flexible-server", "db", "create",
    "--resource-group", $ResourceGroupName,
    "--server-name", $PostgresServerName,
    "--database-name", $PostgresDatabaseName,
    "--output", "none"
)

Write-Output "Allowing Azure-hosted clients to reach PostgreSQL..."
Invoke-AzCli -Arguments @(
    "postgres", "flexible-server", "firewall-rule", "create",
    "--resource-group", $ResourceGroupName,
    "--name", $PostgresServerName,
    "--rule-name", "allow-azure-services",
    "--start-ip-address", "0.0.0.0",
    "--end-ip-address", "0.0.0.0",
    "--output", "none"
)

if ($effectiveClientIp) {
    Write-Output "Allowing operator IP $effectiveClientIp to reach PostgreSQL..."
    Invoke-AzCli -Arguments @(
        "postgres", "flexible-server", "firewall-rule", "create",
        "--resource-group", $ResourceGroupName,
        "--name", $PostgresServerName,
        "--rule-name", "allow-operator-ip",
        "--start-ip-address", $effectiveClientIp,
        "--end-ip-address", $effectiveClientIp,
        "--output", "none"
    )
}

Write-Output "Creating Linux App Service plan $AppServicePlanName..."
Invoke-AzCli -Arguments (@(
    "appservice", "plan", "create",
    "--resource-group", $ResourceGroupName,
    "--name", $AppServicePlanName,
    "--location", $Location,
    "--is-linux",
    "--sku", "B1",
    "--number-of-workers", "1",
    "--tags"
) + $Tags + @(
    "--output", "none"
))

Write-Output "Creating App Service web app $WebAppName..."
Invoke-AzCli -Arguments (@(
    "webapp", "create",
    "--resource-group", $ResourceGroupName,
    "--plan", $AppServicePlanName,
    "--name", $WebAppName,
    "--https-only", "true",
    "--tags"
) + $Tags + @(
    "--output", "none"
))

Write-Output "Configuring App Service runtime and keeping the worker warm..."
Invoke-AzCli -Arguments @(
    "webapp", "config", "set",
    "--resource-group", $ResourceGroupName,
    "--name", $WebAppName,
    "--linux-fx-version", "DOTNETCORE|8.0",
    "--always-on", "true",
    "--http20-enabled", "true",
    "--min-tls-version", "1.2",
    "--number-of-workers", "1",
    "--output", "none"
)

Write-Output "Creating Static Web App $StaticWebAppName..."
Invoke-AzCli -Arguments (@(
    "staticwebapp", "create",
    "--resource-group", $ResourceGroupName,
    "--name", $StaticWebAppName,
    "--location", $Location,
    "--sku", "Free",
    "--tags"
) + $Tags + @(
    "--output", "none"
))

$webAppHostname = az webapp show --resource-group $ResourceGroupName --name $WebAppName --query "defaultHostName" --output tsv
$staticWebHostname = az staticwebapp show --resource-group $ResourceGroupName --name $StaticWebAppName --query "defaultHostname" --output tsv

Write-Output ""
Write-Output "Provisioning complete."
Write-Output "App Service URL: https://$webAppHostname"
Write-Output "Static Web App URL: https://$staticWebHostname"
Write-Output "PostgreSQL host: $PostgresServerName.postgres.database.azure.com"
Write-Output ""
Write-Output "Next steps:"
Write-Output "1. Run deploy\\azure\\set-webapp-settings.ps1 with your JWT, SMTP, DB, and frontend URL values."
Write-Output "2. Add GitHub repository variables and secrets for the two deployment workflows."
Write-Output "3. Run deploy\\azure\\migrate-postgres-to-flexible-server.ps1 after freezing writes."
