using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

public interface IEmailSender
{
    Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

public record EmailMessage(string To, string Subject, string TextBody, string? HtmlBody = null);

public record EmailSendResult(bool Sent, string? DevFilePath = null, string? Error = null);

public sealed class DevSinkEmailSender : IEmailSender
{
    private readonly IWebHostEnvironment _env;
    private readonly ILogger _logger;

    public DevSinkEmailSender(IWebHostEnvironment env, ILogger<DevSinkEmailSender> logger)
    {
        _env = env;
        _logger = logger;
    }

    public async Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        var root = _env.WebRootPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        }

        var outbox = Path.Combine(root, "_outbox");
        Directory.CreateDirectory(outbox);

        var fileName = $"email-{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{Sanitize(message.To)}.eml";
        var fullPath = Path.Combine(outbox, fileName);

        var content = BuildEml(message);
        await File.WriteAllTextAsync(fullPath, content, Encoding.UTF8, cancellationToken);

        _logger.LogInformation("DEV email written to {File}", fullPath);
        return new EmailSendResult(true, fullPath);
    }

    private static string BuildEml(EmailMessage msg)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"To: {msg.To}");
        sb.AppendLine($"Subject: {msg.Subject}");
        sb.AppendLine("Date: " + DateTime.UtcNow.ToString("R"));
        sb.AppendLine("Content-Type: text/plain; charset=utf-8");
        sb.AppendLine();
        sb.AppendLine(msg.TextBody);

        if (!string.IsNullOrWhiteSpace(msg.HtmlBody))
        {
            sb.AppendLine();
            sb.AppendLine("---- html preview ----");
            sb.AppendLine(msg.HtmlBody);
        }

        return sb.ToString();
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) ? '-' : c).ToArray();
        return new string(chars);
    }
}

public sealed class SmtpEmailSender : IEmailSender
{
    private readonly IConfiguration _cfg;
    private readonly ILogger _logger;

    public SmtpEmailSender(IConfiguration cfg, ILogger<SmtpEmailSender> logger)
    {
        _cfg = cfg;
        _logger = logger;
    }

    public async Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        var host = _cfg["Smtp:Host"];
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new InvalidOperationException("SMTP host not configured");
        }

        int port = 25;
        if (int.TryParse(_cfg["Smtp:Port"], out var parsed))
        {
            port = parsed;
        }

        var from = _cfg["Smtp:From"] ?? _cfg["Smtp:User"] ?? "no-reply@localhost";
        var user = _cfg["Smtp:User"];
        var pass = _cfg["Smtp:Pass"];
        var useSsl = bool.TryParse(_cfg["Smtp:UseSsl"], out var ssl) && ssl;

        using var mail = new MailMessage();
        mail.From = new MailAddress(from);
        mail.To.Add(message.To);
        mail.Subject = message.Subject;
        mail.BodyEncoding = Encoding.UTF8;
        mail.SubjectEncoding = Encoding.UTF8;
        mail.Body = message.TextBody;
        mail.IsBodyHtml = false;

        if (!string.IsNullOrWhiteSpace(message.HtmlBody))
        {
            var htmlView = AlternateView.CreateAlternateViewFromString(message.HtmlBody, Encoding.UTF8, "text/html");
            mail.AlternateViews.Add(htmlView);
        }

        using var client = new SmtpClient(host, port)
        {
            EnableSsl = useSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network
        };

        if (!string.IsNullOrWhiteSpace(user))
        {
            client.Credentials = new NetworkCredential(user, pass);
        }

        try
        {
#if NET8_0_OR_GREATER
            await client.SendMailAsync(mail, cancellationToken);
#else
            await client.SendMailAsync(mail);
#endif
            _logger.LogInformation("SMTP email sent to {Recipient}", message.To);
            return new EmailSendResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send SMTP email to {Recipient}", message.To);
            return new EmailSendResult(false, null, ex.Message);
        }
    }
}

public static class EmailValidation
{
    private static readonly Regex Pattern = new("^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool IsValid(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;

        var trimmed = email.Trim();
        if (trimmed.Length > 320) return false;
        if (!Pattern.IsMatch(trimmed)) return false;

        var parts = trimmed.Split('@');
        if (parts.Length != 2) return false;

        var domain = parts[1];
        if (domain.Length < 4 || domain.Length > 200) return false;
        if (domain.Contains(' ')) return false;
        if (!domain.Contains('.')) return false;

        var tld = domain.Split('.').Last();
        if (tld.Length < 2 || tld.Length > 24) return false;

        return true;
    }
}
