# Spike ② SSH 层结论（SSH.NET 2026.0.1）

> 子代理产出，主会话复核：`.work/full_run.txt` 中 66 处 PASS，PARTIAL/FAIL 与下表一致；sshd/ssh 进程已全部清理。注意：日志里 Q1e 的标题写的是「会话不受影响」，那是测试代码里写的预期，没有更新；实测证据（`Client not connected`）表明会话**会被断开**，以下文为准。

对应设计文档 §5（known_hosts/TOFU）和 §8（SSH 层）的 P0 风险验证。一次性代码。
- 环境：macOS arm64，.NET SDK 10.0.401，SSH.NET 2026.0.1（依赖 BouncyCastle.Cryptography 2.7.0、M.E.Logging.Abstractions 8.0.3），OpenSSH 10.3p1
- 运行：`./env.sh start && dotnet run && ./env.sh stop`（`dotnet run -- Q7` 只跑一组，`-- Q7g` 只跑一项）。完整输出在 `.work/full_run.txt`。吞吐数字都来自 loopback，只能横向比较。

## 0. 测试环境（env.sh）
非 root 的 sshd 第一次就成功，没有走退路。bastion 127.0.0.1:2222，target :2223，hop2 :2224，SOCKS5 :1080（`ssh -N -D` 经 bastion）。启动命令 `sshd -D -e -f cfg`；配置：ListenAddress 127.0.0.1，HostKey/PidFile/AuthorizedKeysFile 都在 .work，UsePAM no，StrictModes no，PasswordAuthentication no，LogLevel DEBUG1（用日志里的 `server_request_direct_tcpip` 证明确实经过了这一跳）。

坑：
- OpenSSH ≥9.8 的 `PerSourcePenalties` 会把「连上但没认证就断开」（比如 host key 不匹配的测试）的 127.0.0.1 罚时，所以测试环境必须设 `PerSourcePenalties no`（同时调大 MaxStartups）。生产环境里，用户反复拒绝 TOFU 或做探测连接，也可能触发服务器端罚时。
- `BSM audit: setaudit_addr failed` 无害。
- `ssh -J` 的子进程不继承 -o 选项，命令行冒烟测试改用 ProxyCommand `-W`。

密钥：id_ed25519_pass（OpenSSH 格式 + passphrase）、id_ed25519、id_rsa（3072）、id_rsa_pass、id_rsa_pem_pass（老 PEM 格式，加密）；passphrase 是 spike-pass。

## 1. 总表
| # | 结果 |
|---|---|
| Q1 跳板链 ForwardedPortLocal 端口 0 | PASS（单跳、两跳、并发）+ 2 个坑 |
| Q2 TOFU | PASS（交互式确认要注意 Timeout） |
| Q3 SOCKS5 / 代理+跳板 | PASS |
| Q4 认证 | PASS |
| Q5 ShellStream | PASS（退出码要靠反射）；没有背压，PARTIAL |
| Q6 Exec | PASS；没有背压，PARTIAL |
| Q7 SFTP | PASS；**Q7g FAIL**：按路径删除/重命名符号链接时作用在目标上（SSH.NET bug） |
| Q8 Keepalive | PARTIAL：发现不了对端已死 |

## 2. 逐题
### Q1
- `new ForwardedPortLocal("127.0.0.1", 0, host, (uint)port)` → `jump.AddForwardedPort(fwd)` → `fwd.Start()`，之后 `BoundPort` 是真实端口（例如 60036）。必须先 Connect 跳板，再 Add/Start。证据：bastion 日志 `server_request_direct_tcpip: originator 127.0.0.1 port 60037, target 127.0.0.1 port 2223`；target 上 `SSH_CONNECTION=127.0.0.1 60038 127.0.0.1 2223`。
- 两跳（Q1b）：bastion 日志转到 2224，hop2 日志转到 2223；每跳 25–55ms。
- 一个跳板挂两个转发，10 条命令并发（Q1c），正确。
- 8 条两跳链并行（Q1g）用 3.5–3.8s，16 个端口互不相同（串行约 120ms/链）。
- 跳板断开（Q1f）：target 在 1–13ms 内触发 `BaseClient.ErrorOccurred`（SshConnectionException），IsConnected=false；但 ShellStream.Closed 不触发，所以断线横幅必须监听 ErrorOccurred。
- 坑：
  1. 转发端口对本机所有进程开放（Q1d：外部 TcpClient 拿到了 target 的 banner）。
  2. 已经建立 target 会话之后调用 `fwd.Stop()`，会话会被断掉（Q1e：`Client not connected`），所以不能连上后立即关闭监听。
  3. 没有接受连接时的过滤；`RequestReceived` 只能事后通知。
- SSH.NET 2026 的替代方案：没有公开的「在 Stream/Channel 上建立连接」、ProxyCommand 或内置 jump。IServiceFactory/ISocketFactory/IConnector 都是 internal，而且 IConnector 返回的是 Socket。ProxyTypes 只有 None/Socks4/Socks5/Http。internal 里有 `ISession.CreateChannelDirectTcpip()` 和 `IChannelDirectTcpip.Open(host,port,IForwardedPort,Socket)/Bind()`。
- 退路原型 Q1h PASS：`SingleAcceptBridge` 自己开 TcpListener(127.0.0.1:0)，只 accept 一次就 Stop 监听，再用反射走 internal direct-tcpip 通道。target 54ms 连上，之后外部连接被拒（ConnectionRefused），20MB 流 119–305MB/s，断开后泵任务正常结束。缺点：依赖 internal API；监听和 accept 之间约有 1ms 窗口。

### Q2
- 每一跳各自挂一个 `HostKeyReceived`，闭包里带这一跳的真实 host:port，不用 ConnectionInfo.Host/Port（经过跳板时那是 127.0.0.1:转发端口）。
- Q2a：存储里只有 `[127.0.0.1]:2222` 和 `[127.0.0.1]:2223`，没有转发端口 60137。
- 指纹 `"SHA256:"+Base64(SHA256(e.HostKey)).TrimEnd('=')`，和 `ssh-keygen -lf` 一致；`e.FingerPrintSHA256` 是同一个值但没有 `SHA256:` 前缀。我们写出的 known_hosts 文件能被 `ssh-keygen -F` 读取。
- Q2b：Strict 模式重连，两跳都是 Match。
- Q2c：Strict + 未知主机 → `SshConnectionException: Host key could not be verified.`（DisconnectReason=KeyExchangeFailed）。
- Q2d：用 regen-target 换掉 target 的 host key 后，bastion Match、target RejectedMismatch，连接被拒，并确认没有执行命令。
- Q2e/Q2f：handler 是同步的，运行在 SSH.NET 的 LongRunning 线程上（不是 UI 线程）。如果阻塞时间超过 ConnectionInfo.Timeout，会抛 SshOperationTimeoutException（2s 超时 + 4s 弹窗 → 失败）。Timeout 可以修改：连接时设成 30s，连上后改回 5s，验证通过。上限还有服务器的 LoginGraceTime（120s）。
- 可以用 `ConnectionInfo.HostKeyAlgorithms.SetPosition(type,0)` 把已保存的 key 类型排到前面。

### Q3
- `new ConnectionInfo(h,p,u,ProxyTypes.Socks5,proxyHost,proxyPort,null,null,auth)`，代理用户名和密码传 null 就是不认证。
- Q3a：SOCKS→target，bastion 日志显示转到 2223。
- Q3b：proxy→hop2→target，代理只用在第一跳（Jumps[0] 是 Socks5，Target 是 None）；bastion 日志转到 2224，hop2 日志转到 2223；TOFU 用的仍是真实地址。
- Q3c：代理端口不通，立即抛 `SocketException: Connection refused`（不是 ProxyException），工厂要自己记录是哪一跳出错。
- 未测：SOCKS 用户名密码认证、HTTP CONNECT 代理。

### Q4（`new PrivateKeyFile(new MemoryStream(bytes), pass)` + `PrivateKeyAuthenticationMethod`）
- ed25519+passphrase：PASS。
- passphrase 错误：加载密钥时就抛 `SshException: The random check bytes ... do not match`，没有专门的异常类型。
- 加密的密钥 + 空 passphrase（规范化成 null）：`SshPassPhraseNullOrEmptyException`，可以作为「需要弹窗输入」的信号。
- 不加密的密钥传 "" 或 null 都可以，所以规则是 `IsNullOrEmpty ? null : p`。
- RSA3072（有/无 passphrase）：PASS，服务器端 pkalg 是 rsa-sha2-512。
- RSA 老 PEM 格式（加密）：PASS。

### Q5
- `CreateShellStream("xterm-256color",80,24,0,0,buf)`：TERM 和 `stty 24 80` 正确。
- `ShellStream.ChangeWindowSize(cols,rows,w,h)` 改成 132×43 后，stty 是 43 132。UTF-8 中文正常。
- `exit 3` 之后约 2ms 触发 Closed，ReadAsync 返回 0；会话还在，只关闭了通道。
- 退出码没有公开 API：用反射拿 `BaseClient.Session`（internal）→ 转成公开的 `Renci.SshNet.Session`，订阅 `.ChannelRequestReceived`，按 `ShellStream._channel.LocalChannelNumber` 过滤 `exit-status`，RequestData 后 4 字节（大端序）就是退出码，拿到 3。
- 泄漏（Q5b）：50 个循环里 fds 88→88，threads 32→21；sshd 没有残留子进程；最后 TCP socket 数为 0。
- 没有背压（Q5c）：100MB 输出 3s 不读，堆增加 +129MB，终端必须一直读，并自己限制缓冲。
- ShellStream 没有重写 ReadAsync，走的是基类的线程池阻塞读。

### Q6
- exit 7、stdout 和 stderr 分开：正确。
- `CommandTimeout=2s` + `ExecuteAsync()` → 2004ms 抛 SshOperationTimeoutException，ExitSignal=TERM，远端进程已杀。
- `ExecuteAsync(ct)` 1.5s 取消 → TaskCanceledException，ExitSignal=TERM，远端进程已杀。手动取消用 `CancelAsync(forceKill, ms)`。
- 64KB 上限：边 ExecuteAsync 边读 `OutputStream`（PipeStream.ReadAsync），8ms 读满 65536 字节，取消后 3ms 结束，内存分配 0.36–1MB，`yes` 已被杀。
- 没有背压（Q6e）：100MB 3s 不读，Length=100000000，堆 +192MB。

### Q7（200MB，SHA 一致）
- 直连 `UploadFileAsync(stream,path,IProgress<UploadFileProgressReport>,ct)` 340MB/s，回调 6406 次；`DownloadFileAsync` 313MB/s。
- 经跳板：236/278MB/s。
- `OpenAsync` + `CopyToAsync(256K)`：写 135、读 309MB/s。
- BufferSize 64K：307MB/s（默认 32K，差别不明显）。
- 用自己写的同步 IProgress（`System.Progress` 会 post 到线程池，可能在完成后才到）。
- 取消（Q7d）：124ms 抛 TaskCanceledException，远端留下 28MB 半截文件（要自己删），客户端还能继续用。
- 递归删除（Q7e）：没有内置（DeleteDirectory 非空目录 → SftpException Failure）。用 `ListDirectoryAsync`（IAsyncEnumerable，跳过 `.` 和 `..`，属性是 lstat 的）递归，文件和链接用 `ISftpFile.DeleteAsync`，最后 DeleteDirectoryAsync。25 个条目 19ms，symlink 没有被跟随。
- ⚠ Q7g：按路径的 API 会先做 realpath，所以：
  - `DeleteFile`/`DeleteFileAsync`/`DeleteAsync(link)` 删的是目标文件；
  - `RenameFileAsync(link)` 重命名的是目标文件；
  - `DeleteDirectoryAsync(linkToDir)` 删的是目标目录；
  - 悬空链接删不掉。
  - 安全做法：`ISftpFile.DeleteAsync/MoveTo`，或 exec `rm -f --`。
  - `SymbolicLink(path=目标, linkPath=链接)` 的顺序是对的。
- 异步 API：ChangeDirectory/Connect/CreateDirectory/Delete/DeleteDirectory/DeleteFile/DownloadFile/Exists/Get/GetAttributes/GetStatus/ListDirectory/Open/RenameFile/UploadFile。只有同步版本的：SymbolicLink、ChangePermissions、SetAttributes、SynchronizeDirectories、Read/WriteAll*、SetLastWriteTime。

### Q8
- `BaseClient.KeepAliveInterval`（默认 -1ms，表示关闭），手动发送用 `SendKeepAlive()`；日志用 `ConnectionInfo.LoggerFactory` 或 `SshNetLoggingConfiguration.InitializeLogging`。
- 实测发送的是 SSH_MSG_IGNORE（1s 间隔，3.5s 里 3 次），不需要回复。
- 把服务器进程树 SIGSTOP 后，6s 内没有任何 ErrorOccurred；只有执行真实操作（exec）才会在 ConnectionInfo.Timeout（3s）后抛 SshOperationTimeoutException。没有公开 API 可以发 want_reply 的 keepalive@openssh.com。

## 3. 生产 IConnectionFactory 建议
`ConnectAsync(hostId, origin{User|Ai|Mcp}, ct) -> IConnectionLease`：
- 链路 = [proxy] + jumpChain + target。第 0 跳用 proxy；后面每一跳用 `IHopTransport.Open(prevClient, realHost, realPort)` 拿到本地端点。
- 每一跳 `tofu.Prefer`，Handler 绑定真实地址；交互式连接时 Timeout 设 60s，连上后改回 10s。
- lease 持有整条链，按 hostId 引用计数，反向释放。

1. 默认用 ForwardedPortLocal(127.0.0.1,0)（功能上足够，**不需要回退到自己写 direct-tcpip**）。转发要活到链结束；订阅 RequestReceived，第一个连接之外再有连接进来，就关闭整条链（fail-closed）。SingleAcceptBridge 作为可选的加固/备用方案：锁定 SSH.NET 版本，加启动自检（反射失败就降级），加测试守护；同时向上游提议加 accept 过滤或公开 Stream 连接的 API。
2. TOFU：用真实 host:port（端口 22 时省略），按 key 类型保存，指纹用 OpenSSH 格式；AI/MCP 一律 Strict。用户连接推荐两阶段（Strict 探测 → 弹窗 → 写入存储 → 重连）；或者在连接期间调大 Timeout。
3. 断线：订阅所有跳的 ErrorOccurred；KeepAlive 设 15–30s，只用来保持 NAT；另外做应用层心跳（空闲时执行带超时的 `GetAttributesAsync(".")` 或 exec `true`），超时就显示重连横幅。
4. Shell：Exited = Closed + ErrorOccurred；退出码用反射拿，失败就当 null；读循环一直跑，缓冲上限由终端控制。
5. Exec：`ExecuteAsync(linkedCt)` + CommandTimeout；边读边计数，到上限就取消。
6. SFTP：全部用 *Async 加自己的 IProgress（UI 节流）；取消后清理半截文件；**删除和重命名只用 ISftpFile 条目的方法**；同步 API 包在 Task.Run 里；Q7g 报给上游。
7. passphrase：`IsNullOrEmpty → null`；`SshPassPhraseNullOrEmptyException` → 弹窗；加载密钥时抛 SshException → 「passphrase 错误或密钥损坏」。

## 4. 文件
env.sh（start/stop/status/regen-target）、SshChainSpike.csproj、Program.cs（Q1a–Q8a）、Infra.cs（TofuStore、ChainFactory、SingleAcceptBridge、Internals、CaptureLoggerFactory、Results）、.work/（gitignored：密钥、配置、日志、full_run.txt、apidump/api.txt）。
