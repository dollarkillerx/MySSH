namespace MySSH.Ai.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void Presets_have_explicit_base_urls()
    {
        Assert.Equal("https://api.anthropic.com", AiProviderConfig.Claude().BaseUrl);
        Assert.Equal("anthropic", AiProviderConfig.Claude().RawProviderId);
        Assert.Equal("deepseek", AiProviderConfig.DeepSeek().RawProviderId);
    }
}
