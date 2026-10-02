using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SNM.Master.Alerting;
using SNM.Master.Data;
using SNM.Master.Data.Entities;
using SNM.Master.Services;

namespace SNM.Master.Tests;

public class WebhookTests(MasterFactory factory) : IClassFixture<MasterFactory>
{
    private const string Message = "节点\"一\"\n路径 C:\\服务\n**告警** 😀";

    [Theory]
    [InlineData("application/json", "{\"message\":\"{{message}}\"}")]
    [InlineData("text/plain", "{{message}}")]
    [InlineData("text/markdown", "## {{title}}\n{{message}}")]
    [InlineData("application/json", null)]
    [InlineData("text/plain", null)]
    [InlineData("text/markdown", null)]
    [InlineData(null, "{\"message\":\"{{message}}\"}")]
    public async Task DeliveryPreservesBodyAndSignsActualBytes(string? contentType, string? template)
    {
        var capture = new CaptureHandler();
        var dispatcher = new NotificationDispatcher(
            factory.Services.GetRequiredService<IDbContextFactory<SnmDbContext>>(),
            new CaptureFactory(capture), factory.Services.GetRequiredService<SettingsService>(),
            NullLogger<NotificationDispatcher>.Instance);
        var config = new Dictionary<string, object?>
        {
            ["url"] = "https://example.invalid/bot", ["bodyTemplate"] = template,
            ["secret"] = "test-signing-key",
        };
        config["headers"] = new Dictionary<string, string> { ["x-api-key"] = "fake-api-key" };
        if (contentType is not null) config["contentType"] = contentType;
        var ev = new AlertEvent { Title = "测试", Message = Message, NodeName = "节点", StartedAt = DateTime.UtcNow };
        var result = await dispatcher.SendAsync(new NotificationChannel
        {
            Type = "webhook", ConfigJson = JsonSerializer.Serialize(config),
        }, ev, null, CancellationToken.None);
        Assert.True(result.Ok, result.Error);
        Assert.Equal(contentType ?? "application/json", capture.ContentType);
        Assert.Equal(contentType is "text/plain" or "text/markdown" ? "" : "utf-8", capture.Charset);
        Assert.Equal("fake-api-key", capture.ApiKey);
        var expectedSignature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("test-signing-key"), Encoding.UTF8.GetBytes(capture.Timestamp + "." + capture.Body)));
        Assert.Equal("sha256=" + expectedSignature, capture.Signature);
        if (contentType is null or "application/json")
        {
            using var doc = JsonDocument.Parse(capture.Body);
            Assert.Equal(Message, doc.RootElement.GetProperty("message").GetString());
        }
        else if (template is not null)
        {
            Assert.Equal(contentType == "text/plain" ? Message : "## 测试\n" + Message, capture.Body);
        }
        else
        {
            Assert.Equal(AlertTexts.PlainText(ev, null, factory.Services.GetRequiredService<SettingsService>().Snapshot.TimeZone), capture.Body);
        }
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("text/plain")]
    [InlineData("text/markdown")]
    [InlineData(null)]
    public async Task ApiPersistsTypesAndDefaultsOldConfigurations(string? contentType)
    {
        using var client = await factory.LoginAsync();
        var config = new Dictionary<string, object> { ["url"] = "https://example.invalid/bot" };
        if (contentType is not null) config["contentType"] = contentType;
        var response = await client.PostAsJsonAsync("/api/settings/channels", new { name = "format test", type = "webhook", config });
        response.EnsureSuccessStatusCode();
        var data = await MasterFactory.DataAsync(response);
        Assert.Equal(contentType ?? "application/json", data.GetProperty("config").GetProperty("contentType").GetString());
        var id = data.GetProperty("id").GetInt32();
        var updated = await client.PatchJsonAsync($"/api/settings/channels/{id}", new { config = new { timeoutSec = 15 } });
        updated.EnsureSuccessStatusCode();
        Assert.Equal(contentType ?? "application/json", (await MasterFactory.DataAsync(updated)).GetProperty("config").GetProperty("contentType").GetString());
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("text/plain\r\nX-Evil: yes")]
    [InlineData(42)]
    public async Task ApiRejectsUnsupportedContentTypes(object contentType)
    {
        using var client = await factory.LoginAsync();
        var response = await client.PostAsJsonAsync("/api/settings/channels", new { name = "invalid", type = "webhook", config = new { url = "https://example.invalid/bot", contentType } });
        Assert.False(response.IsSuccessStatusCode);
        Assert.Contains("config.contentType", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DraftTestUsesEditedFormatAndStoredSecretWithoutSavingChanges()
    {
        var capture = new CaptureHandler();
        await using var app = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton<IHttpClientFactory>(new CaptureFactory(capture))));
        using var client = app.CreateClient();
        var login = await MasterFactory.DataAsync(await client.PostAsJsonAsync("/api/auth/login", new { username = MasterFactory.AdminUser, password = MasterFactory.AdminPassword }));
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", login.GetProperty("accessToken").GetString());
        var created = await MasterFactory.DataAsync(await client.PostAsJsonAsync("/api/settings/channels", new
        {
            name = "draft format", type = "webhook", enabled = false,
            config = new { url = "https://example.invalid/bot", secret = "stored-secret", headers = new Dictionary<string, string> { ["x-api-key"] = "fake-api-key" } },
        }));
        var id = created.GetProperty("id").GetInt32();
        var response = await client.PostAsJsonAsync("/api/settings/channels/test", new
        {
            id, config = new { contentType = "text/markdown", bodyTemplate = "**{{title}}**\n{{message}}" },
        });
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/markdown", capture.ContentType);
        Assert.StartsWith("**[测试] Server Node Monitor**\n", capture.Body);
        var expected = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("stored-secret"), Encoding.UTF8.GetBytes(capture.Timestamp + "." + capture.Body)));
        Assert.Equal("sha256=" + expected, capture.Signature);
        var channels = await MasterFactory.DataAsync(await client.GetAsync("/api/settings/channels"));
        var saved = channels.EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == id);
        Assert.Equal("application/json", saved.GetProperty("config").GetProperty("contentType").GetString());
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("lastTestAt").ValueKind);
    }

    private sealed class CaptureFactory(CaptureHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string Body = "", ContentType = "", Charset = "", ApiKey = "", Timestamp = "", Signature = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            ContentType = request.Content.Headers.ContentType!.MediaType!;
            Charset = request.Content.Headers.ContentType.CharSet ?? "";
            if (ContentType is "text/plain" or "text/markdown" && request.Content.Headers.ContentType.ToString() != ContentType)
                return new HttpResponseMessage(HttpStatusCode.UnsupportedMediaType) { Content = new StringContent("Text content type must not include parameters") };
            ApiKey = request.Headers.GetValues("x-api-key").Single();
            Timestamp = request.Headers.GetValues("X-SNM-Timestamp").Single();
            Signature = request.Headers.GetValues("X-SNM-Signature").Single();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("1") };
        }
    }
}
