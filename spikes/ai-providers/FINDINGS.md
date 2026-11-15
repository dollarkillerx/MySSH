# Spike ④ AI 提供商：结论

> 子代理产出，主会话复核：`dotnet test` 21/21 通过（2026-11-15）。本 spike **没有调用任何真实 API**（环境里没有 key），结论来自本地 Kestrel mock（按文档手写的 SSE 线格式）、SDK 的 XML 文档和实测出来的请求头和请求体。

问题（design §12 P0）：用官方 `Anthropic` 13.8.0 / `OpenAI` 2.15.0 C# SDK 能否实现 design §7.2 的 `IChatProvider`、流式工具调用、思考块原样回传和 §7.3 Agent 循环？

**结论：可以。** §7.2 的要求，C# SDK 全部能表达，没有需要改走原始 HTTP 的地方。不过有 **3 个 env 变量陷阱**，生产代码里必须专门处理（§4），另外有几处 SDK 的细节和设计稿写法不一样（§3）。

## 1. 结构与运行

```
spikes/ai-providers/
  AiProviders.slnx
  src/AiSpike.Core/          IChatProvider + AnthropicProvider + OpenAICompatibleProvider + AgentLoop + 假工具
    Abstractions.cs          IChatProvider / ChatEvent / AiMessage(RawProvider, RawContent) / StopReason
    AnthropicProvider.cs     Claude（beta Messages 流式）
    OpenAICompatibleProvider.cs  OpenAI / DeepSeek / 自定义兼容端点
    EnvIsolation.cs          IsolatedAnthropicClient + EnvIsolationHandler（env 陷阱的防护）
    ToolInput.cs             严格 JSON 解析 + 最小 JSON-Schema 校验
    AgentLoop.cs             §7.3 循环、IApproval、read_terminal / run_command 假工具
  src/AiSpike.Console/       真实调用（只有设置了 ANTHROPIC_API_KEY / OPENAI_API_KEY / DEEPSEEK_API_KEY 时才会发请求）
  tests/AiSpike.Tests/       xUnit + Kestrel mock（127.0.0.1:0）+ 手写 SSE fixture
```

- `dotnet test spikes/ai-providers/tests/AiSpike.Tests`
- `dotnet run --project spikes/ai-providers/src/AiSpike.Console -- --provider anthropic|openai|deepseek [--model ID] [--effort low|medium|high] [--allow-commands] [--list-models] [--base-url URL] "prompt"`
  - 没有 key 时打印提示并退出（exit 0）。Base URL 只来自预置值或 `--base-url`，**不读 `ANTHROPIC_BASE_URL`**。
  - `run_command` 默认自动拒绝，加 `--allow-commands` 才自动允许。

## 2. 用到的 SDK 类型和方法

### 2.1 Anthropic 13.8.0（`using Anthropic.Models.Beta.Messages;`）

| 功能 | 类型 / 调用 | 备注 |
|---|---|---|
| 客户端 | `new IsolatedAnthropicClient { BaseUrl, ApiKey, MaxRetries, Timeout, Handlers = [new EnvIsolationHandler(...)] }`（继承 `AnthropicClient`，override `protected bool ShouldAutoResolveCredentials => false`） | 见 §4 |
| 流式 | `client.Beta.Messages.CreateStreaming(MessageCreateParams, ct)` → `IAsyncEnumerable<BetaRawMessageStreamEvent>` | beta 端点的路径**仍是 `/v1/messages`**，靠 `anthropic-beta` 头区分，没有 `?beta=true` |
| 事件分派 | `ev.TryPickStart / TryPickContentBlockStart / TryPickContentBlockDelta / TryPickContentBlockStop / TryPickDelta / TryPickStop` | `ping` 事件由 SDK 自己吞掉（fixture 里带了 ping，已验证） |
| block start | `cbs.ContentBlock.TryPickBetaToolUse(out BetaToolUseBlock)`（这里的联合类型名叫 `ContentBlock`，方法带 `Beta` 前缀） | 和 `BetaContentBlock.TryPickToolUse` 的命名不一致 |
| delta | `cbd.Delta.TryPickText / TryPickThinking / TryPickInputJson`（`.Text` / `.Thinking` / `.PartialJson`） | |
| stop / usage | `md.Delta.StopReason?.Raw()`、`md.Delta.StopDetails`（`Category?.Raw()`、`Explanation`）、`md.Usage.OutputTokens`；`start.Message.Usage.InputTokens / CacheReadInputTokens / CacheCreationInputTokens` | |
| 聚合整条消息 | `var agg = new BetaMessageContentAggregator(); stream.CollectAsync(agg)` 照常遍历，结束后调用 `agg.Message()` 得到 `BetaMessage` | XML 文档里写的 `SseAggregator.Add()` 和 `BetaMessageContentAggregator.Build()` **都不是 public**，编译不过；`CollectAsync` 会把所有事件原样透传 |
| 自适应思考 + 摘要 | `Thinking = new BetaThinkingConfigAdaptive { Display = Display.Summarized }` | |
| effort | `OutputConfig = new BetaOutputConfig { Effort = Effort.Medium }` | 线上格式：`output_config.effort:"medium"` |
| 工具 | `new BetaTool { Name, Description, InputSchema = InputSchema.FromRawUnchecked(dict), Strict = true }`，再 `with { EagerInputStreaming = true }`、`with { CacheControl = new BetaCacheControlEphemeral() }` | typed 的 `InputSchema` **没有 `AdditionalProperties` 成员**，所以用 `FromRawUnchecked` 整份透传 schema |
| tool_choice | `ToolChoice = new BetaToolChoiceAuto()` | |
| system 缓存 | `System = new List<BetaTextBlockParam> { new() { Text, CacheControl = new BetaCacheControlEphemeral() } }` | |
| 服务端回退 | `Betas = ["server-side-fallback-2026-07-01"]`，`Fallbacks = new Default()`（`Anthropic.Models.Beta.Messages.Default`） | 线上格式 `"fallbacks":"default"`，请求头 `anthropic-beta: server-side-fallback-2026-07-01`，已验证 |
| fallback 块 | 响应侧 `BetaContentBlock.TryPickFallback(out BetaFallbackBlock)`（`From.Model`、`To.Model`、`Trigger`）；回传用 `BetaFallbackBlockParam { From, To, Trigger = JsonSerializer.SerializeToElement(trigger) }` | `message_start.message.model` 给出实际服务的模型 |
| 历史消息 | `BetaUserMessageParam { Content = string 或 List<BetaContentBlockParam> }`、`BetaAssistantMessageParam { Content = List<BetaContentBlockParam> }` | `BetaMessageParam` 本身不能直接 new |
| 回传块 | `BetaTextBlockParam`、`BetaThinkingBlockParam { Thinking, Signature }`、`BetaRedactedThinkingBlockParam { Data }`、`BetaToolUseBlockParam { ID, Name, Input }`、`BetaToolResultBlockParam { ToolUseID, Content, IsError }` | 没有 `.ToParam()`，只能逐个变体重建 |
| 原始内容持久化 | `JsonSerializer.Serialize(message.Content)` 存进 `AiMessage.RawContent`；回放时用 `JsonSerializer.Deserialize<List<BetaContentBlock>>` 反序列化，再逐块转成 Param | 模拟了存 SQLite 再读回的往返，签名和工具输入都保持不变 |
| 模型列表 | `await client.Models.List(cancellationToken: ct)` → `page.Items`（`ID`、`DisplayName`） | 只取了第一页；生产代码需要分页 |
| 错误 | `catch (AnthropicException)`（`Anthropic.Exceptions`）+ `HttpRequestException` → `Stopped(Error)` | 529 已验证 |

### 2.2 OpenAI 2.15.0

| 功能 | 类型 / 调用 |
|---|---|
| 客户端 | `new ChatClient(model, new ApiKeyCredential(key), new OpenAIClientOptions { Endpoint = new Uri(baseUrl), RetryPolicy = new ClientRetryPolicy(n) })` |
| 流式 | `client.CompleteChatStreamingAsync(messages, options, ct)` → `StreamingChatCompletionUpdate`（`ContentUpdate`、`ToolCallUpdates`、`FinishReason`、`Usage`、`Model`） |
| tool_calls 分片 | `StreamingChatToolCallUpdate.Index / ToolCallId / FunctionName / FunctionArgumentsUpdate (BinaryData)`，按 `Index` 拼接（fixture 里两个调用的分片交错出现，已验证） |
| 工具 | `ChatTool.CreateFunctionTool(name, desc, BinaryData schema, strict?)`，`ChatToolChoice.CreateAutoChoice()`，`AllowParallelToolCalls = true` |
| 历史消息 | `SystemChatMessage`、`UserChatMessage`、`new AssistantChatMessage(IEnumerable<ChatToolCall>)` 加 `.Content.Add(ChatMessageContentPart.CreateTextPart(...))`、`ChatToolCall.CreateFunctionToolCall(id, name, BinaryData)`、`new ToolChatMessage(id, content)` |
| 非流式降级 | `client.CompleteChatAsync(...)` → `ChatCompletion`（`Content`、`ToolCalls`、`FinishReason`、`Usage`） |
| usage | SDK 在流式请求里**自动带上** `stream_options.include_usage:true`（已验证） |
| `reasoning_content`（DeepSeek） | SDK 没有对应的属性。用实验性 API `JsonPatch` 读写（需要 `#pragma warning disable SCME0001`）：读 `update.Patch.TryGetValue("$.choices[0].delta.reasoning_content"u8, out string?)`（非流式用 `"$.choices[0].message.reasoning_content"`），写 `assistantMsg.Patch.Set("$.reasoning_content"u8, text)`。两个方向都已验证 |
| 模型列表 | `new OpenAIModelClient(credential, options).GetModelsAsync(ct)` → `OpenAIModel.Id / OwnedBy`。OpenAI 走 `/v1/models`，DeepSeek（base 不带 `/v1`）走 `/models`，两条路径都已验证 |

## 3. design §7.2 逐条核对

| 要求 | 结果 |
|---|---|
| `AnthropicClient { ApiKey }` + `Messages.CreateStreaming` | **需要改**：`fallbacks` 只在 **beta** 端点上有，所以要用 `client.Beta.Messages.CreateStreaming` 和 beta 命名空间里的类型。另外 SDK 13.x 发送 API key 用的是 `Authorization: Bearer <key>`，不是 `x-api-key`，请求头 `anthropic-version: 2026-10-21`。如果自定义的 Anthropic 兼容网关只认 `x-api-key`，需要用 handler 改写这个头 |
| 自定义端点覆盖 BaseUrl | 确认。但 **必须显式设置**，否则会读 `ANTHROPIC_BASE_URL`（§4） |
| adaptive 思考 + 显式 medium + 不发采样参数 | 确认（请求体里没有 temperature、top_p、top_k，已断言） |
| 界面显示思考摘要 | 确认：`display:"summarized"` → `thinking_delta` → `ThinkingSummaryDelta`。**建议补充**：Opus 5.5 会把工具调用之间的进度说明也放进 thinking 块里返回；如果只想显示进度、不显示推理摘要，可以用 `display:"updates"`（beta `thinking-display-updates-2026-08-18`），设置页可以加这个选项。spike 里没有实现 |
| tool_choice auto + strict + eager_input_streaming | 确认。**需要补充**：(1) SDK 把属性显式设为 null 时会序列化出 `"cache_control": null` 这类字段，可选字段只能在需要时用 `with` 设置（已断言请求体里没有 `:null`）；(2) eager 模式下，服务端不再校验工具输入，所以客户端自己做严格解析和 schema 校验：失败时返回 `is_error` 的 tool_result（无效 JSON 时内容为 `{"INVALID_JSON": "..."}`），不经过审批、不执行（已验证）；(3) 自定义的 Anthropic 兼容代理可能拒绝 `eager_input_streaming`，提供商配置需要一个开关（`AnthropicProviderOptions.EagerInputStreaming`） |
| strict schema | **待用真实 API 验证**：spike 做了保守处理，发送给 API 的 schema 去掉了 `minimum/maximum/minLength/...` 这类约束，改写进 description；完整 schema 仍在客户端执行。strict 模式到底接受哪些 JSON Schema 关键字，本地没法确认，需要对照官方 structured outputs 文档或真实 400 响应 |
| 执行工具前检查 max_tokens / refusal | 确认。Refusal 时不执行工具，**拒绝前已经输出的部分内容丢弃、不追加到历史**（文档要求：mid-stream decline 的 partial 要丢弃）；MaxTokens 时追加当轮内容但不执行工具 |
| refusal + `fallbacks:"default"` | 确认（请求体和请求头都已断言）。`stop_details.category` 会写进 `Stopped.Detail`。**补充回放规则**：响应里出现 `fallback` 块时，必须原样回传并保持原来的位置；最后一个 fallback 块之前的 thinking、redacted_thinking、tool_use 不回传，在它之前出现的 tool_use 也不执行（已实现，pre-output fallback 的场景已测）。回退模型读不到 Opus 5.5 的思考块（API 会静默丢弃，不计费） |
| tools / system 末尾放 cache_control，按字节保持稳定 | 确认：工具按名称排序，最后一个工具带 cache_control，system 用块形式。测试断言了两轮请求的 system、tools 和第一条消息逐字节相同。**建议补充**：只缓存 tools 和 system 时，长对话历史每轮都会被重新计费。可以再加顶层 `MessageCreateParams.CacheControl`（自动缓存最后一个块），4 个断点的上限够用 |
| 历史只追加、思考块原样回传 | 确认：第二轮请求里 assistant 的 thinking 文本、signature、text 和两个 tool_use 的 input 都和第一轮的响应一致。`RawContent` 以 JSON 字符串形式存储，经过反序列化往返后仍然一致。Opus 5.5 有 preserved thinking 机制：2026-08-31 之后创建的账号，如果前缀被修改，回传的思考块会导致 400。所以"system 不放时间戳、工具集合固定"是硬性要求，不只是为了缓存 |
| OpenAI 兼容：流式、tool_calls、能力开关降级 | 确认。分片乱序拼接、无工具模式（请求里不带 `tools`）、无流式模式（走 `CompleteChatAsync`）都已测。`finish_reason:"stop"` 但带了 tool_calls 的情况按 ToolUse 处理。Chat Completions 没有 `is_error` 字段，用内容前缀 `ERROR: ` 表示 |
| `reasoning_content` 显示在「思考过程」里 | 确认，可以拿到（JsonPatch，实验性 API）。注意：DeepSeek 思考模式在**同一轮的工具循环里**要求把 `reasoning_content` 回传，spike 实现了回传；跨轮次是否需要去掉，要按 DeepSeek 当前文档再确认 |
| 切换提供商 | spike 的实现：`RawProvider` 和当前提供商不一致时，降级为只发 text 和 tool_calls（已测 Anthropic 历史回放给 OpenAI）。从 OpenAI 切回 Claude 时没有思考块，属于正常情况 |
| §7.3 循环 | 确认：最多 N 步、可以取消（在审批等待中取消会抛 `OperationCanceledException`）、同一轮的工具并行执行（`Task.WhenAll`）、所有结果按调用顺序放进一条消息、拒绝和出错都回传 `is_error`。工具输出截断上限 64KB |

## 4. env 变量陷阱（桌面应用必须处理）

用 `EnvPitfallTests` 实测（进程内临时设置 env，并把它指向本地的 trap mock，不会发到用户真实的 `ANTHROPIC_BASE_URL`）。DLL 里能找到 SDK 读取的变量：`ANTHROPIC_BASE_URL`、`ANTHROPIC_API_KEY`、`ANTHROPIC_AUTH_TOKEN`、`ANTHROPIC_CUSTOM_HEADERS`、`ANTHROPIC_PROFILE`、`ANTHROPIC_CONFIG_DIR`（`ant` 的 profile）、WIF 相关的 `ANTHROPIC_FEDERATION_RULE_ID / ORGANIZATION_ID / SERVICE_ACCOUNT_ID / IDENTITY_TOKEN(_FILE) / WORKSPACE_ID`、`ANTHROPIC_OPEN_TELEMETRY*`、`ANTHROPIC_WEBHOOK_SIGNING_KEY`。

| 场景 | 实测结果 |
|---|---|
| `new AnthropicClient()` 不传参数 | 请求**发到 `ANTHROPIC_BASE_URL`**，带的是 `ANTHROPIC_API_KEY`。从终端启动 MySSH 时会继承用户的 Claude Code 代理设置，**请求和 key 都会发到别处** |
| 显式设置 `BaseUrl` 和 `ApiKey` | BaseUrl 和 key 用的都是显式值（没问题），**但 `ANTHROPIC_CUSTOM_HEADERS` 里的请求头仍然会被合并进每个请求**，包括 `GET /v1/models` |
| `ApiKey = null`（用户没填 key） | XML 文档说设为 null 就不会读 env，实测在对象初始化器里写 `ApiKey = null` **没有效果**：请求带上了 `Authorization: Bearer <ANTHROPIC_AUTH_TOKEN>` |
| 子类 override `ShouldAutoResolveCredentials => false` | 编译能通过，但**同样拦不住** `ANTHROPIC_AUTH_TOKEN`（这个开关只管 profile 和 WIF） |
| 显式 `BaseUrl` 时 | 不读 `ANTHROPIC_BASE_URL`（已验证） |

防护（`EnvIsolation.cs`，测试全部通过）：
1. 一定显式设置 `BaseUrl`，并且只从提供商配置读取。
2. 用 `IsolatedAnthropicClient` 关掉 profile 和 WIF 的自动解析。
3. `Handlers = [new EnvIsolationHandler(hasConfiguredKey)]`：删掉 `ANTHROPIC_CUSTOM_HEADERS` 里声明的请求头；配置里没有 key 时，删掉 `Authorization` 和 `x-api-key`。key 为空、env 也干净时，SDK 不会报缺少凭据，会正常发出不带认证的请求（已测，适用于不需要 key 的本地网关）。
4. **生产建议再加一层**：在 `App` 启动最早的时候，把进程里的 `ANTHROPIC_*` 全部清掉（`Environment.SetEnvironmentVariable(name, null)`），并且显式设置 `ClientOptions.OpenTelemetry`，防止 `ANTHROPIC_OPEN_TELEMETRY*` 打开内容追踪（文档说可以设为 `false` 关闭 span，spike 里没测）。另外要注意，SSH 子进程不应该继承这些变量。

OpenAI SDK：DLL 里只有 `OPENAI_DISABLE_TELEMETRY`、`OPENAI_EXPERIMENTAL_ENABLE_OPEN_TELEMETRY` 和两个 TTS / 转写开关，**不读 `OPENAI_API_KEY` 或 `OPENAI_BASE_URL`**，构造函数要求显式传入凭据。`ApiKeyCredential` 不接受空字符串，key 为空时传占位值 `"unused"`。

## 5. 风险 / 未验证

- 没有调用真实 API。以下几点都是按文档手写 fixture 测出来的：线格式细节（`tool_use.caller`、`stop_details` 在 `message_delta.delta` 中的位置、`fallback` 块的 `trigger`），strict 模式接受哪些 schema 关键字，DeepSeek 回传 `reasoning_content` 的要求。拿到 key 后先用 Console 各跑一遍。
- mid-stream fallback（拒绝前已经输出了一部分，之后才出现 fallback 块）只覆盖了回放规则的代码，没有对应的 fixture。
- `ant` profile 文件（`~/.config/anthropic`）的读取：测试把 `ANTHROPIC_CONFIG_DIR` 指向一个空目录做隔离，没有构造真实的 profile 来验证 `IsolatedAnthropicClient` 能挡住它（依据是 XML 文档）。
- `JsonPatch` 是实验性 API（SCME0001），OpenAI SDK 升级时可能改名。

## 6. 对正式 MySSH.Ai 的建议

1. **保留这层薄抽象**（`IChatProvider` + 归一化的 `ChatEvent` + `AiMessage.RawProvider/RawContent`），不要走 M.E.AI。spike 证明 Claude 的 fallback 块、思考签名、refusal 详情都需要原生类型来处理。`Stopped` 事件携带的 assistant 消息就是要追加的历史，**Agent 循环只执行 `Stopped.Message.ToolCalls` 里的调用**，不执行流式事件里出现过的调用（这样 fallback 边界之前的调用、被 refusal 截断的调用都不会被执行）。
2. AnthropicProvider 统一走 beta 端点。`server-side-fallback-2026-07-01` 可以在设置里关闭，关闭后不发 beta 头和 `fallbacks`。`EagerInputStreaming` 默认对官方端点打开、对自定义端点关闭。
3. 把 `EnvIsolation` 和启动时清理 env 作为必须遵守的规则，加进 `MySSH.Ai` 的单元测试，直接复用 `EnvPitfallTests` 的做法。
4. 工具 schema 只在 `MySSH.Tools` 的 ToolCatalog 里定义一次。客户端校验换成正式的 JSON Schema 库（例如 JsonSchema.Net），不要用 spike 里手写的子集。发给 Claude strict 模式的 schema 需要做一次转换，具体规则以真实 API 的验证结果为准。
5. 持久化：`RawContent` 存 SDK 序列化出来的 `BetaContentBlock[]` JSON，用 MasterKey 加密后存进 SQLite。回放时反序列化再逐块重建 Param，遇到未知的块类型直接报错，不要静默丢弃。丢块会破坏 preserved thinking 的前缀校验。
6. 缓存：tools 和 system 各放一个断点，再加顶层 auto `cache_control` 缓存对话历史；从 `Usage.CacheReadTokens` 统计命中率，显示在界面的 token 用量里。
7. 模型列表需要分页（Anthropic `has_more`）。设置页「测试连接」的流程可以直接复用 `ListModelsAsync`，再发一次带 1 个工具的小请求。
8. mock 服务器和 SSE fixture 可以直接搬到正式测试工程，作为提供商层的回归测试。
