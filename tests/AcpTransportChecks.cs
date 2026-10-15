using System.Threading.Channels;
using System.Text;
using System.Text.Json;
using Acp.Transport;

internal static partial class AcpTransportChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> Tests() =>
    [
        ("ACP transport correlates responses and preserves JSON values", async () =>
        {
            using var pair = new DuplexPair();
            using (var probeReader = pair.CreateServerReader())
            {
                await pair.ClientOutput.WriteBytesAsync(Encoding.UTF8.GetBytes("probe\n"));
                Check(await probeReader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)) == "probe", "测试双工流未传递字节");
            }
            await using var transport = new AcpTransport(pair.ClientInput, pair.ClientOutput,
                new AcpTransportOptions(TimeSpan.FromSeconds(2)));
            using var peerReader = pair.CreateServerReader();
            using var peerWriter = pair.CreateServerWriter();
            var pending = transport.RequestAsync("ping", new { value = "测试" });
            using var request = JsonDocument.Parse(await peerReader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2))
                ?? throw new Exception("ACP 请求未发送"));
            Check(request.RootElement.GetProperty("jsonrpc").GetString() == "2.0", "JSON-RPC 版本错误");
            Check(request.RootElement.GetProperty("method").GetString() == "ping", "ACP 方法错误");
            var id = request.RootElement.GetProperty("id").GetInt64();
            await peerWriter.WriteLineAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"ok\":true,\"text\":\"收到\"}}}}");
            using var response = await pending;
            Check(response.RootElement.GetProperty("ok").GetBoolean()
                && response.RootElement.GetProperty("text").GetString() == "收到", "ACP 响应内容错误");
        }),
        ("ACP session cancel is a notification without a request ID", async () =>
        {
            using var pair = new DuplexPair();
            await using var transport = new AcpTransport(pair.ClientInput, pair.ClientOutput,
                new AcpTransportOptions(TimeSpan.FromSeconds(2)));
            using var peerReader = pair.CreateServerReader();
            await transport.NotifyAsync("session/cancel", new { sessionId = "s-1" });
            using var message = JsonDocument.Parse(await peerReader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2))
                ?? throw new Exception("ACP cancel 通知未发送"));
            Check(message.RootElement.GetProperty("method").GetString() == "session/cancel"
                && !message.RootElement.TryGetProperty("id", out var requestId), "ACP session/cancel 必须是无 ID 通知");
        }),
        ("ACP transport dispatches notifications and reverse requests", async () =>
        {
            using var pair = new DuplexPair();
            var notification = new TaskCompletionSource<(string Method, JsonElement? Params)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var inbound = new TaskCompletionSource<AcpInboundRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var transport = new AcpTransport(pair.ClientInput, pair.ClientOutput,
                new AcpTransportOptions(TimeSpan.FromSeconds(2)),
                (request, _) =>
                {
                    inbound.TrySetResult(request);
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { accepted = true }));
                },
                (method, parameters) => notification.TrySetResult((method, parameters)));
            transport.Start();
            using var peerReader = pair.CreateServerReader();
            using var peerWriter = pair.CreateServerWriter();
            await peerWriter.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"state\":\"working\"}}");
            await peerWriter.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":\"server-1\",\"method\":\"fs/read\",\"params\":{\"path\":\"a.txt\"}}");
            var note = await notification.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var request = await inbound.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(note.Method == "session/update" && note.Params?.GetProperty("state").GetString() == "working",
                "ACP 通知未分发");
            Check(request.Id.ValueKind == JsonValueKind.String && request.Id.GetString() == "server-1"
                && request.Method == "fs/read", "ACP 反向请求未分发");
            using var response = JsonDocument.Parse(await peerReader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2))
                ?? throw new Exception("ACP 反向请求没有响应"));
            Check(response.RootElement.GetProperty("id").GetString() == "server-1"
                && response.RootElement.GetProperty("result").GetProperty("accepted").GetBoolean(), "ACP 反向响应错误");
        }),
        ("ACP reverse requests do not block later notifications", async () =>
        {
            using var pair = new DuplexPair();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var notification = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var transport = new AcpTransport(pair.ClientInput, pair.ClientOutput,
                new AcpTransportOptions(TimeSpan.FromSeconds(2)),
                async (_, token) =>
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(token);
                    return JsonSerializer.SerializeToElement(new { ok = true });
                },
                (method, _) => notification.TrySetResult(method));
            transport.Start();
            using var peerReader = pair.CreateServerReader();
            using var peerWriter = pair.CreateServerWriter();
            await peerWriter.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":\"slow\",\"method\":\"fs/read\"}");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await peerWriter.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"session/update\"}");
            Check(await notification.Task.WaitAsync(TimeSpan.FromSeconds(2)) == "session/update", "反向请求阻塞了通知分发");
            release.TrySetResult();
            using var response = JsonDocument.Parse(await peerReader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2))
                ?? throw new Exception("反向请求没有响应"));
            Check(response.RootElement.GetProperty("id").GetString() == "slow", "并发反向请求响应 ID 错误");
        }),
        ("ACP transport rejects mismatched response IDs and fails pending work", async () =>
        {
            using var pair = new DuplexPair();
            await using var transport = new AcpTransport(pair.ClientInput, pair.ClientOutput,
                new AcpTransportOptions(TimeSpan.FromSeconds(2)));
            using var peerReader = pair.CreateServerReader();
            using var peerWriter = pair.CreateServerWriter();
            var pending = transport.RequestAsync("ping");
            using var request = JsonDocument.Parse(await peerReader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2))
                ?? throw new Exception("ACP 请求未发送"));
            var id = request.RootElement.GetProperty("id").GetInt64();
            await peerWriter.WriteLineAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{id + 100},\"result\":{{}}}}");
            await Rejects<InvalidDataException>(() => pending);
            await Rejects<InvalidOperationException>(() => transport.RequestAsync("after-fault"));
        }),
        ("ACP transport reports timeout and cleans pending requests", async () =>
        {
            using var pair = new DuplexPair();
            await using var transport = new AcpTransport(pair.ClientInput, pair.ClientOutput,
                new AcpTransportOptions(TimeSpan.FromSeconds(2)));
            using var peerReader = pair.CreateServerReader();
            var pending = transport.RequestAsync("stall", timeout: TimeSpan.FromMilliseconds(80));
            _ = await peerReader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await Rejects<TimeoutException>(() => pending);
            var second = transport.RequestAsync("second", timeout: TimeSpan.FromMilliseconds(80));
            _ = await peerReader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await Rejects<TimeoutException>(() => second);
        }),
        ("ACP transport enforces message limits and strict UTF-8", async () =>
        {
            using var pair = new DuplexPair();
            await using var transport = new AcpTransport(pair.ClientInput, pair.ClientOutput,
                new AcpTransportOptions(TimeSpan.FromSeconds(2), 1024));
            using var peerReader = pair.CreateServerReader();
            var oversized = transport.RequestAsync("too-large", new { value = new string('x', 1500) });
            await Rejects<InvalidDataException>(() => oversized);

            var pending = transport.RequestAsync("bad-utf8");
            _ = await peerReader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await pair.ServerOutput.WriteBytesAsync([0xff, 0x0a]);
            await Rejects<DecoderFallbackException>(() => pending);

            using var inboundPair = new DuplexPair();
            await using var inboundTransport = new AcpTransport(inboundPair.ClientInput, inboundPair.ClientOutput,
                new AcpTransportOptions(TimeSpan.FromSeconds(2), 1024));
            using var inboundReader = inboundPair.CreateServerReader();
            using var inboundWriter = inboundPair.CreateServerWriter();
            var inboundPending = inboundTransport.RequestAsync("oversized-response");
            _ = await inboundReader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await inboundWriter.WriteLineAsync("{" + new string('x', 1100) + "}");
            await Rejects<InvalidDataException>(() => inboundPending);
        }),
        ("ACP transport fails pending requests when the peer closes", async () =>
        {
            using var pair = new DuplexPair();
            await using var transport = new AcpTransport(pair.ClientInput, pair.ClientOutput,
                new AcpTransportOptions(TimeSpan.FromSeconds(2)));
            using var peerReader = pair.CreateServerReader();
            var pending = transport.RequestAsync("close");
            _ = await peerReader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
            pair.ServerOutput.CompleteWrites();
            await Rejects<EndOfStreamException>(() => pending);
        })
    ];

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static async Task Rejects<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}");
    }

    private sealed class DuplexPair : IDisposable
    {
        private readonly Channel<byte[]> _clientToServer = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<byte[]> _serverToClient = Channel.CreateUnbounded<byte[]>();
        public ChannelStream ClientInput { get; }
        public ChannelStream ClientOutput { get; }
        public ChannelStream ServerInput { get; }
        public ChannelStream ServerOutput { get; }

        public DuplexPair()
        {
            ClientInput = new ChannelStream(_serverToClient.Reader, _serverToClient.Writer);
            ClientOutput = new ChannelStream(_clientToServer.Reader, _clientToServer.Writer);
            ServerInput = new ChannelStream(_clientToServer.Reader, _clientToServer.Writer);
            ServerOutput = new ChannelStream(_serverToClient.Reader, _serverToClient.Writer);
        }

        public StreamReader CreateServerReader() => new(ServerInput, new UTF8Encoding(false), false, 1024, leaveOpen: true);
        public StreamWriter CreateServerWriter() => new(ServerOutput, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        public void Dispose()
        {
            _clientToServer.Writer.TryComplete();
            _serverToClient.Writer.TryComplete();
        }
    }

    private sealed class ChannelStream(ChannelReader<byte[]> reader, ChannelWriter<byte[]> writer) : Stream
    {
        private readonly ChannelReader<byte[]> _reader = reader;
        private readonly ChannelWriter<byte[]> _writer = writer;
        private byte[]? _current;
        private int _currentOffset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ReadCoreAsync(buffer, cancellationToken);
        private async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (buffer.Length == 0) return 0;
            while (_current is null || _currentOffset >= _current.Length)
            {
                try { _current = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false); }
                catch (ChannelClosedException) { return 0; }
                _currentOffset = 0;
            }
            var count = Math.Min(buffer.Length, _current.Length - _currentOffset);
            _current.AsMemory(_currentOffset, count).CopyTo(buffer);
            _currentOffset += count;
            return count;
        }
        public override void Write(byte[] buffer, int offset, int count)
            => WriteAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => WriteCoreAsync(buffer, cancellationToken);
        private async ValueTask WriteCoreAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            if (!buffer.IsEmpty) await _writer.WriteAsync(buffer.ToArray(), cancellationToken).ConfigureAwait(false);
        }
        public Task WriteBytesAsync(byte[] bytes, CancellationToken cancellationToken = default) => WriteCoreAsync(bytes, cancellationToken).AsTask();
        public void CompleteWrites() => _writer.TryComplete();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
