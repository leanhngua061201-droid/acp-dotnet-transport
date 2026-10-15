# ACP .NET Transport

[![Verify](https://github.com/leanhngua061201-droid/acp-dotnet-transport/actions/workflows/verify.yml/badge.svg)](https://github.com/leanhngua061201-droid/acp-dotnet-transport/actions/workflows/verify.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

[English](README.md) · [验收记录](VALIDATION.md) · [参与贡献](CONTRIBUTING.md)

这是一个面向 ACP 集成的实验性 .NET 8 双工传输组件：输入、输出各一个 `Stream`，使用 UTF-8 换行分帧的 JSON-RPC 2.0，零第三方依赖。

让 .NET 宿主接入 ACP Agent 时，能够同时处理主动请求、推送通知和 Agent 发回的请求。你负责定义宿主能力，它负责传递消息、关联响应，以及处理超时、取消和连接释放。

## 它提供什么

- 后台读取、请求与响应关联、通知分发、Agent 反向请求。
- 并发反向请求、串行写入、请求超时与取消等待。
- 消息大小限制、严格 UTF-8 校验、断开后结束待处理请求。
- 独立测试入口和可以直接运行的本地双工示例。

它的定位是传输库。完整的 ACP SDK、Agent 宿主、模型调用、认证、会话恢复、文件与终端操作、权限策略，均由上层应用实现。

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

适合作为 0.1.0 实验性组件公开：有独立构建、可运行示例、测试、MIT 许可和 CI 配置。现有 20 项检查包含发送阻塞、取消语义、关闭竞态和取消回调异常清理等回归场景；并行压力测试核验 10,000 次请求的关联与分帧，以及 2,056 个迟到响应的清理。另已实跑 60 秒持续双向通信，结束时待处理请求与反向请求均为零。公开 API 暂不承诺稳定。

2026-10-15 使用独立组件接入 `claude-agent-acp 0.90.0` 和 Claude CLI 2.1.210，已跑通协议版本 1 的握手、创建会话及真实模型回复。Agent 通过 Anthropic 兼容接口调用 `glm-5.3-flash`，约 13.78 秒内收到精确的验收文本 `ACP_TRANSPORT_OK`，以 `end_turn` 正常结束，期间收到 5 条会话更新，没有宿主操作。这证明了一次真实 Agent 文本往返；本轮使用的是 GLM 模型，尚未验证 Anthropic Claude 模型、真实工具反向请求、取消或会话恢复。精确范围见 [VALIDATION.md](VALIDATION.md)。

通知写入开始后发生取消，连接会终止，避免半条消息和后续消息拼接；仅在等待写锁时取消则保留连接。迟到响应的 ID 集合与顺序队列均限制为最多 1024 项。

释放连接会取消并等待读循环、发送操作和反向处理器结束；多处同时释放会等待同一次清理。底层输入、输出和处理器必须响应取消，否则释放会持续等待；回调内部也应避免等待同一连接的释放。释放后调用方的流保持打开。取消回调抛异常时，先完成清理，再由 `DisposeAsync` 报错。

本机验证使用 Windows。CI 同时配置了 Windows 与 Linux，远端尚未运行。详见英文 README 的流所有权、回调、错误处理与协议范围说明。
