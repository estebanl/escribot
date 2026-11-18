using Escribot.Api.Data.Models;

namespace Escribot.Api.Auth;

/// <summary>
/// Who may sign up and who is the owner or an admin, read once at startup from
/// <c>OWNER_EMAIL</c>, <c>ALLOWED_SIGNUP_EMAILS</c>, <c>ADMIN_EMAILS</c>,
/// <c>GOOGLE_CLIENT_ID</c> and <c>GOOGLE_CLIENT_SECRET</c>.
/// </summary>
public sealed class AuthSettings
{
    private readonly HashSet<string> _allowedSignupEmails;
    private readonly HashSet<string> _adminEmails;

    public AuthSettings(IConfiguration configuration)
    {
        OwnerEmail = Normalize(configuration["OWNER_EMAIL"]);
        _allowedSignupEmails = EmailList(configuration["ALLOWED_SIGNUP_EMAILS"]);
        _adminEmails = EmailList(configuration["ADMIN_EMAILS"]);
        GoogleClientId = configuration["GOOGLE_CLIENT_ID"]?.Trim() ?? string.Empty;
        GoogleClientSecret = configuration["GOOGLE_CLIENT_SECRET"]?.Trim() ?? string.Empty;
    }

    /// <summary>Lower-cased, or empty when there is no owner.</summary>
    public string OwnerEmail { get; }

    public string GoogleClientId { get; }

    public string GoogleClientSecret { get; }

    public bool GoogleEnabled => GoogleClientId.Length > 0 && GoogleClientSecret.Length > 0;

    /// <summary>With an empty allowlist anyone may sign up. The owner always may.</summary>
    public bool IsSignupAllowed(string email)
    {
        var candidate = Normalize(email);
        return _allowedSignupEmails.Count == 0
            || _allowedSignupEmails.Contains(candidate)
            || (OwnerEmail.Length > 0 && candidate == OwnerEmail);
    }

    /// <summary>Listed in <c>ALLOWED_SIGNUP_EMAILS</c>: trusted like a verified address for Free use of the platform's AI keys.</summary>
    public bool IsInvited(string email) => _allowedSignupEmails.Contains(Normalize(email));

    /// <summary>
    /// The owner address counts only once it is verified. Otherwise anyone could sign up
    /// with the owner's address first and take the owner's place.
    /// </summary>
    public bool IsOwner(UserAccount account) =>
        account.EmailVerified && OwnerEmail.Length > 0 && Normalize(account.Email) == OwnerEmail;

    /// <summary>The verified owner, or a verified address listed in <c>ADMIN_EMAILS</c>.</summary>
    public bool IsAdmin(UserAccount account) =>
        IsOwner(account) || (account.EmailVerified && _adminEmails.Contains(Normalize(account.Email)));

    private static string Normalize(string? email) => email?.Trim().ToLowerInvariant() ?? string.Empty;

    private static HashSet<string> EmailList(string? value) =>
        (value ?? string.Empty).Split(',').Select(Normalize).Where(e => e.Length > 0).ToHashSet();
}
