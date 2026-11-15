using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace MySSH.Spike.McpHost;

/// <summary>
/// Loopback hardening for the Streamable HTTP endpoint:
///  1. Origin header, if present, must be a loopback origin (DNS-rebinding / browser CSRF defence; spec MUST).
///  2. Host header must be a loopback name (second DNS-rebinding defence).
///  3. Authorization: Bearer &lt;random token&gt; (constant-time compare). Missing/wrong -> 401.
/// </summary>
public static class LocalSecurity
{
    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static void UseLocalMcpSecurity(this WebApplication app, string token, EventLog events)
    {
        var expected = Encoding.UTF8.GetBytes("Bearer " + token);
        app.Use(async (ctx, next) =>
        {
            var origin = ctx.Request.Headers.Origin.ToString();
            if (origin.Length > 0 && !IsLoopbackOrigin(origin))
            {
                events.Write("HTTP REJECT 403", $"bad Origin '{origin}'");
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsync("forbidden origin");
                return;
            }
            if (!IsLoopbackHost(ctx.Request.Host.Host))
            {
                events.Write("HTTP REJECT 421", $"bad Host '{ctx.Request.Host}'");
                ctx.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
                return;
            }
            var auth = Encoding.UTF8.GetBytes(ctx.Request.Headers.Authorization.ToString());
            if (!CryptographicOperations.FixedTimeEquals(auth, expected))
            {
                events.Write("HTTP REJECT 401", auth.Length == 0 ? "missing token" : "wrong token");
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                ctx.Response.Headers.WWWAuthenticate = "Bearer realm=\"myssh\"";
                return;
            }
            await next(ctx);
        });
    }

    internal static bool IsLoopbackOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var u)
        && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
        && IsLoopbackHost(u.Host);

    internal static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
}
