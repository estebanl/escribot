using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using FluentEscritor.Api.Data;
using FluentEscritor.Api.Data.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace FluentEscritor.Api.Auth;

/// <summary>The signed-in user of the current request, as loaded when the session cookie was checked.</summary>
public sealed record CurrentSession(UserAccount Account, AuthSession Session, bool IsOwner, bool IsAdmin);

/// <summary>
/// Browser sessions. The cookie holds a random token and nothing else; the session
/// itself is a document found by the hash of that token, so signing out, resetting a
/// password or deleting the account ends a session immediately.
/// </summary>
public sealed class SessionManager(IAuthSessionStore sessions, TimeProvider time, IHostEnvironment environment)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    /// <summary>A session in use is given a full lifetime again once this much of it has passed.</summary>
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(1);

    private const string CookieName = "fluentescritor-session";
    private const int MaxTokenLength = 128;

    public async Task StartAsync(HttpContext context, string userId, CancellationToken cancellationToken = default)
    {
        // Signing in again in the same browser replaces the session instead of leaving
        // the old one valid with no cookie pointing at it.
        if (ReadToken(context.Request) is { } previous)
        {
            await sessions.RemoveAsync(previous, cancellationToken);
        }

        var token = NewToken();
        var expiresAt = time.GetUtcNow() + Lifetime;
        await sessions.CreateAsync(token, userId, expiresAt, cancellationToken);
        WriteCookie(context, token, expiresAt);
    }

    public async Task EndAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        if (ReadToken(context.Request) is { } token)
        {
            await sessions.RemoveAsync(token, cancellationToken);
        }

        ClearCookie(context);
    }

    /// <summary>A random value for a session or an emailed link: 256 bits, safe in a URL.</summary>
    public static string NewToken() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    internal string? ReadToken(HttpRequest request)
    {
        var token = request.Cookies[Name(request)];
        return string.IsNullOrEmpty(token) || token.Length > MaxTokenLength ? null : token;
    }

    internal void WriteCookie(HttpContext context, string token, DateTimeOffset expiresAt) =>
        context.Response.Cookies.Append(Name(context.Request), token, Options(context.Request, expiresAt));

    internal void ClearCookie(HttpContext context) =>
        context.Response.Cookies.Delete(Name(context.Request), Options(context.Request, null));

    // Production is always served over HTTPS (App:PublicUrl must be), so the cookie is
    // Secure there even if a proxy hides the scheme. The __Host- prefix makes the browser
    // refuse the cookie unless it is Secure, has Path=/ and is bound to this exact host.
    private bool IsSecure(HttpRequest request) => environment.IsProduction() || request.IsHttps;

    private string Name(HttpRequest request) => IsSecure(request) ? $"__Host-{CookieName}" : CookieName;

    private CookieOptions Options(HttpRequest request, DateTimeOffset? expiresAt) => new()
    {
        HttpOnly = true,
        Secure = IsSecure(request),
        SameSite = SameSiteMode.Lax,
        Path = "/",
        Expires = expiresAt,
        IsEssential = true,
    };
}

public sealed class SessionAuthenticationOptions : AuthenticationSchemeOptions;

/// <summary>
/// Checks the session cookie against the database on every request: the session must
/// exist and not have expired, and its account must still exist.
/// </summary>
internal sealed class SessionAuthenticationHandler(
    IOptionsMonitor<SessionAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    SessionManager manager,
    IAuthSessionStore sessions,
    IUserAccountRepository accounts,
    AuthSettings settings,
    TimeProvider time)
    : AuthenticationHandler<SessionAuthenticationOptions>(options, logger, encoder)
{
    public const string SchemeName = "Session";

    public const string AdminRole = "admin";

    public const string OwnerRole = "owner";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = manager.ReadToken(Request);
        if (token is null)
        {
            return AuthenticateResult.NoResult();
        }

        var cancellationToken = Context.RequestAborted;
        var session = await sessions.FindAsync(token, cancellationToken);
        var account = session is null ? null : await accounts.GetAsync(session.UserId, cancellationToken);
        if (session is null || account is null)
        {
            manager.ClearCookie(Context);
            return AuthenticateResult.NoResult();
        }

        var now = time.GetUtcNow();
        var lastRefreshed = session.ExpiresAt - SessionManager.Lifetime;
        if (now - lastRefreshed >= SessionManager.RefreshAfter)
        {
            var expiresAt = now + SessionManager.Lifetime;
            if (await sessions.ExtendAsync(token, expiresAt, cancellationToken))
            {
                manager.WriteCookie(Context, token, expiresAt);
            }
        }

        var current = new CurrentSession(account, session, settings.IsOwner(account), settings.IsAdmin(account));
        Context.Features.Set(current);

        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, account.Id),
            new(ClaimTypes.Email, account.Email),
            new(ClaimTypes.Name, account.Name),
        ];
        if (current.IsOwner)
        {
            claims.Add(new Claim(ClaimTypes.Role, OwnerRole));
        }

        if (current.IsAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, AdminRole));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        ApiResults.WriteUnauthorizedAsync(Context);

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        ApiResults.WriteErrorAsync(Context, StatusCodes.Status403Forbidden, "You do not have access to this.");
}

internal static class CurrentSessionExtensions
{
    /// <summary>The signed-in user, or null when the request has no valid session.</summary>
    public static CurrentSession? GetSession(this HttpContext context) => context.Features.Get<CurrentSession>();
}
