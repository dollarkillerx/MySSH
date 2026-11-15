# P0 Spike 结论备忘录

> 2026-11-15 · macOS arm64 · .NET 10.0.401 · 对应 docs/design/avalonia-carbon-mcp.md §12 P0
> 四项全部完成。**结论：设计可行，进入 P1。** 下面先列选型决定，再列需要修改的设计条目、仍未验证的风险。

| Spike | 结论 | 详情 |
|---|---|---|
| ① 终端 | ✅ VT 引擎选 **XTerm.NET 3.0.3**（24/24，175 MB/s）；原生渲染加输入法原型能跑真实 vim。⚠️ 满屏重绘偏慢（12–16ms），输入法需要人工验收 | [terminal/FINDINGS.md](terminal/FINDINGS.md) |
| ② SSH | ✅ **SSH.NET 2026.0.1** 全部能实现：多跳（`ForwardedPortLocal` 端口 0）、SOCKS5、按真实地址做 TOFU、各种密钥格式、Shell/Exec/SFTP（约 300 MB/s）。⚠️ 发现一个 SFTP 符号链接的 bug，还有几处需要自己补的能力 | [ssh-chain/FINDINGS.md](ssh-chain/FINDINGS.md) |
| ③ MCP | ✅ **ModelContextProtocol 2.4.0** 在 Avalonia 进程内同时跑 HTTP 和 UDS，审批弹窗、超时、取消全部通过（37/37）；NativeAOT 也能用 | [mcp-host/FINDINGS.md](mcp-host/FINDINGS.md) |
| ④ AI | ✅ **Anthropic 13.8.0 / OpenAI 2.15.0** 能实现 §7.2 的全部要求（21/21，mock）。⚠️ Anthropic SDK 会读取环境变量，必须隔离 | [ai-providers/FINDINGS.md](ai-providers/FINDINGS.md) |

## 1. 锁定的依赖版本（写进 `Directory.Packages.props`）
Avalonia 12.1.6 · XTerm.NET 3.0.3 · SSH.NET 2026.0.1 · ModelContextProtocol(+AspNetCore) 2.4.0 · Anthropic 13.8.0 · OpenAI 2.15.0。
测试时用 Porta.Pty 2.2.4 提供本地终端。
⚠️ 正式发布前要把 .NET SDK 和运行时升到修复 NU1903 的补丁版本（问题出在 `Microsoft.DotNet.ILCompiler` 10.0.12）。

## 2. 需要回写设计文档的修改（v0.3）

**§4 终端**
- 删掉「自研解析器兜底」，改为「XTerm.NET（MIT，必要时 fork）」。
- `ITerminalEngine` 不再自己抽象一层，直接使用 XTerm.NET 的数据模型。
- 新增性能目标：160×50 满屏重绘不超过 4ms。做法是按行缓存加 GlyphRun 缓存，利用引擎的 `GetRowVersion`。
- 制表符改为程序绘制。
- 注意：XTerm.NET 写入数据后要调用 `RefreshContentVersion()`。

**§5 / §8 SSH**
- 新增 `IHopTransport` 接口：默认用 `ForwardedPortLocal`，转发要存活到整条链路结束；有第二个连接进来就关闭整条链。`SingleAcceptBridge`（反射调用内部 API）作为可选的加固方案。
- TOFU 对每一跳分别校验真实的 host:port；交互确认期间把超时调大（60s），或者先严格模式探测、确认后再重连；AI 和 MCP 一律严格模式。
- 断线检测：监听所有跳的 `ErrorOccurred`，再加应用层心跳（SSH.NET 的 KeepAlive 发现不了对端已经挂死）。
- Shell、Exec 都没有背压：读循环必须一直跑，缓冲上限由终端自己控制。Shell 的退出码只能用反射取。
- **SFTP 的删除、重命名只能用 `ISftpFile` 上的方法**。按路径调用的 API 会作用到符号链接的目标上，这是 SSH.NET 的 bug，要报给上游。
- SFTP 没有递归删除，需要自己实现；取消传输后要清理远端留下的半截文件。
- passphrase 处理规则：空字符串当作没有；遇到 `SshPassPhraseNullOrEmptyException` 就弹窗让用户输入。

**§6 MCP**
- HTTP 保持 Stateless（2.4 的默认值；有状态模式下取消请求无法到达服务端）。
- 旧协议版本的客户端在 Stateless 下拿不到 ClientInfo，客户端身份改用 Token 或客户端标签标识。
- UDS 会话读到 EOF 时要主动取消会话。
- Host 头和 Origin 自己校验，拒绝顺序为 403 → 421 → 401。
- 端口第一次随机分配，之后固定下来；Token 存放在系统密钥库。
- `myssh-mcp` 桥做成独立的 AOT 小程序（约 2MB）。

**§7 AI**
- AnthropicProvider 统一走 **beta 端点**（服务端回退只有 beta 有）。SDK 默认用 `Authorization: Bearer` 发送 Key；只认 `x-api-key` 的网关需要改写请求头。
- **环境变量隔离是硬性要求**：使用 `IsolatedAnthropicClient` 加 `EnvIsolationHandler`，应用启动时清除所有 `ANTHROPIC_*`，SSH 子进程也不能继承这些变量。
- Agent 循环只执行 `Stopped.Message.ToolCalls`，fallback 块按专门的规则回放。
- 缓存在 tools、system 之外，再加一个对话历史的自动断点。
- 思考显示增加 `updates` 选项。

**§1 / §12 构建发布**
- **从第一天起按 NativeAOT 设计**：所有 JSON 序列化都用 source-gen，绑定用 compiled bindings。原因：MCP 宿主的内存从 153MB 降到 99MB，Avalonia、ASP.NET Core、MCP SDK 都没有 trim 警告。
- CI 里加一步 AOT 发布，再跑 McpProbe 冒烟测试。

## 3. 仍未验证（P1/P2 期间补上）
| 项目 | 怎么验证 | 谁来 |
|---|---|---|
| 输入法：中文、日文的候选窗位置、上屏是否重复（macOS / Windows TSF / Linux IBus 和 Fcitx5） | `dotnet run --project spikes/terminal/TermProto`，按 terminal/FINDINGS.md 第 3 节的清单检查 | **需要你在真机上测** |
| 真实 API：Claude strict 模式接受哪些 schema 关键字、fallback 块的线格式、DeepSeek 回传 `reasoning_content` 的规则 | `dotnet run --project spikes/ai-providers/src/AiSpike.Console -- --provider …`（需要 API Key） | 需要 Key |
| Windows 命名管道、Linux `SO_PEERCRED` | 在对应平台上跑 mcp-host 的 run.sh | P1 CI |
| SOCKS5 用户名密码认证、HTTP CONNECT 代理 | 扩展 ssh-chain 的测试环境 | P2 |
| 满屏重绘优化后的帧耗时 | RenderTest 的 dense 基准 | P2 |

## 4. 下一步：P1
1. 在当前 `main` 上打 tag `tauri-final`，然后删除 Tauri 代码（先提取 locales 和迁移用的 fixture）。
2. 搭建 `MySSH.sln` 骨架和 `Directory.Packages.props`；CI 先做 macOS 上的 build、test 和 AOT 发布。
3. 把四个 spike 中可以复用的部分搬进正式工程：TerminalControl、ChainFactory/TofuStore、MCP 宿主和 LocalSocketListener、EnvIsolation 和 SSE mock，各自配上测试。
4. 实现 Carbon token 生成器和核心控件，加 Gallery 页。
5. 实现 Storage/Vault 和旧数据导入。

`spikes/` 是一次性代码，P1 搬完有用的部分后，在 P4 和 Tauri 代码一起删除，或者移到 `archive/` 分支。
