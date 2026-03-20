using System.Net;
using System.Net.Mail;
using Cashlane.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Cashlane.Api.Infrastructure.Email;

public interface IEmailService
{
    Task SendPasswordResetAsync(string email, string displayName, string resetUrl, CancellationToken cancellationToken = default);
    Task SendRegistrationVerificationAsync(string email, string displayName, string verificationUrl, CancellationToken cancellationToken = default);
}

public sealed class SmtpEmailService(
    IOptions<SmtpOptions> options,
    ILogger<SmtpEmailService> logger) : IEmailService
{
    private readonly SmtpOptions _options = options.Value;

    public async Task SendPasswordResetAsync(string email, string displayName, string resetUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.Username) ||
            string.IsNullOrWhiteSpace(_options.Password) ||
            string.IsNullOrWhiteSpace(_options.FromEmail))
        {
            logger.LogWarning("SMTP is not configured. Password reset URL for {Email}: {ResetUrl}", email, resetUrl);
            return;
        }

        using var message = new MailMessage
        {
            From = new MailAddress(_options.FromEmail, _options.FromName),
            Subject = "Reset your Cashlane password",
            Body = $"Hello {displayName},\n\nUse the link below to reset your Cashlane password:\n{resetUrl}\n\nIf you did not request this, you can ignore this message.",
            IsBodyHtml = false
        };
        message.To.Add(email);

        using var client = new SmtpClient(_options.Host, _options.Port)
        {
            EnableSsl = _options.UseSsl,
            Credentials = new NetworkCredential(_options.Username, _options.Password)
        };

        cancellationToken.ThrowIfCancellationRequested();
        await client.SendMailAsync(message, cancellationToken);
    }

    public async Task SendRegistrationVerificationAsync(string email, string displayName, string verificationUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.Username) ||
            string.IsNullOrWhiteSpace(_options.Password) ||
            string.IsNullOrWhiteSpace(_options.FromEmail))
        {
            logger.LogWarning("SMTP is not configured. Registration verification URL for {Email}: {VerificationUrl}", email, verificationUrl);
            return;
        }

        using var message = new MailMessage
        {
            From = new MailAddress(_options.FromEmail, _options.FromName),
            Subject = "Verify your Cashlane email",
            Body =
                $"Hello {displayName},\n\nUse the link below to verify your email and finish creating your Cashlane account:\n{verificationUrl}\n\nIf you did not request this, you can ignore this message.",
            IsBodyHtml = false
        };
        message.To.Add(email);

        using var client = new SmtpClient(_options.Host, _options.Port)
        {
            EnableSsl = _options.UseSsl,
            Credentials = new NetworkCredential(_options.Username, _options.Password)
        };

        cancellationToken.ThrowIfCancellationRequested();
        await client.SendMailAsync(message, cancellationToken);
    }
}
