using System.Security.Cryptography;
using System.Text.Json;
using OpenXLR.Core;

namespace OpenXLR.Daemon;

/// <summary>
/// The control API's credential. The loopback bind and the Origin check
/// keep other machines and web pages out; the token keeps other local
/// users out: it lives in a file only this user can read, is new at every
/// daemon start, and a client's first message must present it before the
/// daemon sends anything or accepts a command.
/// </summary>
public static class ApiToken
{
    private static string? _current;

    /// <summary>The token in force, or null before <see cref="Initialize"/>.</summary>
    public static string? Current => _current;

    /// <summary>Generate a fresh token and write it for the clients. Returns the file path.</summary>
    public static string Initialize()
    {
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        OpenXlrPaths.WriteAtomic(OpenXlrPaths.TokenPath, token + "\n");
        _current = token;
        return OpenXlrPaths.TokenPath;
    }

    /// <summary>Whether a client's first message is an "auth" carrying the current token.</summary>
    public static bool Accepts(ReadOnlySpan<byte> firstMessage) => Matches(_current, firstMessage);

    /// <summary>Pure check, for tests: the message must be {"cmd":"auth","token":expected}.</summary>
    public static bool Matches(string? expected, ReadOnlySpan<byte> message)
    {
        if (expected is null) return false;
        string? presented;
        try
        {
            using var doc = JsonDocument.Parse(message.ToArray());
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!doc.RootElement.TryGetProperty("cmd", out JsonElement cmd) || cmd.ValueKind != JsonValueKind.String || cmd.GetString() != "auth") return false;
            if (!doc.RootElement.TryGetProperty("token", out JsonElement tok) || tok.ValueKind != JsonValueKind.String) return false;
            presented = tok.GetString();
        }
        catch (JsonException) { return false; }
        if (presented is null) return false;
        // Constant time, so the comparison leaks nothing about how much matched.
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(presented), System.Text.Encoding.UTF8.GetBytes(expected));
    }
}
