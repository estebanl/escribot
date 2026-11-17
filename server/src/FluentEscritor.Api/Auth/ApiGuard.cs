using FluentEscritor.Api.Configuration;
using FluentEscritor.Api.Data;
using Microsoft.Extensions.Options;

namespace FluentEscritor.Api.Auth;

/// <summary>
/// The rule for every <c>/api</c> request, applied before routing decides anything:
/// <list type="bullet">
/// <item>A session is required, except for sign-in itself, the Stripe webhook (which
/// proves itself with Stripe's signature), error reports (sent from signed-out pages
/// too) and the pricing page's prices. A new route is therefore protected without having to remember to say so.</item>
/// <item>A request that changes something and comes from a web page must come from this
/// site's own pages. Together with the SameSite cookie this stops other sites from
/// acting with a visitor's session.</item>
/// </list>
/// </summary>
internal sealed class ApiGuard(RequestDelegate next, IOptions<AppOptions> options)
{
    private static readonly PathString Api = "/api";

    private static readonly PathString[] PublicPaths = ["/api/auth", "/api/stripe/webhook", "/api/errors", "/api/billing/prices"];

    private readonly string? _publicOrigin = options.Value.PublicUrl?.GetLeftPart(UriPartial.Authority);

    public Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (!request.Path.StartsWithSegments(Api))
        {
            return next(context);
        }

        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method)
            && request.Headers.Origin.ToString() is { Length: > 0 } origin
            && !IsOwnOrigin(request, origin))
        {
            return ApiResults.WriteErrorAsync(context, StatusCodes.Status403Forbidden, "This request came from another site.");
        }

        if (context.User.Identity?.IsAuthenticated != true && !PublicPaths.Any(p => request.Path.StartsWithSegments(p)))
        {
            return ApiResults.WriteUnauthorizedAsync(context);
        }

        return next(context);
    }

    private bool IsOwnOrigin(HttpRequest request, string origin) =>
        string.Equals(origin, _publicOrigin, StringComparison.OrdinalIgnoreCase)
        || string.Equals(origin, $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Per-address limits on the sign-in endpoints, counted in the database so they hold across instances.</summary>
internal static class AuthRateLimits
{
    public static TBuilder LimitPerAddress<TBuilder>(this TBuilder builder, string name, int limit, TimeSpan window)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var address = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var result = await http.RequestServices.GetRequiredService<IRateLimiter>()
                .CheckAsync($"auth:{name}:{address}", limit, window, http.RequestAborted);
            if (!result.Allowed)
            {
                http.Response.Headers.RetryAfter = result.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return ApiResults.Error(StatusCodes.Status429TooManyRequests, "Too many requests. Please try again later.", "RATE_LIMITED");
            }

            return await next(context);
        });
}
