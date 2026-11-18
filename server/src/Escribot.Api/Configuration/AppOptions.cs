namespace Escribot.Api.Configuration;

/// <summary>
/// Application settings, bound from the <c>App</c> configuration section.
/// As environment variables: <c>App__EncryptionSecret</c>, <c>App__PublicUrl</c>, <c>App__TrustForwardedHeaders</c>.
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
}
