using Escribot.Api.Api;
using Escribot.Api.Configuration;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Escribot.Api.Auth;

internal static class AuthExtensions
{
    public const string AdminPolicy = "Admin";

    /// <summary>
    /// Registers sign-in: ASP.NET Core Identity over the account repository, the session
    /// cookie as the way requests are authenticated, and Google when it is configured.
    /// </summary>
    public static IHostApplicationBuilder AddAuth(this IHostApplicationBuilder builder)
    {
        var settings = new AuthSettings(builder.Configuration);
        builder.Services.AddSingleton(settings);

        builder.Services.AddIdentityCore<AppUser>(options =>
            {
                // Length is the only password rule, as before.
                options.Password.RequiredLength = AuthEndpoints.MinPasswordLength;
                options.Password.RequiredUniqueChars = 1;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;

                // The user name is the email address, so it may hold whatever an address may.
                options.User.AllowedUserNameCharacters = string.Empty;
                options.User.RequireUniqueEmail = true;
            })
            .AddUserStore<AccountUserStore>();

        builder.Services.AddSingleton<SessionManager>();
        builder.Services.AddSingleton<NewUserProvisioner>();
        builder.Services.AddSingleton<AccountDeletion>();
        builder.Services.AddScoped<AccountService>();
        builder.Services.AddSingleton<AuthEmails>();

        var authentication = builder.Services.AddAuthentication(SessionAuthenticationHandler.SchemeName)
            .AddScheme<SessionAuthenticationOptions, SessionAuthenticationHandler>(SessionAuthenticationHandler.SchemeName, null);
        if (settings.GoogleEnabled)
        {
            authentication.AddGoogleSignIn(settings);
        }

        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(AdminPolicy, policy => policy.RequireRole(SessionAuthenticationHandler.AdminRole));

        return builder;
    }

    /// <summary>
    /// Takes the client address and scheme from the proxy in front of the app, when it is
    /// trusted. Call first, so that everything after it, HSTS included, sees the scheme and
    /// address the visitor actually used.
    /// </summary>
    public static WebApplication UseProxyHeaders(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<AppOptions>>().Value;
        if (options.TrustForwardedHeaders)
        {
            // The proxy appends the address it saw to X-Forwarded-For. Only that last
            // entry is used (ForwardLimit is 1), so a value sent by the client is ignored.
            var forwarded = new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
                ForwardLimit = 1,
            };
            forwarded.KnownIPNetworks.Clear();
            forwarded.KnownProxies.Clear();
            app.UseForwardedHeaders(forwarded);

            // Behind Cloudflare, that last entry is a Cloudflare edge address; the visitor's
            // own address is in CF-Connecting-IP.
            var cloudflare = options.CloudflareNetworks();
            if (cloudflare.Count > 0)
            {
                app.UseMiddleware<CloudflareClientAddress>(cloudflare);
            }
        }

        return app;
    }

    public static WebApplication UseAuth(this WebApplication app)
    {
        // Outermost, so a failure anywhere below is recorded and answered as JSON.
        app.UseMiddleware<ApiErrorHandler>();
        app.UseAuthentication();
        app.UseMiddleware<ApiGuard>();
        app.UseAuthorization();
        app.MapAuthEndpoints();

        return app;
    }
}
