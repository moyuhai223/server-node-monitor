using System.Reflection;
using MessagePack;
using SNM.Contracts.Dtos;

namespace SNM.Contracts.Tests;

/// <summary>
/// Guards the public-dashboard privacy contract (PRD 6.2): nothing that identifies the host, its addresses,
/// its finances or its secrets may exist on any DTO reachable from PublicSnapshotDto.
/// </summary>
public sealed class PublicDtoLeakTests
{
    private static readonly string[] ForbiddenFragments =
    [
        "ip", "host", "remark", "vendor", "price", "currency", "expire", "renew", "key", "token", "secret",
        "note", "kernel", "os", "virt", "email", "password", "remote",
    ];

    // Words that legitimately contain a forbidden fragment.
    private static readonly string[] Allowed = ["ShowSpecs", "OfflineTimeoutSec", "ShowTraffic"];

    public static IEnumerable<object[]> PublicTypes() =>
    [
        [typeof(PublicSnapshotDto)], [typeof(PublicSiteDto)], [typeof(PublicNodeDto)], [typeof(PublicNodeLiveDto)],
        [typeof(PublicHistoryDto)], [typeof(PublicBatchDto)],
    ];

    [Theory]
    [MemberData(nameof(PublicTypes))]
    public void Public_dtos_have_no_sensitive_members(Type type)
    {
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (Allowed.Contains(p.Name)) continue;
            var lower = p.Name.ToLowerInvariant();
            foreach (var frag in ForbiddenFragments)
            {
                Assert.False(lower.Contains(frag, StringComparison.Ordinal), $"{type.Name}.{p.Name} looks sensitive (contains '{frag}')");
            }
            var key = p.GetCustomAttribute<KeyAttribute>();
            Assert.NotNull(key);
            Assert.NotNull(key!.StringKey);
            var lowerKey = key.StringKey!.ToLowerInvariant();
            foreach (var frag in ForbiddenFragments)
            {
                if (Allowed.Any(a => string.Equals(a, p.Name, StringComparison.Ordinal))) continue;
                Assert.False(lowerKey.Contains(frag, StringComparison.Ordinal), $"{type.Name}.{p.Name} wire key '{key.StringKey}' looks sensitive");
            }
        }
    }

    [Fact]
    public void Public_dto_graph_only_references_public_types()
    {
        var visited = new HashSet<Type>();
        Visit(typeof(PublicSnapshotDto), visited);
        foreach (var t in visited)
        {
            Assert.True(t.Name.StartsWith("Public", StringComparison.Ordinal), $"{t.FullName} is reachable from PublicSnapshotDto");
        }
    }

    private static void Visit(Type t, HashSet<Type> visited)
    {
        if (t.IsArray) t = t.GetElementType()!;
        if (t.Namespace != typeof(PublicSnapshotDto).Namespace || !visited.Add(t)) return;
        foreach (var p in t.GetProperties()) Visit(p.PropertyType, visited);
    }

    [Fact]
    public void Browser_dtos_use_string_keys_and_agent_dtos_use_int_keys()
    {
        foreach (var t in typeof(PublicSnapshotDto).Assembly.GetTypes().Where(t => t.Namespace == typeof(PublicSnapshotDto).Namespace && t.IsClass))
        {
            var isBrowser = t.Name.StartsWith("Public", StringComparison.Ordinal) || t.Name.StartsWith("Admin", StringComparison.Ordinal);
            var keys = t.GetProperties().Select(p => p.GetCustomAttribute<KeyAttribute>()).ToList();
            Assert.All(keys, k => Assert.NotNull(k));
            if (isBrowser)
            {
                Assert.All(keys, k => Assert.NotNull(k!.StringKey));
            }
            else
            {
                Assert.All(keys, k => Assert.Null(k!.StringKey));
                var ints = keys.Select(k => k!.IntKey!.Value).OrderBy(x => x).ToArray();
                Assert.Equal(Enumerable.Range(0, ints.Length).ToArray(), ints); // contiguous, starting at 0
                var fieldCount = (int)t.GetField("FieldCount", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
                Assert.Equal(ints.Length, fieldCount);
            }
        }
    }
}
