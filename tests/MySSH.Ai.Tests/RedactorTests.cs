namespace MySSH.Ai.Tests;

public sealed class RedactorTests
{
    [Theory]
    [InlineData("export DB_PASSWORD=hunter2hunter2", "hunter2hunter2")]
    [InlineData("password: s3cr3t!pass", "s3cr3t!pass")]
    [InlineData("\"api_key\": \"abcd1234efgh\"", "abcd1234efgh")]
    [InlineData("AWS_SECRET_ACCESS_KEY=wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "wJalrXUtnFEMI")]
    [InlineData("Authorization: Bearer abcdef123456789", "abcdef123456789")]
    [InlineData("key AKIAIOSFODNN7EXAMPLE here", "AKIAIOSFODNN7EXAMPLE")]
    [InlineData("token ghp_0123456789abcdefghij0123456789abcdef", "ghp_0123456789")]
    [InlineData("sk-ant-api03-AAAAAAAAAAAAAAAAAAAAAAAA", "sk-ant-api03")]
    [InlineData("sk-proj-AAAAAAAAAAAAAAAAAAAAAAAAAAAA", "sk-proj-AAAA")]
    [InlineData("postgres://app:p4ssw0rd@db:5432/app", "p4ssw0rd")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U", "dozjgNryP4J3")]
    public void Secrets_are_masked(string input, string secret)
    {
        var r = Redactor.Redact(input);
        Assert.DoesNotContain(secret, r.Text, StringComparison.Ordinal);
        Assert.Contains(Redactor.Mask, r.Text, StringComparison.Ordinal);
        Assert.True(r.Count >= 1);
    }

    [Fact]
    public void Private_key_blocks_are_removed_whole()
    {
        const string key = "-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQ\nAAAA\n-----END OPENSSH PRIVATE KEY-----";
        var r = Redactor.Redact("cat id_ed25519\n" + key + "\n$ ");
        Assert.DoesNotContain("b3BlbnNz", r.Text, StringComparison.Ordinal);
        Assert.Equal(1, r.Count);
        Assert.StartsWith("cat id_ed25519\n", r.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("nginx: [emerg] bind() to 0.0.0.0:80 failed (98: Address already in use)")]
    [InlineData("-rw-r--r-- 1 deploy deploy 1873 Nov 12 18:40 docker-compose.yml")]
    [InlineData("passwd: all authentication tokens updated successfully.")]
    [InlineData("Enter password:")]
    [InlineData("password=")]
    public void Ordinary_output_is_left_alone(string input)
    {
        var r = Redactor.Redact(input);
        Assert.Equal(input, r.Text);
        Assert.Equal(0, r.Count);
    }

    [Fact]
    public void Context_is_wrapped_and_redacted()
    {
        var c = AiContextComposer.Compose("why?", [
            new AiContextItem(AiContextKind.Host, "web-01", AiContextComposer.DescribeHost("web-01", "10.0.1.11", 22, "deploy", "/srv", Guid.Empty)),
            new AiContextItem(AiContextKind.Terminal, "Terminal \"x\"", "$ env\nTOKEN=abcdefghijkl\n"),
        ], redact: true);
        Assert.StartsWith("<context>\n<current_host label=\"web-01\">", c.Text, StringComparison.Ordinal);
        Assert.Contains("<terminal_output label=\"Terminal 'x'\">", c.Text, StringComparison.Ordinal);
        Assert.EndsWith("</context>\n\nwhy?", c.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijkl", c.Text, StringComparison.Ordinal);
        Assert.Equal(1, c.Redactions);
        Assert.Equal("why?", AiContextComposer.Compose(" why? ", [], redact: true).Text);
    }
}
