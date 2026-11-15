using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FluentEscritor.Api.Storage;

// Failures report a fixed description. The exception goes to the logs only, so the
// health endpoint never reveals account names or endpoints.

internal sealed class DocumentDatabaseHealthCheck(IDocumentDatabase database) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await database.PingAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "The document database is unreachable.", ex);
        }
    }
}

internal sealed class FileStoreHealthCheck(IFileStore fileStore) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await fileStore.PingAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "The file store is unreachable.", ex);
        }
    }
}
