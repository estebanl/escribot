using System.Net.Mail;
using Azure;
using Azure.Communication.Email;

namespace FluentEscritor.Api.Email;

public sealed record OutgoingEmail(string To, string Subject, string Text);

/// <summary>Sends the app's emails: the verification link and the password reset link.</summary>
public interface IEmailSender
{
    /// <summary>Throws <see cref="EmailDeliveryException"/> if the message was not accepted for delivery.</summary>
    Task SendAsync(OutgoingEmail email, CancellationToken cancellationToken = default);
}

/// <summary>Its message never contains connection details or the content of the email.</summary>
public sealed class EmailDeliveryException(string message) : Exception(message);

/// <summary>
/// What the app can do with email, decided at startup from
/// <c>AZURE_COMMUNICATION_CONNECTION_STRING</c> and <c>EMAIL_FROM</c>. Both must be set.
/// </summary>
public sealed class EmailSettings
{
    private readonly bool _isProduction;

    public EmailSettings(IConfiguration configuration, IHostEnvironment environment)
    {
        ConnectionString = configuration["AZURE_COMMUNICATION_CONNECTION_STRING"]?.Trim() ?? string.Empty;
        SenderAddress = BareAddress(configuration["EMAIL_FROM"]);
        _isProduction = environment.IsProduction();
    }

    public string ConnectionString { get; }

    /// <summary>Empty when <c>EMAIL_FROM</c> is not set or holds no address.</summary>
    public string SenderAddress { get; }

    public bool IsConfigured => ConnectionString.Length > 0 && SenderAddress.Length > 0;

    /// <summary>New accounts must verify their address only when the email can actually be delivered.</summary>
    public bool VerificationRequired => IsConfigured;

    /// <summary>Outside production the link goes to the server console instead.</summary>
    public bool SendsVerificationLinks => IsConfigured || !_isProduction;

    /// <summary>Reset needs a way to deliver the link: real email, or the console outside production.</summary>
    public bool PasswordResetAvailable => IsConfigured || !_isProduction;

    /// <summary>
    /// Azure Communication Services accepts only a bare address as the sender, so
    /// <c>Name &lt;address@domain&gt;</c> is reduced to the address. The display name is
    /// set on the MailFrom address in Azure.
    /// </summary>
    internal static string BareAddress(string? value) =>
        MailAddress.TryCreate(value?.Trim(), out var parsed) ? parsed.Address : string.Empty;
}

/// <summary>Delivers through Azure Communication Services Email.</summary>
internal sealed class AzureCommunicationEmailSender : IEmailSender
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly EmailClient _client;
    private readonly string _senderAddress;

    public AzureCommunicationEmailSender(EmailSettings settings)
    {
        try
        {
            _client = new EmailClient(settings.ConnectionString);
        }
        catch (Exception)
        {
            // The original message is dropped: it can quote parts of the value.
            throw new InvalidOperationException("AZURE_COMMUNICATION_CONNECTION_STRING is not a valid connection string.");
        }

        _senderAddress = settings.SenderAddress;
    }

    public async Task SendAsync(OutgoingEmail email, CancellationToken cancellationToken = default)
    {
        var message = new EmailMessage(_senderAddress, email.To, new EmailContent(email.Subject) { PlainText = email.Text });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            // Returns once Azure has accepted the message. Waiting for final delivery
            // can take many seconds and would stall sign-up and reset requests.
            await _client.SendAsync(WaitUntil.Started, message, timeout.Token);
        }
        catch (RequestFailedException ex)
        {
            throw new EmailDeliveryException($"Email delivery failed ({ex.Status}).");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new EmailDeliveryException("Email delivery failed (timed out).");
        }
    }
}

/// <summary>
/// Used outside production when email is not configured: prints the message, link
/// included, so it can be copied from the terminal. It writes to standard output
/// directly, not through logging, so the link never enters telemetry.
/// </summary>
internal sealed class ConsoleEmailSender : IEmailSender
{
    public Task SendAsync(OutgoingEmail email, CancellationToken cancellationToken = default)
    {
        Console.Out.WriteLine($"[email not configured] To: {email.To}\nSubject: {email.Subject}\n\n{email.Text}\n");
        return Task.CompletedTask;
    }
}

/// <summary>Used in production when email is not configured. Links are never written to production logs.</summary>
internal sealed partial class DisabledEmailSender(ILogger<DisabledEmailSender> logger) : IEmailSender
{
    public Task SendAsync(OutgoingEmail email, CancellationToken cancellationToken = default)
    {
        LogNotSent(logger, email.Subject);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Email \"{Subject}\" was not sent: set AZURE_COMMUNICATION_CONNECTION_STRING and EMAIL_FROM.")]
    private static partial void LogNotSent(ILogger logger, string subject);
}

internal static class EmailExtensions
{
    public static IHostApplicationBuilder AddEmail(this IHostApplicationBuilder builder)
    {
        builder.Services.AddSingleton<EmailSettings>();
        builder.Services.AddSingleton<IEmailSender>(services =>
        {
            var settings = services.GetRequiredService<EmailSettings>();
            if (settings.IsConfigured)
            {
                return new AzureCommunicationEmailSender(settings);
            }

            return services.GetRequiredService<IHostEnvironment>().IsProduction()
                ? ActivatorUtilities.CreateInstance<DisabledEmailSender>(services)
                : new ConsoleEmailSender();
        });

        return builder;
    }

    /// <summary>Creates the sender before the app accepts requests, so a malformed connection string stops the app at startup.</summary>
    public static WebApplication EnsureEmailConfigured(this WebApplication app)
    {
        app.Services.GetRequiredService<IEmailSender>();
        return app;
    }
}
