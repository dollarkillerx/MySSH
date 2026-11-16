using System.Text;
using MySSH.Core;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace MySSH.Ssh;

/// <summary>Turns an <see cref="Identity"/> + its secrets into SSH.NET authentication methods (passphrase prompts included).</summary>
internal static class Credentials
{
    /// <summary>Production rule (P0 Q4): an empty passphrase means "no passphrase".</summary>
    public static string? NormalizePassphrase(string? passphrase) => string.IsNullOrEmpty(passphrase) ? null : passphrase;

    public const string WrongPassphraseMessage = "Wrong passphrase or damaged private key.";

    /// <summary>
    /// Loads a private key. Encrypted key without a passphrase → prompt (user origin) or <see cref="SshFailureKind.PassphraseRequired"/>;
    /// wrong passphrase → re-prompt with an error message, at most <paramref name="maxAttempts"/> prompts, then
    /// <see cref="SshFailureKind.WrongPassphrase"/>. <paramref name="loader"/> is injectable for tests.
    /// </summary>
    public static async Task<T> LoadKeyAsync<T>(
        Identity identity, string privateKey, string? storedPassphrase, ConnectionOrigin origin, ICredentialPrompt prompt,
        Func<string, string?, T> loader, int maxAttempts, int hopIndex, string hopLabel, CancellationToken ct)
    {
        string? passphrase = NormalizePassphrase(storedPassphrase);
        string? error = null;
        int prompts = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return loader(privateKey, passphrase);
            }
            catch (SshPassPhraseNullOrEmptyException)
            {
                error = null;
            }
            catch (SshException) when (passphrase is not null)
            {
                // SSH.NET has no dedicated type: "random check bytes do not match", bad padding, ... (P0 Q4).
                error = prompts == 0 ? "The saved passphrase is not correct." : WrongPassphraseMessage;
            }
            catch (Exception ex) when (ex is SshException or InvalidOperationException or NotSupportedException or FormatException or ArgumentException)
            {
                throw new SshConnectException(SshFailureKind.InvalidKey,
                    $"The private key of identity '{identity.Label}' is not supported or damaged: {ex.Message}", hopIndex, hopLabel, ex);
            }

            if (HostKeyPolicy.IsStrict(origin))
            {
                throw error is null
                    ? new SshConnectException(SshFailureKind.PassphraseRequired,
                        $"The private key of identity '{identity.Label}' is encrypted and no passphrase is saved; {origin} connections cannot prompt.", hopIndex, hopLabel)
                    : new SshConnectException(SshFailureKind.WrongPassphrase, $"Identity '{identity.Label}': {WrongPassphraseMessage}", hopIndex, hopLabel);
            }
            if (prompts >= maxAttempts)
                throw new SshConnectException(SshFailureKind.WrongPassphrase, $"Identity '{identity.Label}': {WrongPassphraseMessage}", hopIndex, hopLabel);

            prompts++;
            var answer = await prompt.PromptPassphraseAsync(identity, error, ct);
            if (answer is null)
                throw new SshConnectException(SshFailureKind.PassphraseCancelled, $"Passphrase entry for identity '{identity.Label}' was cancelled.", hopIndex, hopLabel);
            passphrase = NormalizePassphrase(answer);
            if (passphrase is null) error = null; // empty answer: ask again without blaming the user
        }
    }

    public static PrivateKeyFile LoadPrivateKey(string privateKey, string? passphrase)
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(privateKey));
        return new PrivateKeyFile(ms, passphrase);
    }

    /// <summary>Authentication methods for one hop. Disposables (key files) are added to <paramref name="owned"/>.</summary>
    public static async Task<AuthenticationMethod[]> BuildAsync(
        PlannedHop hop, ChainPlan plan, IIdentityRepository identities, ICredentialPrompt prompt, int maxAttempts,
        List<IDisposable> owned, CancellationToken ct)
    {
        var label = hop.Host.Label;
        var identity = hop.Identity
            ?? throw new SshConnectException(SshFailureKind.InvalidConfiguration, $"Host '{label}' has no identity (credential) configured.", hop.Index, label);
        var user = hop.Username;
        if (string.IsNullOrEmpty(user))
            throw new SshConnectException(SshFailureKind.InvalidConfiguration, $"No username for '{label}': set it on the host or on identity '{identity.Label}'.", hop.Index, label);

        IdentitySecrets? secrets;
        try
        {
            secrets = await identities.GetSecretsAsync(identity.Id, ct);
        }
        catch (VaultLockedException ex)
        {
            throw new SshConnectException(SshFailureKind.InvalidConfiguration, "The vault is locked; unlock it to connect.", hop.Index, label, ex);
        }
        catch (SecretUnavailableException ex)
        {
            throw new SshConnectException(SshFailureKind.InvalidConfiguration, $"The secrets of identity '{identity.Label}' cannot be decrypted.", hop.Index, label, ex);
        }

        switch (identity.Kind)
        {
            case IdentityKind.PrivateKey:
                if (string.IsNullOrWhiteSpace(secrets?.PrivateKey))
                    throw new SshConnectException(SshFailureKind.InvalidConfiguration, $"Identity '{identity.Label}' has no private key stored.", hop.Index, label);
                var key = await LoadKeyAsync(identity, secrets.PrivateKey, secrets.Passphrase, plan.Origin, prompt,
                    LoadPrivateKey, maxAttempts, hop.Index, label, ct);
                owned.Add(key);
                return [new PrivateKeyAuthenticationMethod(user, key)];

            default:
                var password = secrets?.Password;
                if (password is null)
                    throw new SshConnectException(SshFailureKind.InvalidConfiguration, $"Identity '{identity.Label}' has no password stored.", hop.Index, label);
                var pw = new PasswordAuthenticationMethod(user, password);
                var kbd = new KeyboardInteractiveAuthenticationMethod(user);
                kbd.AuthenticationPrompt += (_, e) =>
                {
                    // Servers with PasswordAuthentication off but PAM keyboard-interactive on ask "Password:" here.
                    foreach (var p in e.Prompts)
                        if (!p.IsEchoed) p.Response = password;
                };
                owned.Add(pw);
                owned.Add(kbd);
                return [pw, kbd];
        }
    }
}
