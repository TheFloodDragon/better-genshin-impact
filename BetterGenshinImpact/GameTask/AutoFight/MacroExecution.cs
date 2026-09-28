using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.Common;

namespace BetterGenshinImpact.GameTask.AutoFight;

internal enum MacroMode { Hold, HoldFinish, Toggle }

/// <summary>单次宏拥有自己的软停止、强停、派生工作和显式按下状态。</summary>
internal sealed class MacroExecution
{
    private readonly object _cancellationLock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationTokenSource _forceCts = new();
    private bool _disposed;
    private int _stopRequested;
    private readonly object _inputsLock = new();
    private readonly Dictionary<string, Action> _pressed = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _childrenLock = new();
    private readonly HashSet<Task> _children = [];
    private readonly List<Exception> _childFailures = [];
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal MacroExecution(MacroMode mode)
    {
        Mode = mode;
        StopToken = _cts.Token;
        ForceToken = _forceCts.Token;
    }

    internal MacroMode Mode { get; }
    internal CancellationToken StopToken { get; }
    internal CancellationToken ForceToken { get; }
    internal Task Completion => _completion.Task;
    internal bool StopRequested => Volatile.Read(ref _stopRequested) != 0;

    // 热键锁内只发布停止意图，避免快速重按越过旧执行的软停止。
    internal void RequestStopRepeating() => Interlocked.Exchange(ref _stopRequested, 1);

    internal void StopRepeating()
    {
        RequestStopRepeating();
        // Cancel 和 Dispose 使用同一个锁；软令牌仅供轮次判断，不注册等待回调。
        lock (_cancellationLock)
        {
            if (!_disposed) _cts.Cancel();
        }
        if (Mode == MacroMode.Hold) ReleaseInputs();
    }

    internal void ForceStop()
    {
        lock (_cancellationLock)
        {
            if (!_disposed) _forceCts.Cancel();
        }
    }

    internal void RunRounds<T>(IReadOnlyList<T> commands, Func<bool> enabled,
        Action<T, int> execute, Action<int>? beginRound = null)
    {
        for (var round = 1; !StopRequested && enabled(); round++)
        {
            MacroExecutionScope.Checkpoint();
            beginRound?.Invoke(round);
            foreach (var command in commands)
            {
                MacroExecutionScope.Checkpoint();
                if (Mode == MacroMode.Hold && StopRequested) break;
                execute(command, round);
            }
        }
    }

    internal void InputDown(string identity, Action down, Action up)
    {
        lock (_inputsLock)
        {
            MacroExecutionScope.Checkpoint();
            if (Mode == MacroMode.Hold && StopRequested) return;
            // 先记账，即使输入调用部分成功后抛错，收尾仍补发抬起。
            _pressed[identity] = up;
            down();
        }
    }

    internal void InputUp(string identity, Action up)
    {
        lock (_inputsLock)
        {
            up();
            _pressed.Remove(identity);
        }
    }

    internal void ReleaseInputs()
    {
        lock (_inputsLock)
        {
            List<Exception>? failures = null;
            foreach (var up in _pressed.Values)
            {
                try { up(); }
                catch (Exception ex) { (failures ??= []).Add(ex); }
            }
            _pressed.Clear();
            if (failures != null) throw new AggregateException(failures);
        }
    }

    internal Task RunChild(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_childrenLock) _children.Add(completion.Task);
        _ = Task.Run(() =>
        {
            Exception? failure = null;
            try { action(); }
            catch (NormalEndException) { }
            catch (OperationCanceledException) { }
            catch (Exception ex) { failure = ex; }
            finally
            {
                lock (_childrenLock)
                {
                    if (failure != null) _childFailures.Add(failure);
                    // 长时间重复的宏只保留在途任务；失败由根宏统一报告。
                    completion.TrySetResult();
                    _children.Remove(completion.Task);
                }
            }
        });
        return completion.Task;
    }

    internal async Task JoinChildrenAsync()
    {
        while (true)
        {
            Task[] batch;
            lock (_childrenLock)
            {
                batch = _children.ToArray();
                if (batch.Length == 0)
                {
                    if (_childFailures.Count != 0) throw new AggregateException(_childFailures);
                    return;
                }
            }
            await Task.WhenAll(batch).ConfigureAwait(false);
        }
    }

    internal void DisposeCancellation()
    {
        lock (_cancellationLock)
        {
            _disposed = true;
            _cts.Dispose();
            _forceCts.Dispose();
        }
    }

    internal void Complete() => _completion.TrySetResult();
}

/// <summary>登记先于调度；挂起等待所有旧代次，而非最后一个被热键覆盖的 Task。</summary>
internal sealed class MacroRunner
{
    private readonly object _lifecycleLock = new();
    private readonly HashSet<MacroExecution> _running = [];
    private readonly Action<Exception>? _reportError;
    private MacroExecution? _current;
    private bool _keyDown;
    private bool _cloudSuspended;

    internal MacroRunner(Action<Exception>? reportError = null) => _reportError = reportError;
    internal bool IsCloudSuspended { get { lock (_lifecycleLock) return _cloudSuspended; } }

    internal MacroExecution? KeyDown(MacroMode mode, Func<MacroExecution, Task> body)
    {
        MacroExecution? started = null;
        MacroExecution? stop = null;
        lock (_lifecycleLock)
        {
            if (_cloudSuspended || _keyDown) return null;
            _keyDown = true;
            if (_current == null || _current.StopRequested || _current.Completion.IsCompleted)
            {
                started = new MacroExecution(mode);
                _running.Add(started);
                _current = started;
            }
            else if (mode == MacroMode.Toggle)
            {
                stop = _current;
                stop.RequestStopRepeating();
            }
        }
        stop?.StopRepeating();
        if (started != null)
        {
            var execution = started;
            _ = Task.Run(() => RunAsync(execution, body));
        }
        return started;
    }

    internal void KeyUp()
    {
        MacroExecution? stop;
        lock (_lifecycleLock)
        {
            _keyDown = false;
            stop = _current?.Mode != MacroMode.Toggle ? _current : null;
            stop?.RequestStopRepeating();
        }
        stop?.StopRepeating();
    }

    internal async Task SuspendForCloudAsync()
    {
        MacroExecution[] running;
        lock (_lifecycleLock)
        {
            _cloudSuspended = true;
            _keyDown = false;
            running = _running.ToArray();
        }
        List<Exception>? failures = null;
        foreach (var execution in running)
        {
            try { execution.ForceStop(); }
            catch (Exception ex) { (failures ??= []).Add(ex); }
        }
        // 即使取消回调抛错，也先取消其余宏并排空全部工作，不能提前退出准备阶段。
        // 不使用等待取消或超时：完成信号包含派生任务、抬键及 CTS 收尾。
        try { await Task.WhenAll(running.Select(execution => execution.Completion)).ConfigureAwait(false); }
        catch (Exception ex) { (failures ??= []).Add(ex); }
        if (failures != null) throw new AggregateException(failures);
    }

    internal void ResumeAfterCloud()
    {
        lock (_lifecycleLock)
        {
            // 防止准备阶段取消时过早恢复，调用方仍须先 await SuspendForCloudAsync。
            if (_running.Count != 0) return;
            _cloudSuspended = false;
            _keyDown = false;
        }
    }

    private async Task RunAsync(MacroExecution execution, Func<MacroExecution, Task> body)
    {
        using var scope = new MacroExecutionScope(execution.ForceToken, execution);
        try
        {
            MacroExecutionScope.Checkpoint();
            await body(execution).ConfigureAwait(false);
        }
        catch (NormalEndException) { }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _reportError?.Invoke(ex); }
        finally
        {
            try
            {
                // 派生工作仍可能持有截图/焦点等待，必须先排空再销毁强停源。
                await execution.JoinChildrenAsync().ConfigureAwait(false);
            }
            catch (Exception ex) { _reportError?.Invoke(ex); }
            finally
            {
                try { execution.ReleaseInputs(); }
                catch (Exception ex) { _reportError?.Invoke(ex); }
                finally
                {
                    execution.DisposeCancellation();
                    lock (_lifecycleLock)
                    {
                        execution.Complete();
                        _running.Remove(execution);
                        if (ReferenceEquals(_current, execution)) _current = null;
                    }
                }
            }
        }
    }
}
