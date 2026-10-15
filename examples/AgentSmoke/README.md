# Opt-in real-agent smoke client

This example launches an ACP agent as a child process and exchanges messages
through its redirected stdin/stdout. It uses a new, empty OS temporary directory
as the agent's working directory, advertises no file or terminal capabilities,
rejects reverse requests, and discards (but continuously drains) stderr. It does
not print raw protocol messages, credentials, session IDs, or model text.

Configure the chosen agent's authentication using its own supported environment
variables before starting this example. The example inherits the environment;
it does not read credential files or perform interactive login.

Required environment:

- `ACP_AGENT_EXECUTABLE`: executable to launch directly, without a shell.
- `ACP_AGENT_ARGUMENTS_JSON`: optional JSON array of individual argument strings.
- `ACP_SMOKE_SESSION_META_JSON`: optional JSON object sent as `session/new._meta`;
  extensions in it are specific to your agent, not part of this transport library.

Run from the repository root:

```sh
# Initialize only; no session or model request.
dotnet run --project examples/AgentSmoke/Acp.Transport.AgentSmoke.csproj -c Release

# Initialize and create a session.
dotnet run --project examples/AgentSmoke/Acp.Transport.AgentSmoke.csproj -c Release -- --session

# Also request one short model reply using the agent's configured service.
dotnet run --project examples/AgentSmoke/Acp.Transport.AgentSmoke.csproj -c Release -- --prompt
```

The prompt check requires an `end_turn` response, text chunks containing exactly
`ACP_TRANSPORT_OK` (ignoring surrounding whitespace), and zero host operations.
It retains at most 64 characters in memory for comparison and does not print
the text. This detects ordinary authentication/error replies, but it still trusts
the external agent's assertions about its model service. All operations share a
120-second deadline; individual requests have a
90-second timeout. On exit, the client closes stdin, waits up to five seconds,
then terminates its own child process tree if needed. Cleanup steps run
independently; a cleanup failure sets a nonzero exit code and prints only its
exception type. Agent-created artifacts
remain in the temporary directory. The host application owns cleanup of any
additional stores the agent uses outside that directory.

## Claude Agent ACP example (PowerShell)

With Node and `@agentclientprotocol/claude-agent-acp` already installed globally,
the following configures the command without embedding a local machine path:

```powershell
$env:ACP_AGENT_EXECUTABLE = (Get-Command node -ErrorAction Stop).Source
$agentEntry = Join-Path (& npm root -g) '@agentclientprotocol/claude-agent-acp/dist/index.js'
$env:ACP_AGENT_ARGUMENTS_JSON = ConvertTo-Json -Compress -InputObject @($agentEntry)
$env:ACP_SMOKE_SESSION_META_JSON = '{"claudeCode":{"options":{"settingSources":[],"tools":[],"plugins":[],"persistSession":false,"maxTurns":2,"systemPrompt":"Verify ACP connectivity. Do not use tools."}}}'
dotnet run --project examples/AgentSmoke/Acp.Transport.AgentSmoke.csproj -c Release -- --prompt
```

These agent-specific options disable settings-file sources, built-in tools and
explicit plugins for this check. Pass authentication via the agent's environment
first. They do not sandbox the external executable or prevent it from accessing
its own internal stores. No MCP servers are configured. Other agents may require
different session metadata or authentication.

Normal verification and CI only **build** this example. They never launch an
external agent or contact a model API.

## Observed interoperability

On 2026-10-15, this client completed initialization (protocol version 1), session
creation and a prompt with `@agentclientprotocol/claude-agent-acp` 0.90.0,
Claude CLI 2.1.210 and Node 24.15.0 on Windows. The agent selected
`glm-5.3-flash` through an Anthropic-compatible backend. The full smoke run took
13.78 seconds, received five session updates, matched the exact sentinel and
finished with `end_turn` and zero host operations. The process exited with code
0, including cleanup. This does not verify an Anthropic Claude model, real-agent
tool operations, cancellation or session resume. See [VALIDATION.md](../../VALIDATION.md).
