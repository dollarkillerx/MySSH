using System.Net;
using MySSH.Ai;
using MySSH.Core;

namespace MySSH.Desktop.Services;

/// <summary>
/// AI assistant settings and provider instances. Settings are JSON in the settings table (<see cref="AiSettings.Key"/>);
/// each provider's API key is a vault-encrypted secret. One shared HttpClient (no overall timeout: responses stream for
/// minutes; every request is cancelled by its own token).
/// </summary>
public sealed class AiSettingsService(ISettingsStore settings, ISecretSettingsStore secrets, HttpClient? http = null) : IDisposable
{
    readonly HttpClient _http = http ?? CreateHttpClient();
    readonly bool _ownsHttp = http is null;

    public AiSettings Current { get; private set; } = new();

    /// <summary>Raised after <see cref="SaveAsync"/> (any thread).</summary>
    public event Action? Changed;

    public HttpClient Http => _http;

    /// <summary>Tests replace this to script providers.</summary>
    public Func<AiProviderConfig, string?, HttpClient, IChatProvider> ProviderFactory { get; set; } = AiProviders.Create;

    public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(20),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public async Task<AiSettings> LoadAsync(CancellationToken ct = default)
    {
        Current = await settings.GetAsync(AiSettings.Key, AiJsonContext.Default.AiSettings, ct).ConfigureAwait(false) ?? new AiSettings();
        return Current;
    }

    public async Task SaveAsync(AiSettings value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        await settings.SetAsync(AiSettings.Key, value, AiJsonContext.Default.AiSettings, ct).ConfigureAwait(false);
        Current = value;
        Changed?.Invoke();
    }

    public Task<string?> GetApiKeyAsync(AiProviderConfig provider, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return secrets.GetAsync(provider.ApiKeySecretName, ct);
    }

    /// <summary>Null or empty removes the key (keyless local servers).</summary>
    public async Task SetApiKeyAsync(AiProviderConfig provider, string? apiKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (string.IsNullOrWhiteSpace(apiKey)) await secrets.RemoveAsync(provider.ApiKeySecretName, ct).ConfigureAwait(false);
        else await secrets.SetAsync(provider.ApiKeySecretName, apiKey.Trim(), ct).ConfigureAwait(false);
    }

    /// <summary>Removes the provider and its key; the default moves to the first remaining provider.</summary>
    public async Task RemoveProviderAsync(Guid id, CancellationToken ct = default)
    {
        var current = Current;
        if (current.Providers.FirstOrDefault(p => p.Id == id) is not { } removed) return;
        await secrets.RemoveAsync(removed.ApiKeySecretName, ct).ConfigureAwait(false);
        var rest = current.Providers.Where(p => p.Id != id).ToList();
        await SaveAsync(current with
        {
            Providers = rest,
            DefaultProviderId = current.DefaultProviderId == id ? rest.FirstOrDefault()?.Id : current.DefaultProviderId,
            DataNoticeAcknowledged = [.. current.DataNoticeAcknowledged.Where(g => g != id)],
        }, ct).ConfigureAwait(false);
    }

    /// <summary>A provider instance with its current key (the key is read from the vault each time).</summary>
    public async Task<IChatProvider> CreateProviderAsync(AiProviderConfig provider, CancellationToken ct = default) =>
        ProviderFactory(provider, await GetApiKeyAsync(provider, ct).ConfigureAwait(false), _http);

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
