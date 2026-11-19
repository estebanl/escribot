namespace Escribot.Api.Configuration;

/// <summary>
/// Application settings, bound from the <c>App</c> configuration section.
/// As environment variables: <c>App__EncryptionSecret</c>, <c>App__PublicUrl</c>, <c>App__TrustForwardedHeaders</c>,
/// <c>App__CloudflareIpRanges</c>.
/// </summary>
public sealed class AppOptions
{
    public const string SectionName = "App";

    public const int MinimumEncryptionSecretLength = 32;

    /// <summary>
    /// Secret from which the key that encrypts the API keys users save is derived.
    /// Required in every environment. It has no default: set it through .NET user
    /// secrets or an Aspire parameter locally, and a Container Apps secret in production.
    /// </summary>
    public string EncryptionSecret { get; set; } = string.Empty;

    /// <summary>
    /// Public address of the deployment, used to build links in emails and redirects.
    /// Required in production, where it must use HTTPS.
    /// </summary>
    public Uri? PublicUrl { get; set; }

    /// <summary>
    /// Set to true only when every request arrives through a reverse proxy that sets
    /// <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c>, as the Azure Container Apps
    /// ingress does. The client address used for rate limits and the scheme used in
    /// redirects then come from those headers. Leave it off when clients connect to the
    /// app directly, because they could then send the headers themselves.
    /// </summary>
    public bool TrustForwardedHeaders { get; set; }

    /// <summary>
    /// Comma-separated CIDR ranges of Cloudflare's edge, set when the app is served through
    /// Cloudflare's proxy. A request whose forwarded address falls in one of these ranges
    /// takes its client address from <c>CF-Connecting-IP</c>; any other request ignores that
    /// header. Requires <see cref="TrustForwardedHeaders"/>. The deployment fills it from
    /// <c>infra/cloudflare-ips.json</c>.
    /// </summary>
    public string? CloudflareIpRanges { get; set; }

    /// <summary>The parsed <see cref="CloudflareIpRanges"/>; empty when it is not set.</summary>
    /// <exception cref="FormatException">An entry is not a CIDR range.</exception>
    public IReadOnlyList<System.Net.IPNetwork> CloudflareNetworks() =>
        (CloudflareIpRanges ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(System.Net.IPNetwork.Parse)
            .ToList();
}
