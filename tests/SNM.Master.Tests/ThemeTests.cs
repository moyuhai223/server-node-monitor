using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SNM.Master.Services;

namespace SNM.Master.Tests;

public class ThemeTests(MasterFactory factory) : IClassFixture<MasterFactory>
{
    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var w = new StreamWriter(z.CreateEntry(name).Open(), Encoding.UTF8);
                w.Write(content);
            }
        }
        return ms.ToArray();
    }

    private static MultipartFormDataContent Form(byte[] zip)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(zip);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(file, "file", "theme.zip");
        return form;
    }

    [Fact]
    public async Task InstallActivateServeAndDeleteAUserTheme()
    {
        var auth = await factory.LoginAsync();
        var anon = factory.CreateClient();
        var zip = Zip(("theme.json", """{"id":"test-theme","name":"测试主题","version":"0.1.0","entry":"index.html","sdk":1}"""),
                      ("index.html", "<html><body id=\"t\">test theme</body></html>"), ("style.css", "body{}"));

        var created = await auth.PostAsync("/api/settings/themes", Form(zip));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var dto = await MasterFactory.DataAsync(created);
        Assert.Equal("test-theme", dto.GetProperty("id").GetString());
        Assert.False(dto.GetProperty("builtIn").GetBoolean());

        var list = await MasterFactory.DataAsync(await auth.GetAsync("/api/settings/themes"));
        Assert.Contains(list.GetProperty("items").EnumerateArray(), t => t.GetProperty("id").GetString() == "test-theme");

        // preview mount
        var preview = await anon.GetAsync("/themes/test-theme/");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Contains("test theme", await preview.Content.ReadAsStringAsync());
        Assert.Equal("test-theme", preview.Headers.GetValues("X-SNM-Theme").Single());
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/themes/test-theme/style.css")).StatusCode);

        // activate -> served at "/"
        (await auth.PatchJsonAsync("/api/settings", new { site = new { theme = "test-theme", themeOptions = "{\"accent\":\"#000\"}" } })).EnsureSuccessStatusCode();
        var root = await anon.GetAsync("/");
        Assert.Equal("test-theme", root.Headers.GetValues("X-SNM-Theme").Single());
        var rootHtml = await root.Content.ReadAsStringAsync();
        Assert.Contains("test theme", rootHtml);
        Assert.Contains("<base href=\"/themes/test-theme/\">", rootHtml);
        // relative assets resolve under the per-theme mount, revalidated by ETag
        var css = await anon.GetAsync("/themes/test-theme/style.css");
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        var etag = css.Headers.ETag!.Tag;
        var again = new HttpRequestMessage(HttpMethod.Get, "/themes/test-theme/style.css");
        again.Headers.TryAddWithoutValidation("If-None-Match", etag);
        Assert.Equal(HttpStatusCode.NotModified, (await anon.SendAsync(again)).StatusCode);

        // public snapshot carries the theme id and options
        var builder = factory.Services.GetRequiredService<SNM.Master.Runtime.LiveSnapshotBuilder>();
        var site = builder.PublicSnapshot(DateTime.UtcNow).Site;
        Assert.Equal("test-theme", site.Theme);
        Assert.Contains("accent", site.ThemeOptions);

        // delete while active -> falls back to default
        var del = await auth.DeleteAsync("/api/settings/themes/test-theme");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        Assert.Equal("default", (await MasterFactory.DataAsync(del)).GetProperty("active").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/themes/test-theme/")).StatusCode);
        Assert.Equal("default", factory.Services.GetRequiredService<ThemeService>().ActiveId);
    }

    [Fact]
    public async Task RejectsDangerousOrIncompatiblePackages()
    {
        var auth = await factory.LoginAsync();
        async Task<(HttpStatusCode, string)> Post(byte[] zip)
        {
            var r = await auth.PostAsync("/api/settings/themes", Form(zip));
            var body = await r.Content.ReadFromJsonAsync<JsonElement>();
            return (r.StatusCode, body.GetProperty("message").GetString() ?? "");
        }

        var (s1, m1) = await Post(Zip(("theme.json", """{"id":"evil","name":"x","entry":"index.html"}"""), ("index.html", "<html></html>"), ("../../etc/passwd", "x")));
        Assert.Equal(HttpStatusCode.BadRequest, s1); Assert.Contains("非法路径", m1);

        var (s2, m2) = await Post(Zip(("theme.json", """{"id":"badext","name":"x","entry":"index.html"}"""), ("index.html", "<html></html>"), ("run.exe", "x")));
        Assert.Equal(HttpStatusCode.BadRequest, s2); Assert.Contains("不允许的文件类型", m2);

        var (s3, _) = await Post(Zip(("index.html", "<html></html>")));
        Assert.Equal(HttpStatusCode.BadRequest, s3);

        var (s4, m4) = await Post(Zip(("theme.json", """{"id":"default","name":"x","entry":"index.html"}"""), ("index.html", "<html></html>")));
        Assert.Equal(HttpStatusCode.Conflict, s4); Assert.Contains("内置", m4);

        var (s5, m5) = await Post(Zip(("theme.json", """{"id":"future","name":"x","entry":"index.html","sdk":99}"""), ("index.html", "<html></html>")));
        Assert.Equal(HttpStatusCode.BadRequest, s5); Assert.Contains("SDK", m5);

        var (s6, m6) = await Post(Zip(("theme.json", """{"id":"Bad Id","name":"x","entry":"index.html"}"""), ("index.html", "<html></html>")));
        Assert.Equal(HttpStatusCode.BadRequest, s6); Assert.Contains("id", m6);

        // built-in themes cannot be deleted, unknown ones are 404
        Assert.Equal(HttpStatusCode.BadRequest, (await auth.DeleteAsync("/api/settings/themes/default")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await auth.DeleteAsync("/api/settings/themes/nope")).StatusCode);
        // anonymous management is rejected
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/settings/themes")).StatusCode);
    }

    [Fact]
    public async Task ThemeFilesCannotEscapeTheThemeDirectory()
    {
        var anon = factory.CreateClient();
        foreach (var path in new[] { "/themes/default/../../appsettings.json", "/themes/default/%2e%2e/%2e%2e/appsettings.json", "/../appsettings.json", "/themes/default/theme.json.bak" })
        {
            var r = await anon.GetAsync(path);
            Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
        }
        // reserved prefixes are never treated as theme files
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/nodes")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/healthz")).StatusCode);
    }

    [Fact]
    public async Task UnknownThemeSettingFallsBackToDefault()
    {
        var auth = await factory.LoginAsync();
        (await auth.PatchJsonAsync("/api/settings", new { site = new { theme = "does-not-exist" } })).EnsureSuccessStatusCode();
        Assert.Equal("default", factory.Services.GetRequiredService<ThemeService>().ActiveId);
        var bad = await auth.PatchJsonAsync("/api/settings", new { site = new { themeOptions = "not json" } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        (await auth.PatchJsonAsync("/api/settings", new { site = new { theme = "default" } })).EnsureSuccessStatusCode();
    }
}
