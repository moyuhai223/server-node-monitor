namespace SNM.Master.Options;

/// <summary>"Snm" configuration section (appsettings.json / Snm__* env vars / SNM_* friendly env vars). Runtime-mutable
/// settings (thresholds, channels, site info) live in the Settings table instead - see SettingsService.</summary>
public sealed class SnmOptions
{
    public const string Section = "Snm";

    /// <summary>Kestrel listen URLs, ';' separated. Bind to 127.0.0.1 and put a reverse proxy in front for TLS.</summary>
    public string Listen { get; set; } = "http://127.0.0.1:5080";

    /// <summary>Directory for snm.db, geoip/ and backups/.</summary>
    public string DataDir { get; set; } = "./data";

    /// <summary>Seed for the site.publicBaseUrl setting (only applied when the setting is empty).</summary>
    public string PublicBaseUrl { get; set; } = "";

    /// <summary>Reverse proxies whose X-Forwarded-* headers are trusted.</summary>
    public string[] KnownProxies { get; set; } = ["127.0.0.1", "::1"];

    /// <summary>Trusted proxy networks in CIDR form (e.g. Docker bridge).</summary>
    public string[] KnownNetworks { get; set; } = [];

    public int ForwardLimit { get; set; } = 1;

    /// <summary>Seed for site.timeZone (IANA id). Empty = server local time zone.</summary>
    public string TimeZone { get; set; } = "";

    public AdminOptions Admin { get; set; } = new();
    public JwtOptions Jwt { get; set; } = new();
    public GeoIpOptions GeoIp { get; set; } = new();
    public DevOptions Dev { get; set; } = new();

    public sealed class AdminOptions
    {
        /// <summary>Initial admin user name (first start only).</summary>
        public string User { get; set; } = "admin";

        /// <summary>Initial admin password (first start only). Empty = generate a random one and log it once.</summary>
        public string Password { get; set; } = "";
    }

    public sealed class JwtOptions
    {
        /// <summary>HS256 secret, at least 32 chars. Empty = generated on first start and stored in Settings.</summary>
        public string Secret { get; set; } = "";
    }

    public sealed class GeoIpOptions
    {
        public bool Enabled { get; set; } = true;
        public string BaseUrl { get; set; } = "https://cdn.jsdelivr.net/npm/@ip-location-db/asn-country";
        public int RefreshDays { get; set; } = 7;
    }

    public sealed class DevOptions
    {
        /// <summary>Development only: serve themes from &lt;dir&gt;/themes and the SDK bundles from &lt;dir&gt;/sdk/dist (the repository's web/ folder).</summary>
        public string WebSourceDir { get; set; } = "";
    }

    public ThemesOptions Themes { get; set; } = new();

    public sealed class ThemesOptions
    {
        /// <summary>Built-in themes shipped with the master (relative to the content root).</summary>
        public string BuiltInDir { get; set; } = "wwwroot/themes";

        /// <summary>User-installed themes (default: &lt;DataDir&gt;/themes).</summary>
        public string UserDir { get; set; } = "";

        /// <summary>Maximum theme package size accepted by the upload endpoint.</summary>
        public long MaxUploadBytes { get; set; } = 20 * 1024 * 1024;
    }
}
