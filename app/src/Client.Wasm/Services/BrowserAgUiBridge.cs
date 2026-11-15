using Microsoft.JSInterop;
using System.Collections.Concurrent;
using System.Text;

namespace AGUIWebChat.Client.Services;

public sealed class BrowserAgUiBridge(IJSRuntime js, ILogger<BrowserAgUiBridge> logger) : IAsyncDisposable
{
    private const int ChunkSize = 8192;
    private const int MaximumBytes = 256 * 1024;
    private Task<IJSObjectReference>? _moduleTask;
    private readonly Lock _moduleGate = new();
    private readonly ILogger<BrowserAgUiBridge> _logger = logger;
    private readonly ConcurrentDictionary<int, RequestLease> _requests = new();
    private bool _disposed;

    private Task<IJSObjectReference> ModuleAsync()
    {
        lock (_moduleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _moduleTask ??= ImportAsync();
        }
    }

    private async Task<IJSObjectReference> ImportAsync()
    {
        try { return await js.InvokeAsync<IJSObjectReference>("import", "./agui-transport.js"); }
        catch (JSException error)
        {
            _logger.LogWarning(error, "Browser AG-UI module could not load.");
            throw new ScenarioTransportException("Browser transport could not load. Reload this page.", "browser_module", true, 0);
        }
    }

    private async ValueTask<T> CallAsync<T>(IJSObjectReference module, string operation, params object?[] arguments)
    {
        try { return await module.InvokeAsync<T>(operation, arguments); }
        catch (JSDisconnectedException) { throw; }
        catch (JSException error)
        {
            _logger.LogWarning(error, "Browser AG-UI operation {Operation} failed.", operation);
            throw new ScenarioTransportException(error.Message, "browser_transport", true, 0);
        }
    }

    private async ValueTask CallAsync(IJSObjectReference module, string operation, params object?[] arguments)
    {
        try { await module.InvokeVoidAsync(operation, arguments); }
        catch (JSDisconnectedException) { throw; }
        catch (JSException error)
        {
            _logger.LogWarning(error, "Browser AG-UI operation {Operation} failed.", operation);
            throw new ScenarioTransportException(error.Message, "browser_transport", true, 0);
        }
    }

    internal async Task<string?> LoadCheckpointAsync(string route)
    {
        var module = await ModuleAsync();
        var info = await CallAsync<CheckpointMetadata>(module, "beginCheckpoint", route, true);
        if (info.Id == 0) return null;
        try
        {
            if (info.Length is < 0 or > MaximumBytes) throw new InvalidOperationException("Checkpoint exceeds its limit.");
            using var bytes = new MemoryStream(info.Length);
            while (bytes.Length < info.Length)
            {
                var chunk = await CallAsync<byte[]>(module, "readCheckpoint", info.Id);
                if (chunk.Length is 0 or > ChunkSize || bytes.Length + chunk.Length > info.Length)
                    throw new InvalidOperationException("Checkpoint chunks are invalid.");
                bytes.Write(chunk);
            }
            return new UTF8Encoding(false, true).GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
        }
        finally { await CallAsync(module, "endCheckpoint", info.Id); }
    }

    internal async Task SaveCheckpointAsync(string route, string content)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        if (bytes.Length > MaximumBytes)
            throw new ScenarioTransportException("Conversation exceeds the tab checkpoint limit. Start a new conversation.",
                "checkpoint_limit", true, 413);
        var module = await ModuleAsync();
        var info = await CallAsync<CheckpointMetadata>(module, "beginCheckpoint", route, false);
        try
        {
            for (int at = 0; at < bytes.Length; at += ChunkSize)
                await CallAsync(module, "appendCheckpoint", info.Id,
                    bytes.AsMemory(at, Math.Min(ChunkSize, bytes.Length - at)).ToArray());
            await CallAsync(module, "commitCheckpoint", info.Id);
        }
        finally { await CallAsync(module, "endCheckpoint", info.Id); }
    }

    internal async Task ClearCheckpointAsync(string route) =>
        await CallAsync(await ModuleAsync(), "clear", route);

    internal async Task<HttpResponseMessage> SendAsync(string path, byte[] body, CancellationToken cancellation)
    {
        if (body.Length > MaximumBytes)
            throw new ScenarioTransportException("Agent request exceeds the browser transport limit. Start a new conversation.",
                "browser_request_limit", true, 413);
        cancellation.ThrowIfCancellationRequested();
        var module = await ModuleAsync();
        int id = await CallAsync<int>(module, "create", path);
        var lease = new RequestLease(this, module, id);
        _requests[id] = lease;
        var stream = new BrowserResponseStream(lease, cancellation);
        try
        {
            for (int at = 0; at < body.Length; at += ChunkSize)
            {
                cancellation.ThrowIfCancellationRequested();
                await CallAsync(module, "append", id, body.AsMemory(at, Math.Min(ChunkSize, body.Length - at)).ToArray());
            }
            var meta = await CallAsync<ResponseMetadata>(module, "start", id);
            cancellation.ThrowIfCancellationRequested();
            return new HttpResponseMessage((System.Net.HttpStatusCode)meta.Status)
            {
                Content = new StreamContent(stream)
                {
                    Headers = { ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(meta.ContentType ?? "application/octet-stream") }
                }
            };
        }
        catch
        {
            await stream.DisposeAsync();
            if (cancellation.IsCancellationRequested) throw new OperationCanceledException(cancellation);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var request in _requests.Values) await request.AbortAsync();
        if (_moduleTask is null) return;
        try { await (await _moduleTask).DisposeAsync(); }
        catch (JSDisconnectedException) { _logger.LogDebug("Browser disconnected while releasing AG-UI transport."); }
    }

    private sealed class ResponseMetadata
    {
        public int Status { get; set; }
        public string? ContentType { get; set; }
    }

    private sealed class CheckpointMetadata
    {
        public int Id { get; set; }
        public int Length { get; set; }
    }

    private sealed class RequestLease(BrowserAgUiBridge owner, IJSObjectReference module, int id)
    {
        private readonly Lock _gate = new();
        private Task? _abort;
        public ValueTask<byte[]> ReadAsync() => owner.CallAsync<byte[]>(module, "read", id);
        public Task AbortAsync()
        {
            lock (_gate) return _abort ??= AbortCoreAsync();
        }
        private async Task AbortCoreAsync()
        {
            try { await owner.CallAsync(module, "abort", id); }
            catch (JSDisconnectedException) { owner._logger.LogDebug("Browser disconnected while releasing an AG-UI request."); }
            finally { owner._requests.TryRemove(id, out _); }
        }
    }

    private sealed class BrowserResponseStream : Stream
    {
        private readonly RequestLease _lease;
        private readonly CancellationToken _cancellation;
        private readonly CancellationTokenRegistration _registration;
        private byte[] _pending = [];
        private int _offset;
        private int _frameBytes;
        private int _lineBytes;
        private bool _disposed;

        public BrowserResponseStream(RequestLease lease, CancellationToken cancellation)
        {
            _lease = lease; _cancellation = cancellation;
            _registration = cancellation.Register(() => _ = lease.AbortAsync());
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _cancellation.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.Length == 0) return 0;
            if (_offset == _pending.Length)
            {
                try { _pending = await _lease.ReadAsync(); }
                catch (ScenarioTransportException) when (_cancellation.IsCancellationRequested)
                { throw new OperationCanceledException(_cancellation); }
                _cancellation.ThrowIfCancellationRequested();
                if (_pending.Length > ChunkSize) throw new InvalidOperationException("Browser stream chunk exceeds its limit.");
                _offset = 0;
            }
            int count = Math.Min(buffer.Length, _pending.Length - _offset);
            foreach (byte value in _pending.AsSpan(_offset, count))
            {
                if (++_frameBytes > MaximumBytes)
                    throw new ScenarioTransportException("An agent event exceeds the stream limit. Start a new conversation.",
                        "browser_event_limit", true, 413);
                if (value == '\n')
                {
                    if (_lineBytes == 0) _frameBytes = 0;
                    _lineBytes = 0;
                }
                else if (value != '\r') _lineBytes++;
            }
            _pending.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask DisposeAsync()
        {
            if (!_disposed) { _disposed = true; _registration.Dispose(); }
            await _lease.AbortAsync();
            GC.SuppressFinalize(this);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed) { _disposed = true; _registration.Dispose(); _ = _lease.AbortAsync(); }
            base.Dispose(disposing);
        }
        public override bool CanRead => !_disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
