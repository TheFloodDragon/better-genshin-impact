using System;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.Service.CloudGenshin;

/// <summary>一次云启动持有的任务锁与取消状态，不依赖 WPF 或应用单例。</summary>
internal sealed class CloudStartOperation : IDisposable
{
    private readonly SemaphoreSlim _semaphore;
    private readonly CancellationTokenSource _cancellation = new();
    private int _preparing = 1;
    private int _ended;
    private readonly object _cleanupLock = new();
    private Task? _cleanupTask;
    private int _disposed;

    private CloudStartOperation(SemaphoreSlim semaphore)
    {
        _semaphore = semaphore;
        Token = _cancellation.Token;
    }

    // 缓存 token，收尾释放 CTS 后仍可安全检查本次启动是否已取消。
    public CancellationToken Token { get; }
    public bool IsPreparing => Volatile.Read(ref _preparing) != 0;
    public bool HasEnded => Volatile.Read(ref _ended) != 0;

    public static CloudStartOperation? TryAcquire(SemaphoreSlim semaphore)
    {
        ArgumentNullException.ThrowIfNull(semaphore);
        if (!semaphore.Wait(0)) return null;
        try { return new CloudStartOperation(semaphore); }
        catch { semaphore.Release(); throw; }
    }

    public void RequestStop()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        // 不持有锁运行取消回调；完成路径可能同时释放 CTS。
        try { _cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public void MarkEnded() => Volatile.Write(ref _ended, 1);

    public Task CleanupAsync(Func<Task> cleanup)
    {
        lock (_cleanupLock)
        {
            // 重复的停止/Ended 通知必须等待同一收尾，不能提前释放任务锁。
            return _cleanupTask ??= Task.Run(async () =>
            {
                try { await cleanup().ConfigureAwait(false); }
                finally { Dispose(); }
            });
        }
    }

    public async Task RunAsync(
        Func<Task> prepare,
        Func<CancellationToken, Task> start,
        Func<bool> isReady,
        Func<Task> stop,
        Func<bool, Task> complete)
    {
        var ready = false;
        try
        {
            // 即使用户取消启动，也必须等旧宏真正退出，不能取消 join 后恢复桌面输入。
            await prepare().ConfigureAwait(false);
            Token.ThrowIfCancellationRequested();
            await start(Token).ConfigureAwait(false);
            ready = isReady();
        }
        finally
        {
            var ended = !ready || Token.IsCancellationRequested || HasEnded;
            try
            {
                if (ended) await stop().ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref _preparing, 0);
                // 准备失败、取消、服务停止抛异常均必须进入本地收尾。
                var completed = false;
                try
                {
                    await complete(ended).ConfigureAwait(false);
                    completed = true;
                }
                finally
                {
                    if (ended || !completed) Dispose();
                }
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _cancellation.Dispose(); }
        finally { _semaphore.Release(); }
    }
}
