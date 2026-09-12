using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.Service.CloudGenshin.Browser;

/// <summary>一个页面一个连接；所有响应由唯一接收循环分派。</summary>
public sealed class CdpClient : IAsyncDisposable
{
    private const int MaxMessageBytes = 32 * 1024 * 1024;
    private readonly ClientWebSocket _socket = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JObject>> _pending = new();
    private Task _receiveTask = Task.CompletedTask;
    private long _nextId;
    private int _disposed;
    private readonly object _lifecycleLock = new();
    private int _activeOperations;
    private TaskCompletionSource? _operationsDrained;
    private Task? _disposeTask;

    private void BeginOperation()
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _activeOperations++;
        }
    }

    private void EndOperation()
    {
        lock (_lifecycleLock)
        {
            if (--_activeOperations == 0) _operationsDrained?.TrySetResult();
        }
    }

    public bool IsConnected => Volatile.Read(ref _disposed) == 0 && _socket.State == WebSocketState.Open;

    public static bool IsLoopbackEndpoint(Uri uri)
    {
        return uri.Scheme == "ws" && IPAddress.TryParse(uri.Host, out var address)
               && IPAddress.IsLoopback(address) && uri.Port > 0
               && string.IsNullOrEmpty(uri.UserInfo);
    }

    public async Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        if (!IsLoopbackEndpoint(endpoint))
            throw new ArgumentException("云游戏调试端点必须是本机回环地址。", nameof(endpoint));
        BeginOperation();
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            linked.CancelAfter(TimeSpan.FromSeconds(15));
            await _socket.ConnectAsync(endpoint, linked.Token).ConfigureAwait(false);
            _receiveTask = ReceiveAsync();
        }
        finally { EndOperation(); }
    }

    public async Task<JObject> SendAsync(string method, JObject? parameters, CancellationToken cancellationToken)
    {
        BeginOperation();
        try { return await SendCoreAsync(method, parameters, cancellationToken).ConfigureAwait(false); }
        finally { EndOperation(); }
    }

    private async Task<JObject> SendCoreAsync(string method, JObject? parameters, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!IsConnected) throw new IOException("云游戏浏览器调试连接已断开。 ");
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        linked.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var request = new JObject { ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JObject() };
            var bytes = Encoding.UTF8.GetBytes(request.ToString(Formatting.None));
            await _sendLock.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, linked.Token).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
            return await completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw new TimeoutException($"浏览器命令 {method} 超时。");
        }
        finally
        {
            if (_pending.TryRemove(id, out var pending)) pending.TrySetCanceled();
            // 取消与断线同时发生时也观察已经完成的失败响应。
            if (completion.Task.IsFaulted) _ = completion.Task.Exception;
        }
    }

    private async Task ReceiveAsync()
    {
        Exception failure = new IOException("云游戏浏览器连接已关闭。");
        try
        {
            var buffer = new byte[64 * 1024];
            using var message = new MemoryStream();
            while (!_lifetime.IsCancellationRequested)
            {
                var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), _lifetime.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.MessageType != WebSocketMessageType.Text) throw new IOException("浏览器返回了非文本调试消息。");
                if (message.Length + result.Count > MaxMessageBytes) throw new IOException("浏览器调试消息超出大小限制。");
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                var payload = JObject.Parse(Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length)));
                message.SetLength(0);
                if (payload["id"]?.Value<long>() is not { } id || !_pending.TryRemove(id, out var completion)) continue;
                if (payload["error"] is JObject error)
                    completion.TrySetException(new IOException($"浏览器命令失败：{error["message"]?.Value<string>()}"));
                else
                    completion.TrySetResult(payload["result"] as JObject ?? new JObject());
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or JsonException or OperationCanceledException or ObjectDisposedException)
        {
            failure = ex;
        }
        finally
        {
            _socket.Abort();
            foreach (var pair in _pending)
                if (_pending.TryRemove(pair.Key, out var completion)) completion.TrySetException(failure);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleLock)
        {
            if (_disposeTask != null) return new ValueTask(_disposeTask);
            _disposed = 1;
            var drained = Task.CompletedTask;
            if (_activeOperations > 0)
            {
                _operationsDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                drained = _operationsDrained.Task;
            }
            _disposeTask = DisposeCoreAsync(drained);
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync(Task drained)
    {
        // 异步跳出 lifecycleLock，取消回调不能在状态锁中执行。
        await Task.Yield();
        _lifetime.Cancel();
        _socket.Abort();
        await drained.ConfigureAwait(false);
        try { await _receiveTask.ConfigureAwait(false); }
        finally
        {
            _socket.Dispose();
            _sendLock.Dispose();
            _lifetime.Dispose();
        }
    }
}
