# Validation record

Local date: 2026-10-15. Platform: Windows x64, .NET 8 SDK.

## Controlled-stream checks

The default console suite passes **20/20** checks. It includes:

- Request correlation, Unicode JSON values, notifications and reverse requests.
- Unknown response IDs, strict UTF-8, frame size limits, peer closure and timeout.
- Cancellation before and during a write, blocked-write timeout, caller cancellation.
- Bounded retention of late-response IDs and their FIFO bookkeeping.
- Disposal during a blocked write with 32 queued senders; concurrent disposal;
  handler cancellation; throwing cancellation callbacks; peer closure while writing.
- 10,000 requests with up to 64 in flight, 628 notifications, 628 reverse
  requests, and 59,393 deliberately fragmented writes. Replies are reordered.
- 2,048 cancelled requests, 8 timed-out requests, and 2,056 drained late replies.
  Pending requests and retained late-response IDs finish at zero; the FIFO stays
  within its 1024-entry limit.

The opt-in **60-second** sustained check completes 3,952 requests, 3,952
notifications and 3,952 reverse requests. It validates reverse responses and
Unicode payloads. Pending and inbound request counts finish at zero. Live
managed-heap samples, with a forced collection approximately every ten seconds,
are 494224, 512344, 520264, 520264, 520264 and 520296 bytes. These samples describe
one run; they do not prove the absence of leaks or establish hours-long endurance.

## Real agent

External agent: `@agentclientprotocol/claude-agent-acp` **0.90.0**.
Claude CLI: **2.1.210**. Node: **24.15.0**.

The standalone AgentSmoke client launches the installed agent over redirected
stdio, in a new empty temporary directory. The session disables settings-file
sources, built-in tools, explicit plugins and session persistence using that
agent's metadata extension. Authentication is passed through the environment
for the test process; no credentials, configuration files, raw protocol
transcripts or agent stderr are included in this repository.

The successful run selects `glm-5.3-flash` through an Anthropic-compatible
backend. Claude Agent ACP and Claude CLI are the external agent software in
this test; the model is GLM, not an Anthropic Claude model.

| Operation | Observed result |
| --- | --- |
| `initialize` | Passed, negotiated protocol version 1 |
| `session/new` | Passed, returned a nonempty session ID |
| Background agent notifications | Accepted without disrupting response correlation |
| Separate backend messages endpoint probe with replacement credential | HTTP 200, exact expected sentinel, `end_turn` |
| `session/prompt` with replacement credential | Passed: exact `ACP_TRANSPORT_OK` text, `end_turn`, five session updates, zero host operations |
| Complete successful smoke run | 13.78 seconds; process exited with code 0 and no reported cleanup errors |
| Real-agent file/terminal/permission requests | Not tested |
| Real-agent cancellation or session resume | Not tested |

An earlier run with a different credential timed out at `session/prompt` after
90 seconds. Separate backend probes returned HTTP 429, with the models endpoint
reporting `API_KEY_QUOTA_EXHAUSTED`. Replacing that credential and selecting an
available model resolved this test's blocker without a transport code change.
The successful run establishes one initialization/session/text-prompt sequence;
it does not establish all agent methods, models, or backend configurations.

## Remote CI

On 2026-10-15, [GitHub Actions run 38735206688](https://github.com/leanhngua061201-droid/acp-dotnet-transport/actions/runs/38735206688)
passed on both `windows-latest` and `ubuntu-latest` for the initial source commit
`d7f4049a04c038dd11ae16b943b4018bb4c883ea`. Both jobs built the library with
warnings treated as errors, ran all 20 transport checks, ran the local duplex
example, and built AgentSmoke. No external agent or model API was invoked by CI.
The real-agent text result above remains a local Windows observation.

## Remaining limits

- Streams and handlers must cooperate with cancellation. Shutdown does not
  forcibly close caller-owned streams or interrupt an uncooperative handler.
- These checks cover selected concurrency failures; they are not an exhaustive
  scheduler exploration, multi-hour soak, or full ACP conformance suite.
- Only the transport component is implemented. The embedding application remains
  responsible for ACP methods, capabilities, authentication and host operations.
