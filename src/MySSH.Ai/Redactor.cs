using System.Text.RegularExpressions;

namespace MySSH.Ai;

/// <summary>
/// Masks secrets before text leaves the machine (design §7.4): private key blocks, password= / token= style assignments,
/// Authorization headers and well-known token formats. A safety net, not a guarantee; on by default.
/// </summary>
public static partial class Redactor
{
    public const string Mask = "[REDACTED]";

    public readonly record struct Result(string Text, int Count);

    public static Result Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return new(text ?? "", 0);
        var count = 0;
        string Count(Match _) { count++; return Mask; }

        // Order matters: whole blocks first, then key=value forms (keep the key), then bare token formats.
        var t = PrivateKeyBlock().Replace(text, m => { count++; return "-----BEGIN PRIVATE KEY-----\n" + Mask + "\n-----END PRIVATE KEY-----"; });
        t = AuthorizationHeader().Replace(t, m => { count++; return m.Groups["prefix"].Value + Mask; });
        t = SecretAssignment().Replace(t, m => { count++; return m.Groups["key"].Value + m.Groups["sep"].Value + m.Groups["quote"].Value + Mask + m.Groups["quote"].Value; });
        t = UrlCredentials().Replace(t, m => { count++; return m.Groups["scheme"].Value + m.Groups["user"].Value + ":" + Mask + "@"; });
        t = KnownTokens().Replace(t, Count);
        return new(t, count);
    }

    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY( BLOCK)?-----[\s\S]*?(-----END [A-Z0-9 ]*PRIVATE KEY( BLOCK)?-----|\z)")]
    private static partial Regex PrivateKeyBlock();

    [GeneratedRegex(@"(?<prefix>\b(?:Authorization|Proxy-Authorization)\s*:\s*(?:Bearer|Basic|Token|Digest)?\s*)[^\s""']{6,}", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorizationHeader();

    // password=..., "api_key": "...", export AWS_SECRET_ACCESS_KEY=..., token: ...
    [GeneratedRegex(@"(?<key>\b[\w.-]*(?:passw(?:or)?d|passwd|pwd|secret|token|api[_-]?key|apikey|access[_-]?key|private[_-]?key|client[_-]?secret|auth)[\w.-]*[""']?)(?<sep>\s*[:=]\s*)(?<quote>[""']?)(?![""']?\s*$)(?!\[REDACTED\])[^\s""',;}]{6,}", RegexOptions.IgnoreCase)]
    private static partial Regex SecretAssignment();

    // scheme://user:password@host
    [GeneratedRegex(@"(?<scheme>\b[a-z][a-z0-9+.-]*://)(?<user>[^\s:/@]+):(?!\[REDACTED\])[^\s/@]+@", RegexOptions.IgnoreCase)]
    private static partial Regex UrlCredentials();

    [GeneratedRegex(
        @"\b(?:" +
        @"sk-ant-[A-Za-z0-9_-]{20,}" +              // Anthropic
        @"|sk-(?:proj-|svcacct-)?[A-Za-z0-9_-]{20,}" + // OpenAI / DeepSeek style
        @"|gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,}" + // GitHub
        @"|glpat-[A-Za-z0-9_-]{20,}" +              // GitLab
        @"|xox[abprs]-[A-Za-z0-9-]{10,}" +          // Slack
        @"|(?:AKIA|ASIA)[0-9A-Z]{16}" +             // AWS access key id
        @"|AIza[0-9A-Za-z_-]{35}" +                 // Google API key
        @"|eyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}" + // JWT
        @")")]
    private static partial Regex KnownTokens();
}
