using Microsoft.Extensions.Logging;
using SNM.Contracts;

namespace SNM.Agent.Cli;

internal enum AgentCommand { Run, Test, Version, Help }

/// <summary>Hand-written CLI/env parsing (docs/PROTOCOL.md 7.2). Precedence: CLI > process env > --env-file.</summary>
internal sealed class CliOptions
{
    public AgentCommand Command = AgentCommand.Run;
    public string Server = "";
    public string Key = "";
    /// <summary>null = system/environment proxy; "none" = force direct; otherwise a proxy URL.</summary>
    public string? Proxy;
    public ushort IntervalMs = ProtocolConstants.DefaultIntervalMs;
    public string? Name;
    public string[]? NetIf;
    public string[]? DiskInclude;
    public string Transport = "auto";
    public bool Insecure;
    public LogLevel LogLevel = LogLevel.Information;
    public string? EnvFile;

    public static bool TryParse(string[] args, Func<string, string?> getEnv, out CliOptions opts, out string error)
    {
        opts = new CliOptions();
        error = "";
        var cli = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "run": opts.Command = AgentCommand.Run; continue;
                case "test": opts.Command = AgentCommand.Test; continue;
                case "--version" or "-v" or "version": opts.Command = AgentCommand.Version; return true;
                case "--help" or "-h" or "help": opts.Command = AgentCommand.Help; return true;
                case "--insecure": flags.Add("insecure"); continue;
            }
            if (!a.StartsWith("--", StringComparison.Ordinal)) { error = $"unknown argument: {a}"; return false; }
            string name, value;
            var eq = a.IndexOf('=');
            if (eq > 0) { name = a[2..eq]; value = a[(eq + 1)..]; }
            else
            {
                name = a[2..];
                if (i + 1 >= args.Length) { error = $"missing value for --{name}"; return false; }
                value = args[++i];
            }
            switch (name)
            {
                case "server" or "key" or "proxy" or "interval" or "name" or "net-if" or "disk-include" or "transport" or "log-level" or "env-file":
                    cli[name] = value;
                    break;
                default:
                    error = $"unknown option: --{name}";
                    return false;
            }
        }

        var fileEnv = new Dictionary<string, string>(StringComparer.Ordinal);
        var envFile = cli.GetValueOrDefault("env-file") ?? getEnv("SNM_ENV_FILE");
        if (!string.IsNullOrWhiteSpace(envFile))
        {
            opts.EnvFile = envFile;
            if (!TryLoadEnvFile(envFile, fileEnv, out error)) return false;
        }

        string? Get(string cliName, string envName) =>
            cli.TryGetValue(cliName, out var v) ? v
            : getEnv(envName) is { Length: > 0 } e ? e
            : fileEnv.GetValueOrDefault(envName);

        opts.Server = (Get("server", "SNM_SERVER") ?? "").Trim().TrimEnd('/');
        opts.Key = (Get("key", "SNM_KEY") ?? "").Trim();
        opts.Proxy = NullIfEmpty(Get("proxy", "SNM_PROXY"));
        opts.Name = NullIfEmpty(Get("name", "SNM_NAME"));
        opts.NetIf = SplitList(Get("net-if", "SNM_NET_IF"));
        opts.DiskInclude = SplitList(Get("disk-include", "SNM_DISK_INCLUDE"));
        opts.Transport = (Get("transport", "SNM_TRANSPORT") ?? "auto").Trim().ToLowerInvariant();
        opts.Insecure = flags.Contains("insecure") || Get("insecure", "SNM_INSECURE") is "1" or "true";

        var intervalRaw = Get("interval", "SNM_INTERVAL");
        if (!string.IsNullOrWhiteSpace(intervalRaw))
        {
            if (!int.TryParse(intervalRaw, out var ms)) { error = $"invalid --interval: {intervalRaw}"; return false; }
            opts.IntervalMs = (ushort)Math.Clamp(ms, ProtocolConstants.MinIntervalMs, ProtocolConstants.MaxIntervalMs);
        }

        var levelRaw = (Get("log-level", "SNM_LOG_LEVEL") ?? "info").Trim().ToLowerInvariant();
        opts.LogLevel = levelRaw switch
        {
            "trace" => LogLevel.Trace,
            "debug" => LogLevel.Debug,
            "info" or "information" => LogLevel.Information,
            "warn" or "warning" => LogLevel.Warning,
            "error" => LogLevel.Error,
            _ => LogLevel.None,
        };
        if (opts.LogLevel == LogLevel.None) { error = $"invalid --log-level: {levelRaw}"; return false; }

        if (opts.Transport is not ("auto" or "websockets" or "longpolling")) { error = $"invalid --transport: {opts.Transport}"; return false; }

        if (opts.Command == AgentCommand.Test) return true;

        if (opts.Server.Length == 0) { error = "--server (or SNM_SERVER) is required"; return false; }
        if (!Uri.TryCreate(opts.Server, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        { error = $"--server must be an http(s) origin such as https://monitor.example.com, got: {opts.Server}"; return false; }
        if (uri.AbsolutePath is not ("/" or "") || uri.Query.Length > 0) { error = "--server must be an origin without a path (the agent appends /hubs/agent)"; return false; }
        if (opts.Key.Length == 0) { error = "--key (or SNM_KEY) is required"; return false; }
        if (!IsKeyFormat(opts.Key)) { error = "--key has an invalid format (expected snmk_ + 43 characters)"; return false; }
        if (opts.Proxy is not null && opts.Proxy != "none")
        {
            if (!Uri.TryCreate(opts.Proxy, UriKind.Absolute, out var p) || p.Scheme is not ("http" or "https" or "socks5" or "socks4" or "socks4a"))
            { error = $"--proxy must be http(s)://, socks5://, socks4:// or socks4a:// (or 'none'), got: {opts.Proxy}"; return false; }
        }
        return true;
    }

    public static bool IsKeyFormat(string key)
    {
        if (key.Length != ProtocolConstants.AgentKeyLength || !key.StartsWith(ProtocolConstants.AgentKeyPrefix, StringComparison.Ordinal)) return false;
        for (var i = ProtocolConstants.AgentKeyPrefix.Length; i < key.Length; i++)
        {
            var c = key[i];
            if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_')) return false;
        }
        return true;
    }

    private static bool TryLoadEnvFile(string path, Dictionary<string, string> target, out string error)
    {
        error = "";
        try
        {
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var k = line[..eq].Trim();
                var v = line[(eq + 1)..].Trim();
                if (v.Length >= 2 && ((v[0] == '"' && v[^1] == '"') || (v[0] == '\'' && v[^1] == '\''))) v = v[1..^1];
                target[k] = v;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"cannot read --env-file {path}: {ex.Message}";
            return false;
        }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string[]? SplitList(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? null : parts;
    }
}

internal static class Usage
{
    public const string Text = """
        snm-agent - Server Node Monitor probe (read-only, no listening ports, no remote execution)

        Usage:
          snm-agent run  --server <https://host[:port]> --key <snmk_...> [options]
          snm-agent test [options]          collect one sample set and print it (no network)
          snm-agent --version | --help

        Options (CLI > environment > --env-file):
          --server <url>          SNM_SERVER        master origin (required)
          --key <agentKey>        SNM_KEY           node key from the admin UI (required)
          --proxy <url|none>      SNM_PROXY         http://, https://, socks5://, socks4://, socks4a:// or none
          --interval <ms>         SNM_INTERVAL      heartbeat interval 1000-60000 (server value wins), default 2000
          --name <hostname>       SNM_NAME          override the reported hostname
          --net-if <a,b>          SNM_NET_IF        NICs whose counters are summed (default: automatic filter)
          --disk-include </,/d>   SNM_DISK_INCLUDE  mount points to report (default: automatic filter)
          --transport <auto|websockets|longpolling>  SNM_TRANSPORT (default auto)
          --insecure              SNM_INSECURE=1    accept invalid TLS certificates (testing only)
          --log-level <level>     SNM_LOG_LEVEL     trace|debug|info|warn|error (default info)
          --env-file <path>       SNM_ENV_FILE      KEY=VALUE file (used by the Windows scheduled task)

        Exit codes: 0 ok / signal, 2 invalid arguments, 3 unsupported platform.
        """;
}
