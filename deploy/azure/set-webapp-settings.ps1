param(
    [Parameter(Mandatory)]
    [string]$ResourceGroupName,

    [Parameter(Mandatory)]
    [string]$WebAppName,

    [Parameter(Mandatory)]
    [string]$PostgresServerName,

    [Parameter(Mandatory)]
    [string]$PostgresAdminUser,

    [Parameter(Mandatory)]
    [SecureString]$PostgresAdminPassword,

    [Parameter(Mandatory)]
    [string]$FrontendBaseUrl,

    [Parameter(Mandatory)]
    [string]$JwtSigningKey,

    [string]$PostgresDatabaseName = "cashlane",
    [string]$JwtIssuer = "cashlane-api",
    [string]$JwtAudience = "cashlane-web",
    [int]$JwtAccessTokenMinutes = 30,
    [int]$JwtRefreshTokenDays = 30,
    [string]$SmtpHost = "smtp.gmail.com",
    [int]$SmtpPort = 587,
    [bool]$SmtpUseSsl = $true,
    [string]$SmtpUsername = "",
    [string]$SmtpPassword = "",
    [string]$SmtpFromEmail = "",
    [string]$SmtpFromName = "Cashlane",
    [string]$ResetPasswordPath = "/",
    [bool]$EnableDemoSeed = $false,
    [string[]]$AllowedOrigins
)

$ErrorActionPreference = "Stop"

function Get-PlainText([SecureString]$Value) {
    return [System.Net.NetworkCredential]::new("", $Value).Password
}

$null = az account show --output none
if ($LASTEXITCODE -ne 0) {
    throw "Run 'az login' before updating App Service settings."
}

$trimmedFrontendBaseUrl = $FrontendBaseUrl.TrimEnd("/")
$postgresPassword = Get-PlainText $PostgresAdminPassword
$connectionString = "Host=$PostgresServerName.postgres.database.azure.com;Port=5432;Database=$PostgresDatabaseName;Username=$PostgresAdminUser;Password=$postgresPassword;Ssl Mode=Require;Trust Server Certificate=false"

$effectiveAllowedOrigins = if ($AllowedOrigins -and $AllowedOrigins.Count -gt 0) {
    $AllowedOrigins
}
else {
    @($trimmedFrontendBaseUrl, "http://localhost:8080")
}

$settings = @(
    "ASPNETCORE_ENVIRONMENT=Production",
    "ConnectionStrings__DefaultConnection=$connectionString",
    "Jwt__Issuer=$JwtIssuer",
    "Jwt__Audience=$JwtAudience",
    "Jwt__SigningKey=$JwtSigningKey",
    "Jwt__AccessTokenMinutes=$JwtAccessTokenMinutes",
    "Jwt__RefreshTokenDays=$JwtRefreshTokenDays",
    "Smtp__Host=$SmtpHost",
    "Smtp__Port=$SmtpPort",
    "Smtp__UseSsl=$SmtpUseSsl",
    "Smtp__Username=$SmtpUsername",
    "Smtp__Password=$SmtpPassword",
    "Smtp__FromEmail=$SmtpFromEmail",
    "Smtp__FromName=$SmtpFromName",
    "AppUrls__FrontendBaseUrl=$trimmedFrontendBaseUrl",
    "AppUrls__ResetPasswordPath=$ResetPasswordPath",
    "Demo__EnableDemoSeed=$EnableDemoSeed"
)

for ($index = 0; $index -lt $effectiveAllowedOrigins.Count; $index++) {
    $settings += "Cors__AllowedOrigins__${index}=$($effectiveAllowedOrigins[$index])"
}

Write-Output "Applying App Service settings to $WebAppName..."
& az webapp config appsettings set `
    --resource-group $ResourceGroupName `
    --name $WebAppName `
    --settings $settings `
    --output none

if ($LASTEXITCODE -ne 0) {
    throw "Failed to update App Service settings."
}

Write-Output "Applied App Service settings."
Write-Output "Frontend URL: $trimmedFrontendBaseUrl"
Write-Output "Allowed CORS origins: $($effectiveAllowedOrigins -join ', ')"
