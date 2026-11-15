# MySSH 重构设计：Avalonia + IBM Carbon + MCP + 内置 AI 助手

> 状态：v0.3 · 2026-11-15（已按 P0 Spike 结论修订，见 `spikes/FINDINGS.md`）
> 范围：用 .NET + Avalonia 重写桌面端（Windows / macOS / Linux），**彻底删除** Tauri + Vue + Rust 实现

**v0.3 的变更（P0 结论）**
- 依赖版本锁定：Avalonia 12.1.6、XTerm.NET 3.0.3、SSH.NET 2026.0.1、ModelContextProtocol 2.4.0、Anthropic 13.8.0、OpenAI 2.15.0。
- **从第一天起按 NativeAOT 设计**：JSON 全部使用 source-gen，绑定使用 compiled bindings（§1）。
- 终端 VT 引擎确定为 XTerm.NET，不再保留自研解析器兜底方案；补充重绘性能目标（§4）。
- SSH 层补充：`IHopTransport`、断线心跳、背压控制、SFTP 符号链接 bug 的规避方式（§8）。
- MCP 保持 Stateless；客户端身份按 Token 识别；UDS 读到 EOF 时取消会话（§6.4）。
- AI：Claude 统一走 beta 端点；**环境变量隔离为硬性要求**（§7.2）。

**v0.2 的变更**
- 确认彻底删除 Tauri 版，不再保留 `legacy/` 目录（见 §11）。
- 终端**不使用 WebView**，改为原生控件：自研渲染层，VT 引擎复用开源实现（见 §4）。
- 新增**内置 AI 助手**：预置 OpenAI、DeepSeek、Claude 三家，只需填 API Key；也可以自定义 OpenAI 兼容或 Anthropic 兼容的端点（见 §7）。
- 新增 `MySSH.Tools`：工具目录、授权策略、审批和审计统一放在这里，MCP 和 AI 助手共用一套。

---

## 0. 目标与非目标

**目标**
1. 桌面端改用 **Avalonia 12 + .NET 10 (LTS)**，单一 C# 技术栈。
2. 界面遵循 **IBM Carbon Design System v11**。
3. **原生终端控件**：支持中日韩字宽和输入法组字、VT 兼容、高吞吐。
4. 内置 **MCP Server**，让外部 AI 客户端（Claude Code、Claude Desktop 等）在用户授权下操作主机。
5. **内置 AI 助手**：支持多家模型提供商，能理解当前终端上下文，并在用户审批下调用工具。
6. 修复旧版问题：主机密钥校验、存储会丢数据、备份 KDF 太弱、会话泄漏、大文件全量读进内存、代码重复。
7. 首次启动自动导入旧版 `servers.json`，并兼容旧版 v1 备份文件。

**非目标（本期不做）**
- 移动端、云同步、团队共享。
- 让 AI 助手连接外部 MCP Server（即 MCP Client 方向）。这一项列为 P6 可选，见 §7.9。

---

## 1. 技术选型

| 领域 | 选择 | 说明 |
|---|---|---|
| 运行时 | .NET 10 LTS | **按 NativeAOT 设计**：MCP 宿主的内存从 153MB 降到 99MB，Avalonia、ASP.NET Core、MCP SDK 都没有 trim 警告；JSON 全部 source-gen；CI 跑 AOT 发布 |
| UI | Avalonia 12.x | 基于 `Avalonia.Themes.Simple` 自写 Carbon ControlTheme |
| MVVM | CommunityToolkit.Mvvm | |
| DI / 宿主 | Microsoft.Extensions.Hosting | 和 MCP 的 ASP.NET Core 宿主共用一个 Host |
| SSH / SFTP | SSH.NET (Renci.SshNet) | 代理、`HostKeyReceived`、`ForwardedPortLocal`、ShellStream、异步 SFTP |
| 终端 | **自研 Avalonia 渲染控件 + XTerm.NET 3.0.3**（MIT，xterm.js 移植） | 见 §4 |
| 存储 | SQLite（Microsoft.Data.Sqlite） | |
| 加密 | `AesGcm` + Argon2id | 主密钥存放在系统密钥库 |
| MCP | `ModelContextProtocol` 2.x + `.AspNetCore` | 官方 C# SDK |
| AI · Claude | 官方 **`Anthropic`** NuGet（`AnthropicClient`） | 原生 Messages API，**不走 OpenAI 兼容层** |
| AI · OpenAI 兼容 | 官方 **`OpenAI`** NuGet（Chat Completions + 自定义 Endpoint） | OpenAI、DeepSeek、自定义端点共用 |
| 日志 | Microsoft.Extensions.Logging + Serilog | API Key 和终端内容不写入日志 |
| 安装 / 更新 | Velopack | |
| 测试 | xUnit、Testcontainers（openssh-server）、Avalonia.Headless | |

---

## 2. 解决方案结构

```
MySSH.sln
src/
  MySSH.Core/          领域模型、服务接口（无 UI、无 IO 实现）
  MySSH.Storage/       SQLite、Vault、系统密钥库、旧数据导入
  MySSH.Ssh/           连接工厂（代理/跳板链）、连接池、Shell/Exec/SFTP、传输队列、known_hosts
  MySSH.Tools/         ★ 工具目录 + 授权策略引擎 + 审批接口 + 审计（MCP 和 AI 共用）
  MySSH.Mcp/           把 Tools 暴露为 MCP Server（HTTP + 本地套接字）
  MySSH.Ai/            ★ 提供商抽象、Claude/OpenAI 兼容实现、Agent 循环、上下文构建、会话存储
  MySSH.Terminal/      ★ VT 引擎适配层 + TerminalControl（渲染、输入、输入法、选择、搜索）
  MySSH.UI.Carbon/     Carbon token、ControlTheme、图标、字体、自定义控件
  MySSH.Desktop/       Avalonia App：Views、ViewModels、组合根、托盘
  MySSH.McpBridge/     myssh-mcp：stdio ⇄ 本地套接字转发
tests/  …Core / Storage / Tools / Mcp / Ai / Terminal（含 VT 回放测试）/ Ssh.Integration / Desktop.UI
tools/  gen-carbon-tokens/  gen-carbon-icons/
```

依赖方向：`Desktop → (UI.Carbon, Terminal, Mcp, Ai) → Tools → (Ssh, Storage) → Core`。

---

## 3. 领域模型

```
Host         id, label, hostname, port, username, identityId?, groupId?, tags[],
             proxyId?, jumpChain[hostId...], keepAliveSec=30, encoding, terminalProfileId?,
             aiPolicy, aiIdentityId?, notes, createdAt, updatedAt
Identity     id, label, username?, kind(Password|PrivateKey), secret(加密), passphrase(加密)?
ProxyProfile id, label, type(Http|Socks5), host, port, username?, password(加密)?
Group        id, name, parentId?, sort
KnownHost    hostPort, keyType, sha256Fingerprint, firstSeen, lastSeen
AiProvider   id, kind(Anthropic|OpenAICompatible), preset(OpenAI|DeepSeek|Claude|Custom),
             label, baseUrl, apiKey(加密)?, defaultModel, extraHeaders(加密)?, capabilities, useAppProxy
AiConversation  id, title, providerId, model, hostId?, createdAt, updatedAt, pinned
AiMessage       id, conversationId, seq, role, rawContent(json，提供商原样格式), usage(json), ts
AuditEntry   id, ts, actor(user | mcp:<client> | ai:<provider>/<model>), tool, hostId, args, decision, exitCode, durationMs
Setting      key, value(json)
```

相比 v0.1 的变化：`mcpPolicy` 改名为 **`aiPolicy`**，MCP 客户端和内置 AI 助手**共用同一套策略**；新增 `aiIdentityId`，可以给 AI 指定一个低权限账号。

---

## 4. 原生终端控件（MySSH.Terminal）

### 4.1 分层
```
SSH 读线程 ─EnqueueOutput─▶ 队列 ─▶ UI 线程每 8ms 取数据交给 XTerm.NET 解析（每帧最多 4MB）
                                        │ RefreshContentVersion() 变化 → 只重绘变化的行（GetRowVersion）
                                        ▼
                         TerminalControl : Control（UI 线程，按帧渲染）
键盘 / 输入法 / 鼠标 / 粘贴 ─▶ Engine.GenerateKeyInput / GenerateMouseEvent / Paste ─▶ SSH 输入字节
```

不再自己抽象 `ITerminalEngine`，直接使用 XTerm.NET 的数据模型（`BufferLine` / `BufferCell` / `AttributeData`），省掉一层复制。数据洪峰时如果 UI 线程解析仍然吃紧，再把解析移到后台线程，配合快照交给 UI 线程。

### 4.2 VT 引擎：XTerm.NET（P0 已选定）
P0 在 4 个候选里做了 24 项一致性用例和吞吐对比，XTerm.NET 3.0.3 结果是 24/24、175 MB/s、内存分配最少，而且维护活跃。它是 MIT 许可，必要时可以 fork。控件本身（渲染、输入、输入法）由我们自己实现，原型见 `spikes/terminal/`。

注意事项：写入数据后必须调用 `RefreshContentVersion()`，否则版本号不变、不会重绘；回绕重排遵循 xterm.js 的语义，光标所在行不参与重排。

### 4.3 必须做到的能力
| 能力 | 方案 |
|---|---|
| **中日韩字宽** | 用 Unicode 15 的 East Asian Width 表加 emoji 表计算 wcwidth；宽字符占 2 格，后一格为占位格；支持字素簇（ZWJ 组合 emoji） |
| **输入法组字** | 实现 Avalonia 的 `TextInputMethodClient`：上报光标矩形让候选窗跟随光标；预编辑文本在光标处带下划线内联绘制；上屏后编码成 UTF-8 发送。三个平台逐一验证：Windows TSF、macOS、Linux IBus/Fcitx5 |
| 字体回退 | IBM Plex Mono → 可配置的 CJK 等宽字体（如 Sarasa Mono SC/J）→ 系统字体；宽字符强制按 2 格对齐绘制 |
| 渲染性能 | 按行缓存（每行一个子 Visual 或缓存的 `GlyphRun` 列表），只重建 `GetRowVersion` 变化的行；滚动时位移已有行缓存；目标 **160×50 满屏重绘 ≤ 4ms**（P0 原型逐帧全量重绘是 12–16ms） |
| 制表符 | 框线和块元素（U+2500–U+259F）用程序绘制，保证行与行之间无缝 |
| 吞吐量 | 解析吞吐 ≥ 100 MB/s（P0 实测 175 MB/s）；数据洪峰时降到 30fps 合帧 |
| 键盘 | 完整的 xterm 键位编码（修饰键、F1–F12、应用光标模式和小键盘模式）；macOS Option 键可设置为 Meta |
| 鼠标 | 选择（双击选词、三击选行、Alt 矩形选择）；SGR 1006 鼠标上报；滚轮在主屏滚动回滚区，在备用屏发送方向键 |
| 剪贴板 | 括号粘贴；粘贴多行时弹确认；OSC 52 写剪贴板需要用户授权 |
| 其他 | 回滚区搜索、URL 和 OSC 8 链接（Ctrl/Cmd+点击打开）、OSC 7 当前目录（SFTP 面板可以跟随切换）、无障碍文本 peer |
| 给 AI 的接口 | `GetVisibleText(maxLines)`、`GetSelection()`，提供给 AI 助手和 MCP 的 `read_terminal` |

### 4.4 测试
- **回放测试**：把 vim、tmux、htop、less 中文日志等会话的字节流录成 fixture，断言最终的屏幕快照（不依赖 UI，跑在 CI 上）。
- **输入法测试**：每个平台准备一份手工验收清单（自动化很难覆盖），另外用 Headless 测试覆盖预编辑渲染。
- **性能基准**：用 BenchmarkDotNet 测解析吞吐量，再加一个渲染帧时间的基准。

---

## 5. 存储与安全
（和 v0.1 相同）
- **密钥层次**：MasterKey 存放在系统密钥库（DPAPI / Keychain / Secret Service）。字段加密使用 AES-256-GCM，AAD 为 `"{table}:{id}:{field}"`。另有可选的主密码模式（Argon2id 包装 MasterKey）。Linux 上没有 Secret Service 时强制使用主密码。
- **解密失败**：记录不丢弃，只读保留并提示用户。
- **known_hosts（TOFU）**：未知主机弹窗显示指纹；指纹不匹配时阻止连接；**由 AI 或 MCP 发起的连接遇到未知或已变更的指纹，一律失败**。
- **备份 v2**：使用 Argon2id，并兼容导入 v1。
- **数据目录**：macOS `~/Library/Application Support/MySSH.Desktop`、Windows `%APPDATA%\MySSH.Desktop`、Linux `$XDG_DATA_HOME/myssh-desktop`。名字刻意和旧版的 `myssh` 区分开（macOS 不区分大小写）。
- **旧数据迁移**：读取 `{data_dir}/myssh/servers.json` 和 `.key`，保留原 ID，`jump_host` 转成 `jumpChain`，从凭据中拆出 Identity，只读取、不删除旧文件。
- **AI Provider 的 API Key** 和所有凭据一样加密存储，不写日志，不通过 MCP 暴露，也不放进备份明文。导出备份时可以选择是否包含 API Key（默认不包含）。

---

## 6. 共享工具层（MySSH.Tools）与 MCP

### 6.1 共享工具层
```
              ┌──────────── MySSH.Tools ─────────────┐
 MCP Server ─▶│ ToolCatalog（名称、JSON Schema、注解） │
 AI 助手    ─▶│ PolicyEngine（按主机 aiPolicy）        │──▶ Ssh / Terminal 服务
              │ IApprovalService（由 GUI 实现）        │
              │ AuditLog                              │
              └───────────────────────────────────────┘
```
- 每个工具只定义一次：名称、描述、输入 Schema（`additionalProperties:false`）、是否只读或破坏性、处理器。MCP 用它生成 `[McpServerTool]` 适配层，AI 助手用它生成各提供商格式的 tool 定义。
- 调用流程：`Validate(schema)` → `PolicyEngine.Authorize` → 必要时 `IApprovalService` → 执行 → 截断输出 → 写审计。
- **审批界面因来源而异**：外部 MCP 客户端用 Modal；内置 AI 助手在聊天面板里显示内联审批卡片（§7.6）。

### 6.2 工具列表
| 工具 | 说明 | 注解 |
|---|---|---|
| `list_hosts(query?, tag?)` | 只列出 `aiPolicy` 不是 Off 的主机，永不返回凭据 | readOnly |
| `run_command(host, command, timeout_s=60, cwd?)` | 走 exec 通道；stdout 和 stderr 各最多 64KB，超出截断 | destructive |
| `sftp_list` / `sftp_stat` / `sftp_read(max_bytes=256KB)` | | readOnly |
| `sftp_write(mode)` / `sftp_mkdir` / `sftp_delete` | overwrite 和 delete 一定要审批 | destructive |
| `list_sessions` / `read_terminal(session, lines)` | 读取用户的终端内容 | readOnly |
| `send_to_terminal(session, text, execute=false)` | 默认**只把文本填进输入行、不回车**；`execute=true` 时必须审批 | destructive |

### 6.3 授权策略（每台主机单独设置，默认 Off，MCP 和 AI 共用）
| 策略 | 只读工具 | 执行和写操作 |
|---|---|---|
| **Off**（默认） | 不可见 | 不可见 |
| **ReadOnly** | 允许 | 只允许白名单命令 |
| **Ask** | 允许 | 每次审批 |
| **Trusted** | 允许 | 自动放行，**命中高风险规则时仍然审批** |

高风险规则只是启发式的防误操作手段，不是安全边界；建议为 AI 单独设置低权限的 `aiIdentityId`。审批等待超时（120 秒）按拒绝处理。

### 6.4 MCP Server
- **HTTP**：Streamable HTTP，**Stateless 模式**（SDK 2.4 默认；有状态模式下旧协议客户端的取消请求到不了服务端）。只监听 `127.0.0.1`；端口首次随机分配，之后固定；Bearer Token 存放在系统密钥库，可以重新生成。自己校验 Origin 和 Host 头，拒绝顺序为 403（Origin）→ 421（Host）→ 401（Token）。客户端身份按 Token / 客户端标签识别（旧协议客户端在 Stateless 下拿不到 ClientInfo）。
- **本地套接字**：macOS 为 `~/Library/Application Support/MySSH.Desktop/mcp.sock`（不能用 `MySSH`：macOS 文件系统不区分大小写，会和旧版的 `myssh` 目录冲突），Linux 为 `$XDG_RUNTIME_DIR/myssh-desktop/mcp.sock`（0600，目录 0700，校验对端 uid），Windows 为 `\\.\pipe\myssh-mcp-{user}`（CurrentUserOnly）。每个连接一个会话；**读到 EOF 时取消会话**（否则正在执行的工具不会停）。残留 socket 能连上就说明已有实例在运行，借此实现单实例。`myssh-mcp` 是独立的 AOT 小程序（约 2MB），只转发字节；GUI 没在运行时用 `--tray` 拉起它。
- **线程**：工具运行在线程池；审批窗口通过 `Dispatcher.UIThread.InvokeAsync` 创建，在线程池上等待结果；退出时在线程池上停止宿主，`ShutdownTimeout` 设为 3s。
- **资源**：`myssh://hosts`、`myssh://sessions/{id}/screen`。**提示**：`diagnose_host`、`analyze_logs`。
- **设置页**：显示端点和 Token；一键复制 `claude mcp add --transport http …` 命令和 Claude Desktop 的 JSON 配置；列出已连接的客户端并可断开；提供总开关（默认关闭）。
- **不提供**的工具：本地文件系统、信任主机指纹、修改配置、读取凭据。

---

## 7. 内置 AI 助手（MySSH.Ai）

### 7.1 提供商配置
| 预置 | 协议 | 默认 Base URL | 用户需要填写 | 模型 |
|---|---|---|---|---|
| **Claude** | Anthropic Messages（原生） | `https://api.anthropic.com` | API Key | 默认 `claude-opus-5-5`；可从 `GET /v1/models` 拉取列表切换 |
| **OpenAI** | OpenAI Chat Completions | `https://api.openai.com/v1` | API Key | 从 `GET /models` 拉取后选择 |
| **DeepSeek** | OpenAI 兼容 | `https://api.deepseek.com` | API Key | 从 `GET /models` 拉取后选择 |
| **自定义** | 可选 OpenAI 兼容或 Anthropic 兼容 | 用户填写 | API Key（本地模型可以留空）、模型 ID、额外请求头 | 手动填写，或点「获取模型列表」 |

- OpenAI 和 DeepSeek 的默认模型**不写死**（这两家的型号变化很快），首次配置时拉取模型列表让用户选择。
- **能力开关**（自定义提供商需要）：支持工具调用 / 支持流式 / 支持图片。「测试连接」按钮会请求一次模型列表，再发一条带 1 个工具的极小请求，自动探测这三项。
- **网络**：每个提供商可以选择「使用应用全局代理」（HTTP 或 SOCKS5），方便网络受限的环境。
- 可以设置**默认提供商**；每个会话可以单独切换提供商和模型（切换后从新消息开始生效）。

### 7.2 提供商抽象
**不使用** Microsoft.Extensions.AI 的通用 `IChatClient` 作为主路径，原因有两个：一是要在每次工具调用前插入策略和审批；二是 Claude 的原生能力（自适应思考、提示缓存、refusal 处理、思考块回传）经过通用层会丢失。所以自己定义一层很薄的抽象，把事件归一化：

```csharp
public interface IChatProvider {
    ProviderCapabilities Capabilities { get; }
    Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct);
    IAsyncEnumerable<ChatEvent> StreamAsync(ChatRequest req, CancellationToken ct);
}
// ChatEvent: TextDelta | ThinkingSummaryDelta | ToolCallStarted | ToolCallInputDelta | ToolCallCompleted
//            | Usage | Stopped(StopReason: EndTurn|ToolUse|MaxTokens|Refusal|Error, detail)
// 每轮的提供商原始 content 原样存入 AiMessage.rawContent，回放时原样送回
```

**AnthropicProvider（官方 `Anthropic` SDK）**
- **统一走 beta 端点**：`client.Beta.Messages.CreateStreaming(...)` 和 beta 命名空间的类型（服务端回退只有 beta 端点支持）。SDK 用 `Authorization: Bearer` 发送 Key；只认 `x-api-key` 的自定义网关由 handler 改写请求头。
- **环境变量隔离（硬性要求）**：SDK 会读取 `ANTHROPIC_BASE_URL`、`ANTHROPIC_API_KEY`、`ANTHROPIC_AUTH_TOKEN`、`ANTHROPIC_CUSTOM_HEADERS` 等变量；即使显式设置了 BaseUrl 和 Key，`ANTHROPIC_CUSTOM_HEADERS` 仍会被附加，没填 Key 时还会带上 `ANTHROPIC_AUTH_TOKEN`。所以：始终显式设置 BaseUrl（只来自提供商配置）；使用 `IsolatedAnthropicClient` 加 `EnvIsolationHandler`；应用启动时清除进程内所有 `ANTHROPIC_*`；SSH 子进程不继承这些变量。
- 思考设为 `{type:"adaptive"}`；`output_config.effort` 可以在界面上选「快速 / 标准 / 深入」，对应 low / medium / high。默认**显式设为 medium**（Opus 5.5 的默认值本来就是 medium，显式写出避免歧义）。不发送 temperature 等采样参数。
- 工具使用 `tool_choice: auto`（Opus 5.5 不支持强制指定工具）。每个工具设 `strict: true` 和 `eager_input_streaming: true`。工具输入**执行前先按 Schema 校验**，校验失败时回传错误类型的 `tool_result`。执行工具前先检查 `stop_reason` 是否为 `max_tokens` 或 `refusal`。
- **refusal 处理**：默认开启服务端回退（beta `server-side-fallback-2026-07-01` 加 `fallbacks: "default"`），设置里可以关闭。最终仍是 refusal 时，在界面上给出说明。
- **提示缓存**：在 tools 和 system 末尾放 `cache_control`，并保证它们按字节稳定（工具按名称排序，system 里不放时间戳）；再加顶层自动缓存，覆盖对话历史。当前主机、终端片段这类动态上下文放进当轮的 user 消息里。前缀稳定对 preserved thinking 是硬性要求，不只是为了缓存。
- **执行范围**：Agent 循环只执行 `Stopped` 事件携带的最终消息里的工具调用；fallback 块之前的调用、被 refusal 截断的调用都不执行。fallback 块按原位置回传。
- **思考显示**：提供 `summarized`（推理摘要）和 `updates`（只显示工具之间的进度说明）两个选项。
- **历史只追加、不改写**：思考块等内容原样回传，不编辑也不删除已有的历史消息。上下文过长时，提示用户「总结后开新会话」（以后可以接入 compaction）。

**OpenAICompatibleProvider（官方 `OpenAI` SDK + 自定义 Endpoint）**
- 使用 Chat Completions 的流式接口和 `tools`/`tool_calls`。按能力开关降级：不支持工具时进入纯问答模式；不支持流式时改为一次性返回。
- DeepSeek 等返回的思维链字段（如 `reasoning_content`）如果存在，就显示在「思考过程」折叠区里。
- 兼容性：容忍 `tool_calls` 参数是分片拼接的 JSON；解析失败时回传错误让模型重试。

### 7.3 Agent 循环（自己实现，便于插入审批）
```
loop (最多 25 步，可以随时取消):
  stream = provider.StreamAsync(history + tools)
  渲染文本、思考摘要、工具调用卡片
  if stop == Refusal / MaxTokens → 提示后结束
  if stop != ToolUse → 结束
  并行执行本轮的所有工具调用：每个都经过 Tools 层（校验 → 策略 → 审批 → 执行 → 审计）
  把所有 tool_result 放进同一条消息追加到历史（拒绝、出错都回传 is_error）
```
- 步数上限、单次工具输出上限、会话 token 上限都可以在设置里调整；界面上显示每个会话的 token 用量。
- 审计里的 actor 记为 `ai:<provider>/<model>`。

### 7.4 上下文构建（用户看得见、可控制）
- 输入框上方用「上下文胶囊」（Carbon Tag，可以逐个移除）显示这次要发送的内容：`当前主机 prod-web-01`、`终端最近 200 行`、`选中文本`、`SFTP 文件 /etc/nginx/nginx.conf`。
- **默认只附带当前主机的元信息**（名称、OS 信息如果已知、当前目录），终端内容要用户点一下才附带。
- **脱敏**（默认开启）：发送前对私钥块、`password=`、`token=`、AWS/GitHub key 等模式做替换，并在界面提示「已脱敏 N 处」。
- system prompt 固定写明：当前是 SSH 运维助手、使用的语言跟随界面语言、执行破坏性操作前要说明风险。

### 7.5 交互入口
1. **右侧 AI 面板**（`Ctrl+Shift+A`，Carbon 侧边面板，宽 400，可以拖动调整）：会话列表加对话区。
2. **终端内联**（`Ctrl+I`）：在终端光标处弹出一个小输入条，用自然语言描述要做的事 → 生成命令建议 → 按 `Tab` 填进输入行（**不自动回车**）、`Esc` 取消。
3. **右键菜单**：选中终端文本后可以「解释这段输出」「分析这个报错」；在 SFTP 文件上可以「解释这个配置文件」。
4. **命令面板**（`Ctrl+K`）里输入 `?` 开头的内容，直接转成向 AI 提问。

### 7.6 工具调用卡片和内联审批
```
┌ [tool] run_command · prod-web-01 ──────────────────── 等待审批 ┐
│ $ sudo systemctl restart nginx                                 │
│ [!] 高风险：systemctl restart                                   │
│ [ 拒绝 ](ghost)   [ 本会话允许相同命令 ](tertiary)   [ 允许 ](danger) │
└────────────────────────────────────────────────────────────────┘
   ↓ 执行后
┌ [tool] run_command · prod-web-01 ───────────── 退出码 0 · 1.2s ┐
│ $ sudo systemctl restart nginx         [展开输出 ▾] [在终端中打开] │
└────────────────────────────────────────────────────────────────┘
```

### 7.7 会话存储与隐私
- 会话保存在 SQLite 里（消息内容用 MasterKey 加密），可以设置「不保存历史」或「N 天后自动删除」。
- 首次启用某个提供商时显示一次提示：「发送的内容会传给 {提供商}，并受其数据政策约束」。

### 7.8 AI 设置页
- 提供商列表（Carbon Data Table）：名称、协议、Base URL、模型、状态（已验证 / 失败）、默认提供商单选。
- 新建或编辑提供商：先选预置模板，再填 API Key（密码框），然后测试连接、选择模型。高级选项：Base URL、额外请求头、代理、能力开关。
- 行为设置：effort 档位（仅 Claude）、是否显示思考摘要、默认附带哪些上下文、脱敏开关、步数和 token 上限、历史保留策略。

### 7.9 后续可做（P6）：MCP Client
让 AI 助手连接用户配置的外部 MCP Server（stdio 或 HTTP），把外部工具和内置工具合并后提供给模型，外部工具一律走 Ask 审批。

---

## 8. SSH 层
（按 P0 结论修订，详见 `spikes/ssh-chain/FINDINGS.md`）
- **连接工厂**：`ConnectAsync(hostId, origin{User|Ai|Mcp}, ct) → IConnectionLease`。多跳链路为 `[proxy] → jump1 → … → target`，代理只用在第一跳。每一跳通过 `IHopTransport.Open(prevClient, realHost, realPort)` 得到本地端点：
  - 默认实现用 `ForwardedPortLocal("127.0.0.1", 0, …)`，转发要**存活到整条链路结束**（提前 `Stop()` 会断掉会话）。转发端口对本机所有进程开放，所以订阅 `RequestReceived`，第一个连接之外再有连接进来就关闭整条链。
  - 可选的加固实现 `SingleAcceptBridge`：只接受一次连接后立即关闭监听，通过反射使用内部 direct-tcpip 通道。需要锁定 SSH.NET 版本，加启动自检，反射失败时降级到默认实现。
- **主机密钥**：每一跳各自挂 `HostKeyReceived`，按真实的 `host:port` 校验（经过跳板时 ConnectionInfo 里是 127.0.0.1:转发端口）。指纹用 OpenSSH 格式 `SHA256:…`。用户连接时先严格模式探测，弹窗确认后写入、再重连；或者在连接期间把 `ConnectionInfo.Timeout` 调到 60s（handler 是同步的，阻塞超过超时会导致连接失败）。AI 和 MCP 发起的连接一律严格模式。
- **连接池**：lease 持有整条链路，按 hostId 引用计数，反向释放；多个通道共用一条连接。
- **断线检测**：订阅所有跳的 `ErrorOccurred`（跳板断开时，target 的 ShellStream.Closed 不会触发）；KeepAlive 设 15–30s，只用来保持 NAT；另加应用层心跳（空闲时执行带超时的 `GetAttributesAsync(".")`），因为 SSH.NET 的 KeepAlive 发现不了对端已经挂死。
- **ShellSession**：`Exited` = `Closed` 或 `ErrorOccurred`。退出码没有公开 API，用反射读 `exit-status`，失败时当作 null。**没有背压**：读循环必须一直运行，缓冲上限由终端控件控制。
- **ExecService**：`ExecuteAsync(linkedCt)` 加 `CommandTimeout`；边读 `OutputStream` 边计数，到上限就取消（取消和超时都会杀掉远端进程）。
- **SFTP**：全部用 `*Async` 加自己写的同步 IProgress（UI 侧节流）。**删除和重命名只能用 `ISftpFile.DeleteAsync` / `MoveTo`**：按路径调用的 API 会先解析符号链接，作用到链接指向的目标上（SSH.NET bug，要报给上游）。递归删除自己实现（`ListDirectoryAsync` 不跟随链接）。取消传输后清理远端的半截文件。只有同步版本的 API（SymbolicLink、ChangePermissions 等）包在 `Task.Run` 里。
- **TransferQueue**：显示进度、可取消、可设置冲突策略。
- **私钥口令**：空字符串当作没有；遇到 `SshPassPhraseNullOrEmptyException` 时弹窗让用户输入；加载时抛 `SshException` 提示「口令错误或密钥损坏」。
- **服务器端罚时**：OpenSSH ≥ 9.8 的 `PerSourcePenalties` 会对反复「连上但不认证」的来源罚时，所以探测连接和重试要控制频率。

---

## 9. Carbon 主题（MySSH.UI.Carbon）

### 9.1 Token（由 `tools/gen-carbon-tokens` 从 `@carbon/themes` 生成，下表只举例）
| Token | White（浅色） | Gray 100（深色） |
|---|---|---|
| `background` | #ffffff | #161616 |
| `layer-01` / `layer-02` | #f4f4f4 / #ffffff | #262626 / #393939 |
| `field-01` | #f4f4f4 | #262626 |
| `border-subtle-01` / `border-strong-01` | #c6c6c6 / #8d8d8d | #393939 / #6f6f6f |
| `text-primary` / `text-secondary` | #161616 / #525252 | #f4f4f4 / #c6c6c6 |
| `interactive` / `focus` | #0f62fe / #0f62fe | #4589ff / #ffffff |
| `button-primary`（hover / active） | #0f62fe（#0050e6 / #002d9c） | 同左 |
| `support-error / success / warning / info` | #da1e28 / #24a148 / #f1c21b / #0043ce | #fa4d56 / #42be65 / #f1c21b / #4589ff |

- `ThemeVariant.Light → White`、`Dark → Gray 100`，并支持跟随系统；Header 和 SideNav 固定使用 Gray 100。
- 终端默认配色取自 Gray 100 加 IBM 调色板。**AI 面板**使用 `layer-01` 背景；AI 回复左侧加一条 2px 的 `border-interactive` 竖线，和用户消息区分开。

### 9.2 排版、间距、形状、动效、图标
- **字体**：IBM Plex Sans、IBM Plex Sans SC/JP、IBM Plex Mono（均为 OFL 许可，随应用打包）。
- **字号层级**：`label-01 12/16` · `body-compact-01 14/18` · `body-01 14/20` · `heading-02 16/24` · `heading-03 20/28` · `heading-04 28/36`。
- **间距**：2 · 4 · 8 · 12 · 16 · 24 · 32 · 40 · 48。
- **形状**：圆角为 0；焦点框为 2px 内描边；输入框只有底部 1px 边框；按钮高度 48 / 40 / 32。
- **动效**：productive 曲线 `cubic-bezier(0.2,0,0.38,0.9)`，时长 70 / 110 / 150 / 240ms。
- **图标**：`@carbon/icons` 转成 `StreamGeometry` 资源，AI 相关只用通用图标，例如 `chat`、`machine-learning`、`magic-wand`，不用 IBM 产品专属图标。
- **合规**：不使用 IBM 名称或 Logo 作为产品品牌。

### 9.3 控件清单
UI Shell Header / SideNav · Button（5 种）· Text input / Password / Text area · Dropdown · Checkbox / Radio / Toggle · Tabs（contained / line）· Data table · Modal · Inline / Toast notification · Overflow menu · Tooltip · Tag · Breadcrumb · Progress · Loading · **Side panel**（AI 面板、主机编辑）· **Chat bubble / Tool card**（自定义，遵循 Carbon tile 规范）。
另外提供只在 Debug 下可见的 Gallery 页。

### 9.4 主窗口线框
```
┌──────────────────────────────────────────────────────────────────────────────────────┐
│ MySSH │ [search] 搜索主机、命令、?提问…  Ctrl+K      │ (●) MCP·1 │ [ai] │ [gear] │ _ □ x │
├────┬──────────────────────────────────────────────────────────────┬──────────────────┤
│[≡] │ ┃prod-web-01 x┃ db-master x │ prod-web-01·SFTP x │ +          │ AI 助手   [+][⋮] │
│[⌂] ├──────────────────────────────────────────────────────────────┤ Claude · Opus 5.5▾│
│[key]│ deploy@prod-web-01:~$ systemctl status nginx                 │──────────────────│
│[shd]│ ● nginx.service - failed                                     │ 你：nginx 起不来？ │
│[ai] │                                                              │┃AI：从日志看是…   │
│    │            原生终端（IBM Plex Mono 14）                       │┃[tool] read_term…✓│
│[⚙] │                                                              │┃建议执行：         │
│    │                                                              │┃ nginx -t [填入]   │
│    │                                                              │──────────────────│
│    │                                                              │[主机×][终端200行×]│
│    │                                                              │ 输入问题…   [发送]│
├────┴──────────────────────────────────────────────────────────────┴──────────────────┤
│ ● 已连接 · bastion → prod-web-01 · 23ms · UTF-8 · 120×40         传输 2/5 ▮▮▯ · AI 3.2k tok │
└──────────────────────────────────────────────────────────────────────────────────────┘
```
主机列表（Data Table）、主机编辑面板、MCP 审批 Modal 的线框和 v0.1 相同。主机编辑面板里的「AI / MCP」分区包括：策略、命令白名单、AI 专用 Identity。

---

## 10. 应用层
- **ViewModels**：Shell、HostList、HostEditor、TerminalTab、SftpTab、AiPanel、AiConversation、AiProviderEditor、McpSettings、AuditLog……
- **国际化**：`.resx` 包含 zh-CN / en-US / ja-JP（从现有的 locales JSON 用脚本转换），支持运行时切换。
- **托盘常驻**：MCP 开启时，关闭窗口后留在托盘继续服务。

---

## 11. 删除 Tauri 版
1. 在当前 `main` 最后一个 Tauri 提交上打 tag **`tauri-final`**，方便以后查阅。
2. P1 的第一个提交删除 `src/`（Vue）、`src-tauri/`、`index.html`、`vite.config.js`、`package.json`、`pnpm-lock.yaml`、`public/`，同时更新 `.gitignore`。
3. 旧代码里两样东西要**先提取再删**：`src/locales/*.json` 转成 `.resx`；用旧代码生成一份迁移 fixture（加密的 `servers.json` 和 `.key`），放进 `tests/fixtures/legacy/`。
4. README 重写为 .NET 版的构建说明；`README/` 下的截图在 P5 换成新界面的截图。

---

## 12. 实施路线

| 阶段 | 内容 | 完成标准 |
|---|---|---|
| **P0 Spike** ✅ | 已完成，结论见 `spikes/FINDINGS.md` | — |
| **P1 基础** ✅ | 已完成（分支 `p1-foundation`）：删除 Tauri、解决方案骨架、Carbon 主题与控件和 Gallery、Storage/Vault、旧数据导入、known_hosts 存储、主窗口、CI。AOT 门禁：自有库逐成员分析；第三方警告对照 `tools/aot/allowed-warnings.txt`；AOT 二进制跑 `--self-test` | 已达成：UI 测试和 AOT 自检都覆盖「导入旧数据并展示主机列表」 |
| **P2 终端与功能对齐** | TerminalControl 完整实现（§4.3）、主机和身份管理、SFTP 与传输队列、代理和跳板链、备份 v2、三语 | 达到旧版全部功能，终端验收清单全部通过 |
| **P3 工具层 + MCP** | `MySSH.Tools`、策略和审批、审计；MCP 先上只读工具，再上执行和写操作，最后上终端读写 | 策略矩阵测试通过，Claude Code 和 Claude Desktop 都能接入 |
| **P4 AI 助手** | 提供商配置（预置三家 + 自定义）、Agent 循环、AI 面板、内联审批、终端内 `Ctrl+I`、上下文胶囊与脱敏、会话存储 | 三家预置加一个本地 OpenAI 兼容端点（如 Ollama）都能完成「分析报错 → 读终端 → 建议命令」的流程 |
| **P5 发布** | Velopack 打包和自动更新、macOS 签名公证、README 和截图 | 三个平台的安装包 |
| P6 可选 | AI 助手连接外部 MCP Server、端口转发界面、Snippets | |

**最大风险**：P0 的终端 Spike 和 P2 的终端实现（输入法、字宽、性能）。建议这一块由专人负责，并且**最先开工**。

---

## 13. 待确认问题
1. **最低系统版本**：建议 Windows 10 1809+、macOS 13+、Ubuntu 22.04+（Wayland/X11 都要支持）。
2. **AI 会话历史**：默认保存（加密）还是默认不保存？本设计默认保存，并提供自动清理选项。
3. **自定义提供商**：除了 OpenAI 兼容，是否也需要「Anthropic 兼容」协议（用于各类中转网关）？本设计默认两种都支持。
4. **终端 CJK 默认字体**：需要找一款可以随应用打包、并且有 CJK 等宽字形的开源字体（如 Sarasa Mono / 更纱黑体，OFL 许可），还是只依赖系统字体？
