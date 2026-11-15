# Spike ③ MCP：在 Avalonia 进程内运行 MCP（HTTP + UDS）、stdio 桥

> 子代理产出，主会话复核：`.work/run-output.txt` 中 A 27/27、B 8/8、C 2/2 PASS；没有残留进程，socket 和 endpoint.json 已清理。

对应设计文档 §6.4。环境：macOS arm64，.NET SDK 10.0.401，Avalonia 12.1.6，ModelContextProtocol / .AspNetCore 2.4.0（commit 4cbf42e）。

**结论：可行，建议采用。** Avalonia UI 线程和进程内 Kestrel、UDS 会话可以共存。审批弹窗在 UI 线程创建，等待在线程池上进行，互不阻塞。SDK 默认的 Stateless 模式能满足我们的工具需求，取消行为也最好。NativeAOT 版全部检查通过（RSS 约 100MB）。

运行：`./run.sh` 依次执行 A(allow)、B(deny)、C(UI 弹窗超时)、残留进程检查。`--no-ui` 跳过 C；`--hybrid` 额外跑 D（只作参考）。证据在 `.work/`。

## 1. 逐项结果（A 27/27、B 8/8、C 2/2 全部 PASS，无残留进程）
| 检查 | 结果 | 证据 |
|---|---|---|
| Kestrel 绑定 127.0.0.1:0，写入 endpoint.json（0600，原子写入） | PASS | `"url":"http://127.0.0.1:58237/mcp"` |
| HTTP 会话模式 | Stateless（默认） | `sessionId=<none>`，没有 `Mcp-Session-Id` |
| 缺少 / 错误 Token → 401（包括 SDK 客户端） | PASS | `401 WWW-Authenticate=Bearer realm="myssh"`；`HttpRequestException ... 401` |
| 非 localhost Origin、`Origin: null` → 403；`http://localhost:5173` 放行 | PASS | `HTTP REJECT 403 bad Origin 'https://evil.example'` |
| Host 头指向外部域名（DNS rebinding） → 421 | PASS | `HTTP REJECT 421 bad Host 'attacker.example:…'` |
| UDS 0600、目录 0700、按 peer uid 校验 | PASS | `srw-------`，`LOCAL SESSION OPEN #1 uds(peer-uid=501)` |
| tools/list 注解 | PASS | `list_hosts.readOnlyHint=True run_command.destructiveHint=True` |
| list_hosts / run_command(allow)：HTTP 2026-07-28 / HTTP 2025-11-25 / stdio 桥 | PASS ×6 | `isError= 5006ms text=[fake] web-01$ …` |
| run_command(deny)：HTTP / stdio | PASS ×2 | `denied (Deny)` |
| UI 模式没人点 → 超时按拒绝处理 | PASS | `APPROVAL WINDOW SHOWN tool-thread=11 window-created-on-thread=1 (UI)`；`APPROVAL Timeout after 5031ms` |
| 弹窗打开时客户端取消 | PASS | `TOOL run_command CANCELLED after 1001ms`；截图 approval-dialog.png |
| `--tray`（无主窗口、OnExplicitShutdown、ShowInDock=false） | PASS | A、B 两阶段无窗口，SIGTERM 后 exit 0 |
| 桥：socket 不存在 → 退出码 3，错误信息清楚 | PASS | `myssh-mcp: MySSH is not running (socket not found: …)` |
| 桥：逐字节转发，stdin EOF 后干净退出 | PASS | `exited=True code=0 in 3ms` |
| 取消：HTTP 2026-07-28 CTS / HTTP 2025-11-25 CTS / HTTP dispose / stdio CTS / stdio 桥被杀 | PASS ×5 | 客户端约 1.5s 收到 `TaskCanceledException`，服务端同时刻 `TOKEN CANCELLED`；stdio 时日志为 `canceled request '2' per client notification` |
| 并发：HTTP×2 + stdio×1 | PASS | `all ok in 5003ms` |
| SIGTERM 退出：exit 0，删除 sock 和 endpoint.json | PASS | `host exit code: 0` |
| D：Hybrid 模式下 2025-11-25 CTS 取消 | 预期内 FAIL | `NOT cancelled, ran to END`（见 G5） |
| NativeAOT 宿主跑 allow 阶段 | PASS 27/27 | RSS 99MB，就绪 284ms |

**启动与内存**
- **tray（JIT）**：宿主启动 250–366ms，就绪 397–583ms，内存 120→153MB（约 +33MB），负载后 173MB。
- **UI 模式**：就绪 534–635ms，内存 133→200MB。
- **AOT**：宿主启动 212ms，就绪 284ms，内存 71→97.5MB。
- 宿主在 `Task.Run` 中启动，不阻塞首帧（UI 初始化 170ms 完成）。

## 2. SDK API
**服务端注册**
- `AddMcpServer(o => { o.ServerInfo = new Implementation{…}; o.ServerInstructions = … })`
- `.WithHttpTransport(h => h.SessionMode = HttpServerSessionMode.Stateless|Stateful|StatefulForInitializeClients)`
- `.WithTools<T>(JsonSerializerOptions?)`；其他重载：`WithTools(IEnumerable<McpServerTool>)`、`WithListToolsHandler`/`WithCallToolHandler`、`AddListToolsFilter`/`AddCallToolFilter`
- `app.MapMcp("/mcp")`
- `WebApplication.CreateSlimBuilder`、`ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0))`；实际端口从 `IServer.Features.Get<IServerAddressesFeature>().Addresses` 取

**工具**
- `[McpServerToolType]`；`[McpServerTool(Name, Title, ReadOnly, Destructive, Idempotent, OpenWorld)]` + `[Description]`
- 客户端看到的是 `ToolAnnotations.ReadOnlyHint/DestructiveHint/…`
- SDK 注入 `CancellationToken`、`McpServer`、`IServiceProvider`、`IProgress<ProgressNotificationValue>`；构造函数参数走 DI
- 返回 `CallToolResult{IsError, Content=[TextContentBlock]}`
- 工具里可读 `McpServer.ClientInfo`、`SessionId`、`NegotiatedProtocolVersion`

**基于流的服务端传输**
- `new StreamServerTransport(in, out, name, loggerFactory)`
- `McpServer.Create(transport, options, loggerFactory, services)`，然后 `RunAsync(ct)`
- 每个会话的选项用 `IOptionsFactory<McpServerOptions>.Create(Options.DefaultName)` 生成
- `WithStreamServerTransport` 只支持单会话，不适合多连接

**客户端**
- `McpClient.CreateAsync(transport, McpClientOptions{ClientInfo, ProtocolVersion})`
- `HttpClientTransport(new HttpClientTransportOptions{Endpoint, TransportMode=HttpTransportMode.StreamableHttp, AdditionalHeaders})`
- `StdioClientTransport(new StdioClientTransportOptions{Command, Arguments, ShutdownTimeout})`
- `StreamClientTransport`
- `ListToolsAsync()` → `McpClientTool.ProtocolTool`；`CallToolAsync(name, dict, progress, options, ct)`

**协议版本**：SDK 2.4 默认用 2026-07-28（没有 initialize 握手和会话，用 `server/discover` 探测）。不支持时自动降级到 2025-11-25。

## 3. 踩坑记录
- **G1 用 Microsoft.NET.Sdk（非 .Web）引用 ASP.NET**：AspNetCore 包会传递带入 FrameworkReference，但 ASP.NET 的 using 要在 csproj 里自己补。`ConfigureKestrel` 还需要 `using Microsoft.AspNetCore.Hosting`。
- **G2 线程模型**
  - 工具运行在线程池上。审批窗口用 `Dispatcher.UIThread.InvokeAsync` 创建，然后在线程池上 `await tcs.Task.WaitAsync(timeout, ct)`。不能在 UI 线程上等，也不能用 `.Result`。
  - 关窗口用 `Post`。窗口被用户关掉也按拒绝处理。
  - `desktop.Exit` 在 UI 线程触发：用 `Task.Run(StopAsync).Wait()` 在线程池上停宿主，避免死锁。
  - `HostOptions.ShutdownTimeout` 调到 3s（默认 30s，有未完成的审批时退出会卡住）。
  - tray 模式必须用 `OnExplicitShutdown`，否则关掉审批窗口应用就退出了。
- **G3 SIGTERM**：WebApplication 的 ConsoleLifetime 接管 SIGTERM。在 `ApplicationStopping` 里 `Post(desktop.Shutdown)` 即可，实测 exit 0。
- **G4 Stateless + 2025-11-25 客户端拿不到 ClientInfo**：工具里 `ClientInfo == null`，弹窗没法显示客户端名字。2026-07-28 客户端能拿到。正式版用 Token 或客户端标签来标识来源。
- **G5 Stateful / Hybrid 模式下 2025-11-25 客户端的 CTS 取消到不了服务端**：trace 里看不到 `notifications/cancelled`，POST 中断也不影响 handler，工具一直执行到 END；只有 DELETE（dispose）才能取消。结论：保持 Stateless。根因没有追查。
- **G6 HTTP 传输在请求成功后也会触发 token**：必须 `using var reg = token.Register(...)`，否则正常完成会被误报成取消。
- **G7 StreamServerTransport 读到 EOF 时不会取消正在执行的请求**：`RunAsync` 会等它们跑完。修复：用 `EofSignalingStream` 包一层输入流，EOF 时取消传给 `RunAsync` 的 CTS。副作用：写完马上关 stdin 的请求会被取消。
- **G8 UDS**
  - 在 bind 和 listen 之间 chmod，没有竞态窗口；目录 0700。
  - 残留 socket：先尝试 connect，连得上说明另一个实例在跑（顺便实现单实例检测），连不上就删除。
  - 对端身份：macOS 用 `getpeereid`；Linux 的 `SO_PEERCRED` 是 TODO。
  - macOS 的 `sun_path` 最长 104 字节。
  - Windows：`NamedPipeServerStream` + `CurrentUserOnly`，客户端也加 `CurrentUserOnly`。只验证了能编译。
- **G9 HTTP 安全**：SlimBuilder 不带 HostFiltering，Host 头要自己校验。Origin 缺失时放行，`null` 一律拒绝。Token 用 `FixedTimeEquals` 比较。顺序是 403 → 421 → 401。
- **G10 AOT**
  - Avalonia、ASP.NET Core、MCP SDK 都没有产生 trim 警告。
  - 运行时坑 1：工具返回自定义类型时启动失败（`HostInfo[]` 缺少 metadata）。要用 source-gen context，再 `WithTools<T>(new JsonSerializerOptions(McpJsonUtilities.DefaultOptions){TypeInfoResolver = JsonTypeInfoResolver.Combine(ctx.Default, McpJsonUtilities.DefaultOptions.TypeInfoResolver)})`。
  - 运行时坑 2：这个 options 不能写在 context partial 类的静态初始化器里（AOT 下 `Default` 为 null，`Combine` 会悄悄丢掉它）。
  - 体积：宿主可执行文件 29MB，另加 native dylib；桥 2.1MB，启动约 0ms。
  - NU1903：`Microsoft.DotNet.ILCompiler` 10.0.12 有高危公告，正式发布前要升级 SDK/运行时补丁。
- **G11 日志噪音**：SDK 把每次取消都记成 `warn ... TaskCanceledException`（带完整堆栈），需要过滤。

## 4. 对 MySSH.Mcp 的建议
1. **宿主**：等首帧出来、总开关打开（默认关闭）后，再在 `Task.Run` 里启动。用 SlimBuilder，只监听 127.0.0.1。端口首次随机，之后持久化。Token 存 Keychain/DPAPI，可以重新生成。设置页显示 `claude mcp add --transport http myssh http://127.0.0.1:<port>/mcp --header "Authorization: Bearer <token>"`。
2. **会话模式**：保持 Stateless。“已连接客户端”在 HTTP 上改成“最近活跃的 Token/客户端”；UDS 会话可以精确列出，也能断开。
3. **工具只定义一次**：从 ToolCatalog 生成 McpServerTool（用 `WithTools(IEnumerable<McpServerTool>)` 或 Handler 适配）。按主机 aiPolicy 过滤用 `AddListToolsFilter/AddCallToolFilter`。所有返回类型都放进 source-gen JSON。
4. **审批**：`ApprovalDecision` 中 Timeout 和 Cancelled 都按拒绝处理。加上来源（Mcp/Ai）和客户端标签。多个弹窗要排队。超时 120s；客户端取消时关闭弹窗。
5. **本地套接字**：macOS 用 `~/Library/Application Support/MySSH/mcp.sock`，Linux 用 `$XDG_RUNTIME_DIR/myssh/mcp.sock`，Windows 用 `\\.\pipe\myssh-mcp-{user}`。校验对端 uid，读到 EOF 就取消会话，用残留 socket 检测实现单实例。
6. **桥**：独立的 AOT 小程序（2MB），只转发字节。TODO：socket 不存在时用 `--tray` 拉起 GUI，然后重试约 10s。
7. **AOT**：可以采用（内存从 153 降到 99MB）。从第一天起用 source-gen JSON 和 compiled bindings，CI 加 AOT 发布 + McpProbe 冒烟测试。
8. **测试**：McpProbe 的检查矩阵可以直接改成集成测试。
