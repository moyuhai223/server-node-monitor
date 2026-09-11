using Microsoft.AspNetCore.StaticFiles;
using SNM.Master.Services;

namespace SNM.Master.Api;

/// <summary>
/// Serves public-dashboard themes: the active theme at "/" and every installed theme at "/themes/{id}/" (used for previews).
/// Themes reference their own files relatively and the shared SDK bundles absolutely under /vendor/, so both mounts work.
/// </summary>
public sealed class ThemeMiddleware(RequestDelegate next, ThemeService themes)
{
    private static readonly string[] Reserved = ["/api", "/hubs", "/admin", "/vendor", "/install", "/healthz"];
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method)) { await next(ctx); return; }
        var path = ctx.Request.Path.Value ?? "/";
        foreach (var r in Reserved)
        {
            if (path.StartsWith(r, StringComparison.OrdinalIgnoreCase) && (path.Length == r.Length || path[r.Length] == '/')) { await next(ctx); return; }
        }

        ThemeInfo? theme;
        string rel;
        if (path.StartsWith("/themes/", StringComparison.OrdinalIgnoreCase) || string.Equals(path, "/themes", StringComparison.OrdinalIgnoreCase))
        {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2) { await next(ctx); return; }
            theme = themes.Get(segments[1]);
            if (theme is null) { await next(ctx); return; }
            if (segments.Length == 2 && !path.EndsWith('/'))
            {
                ctx.Response.Redirect(path + "/" + ctx.Request.QueryString, permanent: false);
                return;
            }
            rel = string.Join('/', segments.Skip(2));
        }
        else
        {
            theme = themes.Active;
            if (theme is null) { await next(ctx); return; }
            rel = path.TrimStart('/');
        }

        if (!ThemeService.TryMapFile(theme, rel, out var file)) { await next(ctx); return; }

        if (!ContentTypes.TryGetContentType(file, out var contentType)) contentType = "application/octet-stream";
        if (contentType.StartsWith("text/", StringComparison.Ordinal) || contentType.Contains("javascript") || contentType.Contains("json") || contentType.Contains("svg"))
            contentType += "; charset=utf-8";
        // Root mount: the entry page gets <base href="/themes/{id}/"> injected so every relative asset resolves to a
        // per-theme URL. Themes therefore never share cached URLs, and switching themes is just a reload.
        var mounted = path.StartsWith("/themes/", StringComparison.OrdinalIgnoreCase);
        var isEntry = contentType.StartsWith("text/html", StringComparison.Ordinal) && (rel.Length == 0 || string.Equals(rel, theme.Entry, StringComparison.OrdinalIgnoreCase));
        var info = new FileInfo(file);
        var etag = $"\"{theme.Id}-{(mounted ? "m" : "r")}-{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"";
        ctx.Response.Headers.ETag = etag;
        ctx.Response.Headers.LastModified = info.LastWriteTimeUtc.ToString("R");
        ctx.Response.Headers.CacheControl = isEntry ? "no-cache" : mounted ? "public, max-age=600" : "no-cache";
        ctx.Response.Headers["X-SNM-Theme"] = theme.Id;
        var inm = ctx.Request.Headers.IfNoneMatch;
        if (inm.Count > 0 && inm.Any(v => v is not null && v.Split(',').Select(x => x.Trim()).Contains(etag)))
        {
            ctx.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }
        ctx.Response.ContentType = contentType;
        if (HttpMethods.IsHead(ctx.Request.Method)) return;
        if (isEntry && !mounted)
        {
            var html = await File.ReadAllTextAsync(file, ctx.RequestAborted);
            if (!html.Contains("<base ", StringComparison.OrdinalIgnoreCase))
            {
                var baseTag = $"<base href=\"/themes/{theme.Id}/\">";
                var headIdx = html.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
                html = headIdx >= 0 ? html.Insert(headIdx + "<head>".Length, baseTag) : baseTag + html;
            }
            await ctx.Response.WriteAsync(html, ctx.RequestAborted);
            return;
        }
        await ctx.Response.SendFileAsync(file, ctx.RequestAborted);
    }
}
