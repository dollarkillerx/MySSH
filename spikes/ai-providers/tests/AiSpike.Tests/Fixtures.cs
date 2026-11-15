namespace AiSpike.Tests;

/// <summary>Hand-written SSE streams following the documented wire formats.</summary>
public static class Fixtures
{
    // ======================= Anthropic Messages streaming =======================

    public const string ThinkingText1 = "The user asks why nginx failed. ";
    public const string ThinkingText2 = "Read the terminal and check port 80.";
    public const string Signature1 = "EqQBCkgIARABGAIiQL2sigFAKEsignature==";

    /// <summary>Step 1: thinking + text + two parallel tool_use blocks with fragmented input_json_delta.</summary>
    public const string AnthropicToolUse = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_01A","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],"stop_reason":null,"stop_sequence":null,"stop_details":null,"usage":{"input_tokens":812,"output_tokens":2,"cache_creation_input_tokens":0,"cache_read_input_tokens":2048}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}

        event: ping
        data: {"type": "ping"}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"The user asks why nginx failed. "}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Read the terminal and check port 80."}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"EqQBCkgIARABGAIiQL2sigFAKEsignature=="}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: content_block_start
        data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Let me look at the terminal "}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"and check what holds port 80."}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":1}

        event: content_block_start
        data: {"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"toolu_01","name":"read_terminal","input":{},"caller":{"type":"direct"}}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{\"session\": \"s"}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"1\", \"lines\""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":": 50}"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":2}

        event: content_block_start
        data: {"type":"content_block_start","index":3,"content_block":{"type":"tool_use","id":"toolu_02","name":"run_command","input":{},"caller":{"type":"direct"}}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":3,"delta":{"type":"input_json_delta","partial_json":"{\"host\":\"prod-web-01\","}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":3,"delta":{"type":"input_json_delta","partial_json":"\"command\":\"ss -ltnp 'sport = :80'\","}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":3,"delta":{"type":"input_json_delta","partial_json":"\"timeout_s\":30}"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":3}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null,"stop_details":null},"usage":{"output_tokens":156}}

        event: message_stop
        data: {"type":"message_stop"}

        """;

    /// <summary>Step 2: final answer after the tool results.</summary>
    public const string AnthropicFinal = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_01B","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],"stop_reason":null,"stop_sequence":null,"stop_details":null,"usage":{"input_tokens":120,"output_tokens":1,"cache_creation_input_tokens":0,"cache_read_input_tokens":2860}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Port 80 is busy; command was denied."}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"SIG2=="}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: content_block_start
        data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"nginx failed because port 80 is already in use."}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":1}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null,"stop_details":null},"usage":{"output_tokens":42}}

        event: message_stop
        data: {"type":"message_stop"}

        """;

    /// <summary>Refusal mid-stream, with stop_details (category cyber).</summary>
    public const string AnthropicRefusal = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_01R","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],"stop_reason":null,"stop_sequence":null,"stop_details":null,"usage":{"input_tokens":50,"output_tokens":1}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Here is how to"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"refusal","stop_sequence":null,"stop_details":{"type":"refusal","category":"cyber","explanation":"This request was declined by a safety classifier."}},"usage":{"output_tokens":5}}

        event: message_stop
        data: {"type":"message_stop"}

        """;

    /// <summary>tool_use whose fragments assemble to invalid JSON + a second call that is valid JSON but fails the schema.</summary>
    public const string AnthropicBadToolInput = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_01X","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],"stop_reason":null,"stop_sequence":null,"stop_details":null,"usage":{"input_tokens":50,"output_tokens":1}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_bad1","name":"run_command","input":{},"caller":{"type":"direct"}}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"host\":\"h\",\"command\":\"echo \"hi\"\",\"timeout_s\":5}"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: content_block_start
        data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_bad2","name":"read_terminal","input":{},"caller":{"type":"direct"}}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"session\":\"s1\",\"lines\":\"fifty\",\"extra\":1}"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":1}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null,"stop_details":null},"usage":{"output_tokens":30}}

        event: message_stop
        data: {"type":"message_stop"}

        """;

    /// <summary>Pre-output server-side fallback: message_start names the fallback model, first block is "fallback".</summary>
    public const string AnthropicFallbackServed = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_01F","type":"message","role":"assistant","model":"claude-opus-4-8","content":[],"stop_reason":null,"stop_sequence":null,"stop_details":null,"usage":{"input_tokens":50,"output_tokens":1}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"fallback","from":{"model":"claude-opus-5-5"},"to":{"model":"claude-opus-4-8"},"trigger":{"type":"refusal","category":"cyber"}}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: content_block_start
        data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Answered by the fallback model."}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":1}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null,"stop_details":null},"usage":{"output_tokens":7}}

        event: message_stop
        data: {"type":"message_stop"}

        """;

    public const string AnthropicModels = """
        {"data":[
          {"type":"model","id":"claude-opus-5-5","display_name":"Claude Opus 5.5","created_at":"2026-10-01T00:00:00Z"},
          {"type":"model","id":"claude-sonnet-5-5","display_name":"Claude Sonnet 5.5","created_at":"2026-10-01T00:00:00Z"}],
         "has_more":false,"first_id":"claude-opus-5-5","last_id":"claude-sonnet-5-5"}
        """;

    // ======================= OpenAI chat.completions streaming =======================

    private const string H = "\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1731600000,\"model\":\"gpt-test\"";

    /// <summary>Two parallel tool_calls whose argument fragments interleave across chunks.</summary>
    public const string OpenAIToolCalls = "data: {" + H + ",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Checking.\"},\"finish_reason\":null}]}\n\n"
        + "data: {" + H + ",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_A\",\"type\":\"function\",\"function\":{\"name\":\"read_terminal\",\"arguments\":\"\"}}]},\"finish_reason\":null}]}\n\n"
        + "data: {" + H + ",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"{\\\"session\\\":\"}}]},\"finish_reason\":null}]}\n\n"
        + "data: {" + H + ",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":1,\"id\":\"call_B\",\"type\":\"function\",\"function\":{\"name\":\"run_command\",\"arguments\":\"{\\\"host\\\":\\\"prod\"}}]},\"finish_reason\":null}]}\n\n"
        + "data: {" + H + ",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\\\"s1\\\",\\\"lines\\\":50}\"}}]},\"finish_reason\":null}]}\n\n"
        + "data: {" + H + ",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":1,\"function\":{\"arguments\":\"-web-01\\\",\\\"command\\\":\\\"uptime\\\",\\\"timeout_s\\\":10}\"}}]},\"finish_reason\":null}]}\n\n"
        + "data: {" + H + ",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n"
        + "data: {" + H + ",\"choices\":[],\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":40,\"total_tokens\":140}}\n\n"
        + "data: [DONE]\n\n";

    public const string OpenAIFinal = "data: {" + H + ",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Port 80 \"},\"finish_reason\":null}]}\n\n"
        + "data: {" + H + ",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"is in use.\"},\"finish_reason\":null}]}\n\n"
        + "data: {" + H + ",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
        + "data: [DONE]\n\n";

    private const string D = "\"id\":\"ds-1\",\"object\":\"chat.completion.chunk\",\"created\":1731600000,\"model\":\"deepseek-reasoner\"";

    /// <summary>DeepSeek style: reasoning_content deltas before content.</summary>
    public const string DeepSeekReasoning = "data: {" + D + ",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":null,\"reasoning_content\":\"Think about \"},\"finish_reason\":null}]}\n\n"
        + "data: {" + D + ",\"choices\":[{\"index\":0,\"delta\":{\"content\":null,\"reasoning_content\":\"port 80.\"},\"finish_reason\":null}]}\n\n"
        + "data: {" + D + ",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Port 80 is busy.\",\"reasoning_content\":null},\"finish_reason\":null}]}\n\n"
        + "data: {" + D + ",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":20,\"total_tokens\":30}}\n\n"
        + "data: [DONE]\n\n";

    public const string OpenAINonStreaming = """
        {"id":"chatcmpl-2","object":"chat.completion","created":1731600000,"model":"local-model",
         "choices":[{"index":0,"message":{"role":"assistant","content":"Hello from a non-streaming server."},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":5,"completion_tokens":6,"total_tokens":11}}
        """;

    public const string OpenAIModels = """
        {"object":"list","data":[
          {"id":"gpt-test","object":"model","created":1731600000,"owned_by":"openai"},
          {"id":"deepseek-chat","object":"model","created":1731600000,"owned_by":"deepseek"}]}
        """;
}
