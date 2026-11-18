using Microsoft.Azure.Cosmos;

namespace Escribot.Api.Storage;

internal sealed class CosmosDocumentDatabase(CosmosClient client) : IDocumentDatabase
{
    public async Task PingAsync(CancellationToken cancellationToken)
    {
        // The SDK call takes no cancellation token, so the wait is cancelled instead.
        await client.ReadAccountAsync().WaitAsync(cancellationToken);
    }
}
