using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Acp.Transport;

internal static partial class AcpTransportChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> LifetimeTests() =>
    [
        ("Blocked request writes report timeout and terminate the connection", async () =>
        {
            using var pair = new DuplexPair();
            using var output = new BlockingFirstWriteStream();
            await using var transport = new AcpTransport(pair.ClientInput, output);
            var pending = transport.RequestAsync("blocked-send", timeout: TimeSpan.FromMilliseconds(100));
            await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Rejects<TimeoutException>(() => pending);
            await Rejects<InvalidOperationException>(() => transport.RequestAsync("after-timeout"));
        }),
        ("Caller cancellation remains cancellation when a blocked write times out", async () =>
        {
            using var pair = new DuplexPair();
            using var output = new BlockingFirstWriteStream();
            using var cancellation = new CancellationTokenSource();
            await using var transport = new AcpTransport(pair.ClientInput, output);
            var pending = transport.RequestAsync("cancelled-send", timeout: TimeSpan.FromMilliseconds(100),
                cancellationToken: cancellation.Token);
            await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            try { await pending; }
            catch (OperationCanceledException error)
            {
                Check(error.CancellationToken == cancellation.Token, "Expected the caller's cancellation token.");
                return;
            }
            throw new Exception("Expected caller cancellation.");
        }),
        ("Cancelled partial notification faults the connection before another frame", async () =>
        {
            using var pair = new DuplexPair();
            using var cancellation = new CancellationTokenSource();
            using var output = new CancelOnFirstWriteStream(cancellation);
            await using var transport = new AcpTransport(pair.ClientInput, output);
            await Rejects<OperationCanceledException>(() => transport.NotifyAsync("first", cancellationToken: cancellation.Token));
            await Rejects<InvalidOperationException>(() => transport.NotifyAsync("second"));
        }),
        ("Cancellation while waiting for the write lock preserves the connection", async () =>
        {
            using var pair = new DuplexPair();
            using var output = new BlockingFirstWriteStream();
            await using var transport = new AcpTransport(pair.ClientInput, output);
            var first = transport.NotifyAsync("first");
            try
            {
                await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                using var cancellation = new CancellationTokenSource();
                var second = transport.NotifyAsync("cancelled-before-write", cancellationToken: cancellation.Token);
                cancellation.Cancel();
                await Rejects<OperationCanceledException>(() => second);
            }
            finally { output.Release.TrySetResult(); }
            await first;
            await transport.NotifyAsync("third");
        }),
        ("Late responses keep both retained IDs and bookkeeping storage bounded", async () =>
        {
            using var pair = new DuplexPair();
            var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var transport = new AcpTransport(pair.ClientInput, pair.ClientOutput,
                notificationHandler: (method, _) => { if (method == "test/drained") drained.TrySetResult(); });
            using var reader = pair.CreateServerReader();
            using var writer = pair.CreateServerWriter();
            for (var index = 0; index < 1100; index++)
            {
                using var cancellation = new CancellationTokenSource();
                var pending = transport.RequestAsync("cancelled", cancellationToken: cancellation.Token);
                using var request = JsonDocument.Parse(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2))
                    ?? throw new Exception("Expected an outbound request."));
                cancellation.Cancel();
                await Rejects<OperationCanceledException>(() => pending);
                await writer.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0", id = request.RootElement.GetProperty("id").GetInt64(), result = new { }
                }));
            }
            await writer.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"test/drained\"}");
            await drained.Task.WaitAsync(TimeSpan.FromSeconds(2));
            // Inspect the retained storage because wire-level success alone cannot detect its growth.
            var queue = (ConcurrentQueue<string>)typeof(AcpTransport)
                .GetField("_abandonedOrder", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(transport)!;
            Check(queue.Count <= 1024, $"Late-response bookkeeping grew to {queue.Count} entries.");
        })
    ];

    private sealed class CancelOnFirstWriteStream(CancellationTokenSource cancellation) : MemoryStream
    {
        private bool _first = true;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_first)
            {
                _first = false;
                await base.WriteAsync(buffer[..Math.Min(5, buffer.Length)], CancellationToken.None);
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            await base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class BlockingFirstWriteStream : MemoryStream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _first = true;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_first)
            {
                _first = false;
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            await base.WriteAsync(buffer, cancellationToken);
        }
    }
}
