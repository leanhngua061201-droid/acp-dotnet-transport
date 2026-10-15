using System.Diagnostics;
using System.Text.Json;
using Acp.Transport;

internal static partial class AcpTransportChecks
{
    public static async Task SoakAsync(int seconds)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 15));
        using var pair = new DuplexPair();
        var notifications = 0;
        var reverseRequests = 0;
        var reverseResponses = 0;
        await using var transport = new AcpTransport(pair.ClientInput, pair.ClientOutput,
            requestHandler: (request, token) =>
            {
                token.ThrowIfCancellationRequested();
                Interlocked.Increment(ref reverseRequests);
                return Task.FromResult(request.Params!.Value.Clone());
            },
            notificationHandler: (_, _) => Interlocked.Increment(ref notifications));
        using var reader = pair.CreateServerReader();
        using var writer = pair.CreateServerWriter();
        using var peerCancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var peer = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var frame = await ReadStressFrameAsync(reader, peerCancellation.Token);
                    var root = frame.RootElement;
                    if (!root.TryGetProperty("method", out var method))
                    {
                        var reverseId = root.GetProperty("result").GetProperty("id").GetInt64();
                        Check(root.GetProperty("id").GetString() == $"reverse-{reverseId}",
                            "Soak reverse response correlation failed.");
                        Interlocked.Increment(ref reverseResponses);
                        continue;
                    }
                    Check(method.GetString() == "soak/echo", "Unexpected soak method.");
                    var id = root.GetProperty("id").GetInt64();
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new
                    {
                        jsonrpc = "2.0", method = "soak/update", @params = new { id }
                    }).AsMemory(), peerCancellation.Token);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new
                    {
                        jsonrpc = "2.0", id = $"reverse-{id}", method = "soak/reverse", @params = new { id }
                    }).AsMemory(), peerCancellation.Token);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new
                    {
                        jsonrpc = "2.0", id, result = root.GetProperty("params")
                    }).AsMemory(), peerCancellation.Token);
                }
            }
            catch (OperationCanceledException) when (peerCancellation.IsCancellationRequested) { }
        });
        var stopwatch = Stopwatch.StartNew();
        long count = 0;
        var samples = new List<long>();
        var nextSample = 0;
        try
        {
            while (stopwatch.Elapsed.TotalSeconds < seconds)
            {
                var sequence = ++count;
                using var response = await transport.RequestAsync("soak/echo",
                    new { sequence, text = "Unicode αβ世界🙂" }, cancellationToken: deadline.Token);
                Check(response.RootElement.GetProperty("sequence").GetInt64() == sequence,
                    "Soak response correlation failed.");
                Check(response.RootElement.GetProperty("text").GetString() == "Unicode αβ世界🙂",
                    "Soak Unicode payload was corrupted.");
                if (stopwatch.Elapsed.TotalSeconds >= nextSample)
                {
                    // Live-heap samples are diagnostic observations, not a leak-proof guarantee.
                    samples.Add(GC.GetTotalMemory(forceFullCollection: true));
                    nextSample += 10;
                }
                await Task.Delay(5, deadline.Token);
            }
            await WaitStressCollectionEmptyAsync(transport, "_inboundRequests", deadline.Token);
            while (Volatile.Read(ref reverseResponses) < count)
            {
                if (peer.IsCompleted) await peer;
                await Task.Delay(1, deadline.Token);
            }
            CheckStressStorage(transport, expectNoAbandoned: true);
            Check(notifications == count && reverseRequests == count && reverseResponses == count, "Soak traffic was lost.");
            Console.WriteLine($"PASS soak: {stopwatch.Elapsed.TotalSeconds:F1}s, {count:N0} requests, "
                + $"{notifications:N0} notifications, {reverseRequests:N0} reverse requests; pending=0, inbound=0.");
            Console.WriteLine($"  Live managed heap samples (bytes, every ~10s): {string.Join(", ", samples)}.");
        }
        finally
        {
            peerCancellation.Cancel();
            await peer.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
