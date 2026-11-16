using System.Security.Claims;
using FluentEscritor.Api.Configuration;
using FluentEscritor.Api.Email;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.Extensions.Options;

namespace FluentEscritor.Api.Auth;

/// <summary>"Continue with Google". Registered only when both Google settings are present.</summary>
internal static class GoogleSignIn
{
    public const string Provider = "google";

    /// <summary>The redirect URI registered with Google is the public URL followed by this path.</summary>
    public const string CallbackPath = "/api/auth/callback/google";

    private const string EmailVerifiedClaim = "email_verified";
    private const string PictureClaim = "picture";
    private const string HostedDomainClaim = "hd";

    public static AuthenticationBuilder AddGoogleSignIn(this AuthenticationBuilder builder, AuthSettings settings)
    {
        builder.AddGoogle(options =>
        {
            options.ClientId = settings.GoogleClientId;
            options.ClientSecret = settings.GoogleClientSecret;
            options.CallbackPath = CallbackPath;
            options.ClaimActions.MapJsonKey(EmailVerifiedClaim, "email_verified");
            options.ClaimActions.MapJsonKey(PictureClaim, "picture");
            options.ClaimActions.MapJsonKey(HostedDomainClaim, "hd");
            options.Events.OnTicketReceived = CompleteAsync;
            options.Events.OnRemoteFailure = context =>
            {
                // The person cancelled at Google, or the round trip was tampered with or expired.
                context.Response.Redirect("/login?error=google_sign_in_failed");
                context.HandleResponse();
                return Task.CompletedTask;
            };
        });

        builder.Services.AddOptions<GoogleOptions>(GoogleDefaults.AuthenticationScheme)
            .Configure<IOptions<AppOptions>>((options, app) =>
                options.DataProtectionProvider = new SecretDataProtectionProvider(app.Value.EncryptionSecret));

        return builder;
    }

    /// <summary>Google has confirmed who the person is: open one of our own sessions for them.</summary>
    private static async Task CompleteAsync(TicketReceivedContext context)
    {
        var http = context.HttpContext;
        var principal = context.Principal;
        var subject = principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var email = principal?.FindFirstValue(ClaimTypes.Email);
        var emailVerified = string.Equals(principal?.FindFirstValue(EmailVerifiedClaim), "true", StringComparison.OrdinalIgnoreCase);

        context.HandleResponse();

        if (string.IsNullOrEmpty(subject) || string.IsNullOrEmpty(email) || !emailVerified)
        {
            http.Response.Redirect("/login?error=google_email_not_verified");
            return;
        }

        var result = await http.RequestServices.GetRequiredService<AccountService>().SignInExternalAsync(
            Provider,
            subject,
            email,
            IsGoogleAuthoritativeFor(email, principal!.FindFirstValue(HostedDomainClaim)),
            principal!.FindFirstValue(ClaimTypes.Name),
            principal!.FindFirstValue(PictureClaim),
            http.RequestAborted);

        if (result.User is null)
        {
            http.Response.Redirect(result.Outcome switch
            {
                AccountCreation.NotAllowed => "/login?error=signup_not_allowed",
                AccountCreation.NotLinked => "/login?error=google_account_not_linked",
                _ => "/login?error=google_sign_in_failed",
            });
            return;
        }

        // A new account whose address Google could not vouch for verifies it by email, like any other.
        if (result.IsNew && !result.User.EmailVerified && http.RequestServices.GetRequiredService<EmailSettings>().SendsVerificationLinks)
        {
            await http.RequestServices.GetRequiredService<AuthEmails>().TrySendVerificationAsync(http, result.User, context.ReturnUri, http.RequestAborted);
        }

        await http.RequestServices.GetRequiredService<SessionManager>().StartAsync(http, result.User.Id, http.RequestAborted);
        http.Response.Redirect(LocalPath.OrRoot(context.ReturnUri));
    }
    /// <summary>
    /// Whether Google itself knows who controls the address today. It does for Gmail
    /// addresses and for Google Workspace accounts, which carry their domain in <c>hd</c>.
    /// For any other address Google only checked the mailbox when the Google Account was
    /// made, and <c>email_verified</c> stays true even if the mailbox has since changed hands.
    /// </summary>
    internal static bool IsGoogleAuthoritativeFor(string email, string? hostedDomain) =>
        !string.IsNullOrEmpty(hostedDomain)
        || email.EndsWith("@gmail.com", StringComparison.OrdinalIgnoreCase)
        || email.EndsWith("@googlemail.com", StringComparison.OrdinalIgnoreCase);
}

internal static class LocalPath
{
    /// <summary>
    /// A redirect target taken from a request is used only if it is a path on this site.
    /// Anything else, such as another site's address, becomes "/".
    /// </summary>
    public static string OrRoot(string? path) => IsLocal(path) ? path! : "/";

    public static bool IsLocal(string? path) =>
        !string.IsNullOrEmpty(path)
        && path[0] == '/'
        && (path.Length == 1 || (path[1] != '/' && path[1] != '\\'))
        && !path.Any(char.IsControl);
}
