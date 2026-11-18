using Escribot.Api.Auth;
using Escribot.Api.Data;
using Escribot.Api.Data.Models;

namespace Escribot.Api.Api;

/// <summary>
/// Records an error for the admin view and logs it. It never throws: reporting an error
/// must not turn one failure into two. The repository removes anything that looks like a
/// credential before storing; callers must not put essay text in an error.
/// </summary>
public sealed partial class ErrorReporter(IErrorEventRepository errors, ILogger<ErrorReporter> logger)
{
    public async Task ReportServerErrorAsync(Exception exception, string? path, string? userId = null)
    {
        LogServerError(logger, exception, path);
        await StoreAsync(new ErrorEvent { Source = "server", Message = exception.Message, Stack = exception.StackTrace, Path = path, UserId = userId });
    }

    public Task ReportClientErrorAsync(string message, string? stack, string? path, string? digest, string? userId) =>
        StoreAsync(new ErrorEvent { Source = "client", Message = message, Stack = stack, Path = path, Digest = digest, UserId = userId });

    private async Task StoreAsync(ErrorEvent errorEvent)
    {
        try
        {
            // Not tied to the request: it may already have been aborted.
            await errors.RecordAsync(errorEvent, CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogStoreFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled error in {Path}")]
    private static partial void LogServerError(ILogger logger, Exception exception, string? path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to store an error report.")]
    private static partial void LogStoreFailed(ILogger logger, Exception exception);
}

/// <summary>What a route answers when it fails unexpectedly, in place of the general message.</summary>
internal sealed record FailureMessage(string Text);

/// <summary>
/// Turns an unhandled exception into a recorded error and a JSON 500, and a request the
/// framework could not read into a JSON 400. Nothing about the exception reaches the caller.
/// </summary>
internal sealed class ApiErrorHandler(RequestDelegate next, ErrorReporter reporter)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The caller went away; there is nobody to answer and nothing went wrong.
        }
        catch (BadHttpRequestException ex) when (!context.Response.HasStarted)
        {
            await ApiResults.WriteErrorAsync(context, ex.StatusCode, "The request could not be read.");
        }
        catch (Exception ex)
        {
            // The route template, not the address requested: the address can hold ids.
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(no route)";
            await reporter.ReportServerErrorAsync(ex, $"{context.Request.Method} {route}", context.GetSession()?.Account.Id);

            if (context.Response.HasStarted)
            {
                throw;
            }

            var message = context.GetEndpoint()?.Metadata.GetMetadata<FailureMessage>()?.Text ?? "Something went wrong.";
            context.Response.Clear();
            await ApiResults.WriteErrorAsync(context, StatusCodes.Status500InternalServerError, message);
        }
    }
}

internal static class ErrorReportingExtensions
{
    /// <summary>The message this route answers with a 500.</summary>
    public static TBuilder OnFailure<TBuilder>(this TBuilder builder, string message)
        where TBuilder : IEndpointConventionBuilder => builder.WithMetadata(new FailureMessage(message));
}
