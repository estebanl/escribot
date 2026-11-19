using System.Net;

namespace Escribot.Api.Auth;

/// <summary>
/// Behind Cloudflare's proxy, the address the Container Apps ingress forwards is a
/// Cloudflare edge server, shared by many visitors. This puts the visitor's own address,
/// from <c>CF-Connecting-IP</c>, in its place, so per-address rate limits count each
/// visitor separately. The header is used only when the forwarded address is one of
/// Cloudflare's: anyone else could set it to whatever they like.
/// </summary>
/// <remarks>Runs after the forwarded headers middleware has set the connection's remote address.</remarks>
internal sealed class CloudflareClientAddress(RequestDelegate next, IReadOnlyList<IPNetwork> cloudflareNetworks)
{
    public const string HeaderName = "CF-Connecting-IP";

    public Task InvokeAsync(HttpContext context)
    {
        var connection = context.Connection;
        if (connection.RemoteIpAddress is { } proxy
            && IsCloudflare(proxy)
            && context.Request.Headers[HeaderName] is [{ } value]
            && IPAddress.TryParse(value.Trim(), out var visitor))
        {
            connection.RemoteIpAddress = visitor.IsIPv4MappedToIPv6 ? visitor.MapToIPv4() : visitor;
        }

        return next(context);
    }

    private bool IsCloudflare(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        foreach (var network in cloudflareNetworks)
        {
            if (network.Contains(address))
            {
                return true;
            }
        }

        return false;
    }
}
