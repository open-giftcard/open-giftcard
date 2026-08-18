using System.Collections.Concurrent;
using System.Net;
using System.Net.Mail;
using GiftCardPlatform.Modules.Notifications.Contracts;
using Microsoft.AspNetCore.DataProtection;

namespace GiftCardPlatform.Api.Services;

/// <summary>
/// Protects the outbox's credential-bearing columns with ASP.NET Data
/// Protection.
///
/// The adapter lives in the host, not the module, for the same reason the audit
/// checkpoint signer does: the module should not know where keys come from.
///
/// Data Protection keys must be persisted and shared by every instance, or a
/// restart makes queued activation links undecryptable and they dead-letter.
/// That is already a recorded deployment gate.
/// </summary>
internal sealed class DataProtectionNotificationProtector : INotificationPayloadProtector
{
    private const string Purpose = "GiftCardPlatform.Notifications.OutboxPayload.v1";

    private readonly IDataProtector protector;

    public DataProtectionNotificationProtector(IDataProtectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        protector = provider.CreateProtector(Purpose);
    }

    public string Protect(string plaintext) => protector.Protect(plaintext);

    public string? TryUnprotect(string protectedValue)
    {
        try
        {
            return protector.Unprotect(protectedValue);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Keys rotated away, or the value was tampered with. The dispatcher
            // turns this into a dead letter rather than an exception, so one bad
            // row cannot stall the queue.
            return null;
        }
    }
}

/// <summary>
/// Captures what would have been sent instead of contacting a provider. Used in
/// Development and tests so the whole activation journey can be demonstrated
/// without a mail server.
/// </summary>
internal sealed class CapturingNotificationSender(NotificationChannel channel)
    : INotificationChannelSender
{
    private readonly ConcurrentDictionary<Guid, NotificationMessage> captured = new();

    public NotificationChannel Channel { get; } = channel;

    public IReadOnlyCollection<NotificationMessage> Captured => captured.Values.ToArray();

    public Task<NotificationDeliveryResult> SendAsync(
        NotificationMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        captured[message.Id] = message;
        return Task.FromResult(new NotificationDeliveryResult(true, Retryable: false, null));
    }
}

public sealed class SmtpNotificationOptions
{
    public const string SectionName = "Notifications:Smtp";

    public bool Enabled { get; set; }

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    public bool UseStartTls { get; set; } = true;

    /// <summary>The mailbox messages are sent from, and the SMTP username.</summary>
    public string FromAddress { get; set; } = string.Empty;

    public string FromDisplayName { get; set; } = "Gift Card Platform";

    /// <summary>
    /// Supplied through user secrets or the environment, never committed. For a
    /// personal mailbox this is an application-specific password, not the
    /// account password.
    /// </summary>
    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// Sends real email over SMTP. Intended for demonstrations against an ordinary
/// mailbox, not as the production transport: there is no per-provider bounce
/// handling, suppression list, or reputation management here, and consumer mail
/// providers rate-limit aggressively.
///
/// Failures are reported as retryable so the outbox backs off and tries again;
/// a rejected recipient is permanent and dead-letters instead.
/// </summary>
internal sealed class SmtpNotificationSender(SmtpNotificationOptions options)
    : INotificationChannelSender
{
    public NotificationChannel Channel => NotificationChannel.Email;

    public async Task<NotificationDeliveryResult> SendAsync(
        NotificationMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        using var client = new SmtpClient(options.Host, options.Port)
        {
            EnableSsl = options.UseStartTls,
            Credentials = new NetworkCredential(options.FromAddress, options.Password),
            DeliveryMethod = SmtpDeliveryMethod.Network,
        };

        using var mail = new MailMessage
        {
            From = new MailAddress(options.FromAddress, options.FromDisplayName),
            Subject = message.Subject,
            Body = message.Body,
            IsBodyHtml = false,
        };
        mail.To.Add(message.Recipient);

        try
        {
            await client.SendMailAsync(mail, cancellationToken).ConfigureAwait(false);
            return new NotificationDeliveryResult(true, Retryable: false, null);
        }
        catch (SmtpFailedRecipientException)
        {
            // The address will not start working. Retrying only delays the dead
            // letter that tells someone to look at it.
            return new NotificationDeliveryResult(false, Retryable: false, "smtp.recipient_rejected");
        }
        catch (SmtpException exception)
        {
            var permanent = exception.StatusCode is SmtpStatusCode.MailboxNameNotAllowed
                or SmtpStatusCode.MailboxUnavailable
                or SmtpStatusCode.ClientNotPermitted;
            return new NotificationDeliveryResult(
                false,
                Retryable: !permanent,
                permanent ? "smtp.rejected" : "smtp.transient");
        }
        catch (InvalidOperationException)
        {
            // Misconfiguration, such as an unusable host or port.
            return new NotificationDeliveryResult(false, Retryable: false, "smtp.misconfigured");
        }
    }
}
