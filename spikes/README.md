# P0 Spikes

一次性验证代码，用于回答 docs/design/avalonia-carbon-mcp.md §12 P0 的问题。不进入正式解决方案。

| 目录 | 问题 |
|---|---|
| terminal/ | VT 引擎选型 + 原生渲染/输入法原型 |
| ssh-chain/ | SSH.NET 多跳、代理、TOFU、Shell/Exec/SFTP |
| mcp-host/ | MCP SDK 在 Avalonia 进程内跑 HTTP + UDS，stdio 桥 |
| ai-providers/ | Anthropic / OpenAI 兼容提供商的流式工具调用 |

结论汇总见 FINDINGS.md。
