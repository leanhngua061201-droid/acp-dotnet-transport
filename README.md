# ACP .NET Transport

[![Verify](https://github.com/leanhngua061201-droid/acp-dotnet-transport/actions/workflows/verify.yml/badge.svg)](https://github.com/leanhngua061201-droid/acp-dotnet-transport/actions/workflows/verify.yml)
[![.NET 8](https://img.shields.io/badge/.NET-8-512BD4)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

[中文说明](README.zh-CN.md) · [Validation record](VALIDATION.md) · [Contributing](CONTRIBUTING.md)

An experimental .NET 8 transport component for [Agent Client Protocol](https://agentclientprotocol.com/) integrations. It exchanges newline-delimited UTF-8 JSON-RPC 2.0 messages over caller-supplied input and output streams. It uses the .NET base class library and has no third-party package dependencies.

This repository contains the transport layer, focused checks, and a runnable local example. It does not implement a complete ACP client, agent runtime, session manager, authentication, file access, terminal execution, or permission policy.

## Why use it?

- **Bring your own streams.** Connect to an ACP agent over redirected stdio or embed the transport in an existing .NET host.
- **Keep both directions moving.** A background reader correlates responses, dispatches notifications and handles agent-to-host requests without waiting for one outbound request to finish.
- **Make shutdown behavior explicit.** Serialized writes, cancellation handling, bounded late-response tracking and shared disposal completion have focused regression checks.
- **Start without extra packages.** The library targets .NET 8 and uses only the base class library. Runnable examples show local duplex traffic and an opt-in real-agent connection.
- **Inspect the evidence.** Twenty checks include 10,000 requests with up to 64 in flight and fragmented writes. A real external ACP agent has completed a text round trip; [the validation record](VALIDATION.md) identifies the exact scope.

## Build and run

Install the .NET 8 SDK, then run from the repository root:

```sh
dotnet build src/Acp.Transport.csproj -c Release --warnaserror
dotnet run --project tests/Acp.Transport.Tests.csproj -c Release
dotnet run --project examples/Loopback/Acp.Transport.Example.csproj -c Release
dotnet build examples/AgentSmoke/Acp.Transport.AgentSmoke.csproj -c Release --warnaserror
```

The checks use controlled streams. The Loopback example connects two local transports with anonymous pipes and simulates a peer; it does not contact a real agent or an API. The opt-in [AgentSmoke client](examples/AgentSmoke/README.md) launches a real external agent; normal verification only builds it. The GitHub Actions workflow has passed on Windows and Linux. The real-agent text check was run locally on Windows; see [VALIDATION.md](VALIDATION.md).

On Windows, `powershell -NoProfile -ExecutionPolicy Bypass -File ./verify.ps1` runs these steps. Pass `-DotNetPath` if the SDK executable is outside `PATH`. The test runner is a console program; use `dotnet run` as shown above rather than `dotnet test`.

An optional timed local check keeps one connection active, verifies both directions, and reports managed-heap samples:

```sh
dotnet run --project tests/Acp.Transport.Tests.csproj -c Release -- --soak-seconds 60
```

The duration is configurable from 1 to 3600 seconds. This check runs separately from the default suite and does not call a model API.

## Integration

Add a project reference to `src/Acp.Transport.csproj`. To attach to an agent process, redirect its standard input and standard output and supply `process.StandardOutput.BaseStream` as input and `process.StandardInput.BaseStream` as output. Keep diagnostic output on stderr and drain any redirected stderr separately.

```csharp
using System.Text.Json;
using Acp.Transport;

await using var transport = new AcpTransport(input, output,
    requestHandler: async (request, cancellationToken) =>
    {
        // Dispatch only methods supported by your host. This example returns
        // a neutral demonstration result; it does not access files or terminals.
        await Task.CompletedTask;
        return JsonSerializer.SerializeToElement(new { accepted = false });
    },
    notificationHandler: (method, parameters) =>
        Console.Error.WriteLine($"Notification: {method}"));

using var result = await transport.RequestAsync("initialize", new
{
    protocolVersion = 1,
    clientCapabilities = new { }
});
Console.Error.WriteLine(result.RootElement);
```

`input` and `output` above are the streams supplied by the embedding application. For a complete runnable program, see [examples/Loopback](examples/Loopback/Program.cs).

## Behavior and ownership

- One background reader correlates outbound responses, handles notifications, and dispatches inbound requests. Inbound request handlers run concurrently, up to 32 at a time; the next request faults the connection.
- Writes are serialized. Incoming and outgoing messages are byte-limited; the default is 1 MiB, configurable from 1 KiB to 16 MiB. Input uses strict UTF-8 validation.
- Request IDs generated locally are integers. Incoming requests support string and integer IDs. Fractional and null IDs, JSON-RPC batches, and Content-Length framing are not supported.
- A genuinely unknown response ID faults the connection. Responses to recently timed-out or cancelled requests are ignored using bounded ID retention. This is strict transport behavior, not a claim of general JSON-RPC conformance.
- The default request timeout is one minute. Caller cancellation stops waiting for a result after a complete outbound frame has been written; it does not send ACP `session/cancel`. Send that notification explicitly if needed.
- Cancelling a notification while it waits for the write lock leaves the connection usable. Cancellation after writing starts faults the transport because a partial frame may have reached the peer.
- `Start()` is idempotent; outbound calls start the reader automatically. `Completion` exposes the reader task, but receive errors are recorded internally and fail pending/future requests rather than being rethrown by that task.
- Callers own the underlying streams and the process. Disposing the transport leaves streams open, cancels pending work, and waits for the reader, active sends, and inbound handlers to finish. Concurrent disposers share that completion. Input/output operations and inbound handlers must honor cancellation for shutdown to complete; there is no forced shutdown timeout. Do not await transport disposal inside one of its callbacks or handlers, which would wait on itself. A blocking notification callback blocks the reader, so keep it short and dispatch lengthy work separately.
- A cancellation callback that throws does not bypass cleanup. Its error is reported by `DisposeAsync` after operations drain and internal synchronization resources are released.
- Returned `JsonDocument` instances are owned by the caller and should be disposed. Notification/request JSON values are cloned before dispatch.
- Missing or failing inbound handlers receive a generic error response. Exception messages from a handler are sent to its peer, so translate application errors before throwing them.

## Status and limits

Version 0.1.0 is an experimental source release with no API stability promise. Twenty checks cover correlation, cancellation notification shape, notifications/reverse requests, nonblocking reverse-request dispatch, unknown response IDs, request timeout, message size/UTF-8, peer closure, blocked-send timeout and caller cancellation, partial-write cancellation, cancellation before writing, bounded late-response bookkeeping, shutdown races, throwing cancellation callbacks, and concurrent traffic. The default suite includes 10,000 requests with fragmented writes and 2,056 late replies after cancellation or timeout. These are focused checks, not exhaustive coverage of races or backpressure.

On 2026-10-15, the standalone client completed `initialize` (protocol version 1), `session/new`, and `session/prompt` against `@agentclientprotocol/claude-agent-acp` 0.90.0 with Claude CLI 2.1.210. The agent used `glm-5.3-flash` through an Anthropic-compatible backend; the prompt returned the exact expected sentinel with `end_turn`, five session updates, and no host operations in 13.78 seconds. This validates one real-agent text round trip, not an Anthropic Claude model or every ACP capability. Real-agent tool requests, cancellation, and session resume remain unverified. See [VALIDATION.md](VALIDATION.md) for the precise test scope and limitations.

A complete host must implement ACP initialization/capability negotiation and session methods, authentication, cancellation, and any advertised reverse-request capabilities. Transport-level support for arbitrary method names does not implement those methods.

See [README.zh-CN.md](README.zh-CN.md) for the Chinese overview. License: [MIT](LICENSE).
