using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Escribot.DataModel;

namespace Escribot.Api.Storage;

internal sealed class BlobFileStore(BlobServiceClient client) : IFileStore
{
    /// <summary>The one blob container the app keeps its files in.</summary>
    public const string ContainerName = DataModelSchema.FilesBlobContainer;

    private BlobClient Blob(string name) => client.GetBlobContainerClient(ContainerName).GetBlobClient(name);

    public async Task SaveAsync(string name, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken)
    {
        var options = new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } };
        await Blob(name).UploadAsync(new BinaryData(content), options, cancellationToken);
    }

    public async Task<byte[]?> ReadAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var result = await Blob(name).DownloadContentAsync(cancellationToken);
            return result.Value.Content.ToArray();
        }
        catch (RequestFailedException ex) when (ex.Status == StatusCodes.Status404NotFound)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            await Blob(name).DeleteIfExistsAsync(cancellationToken: cancellationToken);
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
