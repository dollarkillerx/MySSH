# Spike ① 终端：VT 引擎选型与原生渲染原型

> 2026-11-15 · macOS arm64 · .NET 10.0.401 · Avalonia 12.1.6

## 结论

- **VT 引擎选 XTerm.NET 3.0.3**（MIT 许可，xterm.js 的 .NET 移植）。它在 24 项一致性用例里全部通过，吞吐量最高（约 175 MB/s），内存分配也最少。项目活跃：仓库 2025-12 创建，最近一次提交在 2026-11-09，有 2 名贡献者。
- 引擎只做解析；**渲染、键盘、输入法、选择都由我们自己的 `TerminalControl` 实现**。原型已经能驱动真实的 vim：备用屏、中日文显示、缩放后重绘、输入中文后保存退出都正常。
- **原型暴露了两个问题，留到正式版解决**：
  - 满屏重绘偏慢：178×56 满屏、随机 256 色时，`Render()` 要 12–16ms。需要做按行缓存和 GlyphRun 缓存，见下文「性能」。
  - 制表符画线没连上：表格竖线行与行之间有缝隙。需要像 xterm.js 的 customGlyphs 那样用程序绘制制表符。
- **输入法只能人工验收**：原型已经接入 Avalonia 的 `TextInputMethodClient`，无头测试证明预编辑的渲染路径没问题；但真实的候选窗位置和上屏流程，必须在三个平台上手工验证（清单见文末）。

## 1. 引擎对比（`EngineEval/`，`dotnet run -c Release`）

| 候选 | 许可 | 一致性 | 吞吐量（63MB 彩色中英混排日志） | 分配 | 备注 |
|---|---|---|---|---|---|
| **XTerm.NET 3.0.3** | MIT | **24/24** | **175 MB/s** | 15 MB | xterm.js 移植；自带键盘、鼠标、kitty 键盘协议编码，选择、搜索、行版本号（脏行追踪）、回绕重排、sixel/kitty 图片 |
| Terminal.Emulation 0.4.5 | MIT | 23/24（不支持 ZWJ 组合 emoji） | 63 MB/s | **1334 MB** | API 最干净；但源码仓库 `b-y-t-e/Terminal.Avalonia` 已返回 404，长期维护风险高 |
| VirtualTerminal 1.11.0 | Apache-2.0 | 15/24 | 145 MB/s | 15 MB | 每格只存一个 `Rune`，存不下字素簇和组合字符；宽字符没有尾格标记；不支持 OSC 8。部分失败可能是适配器读取颜色的方式不对，但上面这几点是结构性缺陷 |
| AgentSpace.VtDotNet 0.1.7 | MIT | — | — | — | 没有公开类型（只给作者自己的 Avalonia 控件用），只能 fork 后使用，排除 |

用例覆盖：自动换行、制表位、中日文宽字符（含在最后一列折行）、半角/全角假名、组合字符、ZWJ emoji、UTF-8 跨批次拆分、DECSTBM 滚动区、1049 备用屏、ICH/DCH/ECH/IL/DL/ED/EL、DECSC/DECRC、256 色和真彩色、OSC 0 标题、OSC 8 链接、2004/1/1000/1006 模式、DA1/DSR 应答、回滚区、缩放后回绕重排。完整结果在 `.work/engine-eval.md`。

注意：XTerm.NET 的回绕重排**遵循 xterm.js 的语义**，光标所在的那一行不参与重排（shell 会在收到 SIGWINCH 后重绘提示符）。用例已按这个语义编写。

## 2. 原型（`TermControl/`、`TermProto/`、`RenderTest/`）

**结构**：SSH/PTY 读线程调用 `EnqueueOutput` 把数据放进队列；UI 线程每 8ms 从队列取数据交给引擎解析（每帧最多 4MB）；只有 `RefreshContentVersion()` 变化时才 `InvalidateVisual`。

> 坑：XTerm.NET 的 `ContentVersion` 是缓存值，写入数据后必须调用 `RefreshContentVersion()`，否则不会重绘。

**渲染策略**
- 背景：同色的相邻格子合并成一个矩形绘制。
- 文字：可打印 ASCII、宽度为 1 且样式相同的格子合成一段绘制（等宽字体的字宽正好等于格宽）。
- **其他字符逐格绘制**：在 1 格或 2 格宽的框内居中并裁剪，所以回退字体（中日文、emoji）永远不会把网格挤歪。
- 粗体（对 0–7 色自动用亮色）、斜体、下划线、删除线、反显、暗淡都已支持。

**输入**
- 特殊键交给 `Engine.GenerateKeyInput`（会遵守应用光标模式）。
- Ctrl/Alt 组合键交给 `GenerateCharInput`。macOS 默认把 Option 当作 Meta。
- 普通文字和输入法上屏的文字统一走 `OnTextInput`。
- 正在组字时（`Key.ImeProcessed`，或者预编辑文本不为空）不处理按键。

**输入法**：`TerminalImeClient : TextInputMethodClient`
- `CursorRectangle` 返回当前光标所在格的矩形，光标移动时通知平台，让候选窗跟随。
- 预编辑文本在光标处内联绘制：Gray 80 底色加 Blue 50 下划线。

**鼠标、剪贴板**
- 拖动选择、双击选词、三击选行。
- 应用开启鼠标上报时，按下、抬起、滚轮都转成上报序列（Shift 可以强制本地选择）。
- 滚轮：备用屏下发送方向键，主屏下滚动回滚区。
- macOS 用 Cmd+C/V，其他平台用 Ctrl+Shift+C/V。粘贴走 `Engine.Paste`，会自动加上括号粘贴标记。

**给 AI 的接口**：`GetVisibleText(maxLines)`、`GetSelectionText()`。

### 自动化验证（`RenderTest/`，无头 Skia 渲染，截图在 `.work/render/`）

| 检查项 | 结果 |
|---|---|
| 混合内容渲染（彩色提示符、中日韩、全角、emoji、ZWJ、组合字符、256 色和真彩色色条、各种样式、vim 状态栏） | ✅ 截图 `01`，对齐正确 |
| 输入法预编辑内联绘制（拼音 `ni hao`、假名 `にほんご`） | ✅ 截图 `02`、`03` |
| 备用屏内容 | ✅ 截图 `04` |
| **真实 PTY 运行 vim**（`-u NONE`），引擎的 DA/DSR 应答写回 PTY | ✅ 进入备用屏，中文注释行正确 |
| 运行中把窗口缩到 83×17 → SIGWINCH → vim 重绘 | ✅ |
| 向 vim 输入 `A 你好`、`:wq`，然后退出 | ✅ 文件内容为 `server { 你好`，退出后回到主屏 |
| 178×56 满屏、随机 256 色，120 帧 | ⚠️ `Render()` 12–16ms；渲染加截图 p50 约 25ms，p95 约 35ms |

### 性能：正式版要做的优化
无头测试用的是最坏情况：每个词都换一种颜色，而且每帧都满屏重绘。日常使用（shell 交互、日志滚动）的绘制量远小于这个。但为了稳定 60fps，P2 需要做到：
1. **按行缓存**：每一行做成一个子 Visual，或者缓存该行的 `GlyphRun` 列表，只重建行版本号变化的行（XTerm.NET 提供了 `GetRowVersion`/`CopyChangedRows`）。
2. 用 `GlyphRun` 加字形缓存代替 `FormattedText`，省掉每次的排版开销。
3. 滚动时整体位移已有的行缓存，只绘制新露出的行。
4. 数据洪峰时降帧（比如 30fps）合并绘制；解析可以移到后台线程，配合快照交给 UI 线程。

## 3. 需要你在真机上验收（输入法、交互）

```bash
dotnet run --project spikes/terminal/TermProto
```
程序会启动本地登录 shell，底部状态栏显示网格大小、渲染耗时、当前模式。请逐项检查：

- [ ] 中文输入法（拼音）：组字时文字显示在光标处，**候选窗贴着光标**；上屏后文字正确，而且不会重复输入
- [ ] 日文输入法：假名组字、变换、确定都正常；文节切换时显示正常
- [ ] vim、htop、`tmux` 分屏，鼠标点击和滚轮都正常
- [ ] `less` 查看中文日志，翻页时没有残影
- [ ] Ctrl+C、Ctrl+R、Option+B/F（Meta）、方向键、F1–F12
- [ ] 拖动选择、双击选词、Cmd+C/V；粘贴多行文本时被正确包裹为括号粘贴
- [ ] 快速连续调整窗口大小时，提示符正确重排
- [ ] `cat` 一个几 MB 的大日志：界面不卡死，Ctrl+C 能立即中断

Windows（TSF）和 Linux（IBus/Fcitx5）需要在对应平台上重复这份清单。原型没有任何平台特定代码，应该可以直接 `dotnet run`。

## 4. 对设计文档的修正
- §4.2 的「兜底方案：自研解析器」可以降级为「备用」：XTerm.NET 已经满足要求，而且是 MIT 许可，可以随时 fork。
- §4.1 的 `ITerminalEngine` 接口改为直接面向 XTerm.NET 的数据模型（`BufferLine`/`BufferCell`/`AttributeData`），省掉一层复制。替换引擎的成本留到真要替换时再付。
- §4.3 的性能目标改为两条：「解析吞吐不低于 100 MB/s（已达成）」和「160×50 满屏重绘不超过 4ms（P2 待做）」。
