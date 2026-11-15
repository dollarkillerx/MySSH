using System.Security.Cryptography;
using System.Text;

namespace MySSH.Storage.Tests;

public class CryptoTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void Field_cipher_round_trips_with_documented_layout()
    {
        var id = Guid.NewGuid();
        var aad = FieldCipher.Aad("identities", id, "password");
        Assert.Equal($"identities:{id:D}:password", aad);

        var blob = FieldCipher.EncryptString(Key, "pässwörd 🔑", aad);
        var plainLen = Encoding.UTF8.GetByteCount("pässwörd 🔑");
        Assert.Equal(1 + 12 + plainLen + 16, blob.Length);
        Assert.Equal(FieldCipher.Version1, blob[0]);
        Assert.Equal("pässwörd 🔑", FieldCipher.DecryptString(Key, blob, aad));

        var again = FieldCipher.EncryptString(Key, "pässwörd 🔑", aad);
        Assert.NotEqual(blob.AsSpan(1, 12).ToArray(), again.AsSpan(1, 12).ToArray()); // fresh nonce
    }

    [Theory]
    [InlineData("proxies", null, "password")]
    [InlineData("identities", "other", "password")]
    [InlineData("identities", null, "passphrase")]
    public void Field_cipher_rejects_aad_mismatch(string table, string? otherId, string field)
    {
        var id = Guid.NewGuid();
        var blob = FieldCipher.EncryptString(Key, "secret", FieldCipher.Aad("identities", id, "password"));
        var wrongAad = FieldCipher.Aad(table, otherId is null ? id : Guid.NewGuid(), field);
        Assert.ThrowsAny<CryptographicException>(() => FieldCipher.DecryptString(Key, blob, wrongAad));
    }

    [Fact]
    public void Field_cipher_rejects_wrong_key_tampering_and_bad_versions()
    {
        var aad = FieldCipher.Aad("t", Guid.Empty, "f");
        var blob = FieldCipher.EncryptString(Key, "secret", aad);
        Assert.ThrowsAny<CryptographicException>(() => FieldCipher.DecryptString(RandomNumberGenerator.GetBytes(32), blob, aad));

        var tampered = (byte[])blob.Clone();
        tampered[14] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => FieldCipher.DecryptString(Key, tampered, aad));

        var badVersion = (byte[])blob.Clone();
        badVersion[0] = 9;
        Assert.ThrowsAny<CryptographicException>(() => FieldCipher.DecryptString(Key, badVersion, aad));
        Assert.ThrowsAny<CryptographicException>(() => FieldCipher.DecryptString(Key, blob.AsSpan(0, 20), aad));
    }

    [Fact]
    public void Legacy_cipher_round_trips_without_aad()
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var combined = FieldCipher.EncryptLegacy(Key, "legacy"u8, nonce);
        Assert.Equal(12 + 6 + 16, combined.Length);
        Assert.Equal(nonce, combined[..12]);
        Assert.Equal("legacy"u8.ToArray(), FieldCipher.DecryptLegacy(Key, combined));
    }

    [Fact]
    public void Deterministic_guid_is_stable_and_version_8()
    {
        var a = DeterministicGuid.Create("legacy-identity:x");
        Assert.Equal(a, DeterministicGuid.Create("legacy-identity:x"));
        Assert.NotEqual(a, DeterministicGuid.Create("legacy-identity:y"));
        Assert.Equal('8', a.ToString("D")[14]);
    }

    [Fact]
    public void Default_argon2_parameters_match_design()
    {
        Assert.Equal(new Argon2Parameters(65536, 3, 1), Argon2Parameters.Default);
        Assert.Equal(16, Argon2Parameters.SaltSize);
    }

    [Fact]
    public async Task Argon2id_is_deterministic_for_same_salt_and_params()
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var k1 = await Argon2Kdf.DeriveKeyAsync("pw", salt, T.FastArgon2, T.Ct);
        var k2 = await Argon2Kdf.DeriveKeyAsync("pw", salt, T.FastArgon2, T.Ct);
        var k3 = await Argon2Kdf.DeriveKeyAsync("pw", salt, T.FastArgon2 with { Iterations = 2 }, T.Ct);
        Assert.Equal(32, k1.Length);
        Assert.Equal(k1, k2);
        Assert.NotEqual(k1, k3);
    }
}
