namespace FluentEscritor.Api.Storage;

/// <summary>
/// Binary file storage for uploaded images and backups. The rest of the app depends on
/// this interface, never on the Azure Storage SDK, which stays inside this layer.
/// </summary>
public interface IFileStore
{
    /// <summary>Throws if the storage account cannot be reached.</summary>
    Task PingAsync(CancellationToken cancellationToken);

    /// <summary>Deletes the file with that name. Does nothing if there is no such file.</summary>
    Task DeleteAsync(string name, CancellationToken cancellationToken);
}
