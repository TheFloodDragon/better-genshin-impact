using System.Collections.Concurrent;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.Common;

namespace BetterGenshinImpact.UnitTest.GameTaskTests;

// 仅调用生产宏内核；不构造 App、TaskControl、Avatar 或真实输入/截图环境。
public class CloudMacroLifecycleTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Watch(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    public async Task OrdinaryStopPreservesAllThreeModes(int modeValue, int expectedCommands)
    {
        var errors = new ConcurrentQueue<Exception>();
        var runner = new MacroRunner(errors.Enqueue);
        var firstCommand = Signal();
        using var finishCommand = new ManualResetEventSlim();
        var executed = new List<int>();
        var mode = (MacroMode)modeValue;
        var execution = runner.KeyDown(mode, current =>
        {
            current.RunRounds(new[] { 1, 2 }, () => true, (command, _) =>
            {
                executed.Add(command);
                if (command == 1)
                {
                    firstCommand.TrySetResult();
                    if (!finishCommand.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("假宏回调等待测试放行超时");
                }
            });
            return Task.CompletedTask;
        })!;
        try
        {
            await Watch(firstCommand.Task);
            runner.KeyUp();
            if (mode == MacroMode.Toggle)
            {
                Assert.False(execution.StopRequested); // 触发模式松键不停止。
                Assert.Null(runner.KeyDown(mode, _ => throw new InvalidOperationException("不能启动第二个宏")));
            }
            Assert.True(execution.StopToken.IsCancellationRequested);
            Assert.False(execution.ForceToken.IsCancellationRequested);
            Assert.False(execution.Completion.IsCompleted); // 不能取消当前动作。
            finishCommand.Set();
            await Watch(execution.Completion);
            Assert.Equal(Enumerable.Range(1, expectedCommands), executed);
            Assert.Empty(errors);
        }
        finally
        {
            finishCommand.Set();
            await runner.SuspendForCloudAsync();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CloudStopReleasesKeyboardAndMouseBeforeCompletion(int modeValue)
    {
        var errors = new ConcurrentQueue<Exception>();
        var runner = new MacroRunner(errors.Enqueue);
        var pressed = Signal();
        var releasing = Signal();
        using var finishRelease = new ManualResetEventSlim();
        var events = new ConcurrentQueue<string>();
        var execution = runner.KeyDown((MacroMode)modeValue, async current =>
        {
            current.InputDown("key:E", () => events.Enqueue("key down"), () =>
            {
                events.Enqueue("key up");
                releasing.TrySetResult();
                if (!finishRelease.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("假宏回调等待测试放行超时");
            });
            current.InputDown("mouse:left", () => events.Enqueue("mouse down"), () => events.Enqueue("mouse up"));
            pressed.TrySetResult();
            await MacroExecutionScope.Delay(30_000, CancellationToken.None);
            events.Enqueue("stale input");
        })!;
        try
        {
            await Watch(pressed.Task);
            var suspended = runner.SuspendForCloudAsync();
            await Watch(releasing.Task);
            Assert.False(suspended.IsCompleted);
            Assert.False(execution.Completion.IsCompleted);
            Assert.True(runner.IsCloudSuspended);
            Assert.Null(runner.KeyDown(MacroMode.Hold, _ => Task.CompletedTask));
            runner.ResumeAfterCloud();
            Assert.True(runner.IsCloudSuspended); // 不允许提前恢复本地输入。
            finishRelease.Set();
            await Watch(suspended);
            events.Enqueue("cloud allowed");
            Assert.Equal(new[] { "key down", "mouse down", "key up", "mouse up", "cloud allowed" }, events);
            runner.ResumeAfterCloud();
            Assert.False(runner.IsCloudSuspended);
            var next = runner.KeyDown(MacroMode.Hold, _ => Task.CompletedTask);
            Assert.NotNull(next);
            await Watch(next.Completion);
            Assert.DoesNotContain("stale input", events);
            Assert.Empty(errors);
        }
        finally
        {
            finishRelease.Set();
            await runner.SuspendForCloudAsync();
        }
    }

    [Fact]
    public async Task CloudStopJoinsOverwrittenMacroAndSkillCheckDescendants()
    {
        var errors = new ConcurrentQueue<Exception>();
        var runner = new MacroRunner(errors.Enqueue);
        var oldStarted = Signal();
        var finishOld = Signal();
        var childStarted = Signal();
        using var finishChild = new ManualResetEventSlim();
        var grandchildStarted = Signal();
        using var finishGrandchild = new ManualResetEventSlim();
        var old = runner.KeyDown(MacroMode.HoldFinish, async _ =>
        {
            oldStarted.TrySetResult();
            await finishOld.Task; // 模拟在途的不可取消底层动作。
        })!;
        try
        {
            await Watch(oldStarted.Task);
            runner.KeyUp();
            var latest = runner.KeyDown(MacroMode.Hold, current =>
            {
                // ESkillCdTracker 使用同一个 RunChild 入口，根执行先结束也不能漏掉它。
                _ = MacroExecutionScope.RunChild(() =>
                {
                    childStarted.TrySetResult();
                    if (!finishChild.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("假宏回调等待测试放行超时");
                    _ = MacroExecutionScope.RunChild(() =>
                    {
                        grandchildStarted.TrySetResult();
                        if (!finishGrandchild.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("假宏回调等待测试放行超时");
                    });
                });
                return Task.CompletedTask;
            })!;
            await Watch(childStarted.Task);
            var suspended = runner.SuspendForCloudAsync();
            Assert.False(suspended.IsCompleted);
            finishChild.Set();
            await Watch(grandchildStarted.Task);
            Assert.False(latest.Completion.IsCompleted);
            finishGrandchild.Set();
            await Watch(latest.Completion);
            Assert.False(old.Completion.IsCompleted);
            Assert.False(suspended.IsCompleted); // 最后一个宏结束不代表全部结束。
            finishOld.TrySetResult();
            await Watch(suspended);
            Assert.True(old.Completion.IsCompletedSuccessfully);
            Assert.Empty(errors);
        }
        finally
        {
            finishChild.Set();
            finishGrandchild.Set();
            finishOld.TrySetResult();
            await runner.SuspendForCloudAsync();
        }
    }

    [Fact]
    public async Task PausedPreparationDoesNotHoldLifecycleLock()
    {
        var runner = new MacroRunner();
        var preparing = Signal();
        using var finishPreparation = new ManualResetEventSlim();
        var execution = runner.KeyDown(MacroMode.Hold, _ =>
        {
            preparing.TrySetResult();
            if (!finishPreparation.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("假宏回调等待测试放行超时");
            MacroExecutionScope.Checkpoint();
            return Task.CompletedTask;
        })!;
        try
        {
            await Watch(preparing.Task);
            var suspended = runner.SuspendForCloudAsync();
            Assert.True(runner.IsCloudSuspended);
            Assert.True(execution.ForceToken.IsCancellationRequested);
            Assert.False(suspended.IsCompleted);
            finishPreparation.Set();
            await Watch(suspended);
        }
        finally
        {
            finishPreparation.Set();
            await runner.SuspendForCloudAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MacroEntryTreatsCancellationAsNormalCompletion(bool normalEnd)
    {
        var errors = new ConcurrentQueue<Exception>();
        var runner = new MacroRunner(errors.Enqueue);
        var execution = runner.KeyDown(MacroMode.Hold, _ =>
        {
            if (normalEnd) throw new NormalEndException("取消");
            throw new OperationCanceledException();
        })!;
        try
        {
            await Watch(execution.Completion);
            Assert.True(execution.Completion.IsCompletedSuccessfully);
            Assert.Empty(errors);
        }
        finally { await runner.SuspendForCloudAsync(); }
    }

    [Fact]
    public async Task KeyUpAndRepeatedSuspendDoNotRaceCancellationDisposal()
    {
        var errors = new ConcurrentQueue<Exception>();
        var runner = new MacroRunner(errors.Enqueue);
        var entered = Signal();
        var execution = runner.KeyDown(MacroMode.Hold, async current =>
        {
            current.InputDown("key:E", () => { }, () => { });
            entered.TrySetResult();
            await MacroExecutionScope.Delay(30_000, CancellationToken.None);
        })!;
        try
        {
            await Watch(entered.Task);
            var keyUps = Task.Run(() => { for (var i = 0; i < 50; i++) runner.KeyUp(); });
            var stop1 = runner.SuspendForCloudAsync();
            var stop2 = runner.SuspendForCloudAsync();
            await Watch(Task.WhenAll(keyUps, stop1, stop2));
            execution.StopRepeating();
            execution.ForceStop();
            Assert.True(execution.Completion.IsCompletedSuccessfully);
            Assert.Empty(errors);
        }
        finally { await runner.SuspendForCloudAsync(); }
    }

    [Fact]
    public async Task ThrowingCancellationCallbackStillCancelsAndJoinsEveryExecution()
    {
        var runner = new MacroRunner();
        var oldStarted = Signal();
        var finishOld = Signal();
        var latestStarted = Signal();
        var latestReleased = false;
        var old = runner.KeyDown(MacroMode.HoldFinish, async current =>
        {
            using var registration = current.ForceToken.Register(() => throw new InvalidOperationException("取消回调失败"));
            oldStarted.TrySetResult();
            await finishOld.Task;
        })!;
        try
        {
            await Watch(oldStarted.Task);
            runner.KeyUp();
            var latest = runner.KeyDown(MacroMode.Hold, async current =>
            {
                current.InputDown("key:E", () => { }, () => latestReleased = true);
                latestStarted.TrySetResult();
                await MacroExecutionScope.Delay(30_000, CancellationToken.None);
            })!;
            await Watch(latestStarted.Task);
            var suspended = runner.SuspendForCloudAsync();
            await Watch(latest.Completion);
            Assert.True(latestReleased);
            Assert.False(old.Completion.IsCompleted);
            Assert.False(suspended.IsCompleted);
            finishOld.TrySetResult();
            await Assert.ThrowsAsync<AggregateException>(() => Watch(suspended));
            Assert.True(old.Completion.IsCompletedSuccessfully);
        }
        finally
        {
            finishOld.TrySetResult();
            await runner.SuspendForCloudAsync();
        }
    }

    [Fact]
    public async Task FailedChildDoesNotSkipJoiningOtherChildren()
    {
        var errors = new ConcurrentQueue<Exception>();
        var runner = new MacroRunner(errors.Enqueue);
        var childStarted = Signal();
        using var finishChild = new ManualResetEventSlim();
        var execution = runner.KeyDown(MacroMode.Hold, current =>
        {
            _ = MacroExecutionScope.RunChild(() => throw new InvalidOperationException("识别失败"));
            _ = MacroExecutionScope.RunChild(() =>
            {
                childStarted.TrySetResult();
                if (!finishChild.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("假宏回调等待测试放行超时");
            });
            return Task.CompletedTask;
        })!;
        try
        {
            await Watch(childStarted.Task);
            var suspended = runner.SuspendForCloudAsync();
            Assert.False(execution.Completion.IsCompleted);
            Assert.False(suspended.IsCompleted);
            finishChild.Set();
            await Watch(suspended);
            Assert.IsType<AggregateException>(Assert.Single(errors));
        }
        finally
        {
            finishChild.Set();
            await runner.SuspendForCloudAsync();
        }
    }

    [Fact]
    public async Task FailedInputDownStillReleasesItsOwnedInput()
    {
        var errors = new ConcurrentQueue<Exception>();
        var runner = new MacroRunner(errors.Enqueue);
        var released = false;
        var execution = runner.KeyDown(MacroMode.Toggle, current =>
        {
            current.InputDown("mouse:left", () => throw new InvalidOperationException("部分输入成功后失败"), () => released = true);
            return Task.CompletedTask;
        })!;
        try
        {
            await Watch(execution.Completion);
            Assert.True(released);
            Assert.IsType<InvalidOperationException>(Assert.Single(errors));
        }
        finally { await runner.SuspendForCloudAsync(); }
    }
}
