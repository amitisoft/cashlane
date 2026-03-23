# Azure Deployment

This folder contains the cheapest viable Azure deployment path for Cashlane in `centralindia`:

- Azure Database for PostgreSQL Flexible Server: `Burstable B1ms`, `32 GiB`, `Premium_LRS`, `7-day` backups, no HA, no geo-redundant backup
- Azure App Service: Linux `B1`, exactly `1` worker
- Azure Static Web Apps: `Free`

## Files

- `provision-cheapest.ps1`: creates the resource group, PostgreSQL flexible server, Linux App Service plan, web app, and Static Web App
- `set-webapp-settings.ps1`: applies production app settings to the App Service backend
- `migrate-postgres-to-flexible-server.ps1`: freezes local writes, creates a `pg_dump -Fc` backup, and restores it into Azure PostgreSQL

## Provision Resources

```powershell
$dbPassword = Read-Host "PostgreSQL admin password" -AsSecureString

.\deploy\azure\provision-cheapest.ps1 `
  -ResourceGroupName "cashlane-prod-rg" `
  -PostgresServerName "cashlane-prod-pg" `
  -PostgresAdminUser "cashlaneadmin" `
  -PostgresAdminPassword $dbPassword `
  -WebAppName "cashlane-prod-api" `
  -StaticWebAppName "cashlane-prod-web"
```

## Configure App Service Settings

Use the Static Web App hostname printed by the provision script. The app currently handles registration verification and password reset on `/`, so keep `ResetPasswordPath=/`.

```powershell
$dbPassword = Read-Host "PostgreSQL admin password" -AsSecureString

.\deploy\azure\set-webapp-settings.ps1 `
  -ResourceGroupName "cashlane-prod-rg" `
  -WebAppName "cashlane-prod-api" `
  -PostgresServerName "cashlane-prod-pg" `
  -PostgresAdminUser "cashlaneadmin" `
  -PostgresAdminPassword $dbPassword `
  -FrontendBaseUrl "https://<your-static-web-app>.azurestaticapps.net" `
  -JwtSigningKey "<32-plus-character-signing-key>" `
  -SmtpUsername "<smtp-username>" `
  -SmtpPassword "<smtp-password>" `
  -SmtpFromEmail "<from-email>"
```

This script sets:

- `ConnectionStrings__DefaultConnection`
- `Jwt__*`
- `Smtp__*`
- `AppUrls__FrontendBaseUrl`
- `AppUrls__ResetPasswordPath`
- `Demo__EnableDemoSeed=false`
- `Cors__AllowedOrigins__0=https://<static-web-app>`
- `Cors__AllowedOrigins__1=http://localhost:8080`

## GitHub Actions Secrets And Variables

The repo now contains:

- `.github/workflows/deploy-api.yml`
- `.github/workflows/deploy-frontend.yml`

Add these repository variables:

- `CASHLANE_AZURE_WEBAPP_NAME`: the App Service name, for example `cashlane-prod-api`
- `CASHLANE_API_BASE_URL`: the App Service base URL, for example `https://cashlane-prod-api.azurewebsites.net`

Add these repository secrets:

- `AZUREAPPSERVICE_PUBLISHPROFILE_CASHLANE_API`: App Service publish profile XML
- `AZURE_STATIC_WEB_APPS_API_TOKEN_CASHLANE_FRONTEND`: Static Web Apps deployment token

Helpful CLI commands:

```powershell
az webapp deployment list-publishing-profiles `
  --resource-group "cashlane-prod-rg" `
  --name "cashlane-prod-api" `
  --xml

az staticwebapp secrets list `
  --resource-group "cashlane-prod-rg" `
  --name "cashlane-prod-web"
```

## Migrate The Database

The migration script stops `cashlane-web` and `cashlane-api` before taking the dump, then starts them again after restore unless `-KeepSourceStopped` is supplied.

```powershell
$dbPassword = Read-Host "Azure PostgreSQL admin password" -AsSecureString

.\deploy\azure\migrate-postgres-to-flexible-server.ps1 `
  -TargetServerName "cashlane-prod-pg" `
  -TargetAdminUser "cashlaneadmin" `
  -TargetAdminPassword $dbPassword
```

## Notes

- Keep the App Service at one worker. The recurring transaction worker runs in-process and is not safe for scale-out.
- The frontend workflow passes `CASHLANE_API_BASE_URL` into the Vite build. If the value is an absolute hostname, the frontend app now appends `/api` automatically.
- Local Podman and Nginx assets remain unchanged for local or on-prem use.
