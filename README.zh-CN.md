# ACP .NET Transport

[![Verify](https://github.com/leanhngua061201-droid/acp-dotnet-transport/actions/workflows/verify.yml/badge.svg)](https://github.com/leanhngua061201-droid/acp-dotnet-transport/actions/workflows/verify.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

[English](README.md) · [验收记录](VALIDATION.md) · [参与贡献](CONTRIBUTING.md)

这是一个面向 ACP 集成的实验性 .NET 8 双工传输组件：输入、输出各一个 `Stream`，使用 UTF-8 换行分帧的 JSON-RPC 2.0，零第三方依赖。

## 许可证

本项目使用 **MIT License**。你可以自由使用、复制、修改、合并、发布、分发、再许可
或出售副本，前提是在副本或实质性部分中保留版权与许可声明。完整条款见
[`LICENSE`](LICENSE)。

让 .NET 宿主接入 ACP Agent 时，能够同时处理主动请求、推送通知和 Agent 发回的请求。你负责定义宿主能力，它负责传递消息、关联响应，以及处理超时、取消和连接释放。

## 它提供什么

- 后台读取、请求与响应关联、通知分发、Agent 反向请求。
- 并发反向请求、串行写入、请求超时与取消等待。
- 消息大小限制、严格 UTF-8 校验、断开后结束待处理请求。
- 独立测试入口和可以直接运行的本地双工示例。

它的定位是传输库。完整的 ACP SDK、Agent 宿主、模型调用、认证、会话恢复、文件与终端操作、权限策略，均由上层应用实现。

## 和其他 .NET ACP 库的对比

.NET 生态已有更完整的 ACP SDK，例如 [dotacp](https://github.com/timxx/dotacp)
（协议 + 客户端 + Agent）、[AcpSdk](https://www.nuget.org/packages/AcpSdk)
（基于 StreamJsonRpc）和 [nuskey8/acp-csharp](https://github.com/nuskey8/acp-csharp)。
需要开箱即用的完整客户端或 Agent 实现时，优先考虑它们。

本仓库刻意只做更低的一层：

- **零第三方依赖。** 只链接 .NET 8 基础类库，不会给项目引入任何额外依赖。
- **只做传输层。** 负责请求关联、通知与反向请求分发、取消与关闭语义；不实现 ACP 会话方法、认证或宿主策略。
- **行为有实测依据。** 列出的每个行为——写入中途取消、关闭竞态、迟到响应簿记、10,000 次请求的分片并发关联——都有对应的回归检查，真实 Agent 往返记录见[验收记录](VALIDATION.md)。

需要完整 SDK 就用完整 SDK；需要一个可审计的小管道把宿主接到任意 ACP Agent，这个仓库就是那根管道。

## 怎么运行

需要 .NET 8 SDK。在本目录打开终端：

```powershell
dotnet build src/Acp.Transport.csproj -c Release --warnaserror
dotnet run --project tests/Acp.Transport.Tests.csproj -c Release
dotnet run --project examples/Loopback/Acp.Transport.Example.csproj -c Release
dotnet build examples/AgentSmoke/Acp.Transport.AgentSmoke.csproj -c Release --warnaserror
```

Loopback 示例用两个本地匿名管道连接传输实例，演示请求、通知和反向请求，不需要 API 密钥。[AgentSmoke 示例](examples/AgentSmoke/README.md) 用于手动连接真实 Agent；常规验证和 CI 只编译它，不会自动调用模型。

持续运行检查独立执行，时长可设为 1–3600 秒，不调用模型 API：

```powershell
dotnet run --project tests/Acp.Transport.Tests.csproj -c Release -- --soak-seconds 60
```

## 成熟度

适合作为 0.2.0 实验性组件公开：有独立构建、可运行示例、测试、MIT 许可和 CI 配置。现有 20 项检查包含发送阻塞、取消语义、关闭竞态和取消回调异常清理等回归场景；并行压力测试核验 10,000 次请求的关联与分帧，以及 2,056 个迟到响应的清理。另已实跑 60 秒持续双向通信，结束时待处理请求与反向请求均为零。公开 API 暂不承诺稳定。

2026-10-15 使用独立组件接入 `claude-agent-acp 0.90.0` 和 Claude CLI 2.1.210，已跑通协议版本 1 的握手、创建会话及真实模型回复。Agent 通过 Anthropic 兼容接口调用 `glm-5.3-flash`，约 13.78 秒内收到精确的验收文本 `ACP_TRANSPORT_OK`，以 `end_turn` 正常结束，期间收到 5 条会话更新，没有宿主操作。这证明了一次真实 Agent 文本往返；本轮使用的是 GLM 模型，尚未验证 Anthropic Claude 模型、真实工具反向请求、取消或会话恢复。精确范围见 [VALIDATION.md](VALIDATION.md)。

通知写入开始后发生取消，连接会终止，避免半条消息和后续消息拼接；仅在等待写锁时取消则保留连接。迟到响应的 ID 集合与顺序队列均限制为最多 1024 项。

释放连接会取消并等待读循环、发送操作和反向处理器结束；多处同时释放会等待同一次清理。底层输入、输出和处理器必须响应取消，否则释放会持续等待；回调内部也应避免等待同一连接的释放。释放后调用方的流保持打开。取消回调抛异常时，先完成清理，再由 `DisposeAsync` 报错。

GitHub Actions 已在 Windows 与 Linux 上通过构建、20 项检查和本地双工示例；真实 Agent 文本验收在本机 Windows 上完成。详见验收记录，以及英文 README 的流所有权、回调、错误处理与协议范围说明。
