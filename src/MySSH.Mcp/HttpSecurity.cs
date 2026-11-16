using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace MySSH.Mcp;

/// <summary>
/// Loopback hardening for the Streamable HTTP endpoint (design §6.4, spike G9). Order: 403 (Origin) → 421 (Host) → 401 (token).
/// <list type="number">
/// <item>Origin, if present, must be http(s)://localhost|127.0.0.1|[::1] (any port); <c>Origin: null</c> is rejected
/// (browser CSRF / DNS rebinding).</item>
/// <item>Host must be a loopback name (DNS rebinding; SlimBuilder has no HostFiltering).</item>
/// <item><c>Authorization: Bearer &lt;token&gt;</c>, compared in constant time; 401 with WWW-Authenticate otherwise.</item>
/// </list>
/// </summary>
internal sealed partial class HttpSecurity(string token, ILogger logger)
{
    private byte[] _expected = Expected(token);

    public void SetToken(string token) => Volatile.Write(ref _expected, Expected(token));

    private static byte[] Expected(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return Encoding.UTF8.GetBytes("Bearer " + token);
    }

    public async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
    {
        var origin = ctx.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && !IsLoopbackOrigin(origin))
        {
            LogReject(logger, 403, "bad Origin");
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsync("forbidden origin").ConfigureAwait(false);
            return;
        }
        if (!IsLoopbackHost(ctx.Request.Host.Host))
        {
            LogReject(logger, 421, "bad Host");
            ctx.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
            return;
        }
        var auth = Encoding.UTF8.GetBytes(ctx.Request.Headers.Authorization.ToString());
        if (!CryptographicOperations.FixedTimeEquals(auth, Volatile.Read(ref _expected)))
        {
            LogReject(logger, 401, auth.Length == 0 ? "missing token" : "wrong token");
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            ctx.Response.Headers.WWWAuthenticate = "Bearer realm=\"myssh\"";
            return;
        }
        await next(ctx).ConfigureAwait(false);
    }

    internal static bool IsLoopbackOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var u)
        && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
        && string.IsNullOrEmpty(u.UserInfo)
        && IsLoopbackHost(u.Host);

    internal static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP HTTP request rejected with {Status}: {Reason}")]
    private static partial void LogReject(ILogger logger, int status, string reason);
}
