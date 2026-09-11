using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SNM.Master.Api;
using SNM.Master.Options;

namespace SNM.Master.Services;

public sealed class ThemeManifest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "0.0.0";
    public string? Author { get; set; }
    public string? Description { get; set; }
    public string? Homepage { get; set; }
    public string Entry { get; set; } = "index.html";
    public string? Preview { get; set; }
    public int Sdk { get; set; } = 1;
}

public sealed record ThemeInfo(string Id, string Name, string Version, string? Author, string? Description, string? Homepage, string Entry, string? Preview, int Sdk, bool BuiltIn, string Directory)
{
    public object ToDto(bool active) => new
    {
        id = Id, name = Name, version = Version, author = Author, description = Description, homepage = Homepage, entry = Entry, sdk = Sdk, builtIn = BuiltIn, active,
        url = $"/themes/{Id}/", preview = Preview is null ? null : $"/themes/{Id}/{Preview}",
    };
}

/// <summary>
/// Public-dashboard theme registry (docs/THEMES.md): built-in themes ship in wwwroot/themes, user themes are unpacked into
/// &lt;DataDir&gt;/themes/&lt;id&gt;. The active theme (setting site.theme) is served at "/", every installed theme at "/themes/{id}/".
/// </summary>
public sealed class ThemeService(IOptions<SnmOptions> options, DataPaths paths, IHostEnvironment env, SettingsService settings, ILogger<ThemeService> logger)
{
    public const string DefaultId = "default";
    public const int SupportedSdk = 1;
    public static readonly Regex IdRule = new("^[a-z0-9][a-z0-9-]{1,31}$", RegexOptions.Compiled);

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm", ".css", ".js", ".mjs", ".json", ".map", ".svg", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".avif", ".ico",
        ".woff", ".woff2", ".ttf", ".otf", ".eot", ".txt", ".md", ".webmanifest", ".mp4", ".webm", ".mp3", ".wasm",
    };

    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web) { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private readonly Lock _sync = new();
    private volatile Dictionary<string, ThemeInfo> _themes = new(StringComparer.Ordinal);
    private bool _loaded;

    public string BuiltInDir
    {
        get
        {
            var dev = options.Value.Dev.WebSourceDir;
            if (dev.Length > 0)
            {
                var devThemes = Path.Combine(Path.GetFullPath(Path.Combine(env.ContentRootPath, dev)), "themes");
                if (Directory.Exists(devThemes)) return devThemes;
            }
            var configured = options.Value.Themes.BuiltInDir;
            return Path.IsPathRooted(configured) ? configured : Path.GetFullPath(Path.Combine(env.ContentRootPath, configured));
        }
    }

    public string UserDir => options.Value.Themes.UserDir.Length > 0 ? Path.GetFullPath(options.Value.Themes.UserDir) : Path.Combine(paths.DataDir, "themes");

    public IReadOnlyCollection<ThemeInfo> All
    {
        get { EnsureLoaded(); return _themes.Values.OrderByDescending(t => t.BuiltIn).ThenBy(t => t.Id, StringComparer.Ordinal).ToList(); }
    }

    public ThemeInfo? Get(string? id)
    {
        EnsureLoaded();
        return id is not null && _themes.TryGetValue(id, out var t) ? t : null;
    }

    public string ActiveId
    {
        get
        {
            var id = settings.Snapshot.Theme;
            return Get(id) is not null ? id : DefaultId;
        }
    }

    /// <summary>Active theme, falling back to "default"; null when no theme is installed at all (web assets not built).</summary>
    public ThemeInfo? Active => Get(ActiveId) ?? Get(DefaultId) ?? All.FirstOrDefault();

    private void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_sync)
        {
            if (_loaded) return;
            ReloadCore();
            _loaded = true;
        }
    }

    public void Reload()
    {
        lock (_sync) { ReloadCore(); _loaded = true; }
    }

    private void ReloadCore()
    {
        var map = new Dictionary<string, ThemeInfo>(StringComparer.Ordinal);
        Scan(BuiltInDir, builtIn: true, map);
        Scan(UserDir, builtIn: false, map);
        _themes = map;
        logger.LogInformation("Themes loaded: {Themes} (built-in dir {BuiltIn}, user dir {User})", string.Join(", ", map.Keys), BuiltInDir, UserDir);
    }

    private void Scan(string root, bool builtIn, Dictionary<string, ThemeInfo> map)
    {
        if (!Directory.Exists(root)) return;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var manifestPath = Path.Combine(dir, "theme.json");
            if (!File.Exists(manifestPath)) continue;
            try
            {
                var manifest = JsonSerializer.Deserialize<ThemeManifest>(File.ReadAllText(manifestPath), ManifestJson);
                var error = Validate(manifest, dir);
                if (error is not null) { logger.LogWarning("Theme at {Dir} ignored: {Error}", dir, error); continue; }
                var m = manifest!;
                if (map.TryGetValue(m.Id, out var existing) && existing.BuiltIn)
                {
                    logger.LogWarning("User theme {Id} at {Dir} ignored: a built-in theme has the same id", m.Id, dir);
                    continue;
                }
                map[m.Id] = new ThemeInfo(m.Id, m.Name, m.Version, m.Author, m.Description, m.Homepage, m.Entry, m.Preview, m.Sdk, builtIn, Path.GetFullPath(dir));
            }
            catch (JsonException ex)
            {
                logger.LogWarning("Theme at {Dir} has an invalid theme.json: {Error}", dir, ex.Message);
            }
        }
    }

    /// <summary>Manifest + file checks; returns an error message or null.</summary>
    public static string? Validate(ThemeManifest? m, string dir)
    {
        if (m is null) return "theme.json 为空";
        if (!IdRule.IsMatch(m.Id)) return "id 须为 2–32 位小写字母、数字或短横线";
        if (string.IsNullOrWhiteSpace(m.Name) || m.Name.Length > 64) return "name 不能为空且不超过 64 字符";
        if (m.Version.Length > 32) return "version 过长";
        if (m.Sdk > SupportedSdk) return $"主题需要 SDK {m.Sdk},当前 Master 支持 {SupportedSdk}";
        if (m.Entry.Contains("..") || m.Entry.StartsWith('/') || !m.Entry.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) return "entry 必须是主题目录内的 .html 文件";
        if (!File.Exists(Path.Combine(dir, m.Entry))) return $"入口文件 {m.Entry} 不存在";
        if (m.Preview is { Length: > 0 } && (m.Preview.Contains("..") || m.Preview.StartsWith('/'))) return "preview 路径非法";
        return null;
    }

    /// <summary>Resolves a request path inside a theme; false for traversal or missing files.</summary>
    public static bool TryMapFile(ThemeInfo theme, string relativePath, out string fullPath)
    {
        fullPath = "";
        var rel = relativePath.Replace('\\', '/').TrimStart('/');
        if (rel.Length == 0) rel = theme.Entry;
        if (rel.Contains("..") || rel.Contains(':') || rel.Contains('\0')) return false;
        var candidate = Path.GetFullPath(Path.Combine(theme.Directory, rel));
        var root = theme.Directory.EndsWith(Path.DirectorySeparatorChar) ? theme.Directory : theme.Directory + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(root, StringComparison.Ordinal)) return false;
        if (!File.Exists(candidate)) return false;
        if (!AllowedExtensions.Contains(Path.GetExtension(candidate))) return false;
        fullPath = candidate;
        return true;
    }

    /// <summary>Installs (or replaces) a user theme from a zip package. The manifest may sit at the root or inside a single top-level folder.</summary>
    public async Task<ThemeInfo> InstallAsync(Stream zipStream, CancellationToken ct)
    {
        var maxBytes = options.Value.Themes.MaxUploadBytes;
        var temp = Path.Combine(Path.GetTempPath(), "snm-theme-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count == 0) throw ApiException.BadRequest("主题包为空");
            if (archive.Entries.Count > 1000) throw ApiException.BadRequest("主题包文件过多(> 1000)");
            var manifestEntry = archive.Entries.Where(e => e.Name == "theme.json").OrderBy(e => e.FullName.Count(c => c == '/')).FirstOrDefault()
                ?? throw ApiException.BadRequest("主题包中没有 theme.json");
            var prefix = manifestEntry.FullName[..^"theme.json".Length];   // "" or "folder/"
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (name.EndsWith('/')) continue;                                    // directory
                if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;     // outside the theme folder
                var rel = name[prefix.Length..];
                if (rel.Length == 0 || rel.StartsWith("__MACOSX") || Path.GetFileName(rel).StartsWith("._")) continue;
                if (rel.Split('/').Any(seg => seg is ".." or "." or "") || rel.Contains(':')) throw ApiException.BadRequest($"非法路径: {rel}");
                if (!AllowedExtensions.Contains(Path.GetExtension(rel))) throw ApiException.BadRequest($"不允许的文件类型: {rel}");
                total += entry.Length;
                if (total > maxBytes) throw ApiException.BadRequest($"解压后超过 {maxBytes / 1024 / 1024} MB");
                var target = Path.GetFullPath(Path.Combine(temp, rel));
                if (!target.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !target.StartsWith(temp + '/', StringComparison.Ordinal)) throw ApiException.BadRequest($"非法路径: {rel}");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var src = entry.Open();
                await using var dst = File.Create(target);
                await src.CopyToAsync(dst, ct);
            }

            var manifest = JsonSerializer.Deserialize<ThemeManifest>(await File.ReadAllTextAsync(Path.Combine(temp, "theme.json"), ct), ManifestJson);
            var error = Validate(manifest, temp);
            if (error is not null) throw ApiException.BadRequest($"theme.json 无效: {error}");
            var id = manifest!.Id;
            if (Get(id) is { BuiltIn: true }) throw ApiException.Business(409, 409, $"主题 id \"{id}\" 与内置主题冲突,请改名");

            Directory.CreateDirectory(UserDir);
            var final = Path.Combine(UserDir, id);
            var old = final + ".old";
            lock (_sync)
            {
                if (Directory.Exists(old)) Directory.Delete(old, true);
                if (Directory.Exists(final)) Directory.Move(final, old);
                Directory.Move(temp, final);
                if (Directory.Exists(old)) Directory.Delete(old, true);
                ReloadCore();
                _loaded = true;
            }
            logger.LogWarning("Theme {Id} v{Version} installed to {Dir}", id, manifest.Version, final);
            return Get(id)!;
        }
        catch (InvalidDataException ex)
        {
            throw ApiException.BadRequest("不是有效的 zip 文件: " + ex.Message);
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { /* best effort */ }
        }
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        var theme = Get(id) ?? throw ApiException.NotFound("主题不存在");
        if (theme.BuiltIn) throw ApiException.Business(400, 400, "内置主题不能删除");
        if (ActiveId == id) await settings.SetInternalAsync("site.theme", SettingsService.J(DefaultId), ct);
        lock (_sync)
        {
            if (Directory.Exists(theme.Directory)) Directory.Delete(theme.Directory, true);
            ReloadCore();
        }
        logger.LogWarning("Theme {Id} deleted", id);
    }
}
