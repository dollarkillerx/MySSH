namespace MySSH.Ai.Tests;

/// <summary>
/// Hand-written Messages API SSE streams following the documented wire format. The first part is ported unchanged from
/// spikes/ai-providers/tests/AiSpike.Tests/Fixtures.cs (renamed); no real API was called to record them.
/// </summary>
public static class AnthropicFixtures
{
    public const string ThinkingText1 = "The user asks why nginx failed. ";
    public const string ThinkingText2 = "Read the terminal and check port 80.";
    public const string Signature1 = "EqQBCkgIARABGAIiQL2sigFAKEsignature==";

    /// <summary>Step 1: thinking + text + two parallel tool_use blocks with fragmented input_json_delta.</summary>
    public const string ToolUse = """
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
    public const string Final = """
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
    public const string Refusal = """
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
    public const string BadToolInput = """
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
    public const string FallbackPreOutput = """
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

    public const string ModelsSinglePage = """
        {"data":[
          {"type":"model","id":"claude-opus-5-5","display_name":"Claude Opus 5.5","created_at":"2026-10-01T00:00:00Z"},
          {"type":"model","id":"claude-sonnet-5-5","display_name":"Claude Sonnet 5.5","created_at":"2026-10-01T00:00:00Z"}],
         "has_more":false,"first_id":"claude-opus-5-5","last_id":"claude-sonnet-5-5"}
        """;

    // ----------------------------------------------------------------- added for MySSH (not in the spike)

    /// <summary>Mid-stream fallback: the first model streams thinking, text and a tool call, declines, and claude-opus-4-8
    /// continues on the same stream after a fallback block.</summary>
    public const string FallbackMidStream = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_01M","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],"stop_reason":null,"stop_sequence":null,"stop_details":null,"usage":{"input_tokens":70,"output_tokens":1,"cache_creation_input_tokens":512,"cache_read_input_tokens":0}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Plan the scan."}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"SIG_A=="}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: content_block_start
        data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Partial answer. "}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":1}

        event: content_block_start
        data: {"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"toolu_pre","name":"run_command","input":{},"caller":{"type":"direct"}}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{\"host\":\"h\",\"command\":\"nmap h\"}"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":2}

        event: content_block_start
        data: {"type":"content_block_start","index":3,"content_block":{"type":"fallback","from":{"model":"claude-opus-5-5"},"to":{"model":"claude-opus-4-8"},"trigger":{"type":"refusal","category":"cyber"}}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":3}

        event: content_block_start
        data: {"type":"content_block_start","index":4,"content_block":{"type":"thinking","thinking":"","signature":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":4,"delta":{"type":"thinking_delta","thinking":"Read the terminal instead."}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":4,"delta":{"type":"signature_delta","signature":"SIG_B=="}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":4}

        event: content_block_start
        data: {"type":"content_block_start","index":5,"content_block":{"type":"text","text":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":5,"delta":{"type":"text_delta","text":"Checking the terminal."}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":5}

        event: content_block_start
        data: {"type":"content_block_start","index":6,"content_block":{"type":"tool_use","id":"toolu_post","name":"read_terminal","input":{},"caller":{"type":"direct"}}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":6,"delta":{"type":"input_json_delta","partial_json":"{\"session\":\"s1\",\"lines\":10}"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":6}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null,"stop_details":null},"usage":{"output_tokens":60}}

        event: message_stop
        data: {"type":"message_stop"}

        """;

    /// <summary>Cut off by max_tokens in the middle of the text.</summary>
    public const string MaxTokens = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_01T","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],"stop_reason":null,"stop_sequence":null,"stop_details":null,"usage":{"input_tokens":10,"output_tokens":1}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"SIG_T=="}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: content_block_start
        data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"The first half of a long"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":1}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"max_tokens","stop_sequence":null,"stop_details":null},"usage":{"output_tokens":1024}}

        event: message_stop
        data: {"type":"message_stop"}

        """;

    /// <summary>Two progress-update thinking blocks (display "updates") around a tool call; one empty reasoning block.</summary>
    public const string ProgressUpdates = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_01U","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],"stop_reason":null,"stop_sequence":null,"stop_details":null,"usage":{"input_tokens":10,"output_tokens":1}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"SIG_R=="}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: content_block_start
        data: {"type":"content_block_start","index":1,"content_block":{"type":"thinking","thinking":"","signature":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":1,"delta":{"type":"thinking_delta","thinking":"Checking the logs."}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":1,"delta":{"type":"signature_delta","signature":"SIG_U1=="}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":1}

        event: content_block_start
        data: {"type":"content_block_start","index":2,"content_block":{"type":"thinking","thinking":"","signature":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":2,"delta":{"type":"thinking_delta","thinking":"Then the config."}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":2,"delta":{"type":"signature_delta","signature":"SIG_U2=="}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":2}

        event: content_block_start
        data: {"type":"content_block_start","index":3,"content_block":{"type":"tool_use","id":"toolu_q","name":"list_sessions","input":{},"caller":{"type":"direct"}}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":3}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null,"stop_details":null},"usage":{"output_tokens":20}}

        event: message_stop
        data: {"type":"message_stop"}

        """;

    /// <summary>Unknown block and delta types, a citations_delta and a null field: all kept generically (minus nulls).</summary>
    public const string UnknownTypes = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_01K","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],"stop_reason":null,"stop_sequence":null,"stop_details":null,"usage":{"input_tokens":10,"output_tokens":1}}}

        event: future_event
        data: {"type":"future_event","whatever":true}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"mystery_block","payload":{"a":1},"note":"x","gone":null}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"mystery_delta","note":"yz","extra":{"b":2}}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: content_block_start
        data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":"","citations":null}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Cited."}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":1,"delta":{"type":"citations_delta","citation":{"type":"char_location","cited_text":"c","document_index":0,"start_char_index":0,"end_char_index":1}}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":1}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null,"stop_details":null},"usage":{"output_tokens":3}}

        event: message_stop
        data: {"type":"message_stop"}

        """;

    /// <summary>An SSE error event before any content.</summary>
    public const string OverloadedErrorEvent = """
        event: error
        data: {"type":"error","error":{"type":"overloaded_error","message":"Overloaded"},"request_id":"req_stream_1"}

        """;

    /// <summary>A stream that is cut after the first text delta (no message_delta).</summary>
    public const string Truncated = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_01Z","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],"stop_reason":null,"stop_sequence":null,"stop_details":null,"usage":{"input_tokens":10,"output_tokens":1}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hel"}}

        """;

    public const string Malformed = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_01Y","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],"usage":{"input_tokens":1,"output_tokens":1}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}

        """;

    /// <summary>The complete (non-streamed) message some gateways return even for stream:true.</summary>
    public const string WholeMessageJson = """
        {"id":"msg_01J","type":"message","role":"assistant","model":"claude-opus-5-5","content":[{"type":"thinking","thinking":"Short.","signature":"SIG_J=="},{"type":"text","text":"Hello from JSON."},{"type":"tool_use","id":"toolu_j","name":"list_hosts","input":{"query":"web"}}],"stop_reason":"tool_use","stop_sequence":null,"usage":{"input_tokens":5,"output_tokens":6,"cache_read_input_tokens":1,"cache_creation_input_tokens":2}}
        """;

    public const string ModelsPage1 = """
        {"data":[{"type":"model","id":"claude-opus-5-5","display_name":"Claude Opus 5.5","created_at":"2026-10-01T00:00:00Z"}],
         "has_more":true,"first_id":"claude-opus-5-5","last_id":"claude-opus-5-5"}
        """;

    public const string ModelsPage2 = """
        {"data":[{"type":"model","id":"claude-sonnet-5-5","display_name":"Claude Sonnet 5.5","created_at":"2026-10-01T00:00:00Z"},
                 {"type":"model","id":"claude-haiku-5-5","created_at":"2026-10-01T00:00:00Z"}],
         "has_more":false,"first_id":"claude-sonnet-5-5","last_id":"claude-haiku-5-5"}
        """;
}
