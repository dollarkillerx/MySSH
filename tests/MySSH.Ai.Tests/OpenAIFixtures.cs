namespace MySSH.Ai.Tests;

/// <summary>Hand-written Chat Completions wire fixtures (ported from spikes/ai-providers, plus edge cases).</summary>
internal static class OpenAIFixtures
{
    const string H = "\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1731600000,\"model\":\"gpt-test-2026-01-01\"";

    /// <summary>Two parallel tool_calls whose argument fragments interleave across chunks.</summary>
    public const string ToolCalls = "data: {" + H + ",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Checking.\"},\"finish_reason\":null}]}\n\n"
        + "data: {" + H + ",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_A\",\"type\":\"function\",\"function\":{\"name\":\"read_terminal\",\"arguments\":\"\"}}]},\"finish_reason\":null}]}\n\n"
        + "data: {" + H + ",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"{\\\"session\\\":\"}}]},\"finish_reason\":null}]}\n\n"
        + "data: {" + H + ",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":1,\"id\":\"call_B\",\"type\":\"function\",\"function\":{\"name\":\"run_command\",\"arguments\":\"{\\\"host\\\":\\\"prod\"}}]},\"finish_reason\":null}]}\n\n"
        + "data: {" + H + ",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\\\"s1\\\",\\\"lines\\\":50}\"}}]},\"finish_reason\":null}]}\n\n"
        + "data: {" + H + ",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":1,\"function\":{\"arguments\":\"-web-01\\\",\\\"command\\\":\\\"uptime\\\",\\\"timeout_s\\\":10}\"}}]},\"finish_reason\":null}]}\n\n"
        + "data: {" + H + ",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n"
        + "data: {" + H + ",\"choices\":[],\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":40,\"total_tokens\":140,\"prompt_tokens_details\":{\"cached_tokens\":30}}}\n\n"
        + "data: [DONE]\n\n";

    public const string Final = "data: {" + H + ",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Port 80 \"},\"finish_reason\":null}]}\n\n"
        + "data: {" + H + ",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"is in use.\"},\"finish_reason\":null}]}\n\n"
        + "data: {" + H + ",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
        + "data: [DONE]\n\n";

    /// <summary>Some servers finish a tool turn with "stop".</summary>
    public const string ToolCallWithStop = "data: {" + H + ",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"tool_calls\":[{\"index\":0,\"id\":\"call_X\",\"type\":\"function\",\"function\":{\"name\":\"read_terminal\",\"arguments\":\"{}\"}}]},\"finish_reason\":null}]}\n\n"
        + "data: {" + H + ",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
        + "data: [DONE]\n\n";

    /// <summary>Servers that send every call with index 0 (distinct ids).</summary>
    public const string ToolCallsSameIndex = "data: {" + H + ",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"a\",\"arguments\":\"{\\\"x\\\":1}\"}}]},\"finish_reason\":null}]}\n\n"
        + "data: {" + H + ",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_2\",\"type\":\"function\",\"function\":{\"name\":\"b\",\"arguments\":\"{\\\"y\\\":2}\"}}]},\"finish_reason\":null}]}\n\n"
        + "data: {" + H + ",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n"
        + "data: [DONE]\n\n";

    public const string Length = "data: {" + H + ",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Partial\"},\"finish_reason\":null}]}\n\n"
        + "data: {" + H + ",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"length\"}]}\n\n"
        + "data: [DONE]\n\n";

    public const string ContentFilter = "data: {" + H + ",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"I will\"},\"finish_reason\":null}]}\n\n"
        + "data: {" + H + ",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"content_filter\"}]}\n\n"
        + "data: [DONE]\n\n";

    /// <summary>Connection closed after some text, without finish_reason / [DONE].</summary>
    public const string Truncated = "data: {" + H + ",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Half\"},\"finish_reason\":null}]}\n\n";

    /// <summary>Non-standard finish_reason (Mistral "model_length", llama.cpp-style "eos", ...): unknown to the SDK enum.</summary>
    public static string UnknownFinish(string finishReason) =>
        "data: {" + H + ",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Done here.\"},\"finish_reason\":null}]}\n\n"
        + "data: {" + H + ",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"" + finishReason + "\"}]}\n\n"
        + "data: [DONE]\n\n";

    /// <summary>An error object inside the SSE stream (gateways / vLLM).</summary>
    public const string StreamError = "data: {" + H + ",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Hi\"},\"finish_reason\":null}]}\n\n"
        + "data: {\"error\":{\"message\":\"upstream model crashed\",\"type\":\"server_error\"}}\n\n";

    /// <summary>A slow stream for cancellation / idle-timeout tests.</summary>
    public const string Slow = "data: {" + H + ",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"one \"},\"finish_reason\":null}]}\n\n"
        + "data: {" + H + ",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"two \"},\"finish_reason\":null}]}\n\n"
        + "data: {" + H + ",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"three\"},\"finish_reason\":null}]}\n\n"
        + "data: {" + H + ",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
        + "data: [DONE]\n\n";

    const string D = "\"id\":\"ds-1\",\"object\":\"chat.completion.chunk\",\"created\":1731600000,\"model\":\"deepseek-reasoner\"";

    /// <summary>DeepSeek style: reasoning_content deltas before content.</summary>
    public const string DeepSeekReasoning = "data: {" + D + ",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":null,\"reasoning_content\":\"Think about \"},\"finish_reason\":null}]}\n\n"
        + "data: {" + D + ",\"choices\":[{\"index\":0,\"delta\":{\"content\":null,\"reasoning_content\":\"port 80.\"},\"finish_reason\":null}]}\n\n"
        + "data: {" + D + ",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Port 80 is busy.\",\"reasoning_content\":null},\"finish_reason\":null}]}\n\n"
        + "data: {" + D + ",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":20,\"total_tokens\":30,\"prompt_cache_hit_tokens\":4,\"prompt_cache_miss_tokens\":6}}\n\n"
        + "data: [DONE]\n\n";

    /// <summary>DeepSeek thinking mode with a tool call (reasoning must travel back within the tool loop).</summary>
    public const string DeepSeekReasoningToolCall = "data: {" + D + ",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":null,\"reasoning_content\":\"Need the terminal.\"},\"finish_reason\":null}]}\n\n"
        + "data: {" + D + ",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_D\",\"type\":\"function\",\"function\":{\"name\":\"read_terminal\",\"arguments\":\"{\\\"lines\\\":5}\"}}]},\"finish_reason\":null}]}\n\n"
        + "data: {" + D + ",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n"
        + "data: [DONE]\n\n";

    public const string NonStreaming = """
        {"id":"chatcmpl-2","object":"chat.completion","created":1731600000,"model":"local-model",
         "choices":[{"index":0,"message":{"role":"assistant","content":"Hello from a non-streaming server."},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":5,"completion_tokens":6,"total_tokens":11}}
        """;

    public const string NonStreamingToolCall = """
        {"id":"chatcmpl-3","object":"chat.completion","created":1731600000,"model":"deepseek-reasoner",
         "choices":[{"index":0,"message":{"role":"assistant","content":"Let me look.","reasoning_content":"Check it.",
           "tool_calls":[{"id":"call_N","type":"function","function":{"name":"read_terminal","arguments":"{\"lines\":3}"}}]},
           "finish_reason":"tool_calls"}],
         "usage":{"prompt_tokens":7,"completion_tokens":8,"total_tokens":15}}
        """;

    public const string Models = """
        {"object":"list","data":[
          {"id":"gpt-test","object":"model","created":1731600000,"owned_by":"openai"},
          {"id":"deepseek-chat","object":"model","created":1731600000,"owned_by":"deepseek"}]}
        """;

    public static string Error(string message, string type = "invalid_request_error") =>
        "{\"error\":{\"message\":\"" + message + "\",\"type\":\"" + type + "\",\"param\":null,\"code\":null}}";
}
