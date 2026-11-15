using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace MySSH.Storage;

/// <summary>Argon2id cost parameters. Persisted next to every salt so they can be raised later without breaking old files.</summary>
public sealed record Argon2Parameters(int MemoryKiB, int Iterations, int Parallelism)
{
    /// <summary>Design §5.1: m = 64 MiB, t = 3, p = 1.</summary>
    public static Argon2Parameters Default { get; } = new(64 * 1024, 3, 1);

    public const int SaltSize = 16;

    // Upper bounds accepted when READING params from a file (backup import): a crafted file must not be able to make us
    // allocate gigabytes or spin for minutes.
    internal const int MaxMemoryKiB = 1024 * 1024;
    internal const int MaxIterations = 64;
    internal const int MaxParallelism = 16;

    internal bool IsWithinSafeBounds =>
        MemoryKiB >= 8 * Parallelism && MemoryKiB <= MaxMemoryKiB
        && Iterations is >= 1 and <= MaxIterations
        && Parallelism is >= 1 and <= MaxParallelism;

    internal void Validate()
    {
        if (!IsWithinSafeBounds)
            throw new ArgumentOutOfRangeException(nameof(Argon2Parameters), this, "Argon2id parameters are out of the supported range.");
    }
}

internal static class Argon2Kdf
{
    /// <summary>Derives a 32-byte key from <paramref name="password"/> (UTF-8, NFC-normalised). Runs off the calling thread.</summary>
    public static Task<byte[]> DeriveKeyAsync(string password, ReadOnlyMemory<byte> salt, Argon2Parameters parameters, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(password);
        parameters.Validate();
        var pwBytes = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormC));
        var saltCopy = salt.ToArray();
        return Task.Run(() =>
        {
            try
            {
                using var argon = new Argon2id(pwBytes)
                {
                    Salt = saltCopy,
                    MemorySize = parameters.MemoryKiB,
                    Iterations = parameters.Iterations,
                    DegreeOfParallelism = parameters.Parallelism,
                };
                return argon.GetBytes(32);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pwBytes);
            }
        }, ct);
    }

    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(Argon2Parameters.SaltSize);
}
