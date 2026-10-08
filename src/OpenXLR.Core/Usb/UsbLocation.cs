using System.Security.Cryptography;
using System.Text;

namespace OpenXLR.Core;

/// <summary>Exact current USB address plus a persistent serial or physical-port identity.</summary>
public sealed record UsbLocation(byte Bus, byte Address, string Port, string? Serial)
{
    public static bool IsInstanceId(string? id) => id is { Length: 26 } && id[4] == ':' && id[9] == '@'
        && id.Where((_, index) => index != 4 && index != 9).All(char.IsAsciiHexDigit);

    public string Key => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Serial is { Length: > 0 } ? Serial : Port)))[..16].ToLowerInvariant();
}
