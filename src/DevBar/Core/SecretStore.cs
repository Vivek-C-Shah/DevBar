using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DevBar.Core;

/// <summary>
/// API keys, encrypted at rest with DPAPI (current Windows user only) in
/// %LOCALAPPDATA%\DevBar\secrets.dat - never in config.json, which people
/// paste into bug reports. Falls back to a {NAME}_API_KEY environment
/// variable so a key already exported in a dev shell just works.
/// </summary>
internal static class SecretStore
{
    private static string FilePath => Path.Combine(Config.Dir, "secrets.dat");
    private static readonly byte[] Entropy = "DevBar.Jarvis.v1"u8.ToArray();
    private static readonly object Gate = new();
    private static Dictionary<string, string>? _cache;

    public static string? Get(string name)
    {
        lock (Gate)
        {
            Load();
            if (_cache!.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
        }
        var env = Environment.GetEnvironmentVariable($"{name.ToUpperInvariant()}_API_KEY");
        return string.IsNullOrWhiteSpace(env) ? null : env.Trim();
    }

    public static bool Has(string name) => Get(name) != null;

    public static void Set(string name, string? value)
    {
        lock (Gate)
        {
            Load();
            if (string.IsNullOrWhiteSpace(value)) _cache!.Remove(name);
            else _cache![name] = value.Trim();
            Save();
        }
    }

    /// <summary>"••••c79" - enough to recognise which key is saved, never the key.</summary>
    public static string Mask(string? value) =>
        string.IsNullOrEmpty(value) ? "" : "••••" + value[^Math.Min(4, value.Length)..];

    private static void Load()
    {
        if (_cache != null) return;
        _cache = new Dictionary<string, string>();
        try
        {
            if (!File.Exists(FilePath)) return;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            _cache = JsonSerializer.Deserialize<Dictionary<string, string>>(Encoding.UTF8.GetString(plain)) ?? new();
        }
        catch { /* unreadable (other user / corrupt) - start empty rather than crash */ }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Config.Dir);
            var plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(_cache));
            File.WriteAllBytes(FilePath, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
        }
        catch { /* never crash over secrets io */ }
    }
}
