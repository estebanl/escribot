using Azure.Storage.Blobs;

namespace FluentEscritor.Api.Storage;

internal sealed class BlobFileStore(BlobServiceClient client) : IFileStore
{
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
