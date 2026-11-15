using System.Security.Cryptography;
using System.Text;

namespace MySSH.Storage;

/// <summary>Name-based UUID (RFC 9562 version 8, SHA-256). Used so re-running an import produces the same ids.</summary>
internal static class DeterministicGuid
{
    public static Guid Create(string name)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes("myssh-desktop:" + name), hash);
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80); // version 8
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC variant
        return new Guid(bytes, bigEndian: true);
    }
}
