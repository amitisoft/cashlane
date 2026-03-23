using System.ComponentModel.DataAnnotations;

namespace Cashlane.Api.Configuration;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required]
    public string Issuer { get; set; } = string.Empty;

    [Required]
    public string Audience { get; set; } = string.Empty;

    [Required]
    [MinLength(32)]
    public string SigningKey { get; set; } = string.Empty;

    [Range(5, 1440)]
    public int AccessTokenMinutes { get; set; } = 30;

    [Range(1, 365)]
    public int RefreshTokenDays { get; set; } = 30;
}

public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    [Required]
    public string Host { get; set; } = string.Empty;

    [Range(1, 65535)]
    public int Port { get; set; } = 587;

    public bool UseSsl { get; set; }

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string FromEmail { get; set; } = string.Empty;

    public string FromName { get; set; } = string.Empty;
}

public sealed class DemoOptions
{
    public const string SectionName = "Demo";

    public bool EnableDemoSeed { get; set; } = true;

    [Required]
    public string DemoEmail { get; set; } = string.Empty;

    [Required]
    public string DemoPassword { get; set; } = string.Empty;

    [Required]
    public string DemoDisplayName { get; set; } = string.Empty;
}

public sealed class AppUrlOptions
{
    public const string SectionName = "AppUrls";

    [Required]
    public string FrontendBaseUrl { get; set; } = "http://localhost:8080";

    [Required]
    public string ResetPasswordPath { get; set; } = "/reset-password";
}

public sealed class CorsOptions
{
    public const string SectionName = "Cors";

    [MinLength(1)]
    public string[] AllowedOrigins { get; set; } = ["http://localhost:8080"];
}
