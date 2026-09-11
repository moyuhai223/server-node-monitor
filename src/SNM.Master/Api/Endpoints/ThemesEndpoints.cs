using SNM.Master.Services;

namespace SNM.Master.Api.Endpoints;

/// <summary>Theme management for the public dashboard (docs/THEMES.md): list, upload (zip), delete, rescan.</summary>
public static class ThemesEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/settings/themes").RequireAuthorization("Admin");

        g.MapGet("/", (ThemeService themes) =>
        {
            var active = themes.ActiveId;
            return Results.Ok(ApiResponse.Ok(new
            {
                active,
                sdk = ThemeService.SupportedSdk,
                userDir = themes.UserDir,
                items = themes.All.Select(t => t.ToDto(t.Id == active)).ToArray(),
            }));
        });

        g.MapPost("/", async (HttpRequest request, HttpContext ctx, ThemeService themes, ILoggerFactory lf, CancellationToken ct) =>
        {
            Stream? stream = null;
            if (request.HasFormContentType)
            {
                var form = await request.ReadFormAsync(ct);
                var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault() ?? throw ApiException.BadRequest("请上传主题 zip 包(字段名 file)");
                if (!file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw ApiException.BadRequest("只接受 .zip 主题包");
                stream = file.OpenReadStream();
            }
            else if (request.ContentType?.Contains("zip", StringComparison.OrdinalIgnoreCase) == true || request.ContentType?.Contains("octet-stream", StringComparison.OrdinalIgnoreCase) == true)
            {
                stream = request.Body;
            }
            if (stream is null) throw ApiException.BadRequest("请以 multipart/form-data(file 字段)或 application/zip 上传主题包");

            // buffer: ZipArchive needs a seekable stream
            await using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            buffer.Position = 0;
            var theme = await themes.InstallAsync(buffer, ct);
            NodesEndpoints.Audit(lf, ctx, $"install theme {theme.Id} v{theme.Version}", warn: true);
            return Results.Created($"/themes/{theme.Id}/", ApiResponse.Ok(theme.ToDto(themes.ActiveId == theme.Id)));
        }).DisableAntiforgery();

        g.MapDelete("/{id}", async (string id, HttpContext ctx, ThemeService themes, ILoggerFactory lf, CancellationToken ct) =>
        {
            await themes.DeleteAsync(id, ct);
            NodesEndpoints.Audit(lf, ctx, $"delete theme {id}", warn: true);
            return Results.Ok(ApiResponse.Ok(new { active = themes.ActiveId }));
        });

        g.MapPost("/reload", (ThemeService themes) =>
        {
            themes.Reload();
            return Results.Ok(ApiResponse.Ok(new { active = themes.ActiveId, count = themes.All.Count }));
        });
    }
}
