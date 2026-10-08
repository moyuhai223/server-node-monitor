using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SNM.Master.Options;
using SNM.Master.Background;
using SNM.Master.Data;
using SNM.Master.Data.Entities;
using SNM.Master.Runtime;
using SNM.Master.Services;

namespace SNM.Master.Tests;

public sealed class GeoIpTests(MasterFactory factory) : IClassFixture<MasterFactory>
{
    [Fact]
    public void Default_source_is_the_maintained_release_dataset()
    {
        Assert.Equal("https://github.com/sapics/ip-location-db/releases/download/latest", new SnmOptions().GeoIp.BaseUrl);
    }

    [Fact]
    public async Task Recent_legacy_cache_is_usable_but_requires_migration()
    {
        using var cache = new TestCache();
        await cache.WriteLegacyAsync();
        var geo = CreateGeo(cache);
        await geo.TryLoadFromDiskAsync(default);
        Assert.True(geo.Ready);
        Assert.Equal("US", geo.Lookup(IPAddress.Parse("217.142.185.22")));
        Assert.True(geo.NeedsRefresh());
    }

    [Fact]
    public async Task Refresh_migrates_legacy_default_and_corrects_Singapore_without_per_IP_rules()
    {
        using var cache = new TestCache();
        using var http = new DatasetHttp();
        await cache.WriteLegacyAsync();
        var geo = CreateGeo(cache, http, o => o.GeoIp.BaseUrl = SnmOptions.GeoIpOptions.LegacyBaseUrl + "/");
        await geo.TryLoadFromDiskAsync(default);
        await geo.RefreshAsync(default);

        Assert.Equal(new[]
        {
            SnmOptions.GeoIpOptions.DefaultBaseUrl + "/server-country-ipv4-num.csv",
            SnmOptions.GeoIpOptions.DefaultBaseUrl + "/server-country-ipv6-num.csv",
        }, http.Requests);
        foreach (var ip in new[] { "217.142.184.0", "217.142.185.22", "217.142.191.255", "::ffff:217.142.185.22", "2400::1" })
            Assert.Equal("SG", geo.Lookup(IPAddress.Parse(ip)));
        foreach (var ip in new[] { "217.142.183.255", "217.142.192.0", "8.8.8.8", "10.0.0.1", "127.0.0.1", "100.64.0.1", "::1", "fd00::1" })
            Assert.Null(geo.Lookup(IPAddress.Parse(ip)));
        Assert.Null(geo.Lookup(null));
        Assert.False(geo.NeedsRefresh());
        Assert.Equal(100_001, geo.Ipv4Rows);
        Assert.Equal(10_000, geo.Ipv6Rows);

        var reloaded = CreateGeo(cache);
        await reloaded.TryLoadFromDiskAsync(default);
        Assert.Equal("SG", reloaded.Lookup(IPAddress.Parse("217.142.185.22")));
        Assert.Equal("SG", reloaded.Lookup(IPAddress.Parse("2400::1")));
        Assert.False(reloaded.NeedsRefresh());
        Assert.Equal(geo.LastRefreshUtc, reloaded.LastRefreshUtc);

        http.V4 = http.V4.Replace(",SG", ",JP");
        await geo.RefreshAsync(default);
        Assert.Equal("JP", geo.Lookup(IPAddress.Parse("217.142.185.22")));
        Assert.Single(Directory.GetDirectories(cache.GeoDir, "generation-*"));
    }

    [Fact]
    public async Task Changing_mirror_or_dataset_invalidates_a_recent_cache()
    {
        using var cache = new TestCache();
        using var http = new DatasetHttp();
        await CreateGeo(cache, http).RefreshAsync(default);
        var geo = CreateGeo(cache, http, o =>
        {
            o.GeoIp.BaseUrl = "https://mirror.example/geoip/";
            o.GeoIp.Dataset = "custom-country";
        });
        await geo.TryLoadFromDiskAsync(default);
        Assert.True(geo.NeedsRefresh());
        await geo.RefreshAsync(default);
        Assert.Equal("https://mirror.example/geoip/custom-country-ipv6-num.csv", http.Requests.Last());
        Assert.False(geo.NeedsRefresh());
    }

    [Theory]
    [InlineData("http")]
    [InlineData("cancel")]
    [InlineData("truncated")]
    [InlineData("overlap")]
    [InlineData("reversed")]
    [InlineData("country")]
    [InlineData("malformed")]
    public async Task Failed_second_file_preserves_both_tables_and_disk_manifest(string failure)
    {
        using var cache = new TestCache();
        using var http = new DatasetHttp();
        var geo = CreateGeo(cache, http);
        await geo.RefreshAsync(default);
        var manifest = await File.ReadAllTextAsync(System.IO.Path.Combine(cache.GeoDir, "meta.json"));
        var refreshedAt = geo.LastRefreshUtc;
        http.V4 = http.V4.Replace(",SG", ",JP");
        var firstV6 = http.V6.Split('\n')[0];
        var parts = firstV6.Split(',');
        switch (failure)
        {
            case "http": http.FailV6 = true; break;
            case "cancel": http.CancelV6 = true; break;
            case "truncated": http.V6 = firstV6 + "\n"; break;
            case "overlap": http.V6 = firstV6 + "\n" + http.V6; break;
            case "reversed": http.V6 = http.V6.Replace(firstV6, $"{parts[1]},{parts[0]},SG"); break;
            case "country": http.V6 = http.V6.Replace(",SG", ",12"); break;
            case "malformed": http.V6 += "not,a,range\n"; break;
        }
        if (failure == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => geo.RefreshAsync(default));
        else if (failure == "http") await Assert.ThrowsAsync<HttpRequestException>(() => geo.RefreshAsync(default));
        else await Assert.ThrowsAsync<InvalidDataException>(() => geo.RefreshAsync(default));
        Assert.Equal("SG", geo.Lookup(IPAddress.Parse("217.142.185.22")));
        Assert.Equal("SG", geo.Lookup(IPAddress.Parse("2400::1")));
        Assert.Equal(refreshedAt, geo.LastRefreshUtc);
        Assert.Equal(manifest, await File.ReadAllTextAsync(System.IO.Path.Combine(cache.GeoDir, "meta.json")));
        Assert.Single(Directory.GetDirectories(cache.GeoDir, "generation-*"));
        var restarted = CreateGeo(cache);
        await restarted.TryLoadFromDiskAsync(default);
        Assert.True(restarted.Ready);
        Assert.Equal("SG", restarted.Lookup(IPAddress.Parse("217.142.185.22")));
        Assert.False(restarted.NeedsRefresh());
    }

    [Fact]
    public async Task Failed_migration_keeps_legacy_cache_available_and_still_due()
    {
        using var cache = new TestCache();
        using var http = new DatasetHttp { FailV6 = true };
        await cache.WriteLegacyAsync();
        var geo = CreateGeo(cache, http);
        await geo.TryLoadFromDiskAsync(default);
        await Assert.ThrowsAsync<HttpRequestException>(() => geo.RefreshAsync(default));
        Assert.Equal("US", geo.Lookup(IPAddress.Parse("217.142.185.22")));
        Assert.True(geo.NeedsRefresh());
        Assert.NotNull(geo.LastError);
        Assert.Empty(Directory.GetDirectories(cache.GeoDir, "generation-*"));
        var restarted = CreateGeo(cache);
        await restarted.TryLoadFromDiskAsync(default);
        Assert.Equal("US", restarted.Lookup(IPAddress.Parse("217.142.185.22")));
        Assert.True(restarted.NeedsRefresh());
    }

    [Fact]
    public async Task Caller_cancellation_discards_staging_and_preserves_the_current_cache()
    {
        using var cache = new TestCache();
        using var http = new DatasetHttp();
        var geo = CreateGeo(cache, http);
        await geo.RefreshAsync(default);
        var manifest = await File.ReadAllTextAsync(System.IO.Path.Combine(cache.GeoDir, "meta.json"));
        using var cancel = new CancellationTokenSource();
        http.BeforeV6 = cancel.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => geo.RefreshAsync(cancel.Token));
        Assert.Equal(manifest, await File.ReadAllTextAsync(System.IO.Path.Combine(cache.GeoDir, "meta.json")));
        Assert.Equal("SG", geo.Lookup(IPAddress.Parse("217.142.185.22")));
        Assert.Single(Directory.GetDirectories(cache.GeoDir, "generation-*"));
        Assert.Null(geo.LastError);
    }

    [Fact]
    public async Task Reconciliation_corrects_existing_and_offline_nodes_preserving_overrides_and_metadata()
    {
        using var cache = new TestCache();
        await cache.WriteLegacyAsync("SG");
        var geo = CreateGeo(cache);
        await geo.TryLoadFromDiskAsync(default);
        var dbFactory = factory.Services.GetRequiredService<IDbContextFactory<SnmDbContext>>();
        var registry = factory.Services.GetRequiredService<NodeRegistry>();
        var nodes = new[]
        {
            NewNode("US"), NewNode(null), NewNode("US", "JP"),
            NewNode("DE", ip: "192.168.0.1"), NewNode("DE", ip: "8.8.8.8"),
        };
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.Nodes.AddRange(nodes);
            await db.SaveChangesAsync();
        }
        foreach (var node in nodes) registry.Upsert(node);
        Assert.False(registry.Get(nodes[0].Id)!.Connected);
        var changed = new List<int>();
        void OnChanged(int[] ids) => changed.AddRange(ids);
        registry.NodesChanged += OnChanged;
        try
        {
            var countries = new NodeCountryService(geo, factory.Services.GetRequiredService<SettingsService>(), dbFactory, registry);
            Assert.Equal(3, await countries.RecalculateAsync(default));
            Assert.Equal(nodes.Take(3).Select(n => n.Id).Order(), changed.Order());
            Assert.Equal("SG", registry.Get(nodes[0].Id)!.CountryCode);
            Assert.Equal("JP", registry.Get(nodes[2].Id)!.CountryCode);
            Assert.Equal("SG", registry.Get(nodes[2].Id)!.Meta.CountryCodeAuto);
            Assert.Equal(0, await countries.RecalculateAsync(default));
            Assert.Equal(3, changed.Count);
            await using var db = await dbFactory.CreateDbContextAsync();
            var saved = await db.Nodes.AsNoTracking().SingleAsync(n => n.Id == nodes[2].Id);
            Assert.Equal("SG", saved.CountryCodeAuto);
            Assert.Equal("JP", saved.CountryCodeOverride);
            Assert.Equal("preserve this remark", saved.AdminRemark);
            Assert.Equal("DE", (await db.Nodes.FindAsync(nodes[3].Id))!.CountryCodeAuto);
        }
        finally { registry.NodesChanged -= OnChanged; }
    }

    [Fact]
    public async Task Admin_refresh_endpoint_recalculates_countries_and_reports_the_source()
    {
        using var http = new DatasetHttp();
        using var app = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Snm:GeoIp:Enabled", "true");
            builder.ConfigureServices(services => services.AddHttpClient("geoip").ConfigurePrimaryHttpMessageHandler(() => http));
        });
        using var client = app.CreateClient();
        var login = await MasterFactory.DataAsync(await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = MasterFactory.AdminUser, password = MasterFactory.AdminPassword,
        }));
        client.DefaultRequestHeaders.Authorization = new("Bearer", login.GetProperty("accessToken").GetString());
        var node = NewNode("US");
        var dbFactory = app.Services.GetRequiredService<IDbContextFactory<SnmDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.Nodes.Add(node);
            await db.SaveChangesAsync();
        }
        app.Services.GetRequiredService<NodeRegistry>().Upsert(node);
        var response = await client.PostAsync("/api/settings/geoip/refresh", null);
        response.EnsureSuccessStatusCode();
        var data = await MasterFactory.DataAsync(response);
        Assert.True(data.GetProperty("ready").GetBoolean());
        Assert.Equal("server-country", data.GetProperty("dataset").GetString());
        Assert.Equal("SG", app.Services.GetRequiredService<NodeRegistry>().Get(node.Id)!.CountryCode);
        await using (var db = await dbFactory.CreateDbContextAsync())
            Assert.Equal("SG", (await db.Nodes.FindAsync(node.Id))!.CountryCodeAuto);

        var status = await MasterFactory.DataAsync(await client.GetAsync("/api/settings/geoip/status"));
        Assert.Equal(SnmOptions.GeoIpOptions.DefaultBaseUrl, status.GetProperty("baseUrl").GetString());
        var settings = await MasterFactory.DataAsync(await client.GetAsync("/api/settings"));
        Assert.Equal("server-country", settings.GetProperty("geoip").GetProperty("dataset").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Background_refresh_and_cached_startup_reconcile_nonempty_countries_but_failure_does_not(bool timeout)
    {
        using var cache = new TestCache();
        using var http = new DatasetHttp { FailV6 = !timeout, CancelV6 = timeout };
        await cache.WriteLegacyAsync("SE");
        var geo = CreateGeo(cache, http);
        await geo.TryLoadFromDiskAsync(default);
        var dbFactory = factory.Services.GetRequiredService<IDbContextFactory<SnmDbContext>>();
        var registry = factory.Services.GetRequiredService<NodeRegistry>();
        var settings = factory.Services.GetRequiredService<SettingsService>();
        var node = NewNode("US");
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.Nodes.Add(node);
            await db.SaveChangesAsync();
        }
        registry.Upsert(node);
        var countries = new NodeCountryService(geo, settings, dbFactory, registry);
        var worker = new GeoIpRefreshService(geo, settings, countries, registry, NullLogger<GeoIpRefreshService>.Instance);
        await TickAsync(worker);
        Assert.Equal("US", registry.Get(node.Id)!.CountryCode);
        Assert.Equal("SE", geo.Lookup(IPAddress.Parse(node.LastRemoteIp!)));
        http.FailV6 = false;
        http.CancelV6 = false;
        await TickAsync(worker);
        Assert.Equal("SG", registry.Get(node.Id)!.CountryCode);

        await using (var db = await dbFactory.CreateDbContextAsync())
            await db.Nodes.Where(n => n.Id == node.Id).ExecuteUpdateAsync(s => s.SetProperty(n => n.CountryCodeAuto, "US"));
        registry.Get(node.Id)!.Meta.CountryCodeAuto = "US";
        var cached = CreateGeo(cache, http);
        await cached.TryLoadFromDiskAsync(default);
        var requests = http.Requests.Count;
        worker = new(cached, settings, new NodeCountryService(cached, settings, dbFactory, registry), registry, NullLogger<GeoIpRefreshService>.Instance);
        await TickAsync(worker);
        Assert.Equal(requests, http.Requests.Count);
        Assert.Equal("SG", registry.Get(node.Id)!.CountryCode);
    }

    private static Task TickAsync(GeoIpRefreshService worker) => (Task)typeof(GeoIpRefreshService)
        .GetMethod("TickAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .Invoke(worker, [DateTime.UtcNow, CancellationToken.None])!;

    private static Node NewNode(string? country, string? countryOverride = null, string ip = "217.142.185.22") => new()
    {
        PublicName = "geo-" + Guid.NewGuid().ToString("N"), AgentKey = Guid.NewGuid().ToString("N"),
        LastRemoteIp = ip, CountryCodeAuto = country, CountryCodeOverride = countryOverride,
        AdminRemark = "preserve this remark", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    private GeoIpService CreateGeo(TestCache cache, DatasetHttp? http = null, Action<SnmOptions>? configure = null)
    {
        var options = new SnmOptions { DataDir = cache.Path };
        configure?.Invoke(options);
        return new(Microsoft.Extensions.Options.Options.Create(options),
            http ?? factory.Services.GetRequiredService<IHttpClientFactory>(),
            factory.Services.GetRequiredService<SettingsService>(), NullLogger<GeoIpService>.Instance);
    }

    private sealed class DatasetHttp : HttpMessageHandler, IHttpClientFactory
    {
        private static readonly string V4Data = BuildV4();
        private static readonly string V6Data = BuildV6();
        public string V4 { get; set; } = V4Data;
        public string V6 { get; set; } = V6Data;
        public bool FailV6 { get; set; }
        public bool CancelV6 { get; set; }
        public Action? BeforeV6 { get; set; }
        public List<string> Requests { get; } = [];
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            var ipv6 = request.RequestUri.AbsolutePath.Contains("ipv6", StringComparison.Ordinal);
            if (ipv6) BeforeV6?.Invoke();
            ct.ThrowIfCancellationRequested();
            if (ipv6 && CancelV6) throw new OperationCanceledException(ct);
            return Task.FromResult(new HttpResponseMessage(ipv6 && FailV6 ? HttpStatusCode.BadGateway : HttpStatusCode.OK)
            { Content = new StringContent(ipv6 ? V6 : V4) });
        }
        private static string BuildV4()
        {
            var text = new StringBuilder();
            for (var i = 0; i < 100_000; i++) text.AppendLine($"{16777216 + i * 2},{16777217 + i * 2},US");
            // Oracle's published ap-singapore-2 range: 217.142.184.0/21.
            return text.AppendLine("3650009088,3650011135,SG").ToString();
        }
        private static string BuildV6()
        {
            var text = new StringBuilder();
            var start = (UInt128)0x2400 << 112;
            for (uint i = 0; i < 10_000; i++) text.AppendLine($"{start + i * 2},{start + i * 2 + 1},SG");
            return text.ToString();
        }
    }

    private sealed class TestCache : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "snm-geoip-tests", Guid.NewGuid().ToString("N"));
        public string GeoDir => System.IO.Path.Combine(Path, "geoip");

        public async Task WriteLegacyAsync(string country = "US")
        {
            Directory.CreateDirectory(GeoDir);
            await File.WriteAllTextAsync(System.IO.Path.Combine(GeoDir, "asn-country-ipv4-num.csv"), $"3650009088,3650011135,{country}\n");
            await File.WriteAllTextAsync(System.IO.Path.Combine(GeoDir, "meta.json"), JsonSerializer.Serialize(new { downloadedAtUtc = DateTime.UtcNow.ToString("O") }));
        }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, true);
        }
    }
}
