using Anthropic;

namespace AiSpike.Core;

/// <summary>
/// AnthropicClient that never resolves credentials from ANTHROPIC_API_KEY / ANTHROPIC_AUTH_TOKEN /
/// `ant` profiles / WIF env vars. Needed because <c>ApiKey = null</c> in an object initializer does NOT
/// stop the SDK from falling back to ANTHROPIC_AUTH_TOKEN (observed in EnvPitfallTests).
/// <see cref="AnthropicClient.ShouldAutoResolveCredentials"/> is the documented switch the cloud
/// clients use; user subclasses can override it.
/// </summary>
public class IsolatedAnthropicClient : AnthropicClient
{
    protected override bool ShouldAutoResolveCredentials => false;
}

/// <summary>
/// Last line of defence against the process environment (observed in EnvPitfallTests):
/// <list type="bullet">
/// <item>removes headers injected from ANTHROPIC_CUSTOM_HEADERS, which the SDK merges into every
/// request even when BaseUrl/ApiKey are explicit (headers the app configures itself are kept);</item>
/// <item>when the provider config has NO key, removes Authorization / x-api-key, because the SDK
/// falls back to ANTHROPIC_AUTH_TOKEN even with <c>ApiKey = null</c> and
/// <c>ShouldAutoResolveCredentials = false</c>.</item>
/// </list>
/// </summary>
public sealed class EnvIsolationHandler(bool hasConfiguredKey, IReadOnlyCollection<string>? keepHeaders = null) : DelegatingHandler
{
    private readonly HashSet<string> _keep = new(keepHeaders ?? [], StringComparer.OrdinalIgnoreCase);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        foreach (var name in EnvHeaderNames())
            if (!_keep.Contains(name)) request.Headers.Remove(name);
        if (!hasConfiguredKey)
        {
            request.Headers.Authorization = null;
            request.Headers.Remove("x-api-key");
        }
        return base.SendAsync(request, ct);
    }

    public static IEnumerable<string> EnvHeaderNames()
    {
        var raw = Environment.GetEnvironmentVariable("ANTHROPIC_CUSTOM_HEADERS");
        if (string.IsNullOrWhiteSpace(raw)) yield break;
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int i = line.IndexOf(':');
            if (i > 0) yield return line[..i].Trim();
        }
    }
}
