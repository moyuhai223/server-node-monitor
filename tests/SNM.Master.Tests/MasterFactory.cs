using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SNM.Master.Services;

namespace SNM.Master.Tests;

/// <summary>One master per test class: temp data directory, GeoIP disabled, fixed admin password.</summary>
public sealed class MasterFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminUser = "admin";
    public const string AdminPassword = "test-password-123";

    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "snm-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Snm:DataDir", DataDir);
        builder.UseSetting("Snm:GeoIp:Enabled", "false");
        builder.UseSetting("Snm:Admin:User", AdminUser);
        builder.UseSetting("Snm:Admin:Password", AdminPassword);
        builder.UseSetting("Snm:PublicBaseUrl", "https://m.test");
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(DataDir);
        _ = Server;
        await Services.GetRequiredService<StartupInitializer>().EnsureInitializedAsync(CancellationToken.None);
    }

    Task IAsyncLifetime.DisposeAsync()
    {
        Dispose();
        try { Directory.Delete(DataDir, true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<HttpClient> LoginAsync()
    {
        var client = CreateClient();
        var resp = await client.PostAsJsonAsync("/api/auth/login", new { username = AdminUser, password = AdminPassword });
        resp.EnsureSuccessStatusCode();
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var token = doc.GetProperty("data").GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public static async Task<JsonElement> DataAsync(HttpResponseMessage resp)
    {
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return doc.GetProperty("data");
    }
}

public static class HttpExtensions
{
    public static Task<HttpResponseMessage> PatchJsonAsync(this HttpClient client, string url, object body) =>
        client.PatchAsync(url, JsonContent.Create(body));
}
