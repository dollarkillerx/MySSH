namespace MySSH.Ssh.Tests;

public sealed class MiscTests
{
    [Theory]
    [InlineData("a/b", "/home/u", "/home/u/a/b")]
    [InlineData("/x/./y//z/", "/home/u", "/x/y/z")]
    [InlineData("../..", "/home/u", "/")]
    [InlineData("/a/b/../c", "/", "/a/c")]
    public void Normalize(string path, string home, string expected) => Assert.Equal(expected, SftpPath.Normalize(path, home));

    [Fact]
    public void Path_parts_and_permissions()
    {
        Assert.Equal("/a", SftpPath.GetParent("/a/b"));
        Assert.Equal("/", SftpPath.GetParent("/a"));
        Assert.Equal("b", SftpPath.GetName("/a/b/"));
        Assert.Equal("/a/b", SftpPath.Combine("/a/", "b"));
        Assert.Equal("/abs", SftpPath.Combine("/a", "/abs"));
        Assert.Equal("drwxr-x--x", SftpPath.FormatPermissions(SftpEntryKind.Directory, false, false, false, false, true, true, true, true, false, true, false, false, true));
        Assert.Equal("lrwxrwxrwx", SftpPath.FormatPermissions(SftpEntryKind.Symlink, false, false, false, false, true, true, true, true, true, true, true, true, true));
        Assert.Equal("srw-------", SftpPath.FormatPermissions(SftpEntryKind.Other, true, false, false, false, true, true, false, false, false, false, false, false, false));
    }

    [Fact]
    public void Shell_quote_and_bounded_buffer()
    {
        Assert.Equal("'it'\\''s'", SshExec.ShellQuote("it's"));
        var b = new SshExec.BoundedBuffer(10);
        Assert.True(b.Append("12345"u8));
        Assert.False(b.Append("678901234"u8));
        Assert.True(b.Truncated);
        Assert.Equal(10, b.Length);
        Assert.Equal("1234567890", b.Decode(System.Text.Encoding.ASCII));
        Assert.False(b.Append("x"u8));
    }

    [Fact]
    public void Text_decoding_strips_a_utf8_bom()
        => Assert.Equal("hé", SshSftpSession.DecodeText([0xEF, 0xBB, 0xBF, (byte)'h', 0xC3, 0xA9], System.Text.Encoding.UTF8));

    [Fact]
    public void Ssh_net_internals_self_check_passes_for_the_pinned_version()
    {
        Assert.True(SshNetInternals.IsExitStatusSupported);
        Assert.True(SshNetInternals.IsBridgeSupported);
        Assert.True(SshNetInternals.IsGlobalRequestSupported);
        Assert.True(SingleAcceptBridgeTransport.IsSupported);
    }

    [Fact]
    public void Empty_passphrase_means_none()
    {
        Assert.Null(Credentials.NormalizePassphrase(""));
        Assert.Null(Credentials.NormalizePassphrase(null));
        Assert.Equal(" ", Credentials.NormalizePassphrase(" "));
    }
}
