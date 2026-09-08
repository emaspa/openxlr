using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace OpenXLR.Core.Mixing;

/// <summary>The optional, versioned bridge package and OpenXLR's private wrappers.</summary>
internal sealed record ManagedYabridge(string Directory, string Version, string SourceCommit)
{
    internal static readonly string[] RequiredFiles =
    [
        "yabridgectl", "yabridge-host.exe", "yabridge-host.exe.so",
        "libyabridge-vst2.so", "libyabridge-vst3.so", "libyabridge-clap.so",
        "libyabridge-chainloader-vst2.so", "libyabridge-chainloader-vst3.so", "libyabridge-chainloader-clap.so",
    ];

    public string Controller => Path.Combine(Directory, "yabridgectl");
    public string CacheKey => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SourceCommit + "\n" + Version + "\n" + Directory)));
    public static string PluginHome
    {
        get
        {
            string? data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (string.IsNullOrWhiteSpace(data) || !Path.IsPathRooted(data))
                data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            return Path.Combine(data, "openxlr", "yabridge");
        }
    }

    public static ManagedYabridge? Discover() => Find(Environment.GetEnvironmentVariable("OPENXLR_YABRIDGE"),
        [Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "yabridge")),
         "/usr/lib/openxlr/yabridge", "/usr/local/lib/openxlr/yabridge"]);

    internal static ManagedYabridge? Find(string? choice, IEnumerable<string> candidates)
    {
        if (string.Equals(choice, "system", StringComparison.OrdinalIgnoreCase)) return null;
        if (!string.IsNullOrWhiteSpace(choice)) candidates = [choice];
        foreach (string candidate in candidates)
        {
            if (!Path.IsPathRooted(candidate)) continue;
            try
            {
                if (RequiredFiles.Any(file => !File.Exists(Path.Combine(candidate, file)))) continue;
                using JsonDocument receipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(candidate, "openxlr-yabridge.json")));
                JsonElement root = receipt.RootElement;
                if (root.GetProperty("formatVersion").GetInt32() != 1 || !root.GetProperty("wineInputFix").GetBoolean()) continue;
                string? version = root.GetProperty("version").GetString();
                string? commit = root.GetProperty("sourceCommit").GetString();
                if (string.IsNullOrWhiteSpace(version) || commit?.Length != 40 || !commit.All(char.IsAsciiHexDigit)) continue;
                return new(Path.GetFullPath(candidate), version, commit);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                or KeyNotFoundException or InvalidOperationException or FormatException) { }
        }
        return null;
    }

    // Runtime selection only changes PATH in the isolated scanner/host. It
    // leaves the plugin's HOME, Wine prefix and settings directories alone.
    public Dictionary<string, string> HostEnvironment() => new()
    {
        ["PATH"] = Directory + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin"),
    };

    public Dictionary<string, string> ControllerEnvironment()
    {
        var environment = HostEnvironment();
        environment["XDG_CONFIG_HOME"] = OpenXlrPaths.ConfigFile("bridge");
        environment["OPENXLR_YABRIDGE_PLUGIN_HOME"] = PluginHome;
        return environment;
    }
}
