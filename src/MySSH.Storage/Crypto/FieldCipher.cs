using System.Security.Cryptography;
using System.Text;

namespace MySSH.Storage;

/// <summary>
/// AES-256-GCM field encryption (design §5.1).
/// Blob layout: <c>version(1) | nonce(12) | ciphertext | tag(16)</c>; AAD = UTF-8 <c>"{table}:{id}:{field}"</c>,
/// which binds a ciphertext to its row and column (a blob copied to another record fails to decrypt).
/// </summary>
internal static class FieldCipher
{
    public const byte Version1 = 1;
    public const int KeySize = 32;
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int Overhead = 1 + NonceSize + TagSize;

    public static string Aad(string table, Guid id, string field) => $"{table}:{id:D}:{field}";

    public static byte[] Encrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext, string aad)
    {
        var aadBytes = Encoding.UTF8.GetBytes(aad);
        var blob = new byte[Overhead + plaintext.Length];
        blob[0] = Version1;
        var nonce = blob.AsSpan(1, NonceSize);
        var ciphertext = blob.AsSpan(1 + NonceSize, plaintext.Length);
        var tag = blob.AsSpan(1 + NonceSize + plaintext.Length, TagSize);
        RandomNumberGenerator.Fill(nonce);
        using var gcm = new AesGcm(key, TagSize);
        gcm.Encrypt(nonce, plaintext, ciphertext, tag, aadBytes);
        return blob;
    }

    /// <exception cref="CryptographicException">Wrong key, wrong AAD, tampered or malformed blob.</exception>
    public static byte[] Decrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> blob, string aad)
    {
        if (blob.Length < Overhead)
            throw new CryptographicException("Encrypted blob is too short.");
        if (blob[0] != Version1)
            throw new CryptographicException($"Unsupported encrypted blob version {blob[0]}.");
        var aadBytes = Encoding.UTF8.GetBytes(aad);
        var nonce = blob.Slice(1, NonceSize);
        var ctLength = blob.Length - Overhead;
        var ciphertext = blob.Slice(1 + NonceSize, ctLength);
        var tag = blob.Slice(1 + NonceSize + ctLength, TagSize);
        var plaintext = new byte[ctLength];
        using var gcm = new AesGcm(key, TagSize);
        gcm.Decrypt(nonce, ciphertext, tag, plaintext, aadBytes);
        return plaintext;
    }

    public static byte[] EncryptString(ReadOnlySpan<byte> key, string plaintext, string aad)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        try { return Encrypt(key, bytes, aad); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static string DecryptString(ReadOnlySpan<byte> key, ReadOnlySpan<byte> blob, string aad)
    {
        var bytes = Decrypt(key, blob, aad);
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (ArgumentException e) { throw new CryptographicException("Decrypted field is not valid UTF-8.", e); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <summary>Legacy (Tauri) format: <c>nonce(12) | ciphertext | tag(16)</c>, no version byte, no AAD.</summary>
    public static byte[] DecryptLegacy(ReadOnlySpan<byte> key, ReadOnlySpan<byte> combined)
    {
        if (combined.Length < NonceSize + TagSize)
            throw new CryptographicException("Invalid encrypted data (too short).");
        var nonce = combined[..NonceSize];
        var ctLength = combined.Length - NonceSize - TagSize;
        var ciphertext = combined.Slice(NonceSize, ctLength);
        var tag = combined.Slice(NonceSize + ctLength, TagSize);
        var plaintext = new byte[ctLength];
        using var gcm = new AesGcm(key, TagSize);
        gcm.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    /// <summary>Legacy format encrypt (tests / fixture generation only).</summary>
    public static byte[] EncryptLegacy(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> nonce)
    {
        var combined = new byte[NonceSize + plaintext.Length + TagSize];
        nonce.CopyTo(combined);
        using var gcm = new AesGcm(key, TagSize);
        gcm.Encrypt(nonce, plaintext, combined.AsSpan(NonceSize, plaintext.Length), combined.AsSpan(NonceSize + plaintext.Length, TagSize));
        return combined;
    }
}
