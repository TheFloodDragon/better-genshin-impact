using System.Collections.Concurrent;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.Common;

namespace BetterGenshinImpact.UnitTest.GameTaskTests;

public class CloudMacroWaitTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Watch(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10));

    [Theory]
    [InlineData("pause")]
    [InlineData("focus")]
    [InlineData("sleep")]
    [InlineData("delay")]
    [InlineData("retry")]
    public async Task ForceStopWakesEveryMacroWait(string waitKind)
    {
        var errors = new ConcurrentQueue<Exception>();
        var runner = new MacroRunner(errors.Enqueue);
        var waiting = Signal();
        var paused = 1;
        var focusLost = 1;
        var pauseReferences = 4; // 模拟其他对象已经拥有的暂停引用。
        var continued = false;
        var execution = runner.KeyDown(MacroMode.HoldFinish, async _ =>
        {
            switch (waitKind)
            {
                case "pause":
                    MacroExecutionScope.WaitWhile(() => Volatile.Read(ref paused) != 0,
                        enter: () => { Interlocked.Increment(ref pauseReferences); waiting.TrySetResult(); },
                        exit: () => Interlocked.Decrement(ref pauseReferences), intervalMilliseconds: 30_000);
                    break;
                case "focus":
                    MacroExecutionScope.WaitWhile(() => Volatile.Read(ref focusLost) != 0,
                        step: () => waiting.TrySetResult(), intervalMilliseconds: 30_000);
                    break;
                case "sleep":
                    waiting.TrySetResult();
                    MacroExecutionScope.Sleep(TimeSpan.FromSeconds(30));
                    break;
                case "delay":
                    waiting.TrySetResult();
                    await MacroExecutionScope.Delay(30_000, CancellationToken.None);
                    break;
                case "retry":
                    NewRetry.Do(() =>
                    {
                        waiting.TrySetResult();
                        throw new RetryException("永久失焦");
                    }, TimeSpan.FromSeconds(30), 3);
                    break;
            }
            continued = true;
        })!;
        try
        {
            await Watch(waiting.Task);
            await Watch(runner.SuspendForCloudAsync());
            Assert.True(execution.Completion.IsCompletedSuccessfully);
            Assert.False(continued);
            Assert.Equal(1, paused); // 宏不能修改全局暂停状态。
            Assert.Equal(1, focusLost);
            Assert.Equal(4, pauseReferences);
            Assert.Empty(errors);
        }
        finally
        {
            // 看门狗只负责断言；释放假环境并真正 join，不遗留后台宏。
            Volatile.Write(ref paused, 0);
            Volatile.Write(ref focusLost, 0);
            await runner.SuspendForCloudAsync();
        }
    }

    [Fact]
    public async Task ScopeIsInheritedByChildAndRestoredWithoutAffectingParallelWork()
    {
        using var force = new CancellationTokenSource();
        using var unrelatedCancellation = new CancellationTokenSource();
        var outsideStarted = Signal();
        var outside = Task.Run(async () =>
        {
            Assert.False(MacroExecutionScope.IsActive);
            outsideStarted.TrySetResult();
            await MacroExecutionScope.Delay(Timeout.Infinite, unrelatedCancellation.Token);
        });
        try
        {
            await Watch(outsideStarted.Task);
            Assert.False(MacroExecutionScope.IsActive);
            using (new MacroExecutionScope(force.Token))
            {
                var insideStarted = Signal();
                var inside = Task.Run(async () =>
                {
                    Assert.Equal(force.Token, MacroExecutionScope.Token);
                    insideStarted.TrySetResult();
                    await MacroExecutionScope.Delay(30_000, CancellationToken.None);
                });
                try
                {
                    await Watch(insideStarted.Task);
                    force.Cancel();
                    await Assert.ThrowsAsync<NormalEndException>(() => Watch(inside));
                }
                finally
                {
                    force.Cancel();
                    try { await inside; } catch (NormalEndException) { }
                }
                Assert.False(outside.IsCompleted);
                Assert.False(unrelatedCancellation.IsCancellationRequested);
            }
            Assert.False(MacroExecutionScope.IsActive);
            Assert.Equal(CancellationToken.None, MacroExecutionScope.Token);
            MacroExecutionScope.Checkpoint();
        }
        finally
        {
            unrelatedCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => outside);
        }
    }

    [Fact]
    public void NestedScopeRestoresPreviousTokenEvenAfterCancellation()
    {
        using var outer = new CancellationTokenSource();
        using var inner = new CancellationTokenSource();
        using (new MacroExecutionScope(outer.Token))
        {
            using (new MacroExecutionScope(inner.Token))
            {
                inner.Cancel();
                Assert.Throws<NormalEndException>(MacroExecutionScope.Checkpoint);
            }
            Assert.Equal(outer.Token, MacroExecutionScope.Token);
            MacroExecutionScope.Checkpoint();
        }
        Assert.False(MacroExecutionScope.IsActive);
    }

    [Fact]
    public async Task ScopedWaitNormalizesExternalCancellationWithoutAggregateException()
    {
        using var force = new CancellationTokenSource();
        using var external = new CancellationTokenSource();
        using var scope = new MacroExecutionScope(force.Token);
        external.Cancel();
        Assert.Throws<NormalEndException>(() => MacroExecutionScope.Sleep(TimeSpan.FromSeconds(30), external.Token));
        await Assert.ThrowsAsync<NormalEndException>(() => MacroExecutionScope.Delay(30_000, external.Token));
        Assert.False(force.IsCancellationRequested);
    }

    [Fact]
    public async Task OutsideScopeKeepsOriginalWaitAndRetryBehavior()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.False(MacroExecutionScope.IsActive);
        MacroExecutionScope.Sleep(TimeSpan.Zero, cancelled.Token); // 原 Thread.Sleep 不使用此令牌。
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MacroExecutionScope.Delay(1, cancelled.Token));
        var attempts = 0;
        var result = NewRetry.Do(() =>
        {
            if (++attempts < 3) throw new RetryException("重试");
            return 7;
        }, TimeSpan.Zero);
        Assert.Equal(7, result);
        Assert.Equal(3, attempts);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("body failure")]
    [InlineData("down failure")]
    public void HoldAlwaysReleasesOnCancellationOrFailure(string outcome)
    {
        using var force = new CancellationTokenSource();
        using var scope = new MacroExecutionScope(force.Token);
        var events = new List<string>();
        var error = Record.Exception(() => MacroExecutionScope.Hold(
            () =>
            {
                events.Add("down");
                if (outcome == "down failure") throw new InvalidOperationException("按下失败");
            },
            () =>
            {
                if (outcome == "cancel")
                {
                    force.Cancel();
                    MacroExecutionScope.Sleep(TimeSpan.FromSeconds(30));
                }
                else throw new InvalidOperationException("动作失败");
            },
            () => events.Add("up")));
        if (outcome == "cancel") Assert.IsType<NormalEndException>(error);
        else Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(new[] { "down", "up" }, events);
    }

    [Fact]
    public void AlreadyCancelledHoldDoesNotSendInput()
    {
        using var force = new CancellationTokenSource();
        force.Cancel();
        using var scope = new MacroExecutionScope(force.Token);
        var inputCount = 0;
        Assert.Throws<NormalEndException>(() => MacroExecutionScope.Hold(
            () => inputCount++, () => { }, () => inputCount++));
        Assert.Equal(0, inputCount);
    }
}
