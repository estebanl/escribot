namespace FluentEscritor.Api.Storage;

/// <summary>
/// The document database. The rest of the app depends on this interface and on the
/// repositories built on it, never on the Cosmos DB SDK, which stays inside this layer.
/// </summary>
public interface IDocumentDatabase
{
    /// <summary>Throws if the database account cannot be reached.</summary>
    Task PingAsync(CancellationToken cancellationToken);
}
