using Azure;
using Azure.Storage.Blobs;

namespace FluentEscritor.Api.Storage;

internal sealed class BlobFileStore(BlobServiceClient client) : IFileStore
{
    /// <summary>The one blob container the app keeps its files in.</summary>
    public const string ContainerName = "files";

    public async Task DeleteAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            await client.GetBlobContainerClient(ContainerName).GetBlobClient(name).DeleteIfExistsAsync(cancellationToken: cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status == StatusCodes.Status404NotFound)
        {
            // No container yet means no files yet.
        }
    }

    public async Task PingAsync(CancellationToken cancellationToken)
    {
        // Listing one container needs only data-plane access, which is all the
        // app's managed identity is given.
        await foreach (var _ in client.GetBlobContainersAsync(cancellationToken: cancellationToken).AsPages(pageSizeHint: 1))
        {
            break;
        }
    }
}
