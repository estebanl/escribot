using FluentEscritor.Api.Auth;
using FluentEscritor.Api.Data;
using Microsoft.Net.Http.Headers;

namespace FluentEscritor.Api.Hosting;

/// <summary>
/// Serves the browser app (the Vite build of <c>client/</c>, copied into <c>wwwroot</c>):
/// its files as they are, and <c>index.html</c> for every page address, so the app's router
/// takes over in the browser.
/// <para>
/// Before answering with the page, the same checks the pages make in the browser are made
/// here, so a page address answers with the right status: signed-out visitors are sent to
/// <c>/login?next=...</c>, and an address that does not exist for this person (an unknown
/// page, someone else's writing session, the admin page for anyone but an admin) answers
/// 404. Either way nothing but the app's own code is sent; the data comes from the API.
/// </para>
/// </summary>
internal static class ClientApp
{
    private const string IndexFile = "index.html";

    /// <summary>Hashed by Vite: a new build gets new names, so these never change.</summary>
    private static readonly PathString AssetsPath = "/assets";

    /// <summary>Pages anyone may open. Every other page needs a session.</summary>
    private static readonly HashSet<string> PublicPages = new(StringComparer.Ordinal)
    {
        "/", "/terms", "/privacy", "/login", "/signup", "/forgot-password", "/reset-password", "/pricing",
    };

    /// <summary>The signed-in pages without a parameter. <c>/admin</c> is checked on its own.</summary>
    private static readonly HashSet<string> SignedInPages = new(StringComparer.Ordinal)
    {
        "/welcome", "/vault", "/drills", "/analytics", "/settings",
    };

    /// <summary>Pages of one writing session: <c>/write/{id}</c> and <c>/session/{id}</c>.</summary>
    private static readonly string[] SessionPages = ["/write/", "/session/"];

    /// <summary>Static files. Call before sign-in, which they do not need.</summary>
    public static WebApplication UseClientAppFiles(this WebApplication app)
    {
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = context =>
            {
                var headers = context.Context.Response.Headers;
                headers.CacheControl = context.Context.Request.Path.StartsWithSegments(AssetsPath)
                    ? "public, max-age=31536000, immutable"
                    // index.html and the files in client/public keep their names, so check each time.
                    : "no-cache";
            },
        });
        return app;
    }

    /// <summary>The page addresses. Mapped as the fallback, so every endpoint of the API comes first.</summary>
    public static WebApplication MapClientApp(this WebApplication app)
    {
        app.MapFallback(ServePageAsync);
        return app;
    }

    private static async Task ServePageAsync(HttpContext context, IWritingRepository writing, IWebHostEnvironment environment)
    {
        var request = context.Request;

        // An API address that matched no endpoint is an API 404, never the page.
        if (request.Path.StartsWithSegments("/api"))
        {
            await ApiResults.WriteErrorAsync(context, StatusCodes.Status404NotFound, "Not found.");
            return;
        }

        // A missing file (an old asset name, a mistyped icon) must not get the page as its body.
        if ((!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method)) || Path.HasExtension(request.Path.Value))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var path = Normalize(request.Path.Value);
        var session = context.GetSession();
        int status;
        if (PublicPages.Contains(path))
        {
            status = StatusCodes.Status200OK;
        }
        else if (session is null)
        {
            context.Response.Redirect($"/login?next={Uri.EscapeDataString($"{path}{request.QueryString}")}");
            return;
        }
        else if (path == "/admin")
        {
            // To anyone else the admin page does not exist.
            status = session.IsAdmin ? StatusCodes.Status200OK : StatusCodes.Status404NotFound;
        }
        else if (SessionId(path) is { } sessionId)
        {
            // Also 404 for another user's session, so its existence is not revealed.
            var owned = await writing.GetOwnedSessionAsync(session.Account.Id, sessionId, context.RequestAborted);
            status = owned is null ? StatusCodes.Status404NotFound : StatusCodes.Status200OK;
        }
        else
        {
            status = SignedInPages.Contains(path) ? StatusCodes.Status200OK : StatusCodes.Status404NotFound;
        }

        var index = environment.WebRootFileProvider.GetFileInfo(IndexFile);
        if (!index.Exists)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync(
                "The browser app is not part of this build. Run the Aspire AppHost, which starts the Vite dev server, or build client/ into wwwroot.",
                context.RequestAborted);
            return;
        }

        // The app itself shows the not-found page; the status tells everyone else.
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers[HeaderNames.CacheControl] = "no-cache";
        if (HttpMethods.IsHead(request.Method))
        {
            context.Response.ContentLength = index.Length;
            return;
        }

        await context.Response.SendFileAsync(index, context.RequestAborted);
    }

    /// <summary>The path without a trailing slash ("/" stays "/").</summary>
    private static string Normalize(string? path) =>
        string.IsNullOrEmpty(path) || path == "/" ? "/" : path.TrimEnd('/') is { Length: > 0 } trimmed ? trimmed : "/";

    /// <summary>The session id of <c>/write/{id}</c> or <c>/session/{id}</c>, otherwise null.</summary>
    private static string? SessionId(string path)
    {
        foreach (var prefix in SessionPages)
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal) && path[prefix.Length..] is { Length: > 0 } id && !id.Contains('/'))
            {
                return Uri.UnescapeDataString(id);
            }
        }

        return null;
    }
}
