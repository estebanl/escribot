namespace Escribot.Api.Hosting;

/// <summary>
/// Adds defensive HTTP response headers to every response from the API and browser app:
/// CSP, clickjacking protection, MIME-sniffing protection, referrer policy, and permissions policy.
/// </summary>
internal static class SecurityHeaders
{
    internal const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; " +
        "font-src 'self' https://fonts.gstatic.com; " +
        "img-src 'self' data: blob:; " +
        "connect-src 'self'; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'";

    internal const string PermissionsPolicy = "camera=(), microphone=(), geolocation=()";

    private static readonly PathString ResetPasswordPath = "/reset-password";
    private static readonly PathString VerifyEmailPath = "/api/auth/verify-email";

    /// <summary>
    /// Registers the security headers middleware. Call early in the pipeline (before static files
    /// and endpoints) so HTML pages, static assets, and API responses all carry the headers.
    /// </summary>
    public static WebApplication UseSecurityHeaders(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(static state =>
            {
                var httpContext = (HttpContext)state;
                var headers = httpContext.Response.Headers;
                var path = httpContext.Request.Path;

                headers.XContentTypeOptions = "nosniff";
                headers.XFrameOptions = "DENY";
                headers["Referrer-Policy"] = IsSensitiveTokenPath(path)
                    ? "no-referrer"
                    : "strict-origin-when-cross-origin";
                headers["Permissions-Policy"] = PermissionsPolicy;
                headers.ContentSecurityPolicy = ContentSecurityPolicy;

                return Task.CompletedTask;
            }, context);

            await next(context);
        });

        return app;
    }

    private static bool IsSensitiveTokenPath(PathString path) =>
        path.StartsWithSegments(ResetPasswordPath, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments(VerifyEmailPath, StringComparison.OrdinalIgnoreCase);
}
