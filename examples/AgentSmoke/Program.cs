using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Acp.Transport;

// Opt-in: authentication belongs to the chosen agent. This program never prints
// environment values, raw protocol messages, session IDs, or agent stderr.
var runPrompt = args.Contains("--prompt", StringComparer.Ordinal);
var createSession = runPrompt || args.Contains("--session", StringComparer.Ordinal);
if (args.Any(arg => arg is not ("--prompt" or "--session")))
{
    Console.Error.WriteLine("Usage: [--session | --prompt]");
    return 2;
}
var executable = Environment.GetEnvironmentVariable("ACP_AGENT_EXECUTABLE");
if (string.IsNullOrWhiteSpace(executable))
{
    Console.Error.WriteLine("Set ACP_AGENT_EXECUTABLE and, if needed, ACP_AGENT_ARGUMENTS_JSON (a JSON string array).");
    return 2;
}

var directory = Path.Combine(Path.GetTempPath(), "acp-transport-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
using var process = new Process();
process.StartInfo = new ProcessStartInfo(executable)
{
    UseShellExecute = false,
    RedirectStandardInput = true,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    CreateNoWindow = true,
    WorkingDirectory = directory
};
var stage = "configuration";
Task? stderrDrain = null;
AcpTransport? transport = null;
long stderrCharacters = 0;
var started = false;
var exitCode = 0;
var stopwatch = Stopwatch.StartNew();
try
{
    var arguments = JsonSerializer.Deserialize<string[]>(
        Environment.GetEnvironmentVariable("ACP_AGENT_ARGUMENTS_JSON") ?? "[]")
        ?? throw new InvalidDataException("Arguments must be a JSON string array.");
    foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
    stage = "process start";
    started = process.Start();
    if (!started) throw new InvalidOperationException("Agent did not start.");
    stderrDrain = DrainStderrAsync();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
    var updates = 0;
    var textCharacters = 0;
    var reverseRequests = 0;
    var reply = new StringBuilder(64);
    transport = new AcpTransport(process.StandardOutput.BaseStream, process.StandardInput.BaseStream,
        new AcpTransportOptions(TimeSpan.FromSeconds(90)),
        requestHandler: (_, _) =>
        {
            Interlocked.Increment(ref reverseRequests);
            // The smoke client advertises no file or terminal capabilities.
            throw new NotSupportedException("The smoke client does not implement host operations.");
        },
        notificationHandler: (method, parameters) =>
        {
            if (method != "session/update" || parameters is not { } values) return;
            Interlocked.Increment(ref updates);
            if (values.TryGetProperty("update", out var update)
                && update.TryGetProperty("sessionUpdate", out var kind)
                && kind.GetString() == "agent_message_chunk"
                && update.TryGetProperty("content", out var content)
                && content.TryGetProperty("type", out var type) && type.GetString() == "text"
                && content.TryGetProperty("text", out var text))
            {
                var chunk = text.GetString() ?? "";
                Interlocked.Add(ref textCharacters, chunk.Length);
                // Retain only enough text to verify the sentinel, never an unbounded transcript.
                lock (reply) reply.Append(chunk.AsSpan(0, Math.Min(chunk.Length, 64 - reply.Length)));
            }
        });
    stage = "initialize";
    using var initialized = await transport.RequestAsync("initialize", new
    {
        protocolVersion = 1,
        clientInfo = new { name = "acp-dotnet-transport-smoke", version = "0.1.0" },
        clientCapabilities = new { }
    }, cancellationToken: deadline.Token);
    var version = initialized.RootElement.GetProperty("protocolVersion").GetInt32();
    if (version != 1) throw new InvalidDataException("This smoke client supports ACP protocol version 1.");
    Console.WriteLine($"PASS initialize: protocolVersion={version}.");

    if (createSession)
    {
        stage = "session/new";
        var parameters = new Dictionary<string, object?> { ["cwd"] = directory, ["mcpServers"] = Array.Empty<object>() };
        var metaJson = Environment.GetEnvironmentVariable("ACP_SMOKE_SESSION_META_JSON");
        if (!string.IsNullOrWhiteSpace(metaJson))
        {
            using var meta = JsonDocument.Parse(metaJson);
            if (meta.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Session meta must be an object.");
            parameters["_meta"] = meta.RootElement.Clone();
        }
        using var session = await transport.RequestAsync("session/new", parameters, cancellationToken: deadline.Token);
        var sessionId = session.RootElement.GetProperty("sessionId").GetString();
        if (string.IsNullOrWhiteSpace(sessionId)) throw new InvalidDataException("Missing session ID.");
        Console.WriteLine("PASS session/new: session created in a temporary empty directory.");
        if (runPrompt)
        {
            stage = "session/prompt";
            using var response = await transport.RequestAsync("session/prompt", new
            {
                sessionId,
                prompt = new[] { new { type = "text", text = "Reply with exactly ACP_TRANSPORT_OK. Do not use any tools." } }
            }, cancellationToken: deadline.Token);
            var reason = response.RootElement.GetProperty("stopReason").GetString();
            string replyText;
            lock (reply) replyText = reply.ToString();
            if (reason != "end_turn" || textCharacters > 64 || reverseRequests != 0
                || !string.Equals(replyText.Trim(), "ACP_TRANSPORT_OK", StringComparison.Ordinal))
                throw new InvalidDataException("Prompt did not finish with the expected sentinel and no host operations.");
            Console.WriteLine($"PASS session/prompt: end_turn, expected sentinel, {updates} updates, no host operations.");
        }
    }
    Console.WriteLine($"Smoke complete in {stopwatch.Elapsed.TotalSeconds:F2}s.");
}
catch (Exception error)
{
    // Errors from an external agent can contain configuration or other private data.
    Console.Error.WriteLine($"FAIL {stage}: {error.GetType().Name}; agent stderr discarded ({Interlocked.Read(ref stderrCharacters)} characters).");
    exitCode = 1;
}
finally
{
    if (started)
    {
        await TryCleanupAsync("stdin", () => { process.StandardInput.Close(); return Task.CompletedTask; });
        await TryCleanupAsync("graceful exit", async () =>
        {
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { /* The next step terminates this owned process. */ }
        });
        await TryCleanupAsync("process termination", async () =>
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        });
    }
    if (transport is not null)
        await TryCleanupAsync("transport", () => transport.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    if (stderrDrain is not null)
        await TryCleanupAsync("stderr drain", () => stderrDrain.WaitAsync(TimeSpan.FromSeconds(5)));
    // Keep any agent-created session artifacts in the OS temporary directory.
}
return exitCode;

async Task TryCleanupAsync(string step, Func<Task> cleanup)
{
    try { await cleanup(); }
    catch (Exception error)
    {
        exitCode = 1;
        Console.Error.WriteLine($"FAIL cleanup ({step}): {error.GetType().Name}.");
    }
}

async Task DrainStderrAsync()
{
    var buffer = new char[4096];
    int read;
    while ((read = await process.StandardError.ReadAsync(buffer.AsMemory())) != 0)
        Interlocked.Add(ref stderrCharacters, read);
}
