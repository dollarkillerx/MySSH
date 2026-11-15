using MySSH.Core;

namespace MySSH.Core.Tests;

public sealed class ModelTests
{
    [Theory]
    [InlineData("example.com", 22, "example.com")]
    [InlineData("example.com", 2222, "[example.com]:2222")]
    [InlineData("10.0.0.1", 22, "10.0.0.1")]
    public void KnownHosts_key_uses_OpenSSH_format(string host, int port, string expected)
    {
        Assert.Equal(expected, KnownHostEntry.FormatHostPort(host, port));
        Assert.Equal(expected, new Host { Label = "x", Hostname = host, Port = port }.KnownHostsKey);
    }

    [Fact]
    public void Secrets_never_appear_in_ToString()
    {
        var s = new IdentitySecrets("hunter2", "-----BEGIN OPENSSH PRIVATE KEY-----", "pp");
        Assert.DoesNotContain("hunter2", s.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", s.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void New_hosts_are_invisible_to_AI_by_default() =>
        Assert.Equal(AiPolicy.Off, new Host { Label = "x", Hostname = "h" }.AiPolicy);

    [Fact]
    public void Legacy_and_new_data_directories_never_collide_case_insensitively()
    {
        foreach (var platform in Enum.GetValues<AppPlatform>())
        {
            var p = AppPaths.Resolve(platform, _ => null, "/home/u", @"C:\Users\u\AppData\Roaming");
            Assert.False(string.Equals(p.DataDirectory, p.LegacyDataDirectory, StringComparison.OrdinalIgnoreCase), platform.ToString());
        }
    }
}
