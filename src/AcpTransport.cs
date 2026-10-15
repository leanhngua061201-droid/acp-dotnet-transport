using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Runtime.ExceptionServices;

namespace Acp.Transport;

public sealed record AcpTransportOptions(TimeSpan RequestTimeout = default, int MaxMessageBytes = 1024 * 1024)
{
    public TimeSpan RequestTimeout { get; init; } = RequestTimeout == default ? TimeSpan.FromMinutes(1) : RequestTimeout;

    public AcpTransportOptions Validate()
    {
        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        if (MaxMessageBytes is < 1024 or > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaxMessageBytes));
        return this;
    }
}

public sealed record AcpInboundRequest(JsonElement Id, string Method, JsonElement? Params);

/// <summary>
/// Persistent JSON-RPC 2.0 transport for ACP stdio or another duplex byte stream.
/// One background reader owns the input; all writes are serialized. It supports
/// server notifications and server-to-client requests without coupling them to
/// task persistence or tool policy.
/// </summary>
public sealed class AcpTransport : IAsyncDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly AcpTransportOptions _options;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonDocument>> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _abandoned = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _abandonedOrder = new();
    private readonly ConcurrentDictionary<long, Task> _inboundRequests = new();
    private const int MaxAbandonedResponses = 1024;
    private const int MaxConcurrentInboundRequests = 32;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _stateGate = new();
    private readonly object _responseGate = new();
    private readonly Func<AcpInboundRequest, CancellationToken, Task<JsonElement>>? _requestHandler;
    private readonly Action<string, JsonElement?>? _notificationHandler;
    private readonly byte[] _inputBuffer = new byte[8192];
    private int _inputBufferOffset;
    private int _inputBufferCount;
    private Task? _readerTask;
    private long _nextId;
    private long _nextInboundId;
    private int _inboundCount;
    private Exception? _fault;
    private Exception? _cancellationError;
    private Task? _disposeTask;
    private TaskCompletionSource? _operationsDrained;
    private int _activeOperations;
    private bool _started;
    private bool _disposed;

    public AcpTransport(Stream input, Stream output, AcpTransportOptions? options = null,
        Func<AcpInboundRequest, CancellationToken, Task<JsonElement>>? requestHandler = null,
        Action<string, JsonElement?>? notificationHandler = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        if (!input.CanRead) throw new ArgumentException("ACP 输入流不可读。", nameof(input));
        if (!output.CanWrite) throw new ArgumentException("ACP 输出流不可写。", nameof(output));
        _options = (options ?? new AcpTransportOptions()).Validate();
        _input = input;
        _output = output;
        _requestHandler = requestHandler;
        _notificationHandler = notificationHandler;
    }

    public Task Completion => _readerTask ?? Task.CompletedTask;

    public void Start()
    {
        lock (_stateGate)
        {
            EnsureNotDisposed();
            if (_started) return;
            _started = true;
            _readerTask = Task.Run(ReadLoopAsync);
        }
    }

    public async Task<JsonDocument> RequestAsync(string method, object? parameters = null,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(method) || method.Length > 256 || method.Any(char.IsControl))
            throw new ArgumentException("ACP 方法名无效。", nameof(method));
        var requestTimeout = timeout ?? _options.RequestTimeout;
        if (requestTimeout <= TimeSpan.Zero || requestTimeout > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        using var operation = EnterOperation();
        cancellationToken.ThrowIfCancellationRequested();
        Start();
        EnsureHealthy();
        var id = Interlocked.Increment(ref _nextId);
        var key = "n:" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters });
        if (StrictUtf8.GetByteCount(request) > _options.MaxMessageBytes)
            throw new InvalidDataException("ACP 消息超过大小限制。");
        var completion = new TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(key, completion)) throw new InvalidOperationException("ACP 请求 ID 冲突。");
        var sent = false;
        var delivered = false;
        using var timeoutSource = new CancellationTokenSource(requestTimeout);
        // A frame is written atomically from the transport's perspective; caller cancellation
        // applies to waiting for the response, while timeout/lifetime still bound pipe I/O.
        using var writeLifetime = CancellationTokenSource.CreateLinkedTokenSource(timeoutSource.Token, _lifetime.Token);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token, _lifetime.Token);
        try
        {
            try { await WriteLineAsync(request, writeLifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException error)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool sendTimedOut;
                lock (_stateGate)
                    // A partial-write cancellation may already have faulted the lifetime.
                    // Preserve an earlier connection fault instead of relabeling it as a timeout.
                    sendTimedOut = timeoutSource.IsCancellationRequested
                        && (!_lifetime.IsCancellationRequested || ReferenceEquals(_fault, error));
                if (!sendTimedOut) throw;
                var timeoutError = new TimeoutException($"ACP 请求发送超时：{method}", error);
                Fault(timeoutError);
                throw timeoutError;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Fault(error);
                throw;
            }
            sent = true;
            try
            {
                var response = await completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
                delivered = true;
                return response;
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            { throw new TimeoutException($"ACP 请求超时：{method}"); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                Exception? fault;
                lock (_stateGate) fault = _fault;
                if (fault is not null) ExceptionDispatchInfo.Capture(fault).Throw();
                throw new ObjectDisposedException(nameof(AcpTransport));
            }
        }
        finally
        {
            // Make removal and late-response registration atomic with response dispatch.
            lock (_responseGate)
            {
                if (_pending.TryRemove(key, out _) && sent) MarkAbandoned(key);
                // A send failure can bypass awaiting the completion, and a response can race cancellation.
                if (completion.Task.IsFaulted) _ = completion.Task.Exception;
                else if (!delivered && completion.Task.IsCompletedSuccessfully) completion.Task.Result.Dispose();
            }
        }
    }

    public async Task NotifyAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(method) || method.Length > 256 || method.Any(char.IsControl))
            throw new ArgumentException("ACP 方法名无效。", nameof(method));
        using var operation = EnterOperation();
        Start();
        EnsureHealthy();
        var notification = JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = parameters });
        if (StrictUtf8.GetByteCount(notification) > _options.MaxMessageBytes)
            throw new InvalidDataException("ACP 消息超过大小限制。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try { await WriteLineAsync(notification, linked.Token).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Fault(error);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        Task? readerTask;
        Task drained;
        TaskCompletionSource completion;
        lock (_stateGate)
        {
            if (_disposeTask is not null) return new ValueTask(_disposeTask);
            _disposed = true;
            readerTask = _readerTask;
            drained = _activeOperations == 0 ? Task.CompletedTask
                : (_operationsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
        }
        _ = CompleteDisposalAsync(completion, readerTask, drained);
        return new ValueTask(completion.Task);
    }

    private async Task CompleteDisposalAsync(TaskCompletionSource completion, Task? readerTask, Task drained)
    {
        try
        {
            // Cancellation callbacks must run outside the state lock. All disposers share this completion.
            var cancellation = _lifetime.CancelAsync();
            FailPending(new ObjectDisposedException(nameof(AcpTransport)));
            Exception? callbackError = null;
            try { await cancellation.ConfigureAwait(false); }
            catch (Exception error) { callbackError = error; }
            if (readerTask is not null)
            {
                try { await readerTask.ConfigureAwait(false); }
                catch (Exception error) { callbackError ??= error; }
            }
            await drained.ConfigureAwait(false);
            _writeGate.Dispose();
            _lifetime.Dispose();
            lock (_stateGate) callbackError ??= _cancellationError;
            if (callbackError is not null) ExceptionDispatchInfo.Capture(callbackError).Throw();
            completion.TrySetResult();
        }
        catch (Exception error) { completion.TrySetException(error); }
    }

    private OperationRegistration EnterOperation()
    {
        lock (_stateGate)
        {
            EnsureNotDisposed();
            _activeOperations++;
            return new OperationRegistration(this);
        }
    }

    private void LeaveOperation()
    {
        lock (_stateGate)
            if (--_activeOperations == 0) _operationsDrained?.TrySetResult();
    }

    private sealed class OperationRegistration(AcpTransport owner) : IDisposable
    {
        private AcpTransport? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.LeaveOperation();
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                var line = await ReadBoundedLineAsync(_lifetime.Token).ConfigureAwait(false);
                using var document = JsonDocument.Parse(line);
                await DispatchAsync(document.RootElement.Clone()).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            Fault(error);
        }
    }

    private Task DispatchAsync(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("jsonrpc", out var version)
            || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0")
            throw new InvalidDataException("ACP 消息不是 JSON-RPC 2.0。");
        var hasId = message.TryGetProperty("id", out var id);
        var hasMethod = message.TryGetProperty("method", out var method);
        if (hasMethod)
        {
            if (method.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(method.GetString()))
                throw new InvalidDataException("ACP 方法名无效。");
            var methodName = method.GetString()!;
            if (methodName.Length > 256 || methodName.Any(char.IsControl))
                throw new InvalidDataException("ACP 方法名无效。");
            message.TryGetProperty("params", out var parameters);
            JsonElement? optionalParams = parameters.ValueKind == JsonValueKind.Undefined ? null : parameters.Clone();
            if (!hasId)
            {
                _notificationHandler?.Invoke(methodName, optionalParams);
                return Task.CompletedTask;
            }
            if (id.ValueKind == JsonValueKind.Number) _ = GetIdKey(id);
            else if (id.ValueKind != JsonValueKind.String || id.GetString() is not { Length: > 0 and <= 256 } text
                || text.Any(char.IsControl)) throw new InvalidDataException("ACP 请求 ID 无效。");
            QueueInboundRequest(new AcpInboundRequest(id.Clone(), methodName, optionalParams));
            return Task.CompletedTask;
        }
        if (!hasId) throw new InvalidDataException("ACP 消息缺少 method 或 id。");
        var key = GetIdKey(id);
        var hasError = message.TryGetProperty("error", out var error);
        var hasResult = message.TryGetProperty("result", out var result);
        if (hasError == hasResult) throw new InvalidDataException("ACP 响应必须且只能包含 result 或 error。");
        if (hasError && (error.ValueKind != JsonValueKind.Object
            || !error.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out _)
            || !error.TryGetProperty("message", out var errorMessage) || errorMessage.ValueKind != JsonValueKind.String))
            throw new InvalidDataException("ACP 错误响应格式无效。");
        lock (_responseGate)
        {
            if (!_pending.TryRemove(key, out var completion))
            {
                if (_abandoned.TryRemove(key, out _)) return Task.CompletedTask;
                throw new InvalidDataException("ACP 响应 ID 不匹配。");
            }
            if (hasError)
            {
                completion.TrySetException(new InvalidOperationException($"ACP 请求失败：{error.GetRawText()}"));
                return Task.CompletedTask;
            }
            completion.TrySetResult(JsonDocument.Parse(result.GetRawText()));
            return Task.CompletedTask;
        }
    }

    private async Task DispatchRequestAsync(AcpInboundRequest request)
    {
        JsonElement result;
        try
        {
            if (_requestHandler is null) throw new InvalidOperationException("No ACP reverse-request handler is registered.");
            result = await _requestHandler(request, _lifetime.Token).ConfigureAwait(false);
            await WriteResponseAsync(request.Id, result, null, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            var responseError = JsonSerializer.SerializeToElement(new { code = -32000, message = error.Message });
            await WriteResponseAsync(request.Id, default, responseError, _lifetime.Token).ConfigureAwait(false);
        }
    }

    private void QueueInboundRequest(AcpInboundRequest request)
    {
        var operation = EnterOperation();
        if (Interlocked.Increment(ref _inboundCount) > MaxConcurrentInboundRequests)
        {
            Interlocked.Decrement(ref _inboundCount);
            operation.Dispose();
            throw new InvalidDataException("ACP 并发反向请求超过限制。");
        }
        var id = Interlocked.Increment(ref _nextInboundId);
        var dispatch = Task.Run(() => DispatchRequestAsync(request));
        _inboundRequests[id] = dispatch;
        _ = ObserveInboundRequestAsync(id, dispatch, operation);
    }

    private async Task ObserveInboundRequestAsync(long id, Task dispatch, OperationRegistration operation)
    {
        try { await dispatch.ConfigureAwait(false); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { Fault(error); }
        finally
        {
            _inboundRequests.TryRemove(id, out _);
            Interlocked.Decrement(ref _inboundCount);
            operation.Dispose();
        }
    }

    private void MarkAbandoned(string key)
    {
        if (!_abandoned.TryAdd(key, 0)) return;
        _abandonedOrder.Enqueue(key);
        // Bound the FIFO itself: late responses may already have removed its dictionary entries.
        while (_abandonedOrder.Count > MaxAbandonedResponses && _abandonedOrder.TryDequeue(out var oldest))
            _abandoned.TryRemove(oldest, out _);
    }

    private async Task WriteResponseAsync(JsonElement id, JsonElement result, JsonElement? error, CancellationToken cancellationToken)
    {
        var payload = error is { } fault
            ? JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = fault })
            : JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result });
        await WriteLineAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteLineAsync(string payload, CancellationToken cancellationToken)
    {
        if (StrictUtf8.GetByteCount(payload) > _options.MaxMessageBytes)
            throw new InvalidDataException("ACP 消息超过大小限制。");
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureHealthy();
            // Write a complete byte frame directly: there is no buffered writer to flush during disposal.
            var frame = StrictUtf8.GetBytes(payload + "\n");
            await _output.WriteAsync(frame.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // A cancelled write may have emitted a partial frame. Never append another frame.
            // Cancellation while waiting for the gate happens above and leaves the connection usable.
            Fault(error);
            throw;
        }
        finally { _writeGate.Release(); }
    }

    private async Task<string> ReadBoundedLineAsync(CancellationToken cancellationToken)
    {
        using var bytes = new MemoryStream(Math.Min(_options.MaxMessageBytes, 4096));
        while (true)
        {
            if (_inputBufferOffset >= _inputBufferCount)
            {
                _inputBufferCount = await _input.ReadAsync(_inputBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                _inputBufferOffset = 0;
                if (_inputBufferCount == 0)
                {
                    if (bytes.Length == 0) throw new EndOfStreamException("ACP 对端已关闭连接。");
                    throw new InvalidDataException("ACP 对端关闭时消息尚未结束。");
                }
            }

            var newline = Array.IndexOf(_inputBuffer, (byte)'\n', _inputBufferOffset, _inputBufferCount - _inputBufferOffset);
            var segmentEnd = newline >= 0 ? newline : _inputBufferCount;
            var segmentLength = segmentEnd - _inputBufferOffset;
            if (bytes.Length + segmentLength > _options.MaxMessageBytes + 1L)
                throw new InvalidDataException("ACP 消息超过大小限制。");
            bytes.Write(_inputBuffer, _inputBufferOffset, segmentLength);
            _inputBufferOffset = newline >= 0 ? newline + 1 : _inputBufferCount;
            if (newline < 0) continue;

            var lineBytes = bytes.ToArray();
            var length = lineBytes.Length;
            if (length > 0 && lineBytes[length - 1] == (byte)'\r') length--;
            if (length > _options.MaxMessageBytes)
                throw new InvalidDataException("ACP 消息超过大小限制。");
            return StrictUtf8.GetString(lineBytes, 0, length);
        }
    }

    private void Fault(Exception error)
    {
        lock (_stateGate)
        {
            if (_disposed) return;
            if (_fault is null) _fault = error;
        }
        FailPending(error);
        try { _lifetime.Cancel(); }
        catch (AggregateException callbackError)
        {
            // Application callbacks must not interrupt the reader or bypass cleanup.
            // Disposal reports the callback error only after operations and resources drain.
            lock (_stateGate) _cancellationError ??= callbackError;
        }
    }

    private void FailPending(Exception error)
    {
        lock (_responseGate)
            foreach (var item in _pending.ToArray())
                if (_pending.TryRemove(item.Key, out var completion)) completion.TrySetException(error);
    }

    private void EnsureHealthy()
    {
        lock (_stateGate)
        {
            EnsureNotDisposed();
            if (!_started) throw new InvalidOperationException("ACP 传输尚未启动。");
            if (_fault is not null) throw new InvalidOperationException("ACP 传输已终止。", _fault);
        }
    }

    private void EnsureNotDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AcpTransport));
    }

    private static string GetIdKey(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.String => "s:" + id.GetString(),
        JsonValueKind.Number when id.TryGetInt64(out var number) => "n:" + number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => throw new InvalidDataException("ACP ID 必须是字符串或整数。")
    };
}
