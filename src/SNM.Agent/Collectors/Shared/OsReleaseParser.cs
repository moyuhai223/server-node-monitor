using System;
using System.Collections.Generic;

namespace SNM.Agent.Collectors.Shared;

/// <summary>Normalizes Linux os-release data into a stable human-readable OS label.</summary>
internal static class OsReleaseParser
{
    public static string? Parse(IEnumerable<string> lines)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            var key = line[..separator].Trim();
            if (key is not ("ID" or "NAME" or "VERSION_ID" or "PRETTY_NAME")) continue;
            fields[key] = Unquote(line[(separator + 1)..].Trim());
        }

        fields.TryGetValue("ID", out var id);
        fields.TryGetValue("NAME", out var name);
        fields.TryGetValue("VERSION_ID", out var version);
        fields.TryGetValue("PRETTY_NAME", out var pretty);

        var family = CanonicalFamily(id, name, pretty);
        if (family is null) return Clean(pretty ?? name);
        if (!string.IsNullOrWhiteSpace(version)) return $"{family} {Clean(version)}";
        return Clean(pretty ?? name ?? family);
    }

    private static string? CanonicalFamily(string? id, string? name, string? pretty)
    {
        var token = id?.Trim().ToLowerInvariant();
        var fromId = token switch
        {
            "debian" => "Debian",
            "ubuntu" => "Ubuntu",
            "alpine" => "Alpine Linux",
            "rocky" => "Rocky Linux",
            "almalinux" or "alma" => "AlmaLinux",
            "centos" => "CentOS",
            "rhel" => "Red Hat Enterprise Linux",
            "fedora" => "Fedora",
            "arch" => "Arch Linux",
            "opensuse-leap" or "opensuse-tumbleweed" or "opensuse" => "openSUSE",
            "amzn" => "Amazon Linux",
            "ol" => "Oracle Linux",
            "linuxmint" => "Linux Mint",
            _ when token?.StartsWith("opensuse", StringComparison.Ordinal) == true => "openSUSE",
            _ => null,
        };
        if (fromId is not null) return fromId;

        var text = $"{name} {pretty}";
        if (text.Contains("debian", StringComparison.OrdinalIgnoreCase)) return "Debian";
        if (text.Contains("ubuntu", StringComparison.OrdinalIgnoreCase)) return "Ubuntu";
        if (text.Contains("alpine", StringComparison.OrdinalIgnoreCase)) return "Alpine Linux";
        if (text.Contains("rocky", StringComparison.OrdinalIgnoreCase)) return "Rocky Linux";
        if (text.Contains("alma", StringComparison.OrdinalIgnoreCase)) return "AlmaLinux";
        if (text.Contains("centos", StringComparison.OrdinalIgnoreCase)) return "CentOS";
        if (text.Contains("red hat", StringComparison.OrdinalIgnoreCase) || text.Contains("rhel", StringComparison.OrdinalIgnoreCase)) return "Red Hat Enterprise Linux";
        if (text.Contains("fedora", StringComparison.OrdinalIgnoreCase)) return "Fedora";
        if (text.Contains("arch linux", StringComparison.OrdinalIgnoreCase)) return "Arch Linux";
        if (text.Contains("opensuse", StringComparison.OrdinalIgnoreCase) || text.Contains("suse", StringComparison.OrdinalIgnoreCase)) return "openSUSE";
        if (text.Contains("amazon linux", StringComparison.OrdinalIgnoreCase)) return "Amazon Linux";
        if (text.Contains("oracle linux", StringComparison.OrdinalIgnoreCase)) return "Oracle Linux";
        if (text.Contains("linux mint", StringComparison.OrdinalIgnoreCase)) return "Linux Mint";
        return Clean(name);
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? null : string.Join(' ', parts);
    }

    private static string Unquote(string value)
    {
        if (value.Length < 2 || (value[0] != '"' && value[0] != '\'') || value[^1] != value[0]) return value;
        var inner = value[1..^1];
        var result = new System.Text.StringBuilder(inner.Length);
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '\\' && i + 1 < inner.Length)
            {
                result.Append(inner[++i] switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    _ => inner[i],
                });
            }
            else result.Append(inner[i]);
        }
        return result.ToString();
    }
}
