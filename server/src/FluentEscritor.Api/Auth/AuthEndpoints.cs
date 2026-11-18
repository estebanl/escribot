using System.Net.Mail;
using FluentEscritor.Api.Configuration;
using FluentEscritor.Api.Data;
using FluentEscritor.Api.Data.Models;
using FluentEscritor.Api.Email;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace FluentEscritor.Api.Auth;

public sealed record SignUpRequest(string? Name, string? Email, string? Password, string? CallbackURL);

public sealed record SignInRequest(string? Email, string? Password);

public sealed record EmailRequest(string? Email, string? CallbackURL, string? RedirectTo);

public sealed record ResetPasswordRequest(string? Token, string? NewPassword);

public sealed record DeleteAccountRequest(string? Password);

public sealed record UserResponse(string Id, string Name, string Email, bool EmailVerified, string? Image, bool HasPassword, bool IsOwner, bool IsAdmin);

/// <summary>
/// The sign-in endpoints, under <c>/api/auth/...</c>.
/// Errors are <c>{ "error": "...", "code": "..." }</c>.
/// </summary>
internal static partial class AuthEndpoints
{
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 128;
    private const int MaxNameLength = 100;
    private const int MaxEmailLength = 254;

    /// <summary>How recently a person without a password must have signed in to delete their account.</summary>
    private static readonly TimeSpan FreshSession = TimeSpan.FromDays(1);

    public static WebApplication MapAuthEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/api/auth").LimitPerAddress("any", 100, TimeSpan.FromMinutes(1));

        auth.MapGet("/config", GetConfig);
        auth.MapGet("/me", GetCurrentUser).RequireAuthorization();
        auth.MapPost("/sign-up/email", SignUpAsync).LimitPerAddress("sign-up", 10, TimeSpan.FromHours(1));
        auth.MapPost("/sign-in/email", SignInAsync).LimitPerAddress("sign-in", 5, TimeSpan.FromMinutes(1));
        auth.MapPost("/sign-out", SignOutAsync);
        auth.MapGet("/verify-email", VerifyEmailAsync);
        auth.MapPost("/send-verification-email", SendVerificationEmailAsync).LimitPerAddress("send-verification", 3, TimeSpan.FromHours(1));
        auth.MapPost("/request-password-reset", RequestPasswordResetAsync).LimitPerAddress("request-reset", 3, TimeSpan.FromHours(1));
        auth.MapPost("/reset-password", ResetPasswordAsync).LimitPerAddress("reset", 5, TimeSpan.FromMinutes(10));
        auth.MapPost("/delete-user", DeleteAccountAsync).RequireAuthorization();
        auth.MapGet("/sign-in/google", SignInWithGoogle);

        return app;
    }

    /// <summary>What the sign-in pages need to know before anyone is signed in.</summary>
    private static IResult GetConfig(AuthSettings settings, EmailSettings email) => Results.Ok(new
    {
        googleEnabled = settings.GoogleEnabled,
        passwordResetAvailable = email.PasswordResetAvailable,
        emailVerificationRequired = email.VerificationRequired,
    });

    private static IResult GetCurrentUser(HttpContext context)
    {
        var session = context.GetSession()!;
        return Results.Ok(new { user = ToResponse(session.Account, session.IsOwner, session.IsAdmin) });
    }

    private static async Task<IResult> SignUpAsync(
        SignUpRequest request,
        HttpContext context,
        AccountService accounts,
        UserManager<AppUser> users,
        SessionManager sessions,
        AuthSettings settings,
        EmailSettings emailSettings,
        AuthEmails emails,
        CancellationToken cancellationToken)
    {
        var email = request.Email?.Trim() ?? string.Empty;
        var name = request.Name?.Trim() ?? string.Empty;
        if (!IsEmailAddress(email))
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "Enter a valid email address.", "INVALID_EMAIL");
        }

        if (PasswordProblem(request.Password) is { } problem)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, problem, "INVALID_PASSWORD");
        }

        if (name.Length > MaxNameLength)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, $"A name can be at most {MaxNameLength} characters long.", "INVALID_NAME");
        }

        var result = await accounts.CreateAsync(new AppUser { Email = email, Name = name }, request.Password, cancellationToken);
        switch (result.Outcome)
        {
            case AccountCreation.NotAllowed:
                return ApiResults.Error(StatusCodes.Status403Forbidden, result.Error!, "SIGNUP_NOT_ALLOWED");

            case AccountCreation.Invalid:
                return ApiResults.Error(StatusCodes.Status400BadRequest, result.Error!, "INVALID_SIGNUP");

            case AccountCreation.EmailTaken when !emailSettings.VerificationRequired:
                return ApiResults.Error(StatusCodes.Status422UnprocessableEntity, result.Error!, "USER_ALREADY_EXISTS");

            case AccountCreation.EmailTaken:
                // Answer exactly as for a new account, so this cannot be used to find out
                // who has one. If the existing account is still unverified, its owner may
                // have lost the first email: send the link again.
                if (await users.FindByEmailAsync(email) is { EmailVerified: false } existing)
                {
                    await emails.TrySendVerificationAsync(context, existing, request.CallbackURL, cancellationToken);
                }

                return Results.Ok(new { user = (UserResponse?)null, verificationRequired = true });
        }

        var user = result.User!;
        if (emailSettings.SendsVerificationLinks)
        {
            await emails.TrySendVerificationAsync(context, user, request.CallbackURL, cancellationToken);
        }

        if (emailSettings.VerificationRequired)
        {
            // No session until the address is verified.
            return Results.Ok(new { user = (UserResponse?)null, verificationRequired = true });
        }

        await sessions.StartAsync(context, user.Id, cancellationToken);
        return Results.Ok(new { user = ToResponse(user, settings), verificationRequired = false });
    }

    private static async Task<IResult> SignInAsync(
        SignInRequest request,
        HttpContext context,
        UserManager<AppUser> users,
        SessionManager sessions,
        AuthSettings settings,
        EmailSettings emailSettings,
        CancellationToken cancellationToken)
    {
        var email = request.Email?.Trim() ?? string.Empty;
        var password = request.Password ?? string.Empty;
        var user = IsEmailAddress(email) && password.Length <= MaxPasswordLength ? await users.FindByEmailAsync(email) : null;

        if (user?.PasswordHash is null)
        {
            // Spend the time a real check takes, so the response does not show whether
            // the address has an account.
            users.PasswordHasher.VerifyHashedPassword(DummyUser, DummyHash.Value, password);
            return InvalidCredentials();
        }

        // Also upgrades the stored hash when the hashing parameters have changed.
        if (!await users.CheckPasswordAsync(user, password))
        {
            return InvalidCredentials();
        }

        if (emailSettings.VerificationRequired && !user.EmailVerified)
        {
            return ApiResults.Error(StatusCodes.Status403Forbidden, "Verify your email address before signing in.", "EMAIL_NOT_VERIFIED");
        }

        await sessions.StartAsync(context, user.Id, cancellationToken);
        return Results.Ok(new { user = ToResponse(user, settings) });
    }

    private static async Task<IResult> SignOutAsync(HttpContext context, SessionManager sessions, CancellationToken cancellationToken)
    {
        await sessions.EndAsync(context, cancellationToken);
        return Results.Ok(new { success = true });
    }

    /// <summary>
    /// The link in the verification email. Verifies the address, signs the person in and
    /// sends them on. Mail scanners and link previews often open a link before the person
    /// does, so opening it again within its hour is not an error: the address stays
    /// verified and the person goes on to the app. Only the first opening signs in.
    /// </summary>
    private static async Task<IResult> VerifyEmailAsync(
        string? token,
        string? callbackURL,
        HttpContext context,
        IOneTimeTokenStore tokens,
        UserManager<AppUser> users,
        SessionManager sessions,
        CancellationToken cancellationToken)
    {
        var destination = LocalPath.OrRoot(callbackURL);
        var userId = string.IsNullOrEmpty(token) ? null : await tokens.FindUserIdAsync(OneTimeTokenPurpose.EmailVerification, token, cancellationToken);
        var user = userId is null ? null : await users.FindByIdAsync(userId);
        if (user is null)
        {
            return Results.Redirect(QueryHelpers.AddQueryString(destination, "error", "invalid_token"));
        }

        if (user.EmailVerified)
        {
            return Results.Redirect(destination);
        }

        user.EmailVerified = true;
        if (!(await users.UpdateAsync(user)).Succeeded)
        {
            return Results.Redirect(QueryHelpers.AddQueryString(destination, "error", "invalid_token"));
        }

        await sessions.StartAsync(context, user.Id, cancellationToken);
        return Results.Redirect(destination);
    }

    private static async Task<IResult> SendVerificationEmailAsync(
        EmailRequest request,
        HttpContext context,
        UserManager<AppUser> users,
        EmailSettings emailSettings,
        AuthEmails emails,
        CancellationToken cancellationToken)
    {
        var email = request.Email?.Trim() ?? string.Empty;
        if (emailSettings.SendsVerificationLinks && IsEmailAddress(email)
            && await users.FindByEmailAsync(email) is { EmailVerified: false } user)
        {
            await emails.TrySendVerificationAsync(context, user, request.CallbackURL, cancellationToken);
        }

        // The same answer whether or not the address has an account.
        return Results.Ok(new { success = true });
    }

    private static async Task<IResult> RequestPasswordResetAsync(
        EmailRequest request,
        HttpContext context,
        UserManager<AppUser> users,
        EmailSettings emailSettings,
        AuthEmails emails,
        CancellationToken cancellationToken)
    {
        if (!emailSettings.PasswordResetAvailable)
        {
            // Without a way to deliver the link, say so rather than pretend an email was sent.
            return ApiResults.Error(StatusCodes.Status400BadRequest, "Password reset is not available.", "RESET_PASSWORD_DISABLED");
        }

        var email = request.Email?.Trim() ?? string.Empty;
        if (IsEmailAddress(email) && await users.FindByEmailAsync(email) is { } user)
        {
            await emails.TrySendPasswordResetAsync(context, user, request.RedirectTo, cancellationToken);
        }

        // The same answer whether or not the address has an account.
        return Results.Ok(new { success = true });
    }

    private static async Task<IResult> ResetPasswordAsync(
        ResetPasswordRequest request,
        UserManager<AppUser> users,
        IOneTimeTokenStore tokens,
        IAuthSessionStore sessionStore,
        EmailSettings emailSettings,
        CancellationToken cancellationToken)
    {
        if (!emailSettings.PasswordResetAvailable)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "Password reset is not available.", "RESET_PASSWORD_DISABLED");
        }

        // Checked before the token is used up, so a rejected password does not cost the link.
        if (PasswordProblem(request.NewPassword) is { } problem)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, problem, "INVALID_PASSWORD");
        }

        var userId = string.IsNullOrEmpty(request.Token) ? null : await tokens.RedeemAsync(OneTimeTokenPurpose.PasswordReset, request.Token, cancellationToken);
        var user = userId is null ? null : await users.FindByIdAsync(userId);
        if (user is null)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "This reset link is invalid or has expired.", "INVALID_TOKEN");
        }

        user.PasswordHash = users.PasswordHasher.HashPassword(user, request.NewPassword!);
        // Changing the security stamp also saves the user.
        if (!(await users.UpdateSecurityStampAsync(user)).Succeeded)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "This reset link is invalid or has expired.", "INVALID_TOKEN");
        }

        // Whoever knew the old password is signed out everywhere, and other reset links stop working.
        await sessionStore.RemoveAllForUserAsync(user.Id, cancellationToken);
        await tokens.RemoveAllForUserAsync(OneTimeTokenPurpose.PasswordReset, user.Id, cancellationToken);

        return Results.Ok(new { success = true });
    }

    private static async Task<IResult> DeleteAccountAsync(
        DeleteAccountRequest request,
        HttpContext context,
        UserManager<AppUser> users,
        SessionManager sessions,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var session = context.GetSession()!;
        var user = await users.FindByIdAsync(session.Account.Id);
        if (user is null)
        {
            return ApiResults.Error(StatusCodes.Status401Unauthorized, ApiResults.UnauthorizedMessage);
        }

        if (user.PasswordHash is not null)
        {
            var password = request.Password ?? string.Empty;
            if (password.Length == 0 || password.Length > MaxPasswordLength || !await users.CheckPasswordAsync(user, password))
            {
                return ApiResults.Error(StatusCodes.Status400BadRequest, "The password is not correct.", "INVALID_PASSWORD");
            }
        }
        else if (time.GetUtcNow() - session.Session.CreatedAt > FreshSession)
        {
            // An account without a password is confirmed by a recent sign-in instead.
            return ApiResults.Error(StatusCodes.Status400BadRequest, "Sign out and sign in again, then delete your account.", "SESSION_NOT_FRESH");
        }

        try
        {
            await users.DeleteAsync(user);
        }
        catch (BillingCancellationException ex)
        {
            return ApiResults.Error(StatusCodes.Status502BadGateway, ex.Message, "BILLING_UNAVAILABLE");
        }

        sessions.ClearCookie(context);
        return Results.Ok(new { success = true });
    }

    private static IResult SignInWithGoogle(string? callbackURL, AuthSettings settings) =>
        settings.GoogleEnabled
            ? Results.Challenge(new AuthenticationProperties { RedirectUri = LocalPath.OrRoot(callbackURL) }, [GoogleDefaults.AuthenticationScheme])
            : ApiResults.Error(StatusCodes.Status404NotFound, "Google sign-in is not enabled.", "PROVIDER_NOT_FOUND");

    private static readonly AppUser DummyUser = new() { Email = "nobody@invalid" };

    private static readonly Lazy<string> DummyHash = new(() => new PasswordHasher<AppUser>().HashPassword(DummyUser, Guid.NewGuid().ToString()));

    private static IResult InvalidCredentials() =>
        ApiResults.Error(StatusCodes.Status401Unauthorized, "Invalid email or password.", "INVALID_EMAIL_OR_PASSWORD");

    private static bool IsEmailAddress(string email) =>
        email.Length is > 0 and <= MaxEmailLength
        && !email.Any(char.IsWhiteSpace)
        && MailAddress.TryCreate(email, out var parsed)
        && parsed.Address == email;

    private static string? PasswordProblem(string? password) => password switch
    {
        null or { Length: < MinPasswordLength } => $"A password must be at least {MinPasswordLength} characters long.",
        { Length: > MaxPasswordLength } => $"A password can be at most {MaxPasswordLength} characters long.",
        _ => null,
    };

    private static UserResponse ToResponse(AppUser user, AuthSettings settings)
    {
        var account = user.ToAccount();
        return ToResponse(account, settings.IsOwner(account), settings.IsAdmin(account));
    }

    private static UserResponse ToResponse(UserAccount account, bool isOwner, bool isAdmin) =>
        new(account.Id, account.Name, account.Email, account.EmailVerified, account.Image, account.PasswordHash is not null, isOwner, isAdmin);
}

/// <summary>The two emails the app sends. Each carries a link that works for one hour.</summary>
internal sealed partial class AuthEmails(
    IEmailSender sender,
    IOneTimeTokenStore tokens,
    IOptions<AppOptions> options,
    TimeProvider time,
    ILogger<AuthEmails> logger)
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(1);

    public Task TrySendVerificationAsync(HttpContext context, AppUser user, string? callbackUrl, CancellationToken cancellationToken) =>
        TrySendAsync(OneTimeTokenPurpose.EmailVerification, user, cancellationToken, token =>
        {
            var link = QueryHelpers.AddQueryString($"{BaseUrl(context)}/api/auth/verify-email", new Dictionary<string, string?>
            {
                ["token"] = token,
                ["callbackURL"] = LocalPath.OrRoot(callbackUrl),
            });
            return new OutgoingEmail(
                user.Email,
                "Verify your FluentEscritor email",
                $"Welcome to FluentEscritor. Confirm your email address by opening this link:\n\n{link}");
        });

    public Task TrySendPasswordResetAsync(HttpContext context, AppUser user, string? redirectTo, CancellationToken cancellationToken) =>
        TrySendAsync(OneTimeTokenPurpose.PasswordReset, user, cancellationToken, token =>
        {
            var page = LocalPath.IsLocal(redirectTo) ? redirectTo! : "/reset-password";
            var link = QueryHelpers.AddQueryString($"{BaseUrl(context)}{page}", "token", token);
            return new OutgoingEmail(
                user.Email,
                "Reset your FluentEscritor password",
                $"Open this link to choose a new password:\n\n{link}\n\nIf you did not request this, you can ignore this email.");
        });

    /// <summary>
    /// A failed delivery is logged, not reported to the caller: the response must not
    /// depend on whether the address has an account, and the person can ask again.
    /// </summary>
    private async Task TrySendAsync(OneTimeTokenPurpose purpose, AppUser user, CancellationToken cancellationToken, Func<string, OutgoingEmail> compose)
    {
        var token = SessionManager.NewToken();
        await tokens.CreateAsync(purpose, token, user.Id, time.GetUtcNow() + TokenLifetime, cancellationToken);
        try
        {
            await sender.SendAsync(compose(token), cancellationToken);
        }
        catch (EmailDeliveryException ex)
        {
            LogDeliveryFailed(logger, purpose, ex.Message);
        }
    }

    /// <summary>
    /// Links use the configured public address. Only outside production, where it may be
    /// unset, do they fall back to the address the request came to.
    /// </summary>
    private string BaseUrl(HttpContext context) =>
        options.Value.PublicUrl?.GetLeftPart(UriPartial.Authority) ?? $"{context.Request.Scheme}://{context.Request.Host}";

    [LoggerMessage(Level = LogLevel.Error, Message = "The {Purpose} email could not be sent. {Reason}")]
    private static partial void LogDeliveryFailed(ILogger logger, OneTimeTokenPurpose purpose, string reason);
}
