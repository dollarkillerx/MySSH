// AiSpike console: drives the agent loop against a real provider ONLY if its API key env var is set.
// Base URLs come from the built-in presets (or --base-url), never from ANTHROPIC_BASE_URL.
//
//   dotnet run --project src/AiSpike.Console -- --provider anthropic|openai|deepseek
//        [--model ID] [--effort low|medium|high] [--allow-commands] [--list-models] [--base-url URL] "prompt"
using AiSpike.Core;

string provider = "anthropic", model = "", effort = "medium", baseUrl = "";
bool allow = false, listModels = false;
var promptParts = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--provider": provider = args[++i]; break;
        case "--model": model = args[++i]; break;
        case "--effort": effort = args[++i]; break;
        case "--base-url": baseUrl = args[++i]; break;
        case "--allow-commands": allow = true; break;
        case "--list-models": listModels = true; break;
        default: promptParts.Add(args[i]); break;
    }
}

var keyVar = provider switch { "anthropic" => "ANTHROPIC_API_KEY", "openai" => "OPENAI_API_KEY", "deepseek" => "DEEPSEEK_API_KEY", _ => "" };
if (keyVar == "") { Console.Error.WriteLine($"unknown provider '{provider}'"); return 2; }
var key = Environment.GetEnvironmentVariable(keyVar);
if (string.IsNullOrEmpty(key))
{
    Console.WriteLine($"{keyVar} is not set; skipping real API call (tests cover the mock path: dotnet test).");
    return 0;
}

IChatProvider p = provider switch
{
    "anthropic" => new AnthropicProvider(new AnthropicProviderOptions
    {
        BaseUrl = baseUrl.Length > 0 ? baseUrl : "https://api.anthropic.com", // explicit; ANTHROPIC_BASE_URL ignored
        ApiKey = key,
    }),
    "openai" => new OpenAICompatibleProvider(OpenAICompatibleOptions.OpenAI(key) with { BaseUrl = baseUrl.Length > 0 ? baseUrl : "https://api.openai.com/v1" }),
    _ => new OpenAICompatibleProvider(OpenAICompatibleOptions.DeepSeek(key) with { BaseUrl = baseUrl.Length > 0 ? baseUrl : "https://api.deepseek.com" }),
};

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

if (listModels || model.Length == 0)
{
    var models = await p.ListModelsAsync(cts.Token);
    foreach (var m in models) Console.WriteLine($"  {m.Id}  {m.DisplayName}");
    if (listModels) return 0;
    model = provider == "anthropic" ? AnthropicProvider.DefaultModel : models.FirstOrDefault()?.Id ?? "";
    if (model.Length == 0) { Console.Error.WriteLine("no models; pass --model"); return 2; }
}

var prompt = promptParts.Count > 0 ? string.Join(' ', promptParts) : "Why did nginx fail to start? Look at my terminal.";
var history = new List<AiMessage> { AiMessage.User(prompt) };
var loop = new AgentLoop(p, [new ReadTerminalTool(), new RunCommandTool()], new ConsoleApproval(allow));

string? mode = null;
void Mode(string m, ConsoleColor c) { if (mode != m) { Console.ForegroundColor = c; Console.Write($"\n[{m}] "); Console.ResetColor(); mode = m; } }

var result = await loop.RunAsync(model, "You are an SSH operations assistant inside MySSH. Explain risks before destructive actions.",
    history, ev =>
    {
        switch (ev)
        {
            case ThinkingSummaryDelta t: Mode("thinking", ConsoleColor.DarkGray); Console.Write(t.Text); break;
            case TextDelta t: Mode("assistant", ConsoleColor.Cyan); Console.Write(t.Text); break;
            case ToolCallStarted s: Mode("tool", ConsoleColor.Yellow); Console.Write($"{s.Name} "); break;
            case ToolCallCompleted c: Console.Write(c.Call.InputError is null ? c.Call.RawInput : $"INVALID: {c.Call.InputError}"); mode = null; break;
            case Usage u: Console.Write($"\n  (tokens in={u.InputTokens} out={u.OutputTokens} cache_read={u.CacheReadTokens})"); mode = null; break;
            case Stopped s: Console.Write($"\n  (stop={s.Reason}{(s.Detail is null ? "" : " " + s.Detail)}{(s.Message?.ServedByModel is { } sm ? " model=" + sm : "")})"); mode = null; break;
        }
    }, cts.Token);
Console.WriteLine($"\n== done: steps={result.Steps} last={result.LastStop} maxSteps={result.HitMaxSteps} {result.Detail}");
return 0;

sealed class ConsoleApproval(bool allow) : IApproval
{
    public Task<bool> ApproveAsync(ToolCall call, CancellationToken ct)
    {
        Console.WriteLine($"\n  [approval] {call.Name} {call.RawInput} -> {(allow ? "ALLOWED (--allow-commands)" : "DENIED (default)")}");
        return Task.FromResult(allow);
    }
}
