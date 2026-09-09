using System.Security.Cryptography;
using System.Text;

namespace SNM.Master.Auth;

/// <summary>PBKDF2-SHA256 password hashing: pbkdf2-sha256$iterations$saltB64$hashB64.</summary>
public static class PasswordHasher
{
    public const int Iterations = 210_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        try
        {
            var parts = stored.Split('$');
            if (parts.Length != 4 || parts[0] != "pbkdf2-sha256") return false;
            var iterations = int.Parse(parts[1]);
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Random password for first start: 16 chars from an unambiguous alphabet.</summary>
    public static string GeneratePassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789";
        var bytes = RandomNumberGenerator.GetBytes(16);
        var sb = new StringBuilder(16);
        foreach (var b in bytes) sb.Append(alphabet[b % alphabet.Length]);
        return sb.ToString();
    }
}

public static class Tokens
{
    /// <summary>32 random bytes as base64url (43 chars).</summary>
    public static string RandomBase64Url(int bytes = 32) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    public static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Sha256Base64(string value) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>snmk_ + 43 base64url chars.</summary>
    public static string NewAgentKey() => SNM.Contracts.ProtocolConstants.AgentKeyPrefix + RandomBase64Url();

    public static bool IsAgentKeyFormat(string? key)
    {
        if (key is null || key.Length != SNM.Contracts.ProtocolConstants.AgentKeyLength) return false;
        if (!key.StartsWith(SNM.Contracts.ProtocolConstants.AgentKeyPrefix, StringComparison.Ordinal)) return false;
        for (var i = SNM.Contracts.ProtocolConstants.AgentKeyPrefix.Length; i < key.Length; i++)
        {
            var c = key[i];
            if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_')) return false;
        }
        return true;
    }
}
