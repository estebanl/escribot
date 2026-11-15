namespace FluentEscritor.Api.Storage;

/// <summary>
/// Binary file storage for uploaded images and backups. The rest of the app depends on
/// this interface, never on the Azure Storage SDK, which stays inside this layer.
/// </summary>
public interface IFileStore
{
    /// <summary>Throws if the storage account cannot be reached.</summary>
    Task PingAsync(CancellationToken cancellationToken);
}
