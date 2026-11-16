using System.Text;
using System.Text.Json;
using MySSH.Ai.Providers.Anthropic;

namespace MySSH.Ai.Tests;

/// <summary>Custom Anthropic-compatible gateways don't get the thinking-binding beta / block_binding field.</summary>
public sealed class AnthropicGatewayTests
{
    static ChatRequest Request => new() { Model = "m", SystemPrompt = "s", History = [AiMessage.User("hi")] };

    [Fact]
    public void Official_preset_sends_the_block_binding_safety_net()
    {
        var config = AiProviderConfig.Claude();
        Assert.Contains(AnthropicRequestBuilder.ThinkingBindingBeta, AnthropicRequestBuilder.Betas(config));
        var body = JsonDocument.Parse(AnthropicRequestBuilder.BuildMessagesBody(config, Request)).RootElement;
        Assert.Equal("drop_block", body.GetProperty("thinking").GetProperty("block_binding").GetProperty("prefix_mismatch_behavior").GetString());
    }

    [Fact]
    public void Custom_gateway_gets_neither_the_beta_nor_the_field()
    {
        var config = AiProviderConfig.Custom(AiProtocol.Anthropic, "http://127.0.0.1:8080");
        Assert.DoesNotContain(AnthropicRequestBuilder.ThinkingBindingBeta, AnthropicRequestBuilder.Betas(config));
        var json = Encoding.UTF8.GetString(AnthropicRequestBuilder.BuildMessagesBody(config, Request));
        Assert.DoesNotContain("block_binding", json, StringComparison.Ordinal);
        Assert.Equal("adaptive", JsonDocument.Parse(json).RootElement.GetProperty("thinking").GetProperty("type").GetString());
    }
}
