using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Acp.Transport;

internal static partial class AcpTransportChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> StressTests() =>
    [
        ("10,000 requests preserve correlation and framing with concurrent traffic", StressConcurrentFramesAsync),
        ("Cancelled and timed-out requests drain late responses within bounded storage", StressLateResponsesAsync)
    ];

    private static async Task StressConcurrentFramesAsync()
    {
        const int requestCount = 10_000;
        const int batchSize = 64;
        const int notificationsPerBatch = 2;
        const int reverseRequestsPerBatch = 4;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var stopwatch = Stopwatch.StartNew();
        using var pair = new DuplexPair();
        using var fragmentedOutput = new FragmentedStressWriteStream(pair.ClientOutput);
        var receivedNotifications = 0;
        var handledReverseRequests = 0;
        await using var transport = new AcpTransport(pair.ClientInput, fragmentedOutput,
            new AcpTransportOptions(TimeSpan.FromSeconds(20)),
            async (request, token) =>
            {
                Check(request.Method == "stress/reverse", "Unexpected reverse-request method.");
                var parameters = request.Params ?? throw new Exception("Missing reverse-request parameters.");
                var sequence = parameters.GetProperty("sequence").GetInt32();
                var text = parameters.GetProperty("text").GetString();
                Check(text == StressPayload(sequence), "Reverse-request payload was corrupted.");
                token.ThrowIfCancellationRequested();
                // Let ordinary sends and other handler responses contend for the write gate.
                await Task.Yield();
                Interlocked.Increment(ref handledReverseRequests);
                return JsonSerializer.SerializeToElement(new { sequence, text });
            },
            (method, parameters) =>
            {
                Check(method == "stress/server-update", "Unexpected server notification.");
                var values = parameters ?? throw new Exception("Missing server notification parameters.");
                var sequence = values.GetProperty("sequence").GetInt32();
                Check(values.GetProperty("text").GetString() == StressPayload(sequence),
                    "Server notification payload was corrupted.");
                Interlocked.Increment(ref receivedNotifications);
            });
        using var reader = pair.CreateServerReader();
        using var writer = pair.CreateServerWriter();
        var batches = 0;
        var observedWireFrames = 0;

        for (var offset = 0; offset < requestCount; offset += batchSize)
        {
            var count = Math.Min(batchSize, requestCount - offset);
            var requests = new Task<JsonDocument>[count];
            for (var index = 0; index < count; index++)
            {
                var sequence = offset + index;
                requests[index] = transport.RequestAsync("stress/echo",
                    new { sequence, text = StressPayload(sequence) }, cancellationToken: deadline.Token);
            }
            var notifications = new Task[notificationsPerBatch];
            for (var index = 0; index < notifications.Length; index++)
            {
                var sequence = offset + index;
                notifications[index] = transport.NotifyAsync("stress/client-update",
                    new { sequence, text = StressPayload(sequence) }, deadline.Token);
            }

            // Both directions are active before reading the client frames. Handler responses,
            // notifications and ordinary requests must share one serialized frame writer.
            for (var index = 0; index < notificationsPerBatch; index++)
            {
                var sequence = offset + index;
                await writer.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0", method = "stress/server-update",
                    @params = new { sequence, text = StressPayload(sequence) }
                }).AsMemory(), deadline.Token);
            }
            for (var index = 0; index < reverseRequestsPerBatch; index++)
            {
                var sequence = offset + index;
                await writer.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0", id = $"reverse-{sequence}", method = "stress/reverse",
                    @params = new { sequence, text = StressPayload(sequence) }
                }).AsMemory(), deadline.Token);
            }

            var receivedRequests = new List<(long Id, int Sequence)>(count);
            var uniqueSequences = new HashSet<int>();
            var notificationSequences = new HashSet<int>();
            var reverseSequences = new HashSet<int>();
            var frameCount = count + notificationsPerBatch + reverseRequestsPerBatch;
            for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                using var frame = await ReadStressFrameAsync(reader, deadline.Token);
                var message = frame.RootElement;
                observedWireFrames++;
                if (message.TryGetProperty("method", out var method))
                {
                    var parameters = message.GetProperty("params");
                    var sequence = parameters.GetProperty("sequence").GetInt32();
                    Check(parameters.GetProperty("text").GetString() == StressPayload(sequence),
                        "Concurrent outbound frames interleaved or changed their payload.");
                    if (method.GetString() == "stress/echo")
                    {
                        Check(sequence >= offset && sequence < offset + count && uniqueSequences.Add(sequence),
                            "An outbound request was duplicated or escaped its bounded batch.");
                        receivedRequests.Add((message.GetProperty("id").GetInt64(), sequence));
                    }
                    else
                    {
                        Check(method.GetString() == "stress/client-update" && !message.TryGetProperty("id", out _),
                            "An outbound notification acquired an ID or an unexpected method.");
                        Check(sequence >= offset && sequence < offset + notificationsPerBatch
                            && notificationSequences.Add(sequence), "An outbound notification was duplicated.");
                    }
                }
                else
                {
                    var result = message.GetProperty("result");
                    var sequence = result.GetProperty("sequence").GetInt32();
                    Check(sequence >= offset && sequence < offset + reverseRequestsPerBatch
                        && reverseSequences.Add(sequence), "A reverse response was duplicated or miscorrelated.");
                    Check(message.GetProperty("id").GetString() == $"reverse-{sequence}"
                        && result.GetProperty("text").GetString() == StressPayload(sequence),
                        "A reverse response ID or payload was corrupted.");
                }
            }
            Check(receivedRequests.Count == count && notificationSequences.Count == notificationsPerBatch
                && reverseSequences.Count == reverseRequestsPerBatch, "The peer did not receive every frame kind.");

            // Reply in descending application sequence, independent of the wire request order.
            // This makes matching a response by arrival order fail deterministically.
            foreach (var request in receivedRequests.OrderByDescending(item => item.Sequence))
                await writer.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0", id = request.Id,
                    result = new { sequence = request.Sequence, text = StressPayload(request.Sequence) }
                }).AsMemory(), deadline.Token);

            var responses = await Task.WhenAll(requests).WaitAsync(deadline.Token);
            try
            {
                for (var index = 0; index < responses.Length; index++)
                {
                    var result = responses[index].RootElement;
                    var expected = offset + index;
                    Check(result.GetProperty("sequence").GetInt32() == expected
                        && result.GetProperty("text").GetString() == StressPayload(expected),
                        "An out-of-order response was delivered to another request.");
                }
            }
            finally
            {
                foreach (var response in responses) response.Dispose();
            }
            await Task.WhenAll(notifications).WaitAsync(deadline.Token);
            batches++;
        }

        Check(receivedNotifications == batches * notificationsPerBatch, "Inbound notifications were lost.");
        Check(handledReverseRequests == batches * reverseRequestsPerBatch, "Reverse requests were lost.");
        await WaitStressCollectionEmptyAsync(transport, "_inboundRequests", deadline.Token);
        CheckStressStorage(transport, expectNoAbandoned: true);
        Console.WriteLine($"  Stress: {requestCount:N0} correlated requests, max {batchSize} in flight, "
            + $"{receivedNotifications + batches * notificationsPerBatch:N0} notifications, "
            + $"{handledReverseRequests:N0} reverse requests, {observedWireFrames:N0} checked outbound frames, "
            + $"{fragmentedOutput.ChunksWritten:N0} write fragments in {stopwatch.Elapsed.TotalSeconds:F2}s.");
    }

    private static async Task StressLateResponsesAsync()
    {
        const int cancelledRequestCount = 2_048;
        const int batchSize = 64;
        const int timedOutRequestCount = 8;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var stopwatch = Stopwatch.StartNew();
        using var pair = new DuplexPair();
        var barriers = new ConcurrentDictionary<int, TaskCompletionSource>();
        await using var transport = new AcpTransport(pair.ClientInput, pair.ClientOutput,
            new AcpTransportOptions(TimeSpan.FromSeconds(20)),
            notificationHandler: (method, parameters) =>
            {
                Check(method == "stress/drained", "Unexpected drain notification.");
                var values = parameters ?? throw new Exception("Missing drain notification parameters.");
                var batch = values.GetProperty("batch").GetInt32();
                Check(barriers.TryRemove(batch, out var barrier), "Unexpected or duplicate drain barrier.");
                barrier!.TrySetResult();
            });
        using var reader = pair.CreateServerReader();
        using var writer = pair.CreateServerWriter();
        var maximumQueueCount = 0;
        var batches = 0;

        for (var offset = 0; offset < cancelledRequestCount; offset += batchSize)
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var requests = Enumerable.Range(offset, Math.Min(batchSize, cancelledRequestCount - offset))
                .Select(sequence => transport.RequestAsync("stress/cancelled", new { sequence },
                    cancellationToken: cancellation.Token)).ToArray();
            var ids = new long[requests.Length];
            for (var index = 0; index < ids.Length; index++)
            {
                using var frame = await ReadStressFrameAsync(reader, deadline.Token);
                ids[index] = frame.RootElement.GetProperty("id").GetInt64();
            }
            // Cancel only after every frame is fully readable: this targets response waiting,
            // not the intentionally connection-fatal partial-write cancellation case.
            cancellation.Cancel();
            await Task.WhenAll(requests.Select(request => Rejects<OperationCanceledException>(() => request)))
                .WaitAsync(deadline.Token);
            Check(GetStressCollectionCount(transport, "_pending") == 0, "Cancelled requests remained pending.");
            await SendStressLateResponsesAndBarrierAsync(ids, batches++, writer, barriers, deadline.Token);
            CheckStressStorage(transport, expectNoAbandoned: true);
            maximumQueueCount = Math.Max(maximumQueueCount, GetStressCollectionCount(transport, "_abandonedOrder"));
        }

        // Run all timeout clocks together, with a generous interval for eight local frames.
        // They deliberately receive no reply; successful completion can only be a timeout.
        var timedOut = Enumerable.Range(0, timedOutRequestCount)
            .Select(sequence => transport.RequestAsync("stress/timed-out", new { sequence },
                timeout: TimeSpan.FromSeconds(2), cancellationToken: deadline.Token)).ToArray();
        var timedOutIds = new long[timedOut.Length];
        for (var index = 0; index < timedOutIds.Length; index++)
        {
            using var frame = await ReadStressFrameAsync(reader, deadline.Token);
            timedOutIds[index] = frame.RootElement.GetProperty("id").GetInt64();
        }
        await Task.WhenAll(timedOut.Select(request => Rejects<TimeoutException>(() => request)))
            .WaitAsync(deadline.Token);
        Check(GetStressCollectionCount(transport, "_pending") == 0, "Timed-out requests remained pending.");
        await SendStressLateResponsesAndBarrierAsync(timedOutIds, batches, writer, barriers, deadline.Token);
        CheckStressStorage(transport, expectNoAbandoned: true);
        maximumQueueCount = Math.Max(maximumQueueCount, GetStressCollectionCount(transport, "_abandonedOrder"));
        Check(barriers.IsEmpty, "A drain barrier remained unobserved.");

        // A live request after all late responses proves they did not poison the reader loop.
        var finalRequest = transport.RequestAsync("stress/still-alive", cancellationToken: deadline.Token);
        using (var frame = await ReadStressFrameAsync(reader, deadline.Token))
            await writer.WriteLineAsync(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = frame.RootElement.GetProperty("id").GetInt64(), result = new { ok = true }
            }).AsMemory(), deadline.Token);
        using var finalResponse = await finalRequest.WaitAsync(deadline.Token);
        Check(finalResponse.RootElement.GetProperty("ok").GetBoolean(), "Late responses terminated the connection.");
        CheckStressStorage(transport, expectNoAbandoned: true);
        Console.WriteLine($"  Stress: {cancelledRequestCount:N0} cancellations, {timedOutRequestCount} timeouts, "
            + $"{cancelledRequestCount + timedOutRequestCount:N0} late responses drained, "
            + $"pending=0, retained IDs=0, maximum FIFO={maximumQueueCount}/1024 "
            + $"in {stopwatch.Elapsed.TotalSeconds:F2}s.");
    }

    private static async Task SendStressLateResponsesAndBarrierAsync(long[] ids, int batch, StreamWriter writer,
        ConcurrentDictionary<int, TaskCompletionSource> barriers, CancellationToken cancellationToken)
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Check(barriers.TryAdd(batch, barrier), "Duplicate drain barrier.");
        foreach (var id in ids.Reverse())
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = new { late = true } })
                .AsMemory(), cancellationToken);
        await writer.WriteLineAsync(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", method = "stress/drained", @params = new { batch }
        }).AsMemory(), cancellationToken);
        // The single reader dispatches this notification only after all preceding replies.
        await barrier.Task.WaitAsync(cancellationToken);
    }

    private static string StressPayload(int sequence)
        => $"payload-{sequence}: αβ世界🙂|" + new string('x', 700 + sequence % 7 * 37);

    private static async Task<JsonDocument> ReadStressFrameAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var line = await reader.ReadLineAsync(cancellationToken)
            ?? throw new EndOfStreamException("The stress peer closed before the expected frame.");
        var document = JsonDocument.Parse(line);
        try
        {
            Check(document.RootElement.GetProperty("jsonrpc").GetString() == "2.0", "Invalid stress frame version.");
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private static int GetStressCollectionCount(AcpTransport transport, string fieldName)
    {
        // Wire success cannot expose pending-task leaks or bounded-ID bookkeeping growth.
        var field = typeof(AcpTransport).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception($"Missing transport state: {fieldName}.");
        var collection = field.GetValue(transport) ?? throw new Exception($"Null transport state: {fieldName}.");
        return (int)(collection.GetType().GetProperty("Count")?.GetValue(collection)
            ?? throw new Exception($"Transport state has no Count: {fieldName}."));
    }

    private static async Task WaitStressCollectionEmptyAsync(AcpTransport transport, string fieldName,
        CancellationToken cancellationToken)
    {
        while (GetStressCollectionCount(transport, fieldName) != 0)
            await Task.Delay(1, cancellationToken);
    }

    private static void CheckStressStorage(AcpTransport transport, bool expectNoAbandoned)
    {
        Check(GetStressCollectionCount(transport, "_pending") == 0, "Requests remained pending after completion.");
        var abandonedCount = GetStressCollectionCount(transport, "_abandoned");
        var queueCount = GetStressCollectionCount(transport, "_abandonedOrder");
        Check(abandonedCount <= 1024 && queueCount <= 1024,
            $"Late-response storage grew beyond its bound: IDs={abandonedCount}, FIFO={queueCount}.");
        if (expectNoAbandoned) Check(abandonedCount == 0, "A drained late-response ID was retained.");
    }

    private sealed class FragmentedStressWriteStream(Stream inner) : Stream
    {
        public long ChunksWritten { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override void Write(byte[] buffer, int offset, int count)
            => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (!buffer.IsEmpty)
            {
                var count = Math.Min(193, buffer.Length);
                await inner.WriteAsync(buffer[..count], cancellationToken);
                ChunksWritten++;
                buffer = buffer[count..];
                // A missing transport write lock would now allow another JSON frame to
                // interleave between fragments and be rejected by the peer's JSON parser.
                await Task.Yield();
            }
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
