using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SNM.Master.Data;
using SNM.Master.Data.Entities;

namespace SNM.Master.Tests;

public class NodesApiTests(MasterFactory factory) : IClassFixture<MasterFactory>
{
    [Fact]
    public async Task CreateReadUpdateDeleteNode()
    {
        var auth = await factory.LoginAsync();
        var create = await auth.PostAsJsonAsync("/api/nodes", new
        {
            publicName = "Crud-Node",
            adminRemark = "remark",
            traffic = new { limitBytes = 1_000_000_000_000L, resetDay = 31, countMode = 3 },
            finance = new { vendor = "V", price = "12.50", currency = "usd", billingCycleMonths = 12, expiresAt = "2030-01-31" },
            alerts = new { cpuAlertPct = 95 },
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var node = await MasterFactory.DataAsync(create);
        var id = node.GetProperty("id").GetInt32();
        var key = node.GetProperty("agentKey").GetString()!;
        Assert.StartsWith("snmk_", key);
        Assert.Equal(48, key.Length);
        Assert.Equal("USD", node.GetProperty("finance").GetProperty("currency").GetString());
        Assert.Equal("12.5", node.GetProperty("finance").GetProperty("price").GetString());
        Assert.Equal(31, node.GetProperty("traffic").GetProperty("resetDay").GetInt32());
        Assert.Equal(3, node.GetProperty("traffic").GetProperty("countMode").GetInt32());
        Assert.Equal(95, node.GetProperty("alerts").GetProperty("cpuAlertPct").GetInt32());

        // duplicate name -> 409/20001
        var dup = await auth.PostAsJsonAsync("/api/nodes", new { publicName = "Crud-Node" });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        Assert.Equal(20001, (await dup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetInt32());

        // detail hides the key, shows the mask
        var detail = await MasterFactory.DataAsync(await auth.GetAsync($"/api/nodes/{id}"));
        Assert.False(detail.TryGetProperty("agentKey", out _));
        Assert.StartsWith("snmk_", detail.GetProperty("agentKeyMasked").GetString());
        Assert.Contains("****", detail.GetProperty("agentKeyMasked").GetString());

        // partial update keeps untouched fields, validation errors are per field
        var patch = await auth.PatchJsonAsync($"/api/nodes/{id}", new { alerts = new { cpuAlertPct = (int?)null }, finance = new { vendor = "W" } });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var patched = await MasterFactory.DataAsync(patch);
        Assert.Equal(JsonValueKind.Null, patched.GetProperty("alerts").GetProperty("cpuAlertPct").ValueKind);
        Assert.Equal("W", patched.GetProperty("finance").GetProperty("vendor").GetString());
        Assert.Equal("12.5", patched.GetProperty("finance").GetProperty("price").GetString());

        var invalid = await auth.PatchJsonAsync($"/api/nodes/{id}", new { intervalMs = 10, countryCodeOverride = "XYZ", finance = new { currency = "GBP", price = "1.234" } });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var errors = (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("errors");
        Assert.True(errors.TryGetProperty("intervalMs", out _));
        Assert.True(errors.TryGetProperty("countryCodeOverride", out _));
        Assert.True(errors.TryGetProperty("finance.currency", out _));
        Assert.True(errors.TryGetProperty("finance.price", out _));

        // key rotation changes the key and the mask
        var rotated = await MasterFactory.DataAsync(await auth.PostAsync($"/api/nodes/{id}/rotate-key", null));
        var newKey = rotated.GetProperty("agentKey").GetString()!;
        Assert.NotEqual(key, newKey);
        var revealed = await MasterFactory.DataAsync(await auth.PostAsync($"/api/nodes/{id}/reveal-key", null));
        Assert.Equal(newKey, revealed.GetProperty("agentKey").GetString());

        // install script renders the key and one-liner
        var install = await MasterFactory.DataAsync(await auth.GetAsync($"/api/nodes/{id}/install-script"));
        Assert.Contains(newKey, install.GetProperty("script").GetString());
        Assert.StartsWith("curl -fsSL https://m.test/install/", install.GetProperty("oneLiner").GetString());
        var token = install.GetProperty("token").GetString()!;
        var anon = factory.CreateClient();
        var script = await anon.GetAsync($"/install/{token}");
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Contains("systemctl", await script.Content.ReadAsStringAsync());
        var ps = await anon.GetAsync($"/install/{token}?os=windows");
        Assert.Contains("Register-ScheduledTask", await ps.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/install/not-a-token")).StatusCode);

        // delete cascades metric/traffic rows
        var dbFactory = factory.Services.GetRequiredService<IDbContextFactory<SnmDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.Metrics1m.Add(new Metric1m { NodeId = id, Ts = 1_700_000_000, Samples = 1 });
            db.TrafficDaily.Add(new TrafficDaily { NodeId = id, Date = new DateOnly(2026, 1, 1), RxBytes = 1, TxBytes = 1, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var del = await auth.DeleteAsync($"/api/nodes/{id}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await auth.GetAsync($"/api/nodes/{id}")).StatusCode);
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            Assert.False(await db.Metrics1m.AnyAsync(x => x.NodeId == id));
            Assert.False(await db.TrafficDaily.AnyAsync(x => x.NodeId == id));
            Assert.False(await db.InstallTokens.AnyAsync(x => x.NodeId == id));
        }
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/install/{token}")).StatusCode);
    }

    [Fact]
    public async Task UnicodeNamesRoundTrip()
    {
        var auth = await factory.LoginAsync();
        var created = await MasterFactory.DataAsync(await auth.PostAsJsonAsync("/api/nodes", new { publicName = "香港节点-01", adminRemark = "核心 DB-勿动" }));
        var id = created.GetProperty("id").GetInt32();
        var detail = await MasterFactory.DataAsync(await auth.GetAsync($"/api/nodes/{id}"));
        Assert.Equal("香港节点-01", detail.GetProperty("publicName").GetString());
        Assert.Equal("核心 DB-勿动", detail.GetProperty("adminRemark").GetString());
        var found = await MasterFactory.DataAsync(await auth.GetAsync("/api/nodes?keyword=" + Uri.EscapeDataString("勿动")));
        Assert.Equal(1, found.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task ListSupportsKeywordAndPaging()
    {
        var auth = await factory.LoginAsync();
        for (var i = 0; i < 3; i++) (await auth.PostAsJsonAsync("/api/nodes", new { publicName = $"List-Node-{i}", adminRemark = i == 1 ? "needle" : null })).EnsureSuccessStatusCode();
        var page = await MasterFactory.DataAsync(await auth.GetAsync("/api/nodes?keyword=List-Node&pageSize=2&pageNo=1"));
        Assert.Equal(3, page.GetProperty("total").GetInt32());
        Assert.Equal(2, page.GetProperty("pageData").GetArrayLength());
        var needle = await MasterFactory.DataAsync(await auth.GetAsync("/api/nodes?keyword=needle"));
        Assert.Equal(1, needle.GetProperty("total").GetInt32());
        Assert.Equal("List-Node-1", needle.GetProperty("pageData")[0].GetProperty("publicName").GetString());
    }

    [Fact]
    public async Task SettingsPatchValidatesAndChannelsRejectBadConfig()
    {
        var auth = await factory.LoginAsync();
        var settings = await MasterFactory.DataAsync(await auth.GetAsync("/api/settings"));
        Assert.Equal("https://m.test", settings.GetProperty("site").GetProperty("publicBaseUrl").GetString());
        Assert.False(settings.TryGetProperty("auth", out var a) && a.TryGetProperty("jwtSecret", out _));

        var bad = await auth.PatchJsonAsync("/api/settings", new { alert = new { cpuPct = 10 }, site = new { timeZone = "Mars/Olympus" } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var errors = (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("errors");
        Assert.True(errors.TryGetProperty("alert.cpuPct", out _));
        Assert.True(errors.TryGetProperty("site.timeZone", out _));

        var ok = await auth.PatchJsonAsync("/api/settings", new { alert = new { cpuPct = 85 } });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(85, (await MasterFactory.DataAsync(ok)).GetProperty("alert").GetProperty("cpuPct").GetInt32());

        var badChannel = await auth.PostAsJsonAsync("/api/settings/channels", new { type = "telegram", name = "tg", config = new { botToken = "bad", chatId = "" } });
        Assert.Equal(HttpStatusCode.BadRequest, badChannel.StatusCode);
        var goodChannel = await auth.PostAsJsonAsync("/api/settings/channels", new { type = "webhook", name = "hook", config = new { url = "https://hooks.example.com/x", secret = "s3cret" } });
        Assert.Equal(HttpStatusCode.Created, goodChannel.StatusCode);
        var created = await MasterFactory.DataAsync(goodChannel);
        Assert.Equal("****", created.GetProperty("config").GetProperty("secret").GetString());
        var list = await MasterFactory.DataAsync(await auth.GetAsync("/api/settings/channels"));
        Assert.Contains(list.EnumerateArray(), c => c.GetProperty("name").GetString() == "hook");
    }
}
