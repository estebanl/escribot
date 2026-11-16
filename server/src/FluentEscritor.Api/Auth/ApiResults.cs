using System.Text.Json.Serialization;

namespace FluentEscritor.Api.Auth;

/// <summary>An error as every <c>/api</c> route reports it. <c>Code</c> is set where the browser needs to tell errors apart.</summary>
public sealed record ApiError(
    string Error,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Code = null);

internal static class ApiResults
{
    /// <summary>The body the Next.js app returns to signed-out API calls, kept as it is.</summary>
    public const string UnauthorizedMessage = "Unauthorized — please sign in.";

    public static IResult Error(int statusCode, string message, string? code = null) =>
        Results.Json(new ApiError(message, code), statusCode: statusCode);

    public static Task WriteUnauthorizedAsync(HttpContext context) =>
        WriteErrorAsync(context, StatusCodes.Status401Unauthorized, UnauthorizedMessage);

    public static Task WriteErrorAsync(HttpContext context, int statusCode, string message, string? code = null)
    {
        context.Response.StatusCode = statusCode;
        return context.Response.WriteAsJsonAsync(new ApiError(message, code), context.RequestAborted);
    }
}
