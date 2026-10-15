using System.Collections.Concurrent;
using System.Reflection;
using Acp.Transport;

internal static partial class AcpTransportChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> ShutdownTests() =>
    [
        ("Disposal drains an active write and queued senders without closing caller streams", async () =>
        {
            using var pair = new DuplexPair();
            using var output = new BlockingFirstWriteStream();
            var transport = new AcpTransport(pair.ClientInput, output);
            var first = transport.NotifyAsync("blocked");
            await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var waiting = Enumerable.Range(0, 32).Select(index => transport.NotifyAsync("queued", new { index })).ToArray();
            var disposal = transport.DisposeAsync().AsTask();
            await Task.WhenAll(waiting.Append(first).Select(ExpectShutdownAsync)).WaitAsync(TimeSpan.FromSeconds(3));
            await disposal.WaitAsync(TimeSpan.FromSeconds(3));
            Check(output.CanWrite && pair.ClientInput.CanRead, "Disposal closed caller-owned streams.");
            Check(output.Length == 0, "Disposal flushed a cancelled frame.");
            await transport.DisposeAsync();
        }),
        ("Concurrent disposers wait for the same reader shutdown", async () =>
        {
            using var input = new DelayedCancellationReadStream();
            using var output = new MemoryStream();
            var transport = new AcpTransport(input, output);
            transport.Start();
            await input.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var first = transport.DisposeAsync().AsTask();
            await input.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var second = transport.DisposeAsync().AsTask();
            try
            {
                Check(!first.IsCompleted && !second.IsCompleted, "A concurrent disposer returned before shutdown completed.");
            }
            finally { input.Release.TrySetResult(); }
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3));
            Check(input.CanRead && output.CanWrite, "Disposal closed caller-owned streams.");
        }),
        ("Disposal cancels an inbound handler and drains its registration", async () =>
        {
            using var pair = new DuplexPair();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var transport = new AcpTransport(pair.ClientInput, pair.ClientOutput, requestHandler: async (_, token) =>
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { cancelled.TrySetResult(); }
                return default;
            });
            transport.Start();
            using var writer = pair.CreateServerWriter();
            await writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":\"shutdown\",\"method\":\"host/wait\"}");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await transport.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            Check(cancelled.Task.IsCompletedSuccessfully, "Inbound handler was not cancelled.");
            var requests = (ConcurrentDictionary<long, Task>)typeof(AcpTransport)
                .GetField("_inboundRequests", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(transport)!;
            Check(requests.IsEmpty, "Inbound registration survived disposal.");
        }),
        ("A throwing cancellation callback cannot bypass disposal cleanup", async () =>
        {
            using var pair = new DuplexPair();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var transport = new AcpTransport(pair.ClientInput, pair.ClientOutput, requestHandler: async (_, token) =>
            {
                using var callback = token.Register(() => throw new InvalidOperationException("callback-boom"));
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return default;
            });
            transport.Start();
            using var writer = pair.CreateServerWriter();
            await writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":\"callback\",\"method\":\"host/wait\"}");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            pair.ServerOutput.CompleteWrites();
            await transport.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            await Rejects<AggregateException>(() => transport.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
            var gate = (SemaphoreSlim)typeof(AcpTransport)
                .GetField("_writeGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(transport)!;
            var lifetime = (CancellationTokenSource)typeof(AcpTransport)
                .GetField("_lifetime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(transport)!;
            await Rejects<ObjectDisposedException>(() => gate.WaitAsync());
            await Rejects<ObjectDisposedException>(() => Task.FromResult(lifetime.Token));
            Check(GetStressCollectionCount(transport, "_inboundRequests") == 0, "Handler survived cleanup.");
            Check(pair.ClientInput.CanRead && pair.ClientOutput.CanWrite, "Caller streams were closed.");
        }),
        ("Peer closure cancels a blocked write and releases pending requests", async () =>
        {
            using var pair = new DuplexPair();
            using var output = new BlockingFirstWriteStream();
            await using var transport = new AcpTransport(pair.ClientInput, output);
            var pending = transport.RequestAsync("blocked-before-close");
            await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            pair.ServerOutput.CompleteWrites();
            await ExpectShutdownAsync(pending).WaitAsync(TimeSpan.FromSeconds(3));
        })
    ];

    private static async Task ExpectShutdownAsync(Task task)
    {
        try { await task; }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or EndOfStreamException)
        { return; }
        throw new Exception("An operation unexpectedly succeeded during shutdown.");
    }

    private sealed class DelayedCancellationReadStream : Stream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var registration = cancellationToken.Register(() => Cancelled.TrySetResult());
            Entered.TrySetResult();
            await Release.Task;
            cancellationToken.ThrowIfCancellationRequested();
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
