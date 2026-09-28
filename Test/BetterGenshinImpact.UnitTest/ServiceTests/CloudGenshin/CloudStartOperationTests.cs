using BetterGenshinImpact.Service.CloudGenshin;

namespace BetterGenshinImpact.UnitTest.ServiceTests.CloudGenshin;

/// <summary>只验证生产使用的启动协调器，不初始化 WPF、App 或真实浏览器。</summary>
public class CloudStartOperationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task CancelDuringPreparationWaitsForOldMacroAndDoesNotStartBrowser()
    {
        using var semaphore = new SemaphoreSlim(1, 1);
        using var operation = Acquire(semaphore);
        var preparing = Signal();
        var released = Signal();
        var started = 0;
        var stopped = 0;
        var cleaned = 0;
        var run = operation.RunAsync(
            async () => { preparing.TrySetResult(); await released.Task; },
            _ => { started++; return Task.CompletedTask; },
            () => false,
            () => { stopped++; return Task.CompletedTask; },
            ended => operation.CleanupAsync(() =>
            {
                Assert.True(ended);
                cleaned++;
                return Task.CompletedTask;
            }));
        try
        {
            await preparing.Task.WaitAsync(Timeout);
            operation.RequestStop();
            Assert.False(run.IsCompleted);
            Assert.Equal(0, semaphore.CurrentCount);
            Assert.Equal(0, started);

            released.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Timeout));
            Assert.Equal(0, started);
            Assert.Equal(1, stopped);
            Assert.Equal(1, cleaned);
            Assert.False(operation.IsPreparing);
            Assert.Equal(1, semaphore.CurrentCount);
        }
        finally
        {
            released.TrySetResult();
            await ObserveFailureAsync(run);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreparationOrStartupFailureAlwaysStopsAndCleans(bool failInPreparation)
    {
        using var semaphore = new SemaphoreSlim(1, 1);
        using var operation = Acquire(semaphore);
        var failure = new IOException("准备或监听建立失败");
        var stopped = 0;
        var cleaned = 0;

        var error = await Assert.ThrowsAsync<IOException>(() => operation.RunAsync(
            () => failInPreparation ? Task.FromException(failure) : Task.CompletedTask,
            _ => failInPreparation ? Task.CompletedTask : Task.FromException(failure),
            () => false,
            () => { stopped++; return Task.CompletedTask; },
            ended => operation.CleanupAsync(() =>
            {
                Assert.True(ended);
                cleaned++;
                return Task.CompletedTask;
            })).WaitAsync(Timeout));

        Assert.Same(failure, error);
        Assert.Equal(1, stopped);
        Assert.Equal(1, cleaned);
        Assert.False(operation.IsPreparing);
        Assert.Equal(1, semaphore.CurrentCount);
    }

    [Fact]
    public async Task StopFailureCannotSkipCleanupOrKeepTheTaskLock()
    {
        using var semaphore = new SemaphoreSlim(1, 1);
        using var operation = Acquire(semaphore);
        var failure = new IOException("停止浏览器失败");
        var cleaned = 0;

        var error = await Assert.ThrowsAsync<IOException>(() => operation.RunAsync(
            () => Task.CompletedTask,
            _ => Task.CompletedTask,
            () => false,
            () => Task.FromException(failure),
            ended => operation.CleanupAsync(() =>
            {
                Assert.True(ended);
                cleaned++;
                return Task.CompletedTask;
            })).WaitAsync(Timeout));

        Assert.Same(failure, error);
        Assert.Equal(1, cleaned);
        Assert.Equal(1, semaphore.CurrentCount);
    }

    [Fact]
    public async Task CompletionFailureStillReleasesTheTaskLock()
    {
        using var semaphore = new SemaphoreSlim(1, 1);
        using var operation = Acquire(semaphore);
        var failure = new IOException("本地收尾失败");

        await Assert.ThrowsAsync<IOException>(() => operation.RunAsync(
            () => Task.CompletedTask,
            _ => Task.CompletedTask,
            () => false,
            () => Task.CompletedTask,
            _ => Task.FromException(failure)).WaitAsync(Timeout));

        Assert.Equal(1, semaphore.CurrentCount);
    }

    [Fact]
    public async Task SuccessfulEntryKeepsTheLeaseUntilTheSessionEnds()
    {
        using var semaphore = new SemaphoreSlim(1, 1);
        using var operation = Acquire(semaphore);
        var stopped = 0;
        await operation.RunAsync(
            () => Task.CompletedTask,
            _ => Task.CompletedTask,
            () => true,
            () => { stopped++; return Task.CompletedTask; },
            ended =>
            {
                Assert.False(ended);
                return Task.CompletedTask;
            }).WaitAsync(Timeout);

        Assert.Equal(0, stopped);
        Assert.False(operation.IsPreparing);
        Assert.Equal(0, semaphore.CurrentCount);
        Assert.Null(CloudStartOperation.TryAcquire(semaphore));

        operation.MarkEnded();
        await operation.CleanupAsync(() => Task.CompletedTask).WaitAsync(Timeout);
        Assert.True(operation.HasEnded);
        Assert.Equal(1, semaphore.CurrentCount);
        using var next = Acquire(semaphore);
    }

    [Fact]
    public async Task EndedNotificationDuringStartupCannotLeaveAReadySessionLease()
    {
        using var semaphore = new SemaphoreSlim(1, 1);
        using var operation = Acquire(semaphore);
        var entered = Signal();
        var release = Signal();
        var stopped = 0;
        var run = operation.RunAsync(
            () => Task.CompletedTask,
            async _ => { entered.TrySetResult(); await release.Task; },
            () => true,
            () => { stopped++; return Task.CompletedTask; },
            ended => operation.CleanupAsync(() =>
            {
                Assert.True(ended);
                return Task.CompletedTask;
            }));
        try
        {
            await entered.Task.WaitAsync(Timeout);
            operation.MarkEnded();
            release.TrySetResult();
            await run.WaitAsync(Timeout);
            Assert.Equal(1, stopped);
            Assert.Equal(1, semaphore.CurrentCount);
        }
        finally
        {
            release.TrySetResult();
            await ObserveFailureAsync(run);
        }
    }

    [Fact]
    public async Task DuplicateCleanupWaitsForTheSameWorkBeforeReleasingTheLease()
    {
        using var semaphore = new SemaphoreSlim(1, 1);
        using var operation = Acquire(semaphore);
        var entered = Signal();
        var release = Signal();
        var calls = 0;
        var first = operation.CleanupAsync(async () =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task;
        });
        try
        {
            await entered.Task.WaitAsync(Timeout);
            operation.RequestStop();
            operation.MarkEnded();
            var duplicate = operation.CleanupAsync(() => throw new InvalidOperationException("不得重复清理"));
            Assert.Same(first, duplicate);
            Assert.False(duplicate.IsCompleted);
            Assert.Equal(0, semaphore.CurrentCount);

            release.TrySetResult();
            await Task.WhenAll(first, duplicate).WaitAsync(Timeout);
            Assert.Equal(1, calls);
            Assert.Equal(1, semaphore.CurrentCount);
            operation.Dispose();
            operation.RequestStop();
            Assert.True(operation.Token.IsCancellationRequested);
            Assert.Equal(1, semaphore.CurrentCount);
        }
        finally
        {
            release.TrySetResult();
            await ObserveFailureAsync(first);
        }
    }

    [Fact]
    public async Task CleanupFailureIsSharedAndDisposalRemainsIdempotent()
    {
        using var semaphore = new SemaphoreSlim(1, 1);
        using var operation = Acquire(semaphore);
        var failure = new IOException("清理失败");
        var cleanup = operation.CleanupAsync(() => Task.FromException(failure));
        Assert.Same(cleanup, operation.CleanupAsync(() => Task.CompletedTask));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => cleanup.WaitAsync(Timeout)));
        operation.Dispose();
        operation.RequestStop();
        Assert.Equal(1, semaphore.CurrentCount);
    }

    private static CloudStartOperation Acquire(SemaphoreSlim semaphore)
        => Assert.IsType<CloudStartOperation>(CloudStartOperation.TryAcquire(semaphore));

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task ObserveFailureAsync(Task task)
    {
        try { await task.WaitAsync(Timeout); }
        catch (Exception ex) when (ex is not TimeoutException) { }
    }
}
