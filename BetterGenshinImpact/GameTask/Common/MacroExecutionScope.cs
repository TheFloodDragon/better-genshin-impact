using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;

namespace BetterGenshinImpact.GameTask.Common;

/// <summary>仅一键宏及其派生工作继承的强停范围；不引用 App 或全局任务状态。</summary>
internal sealed class MacroExecutionScope : IDisposable
{
    private static readonly AsyncLocal<MacroExecutionScope?> Ambient = new();
    private readonly MacroExecutionScope? _previous;
    private readonly MacroExecution? _execution;
    private readonly CancellationToken _token;

    internal MacroExecutionScope(CancellationToken token, MacroExecution? execution = null)
    {
        _previous = Ambient.Value;
        _token = token;
        _execution = execution;
        Ambient.Value = this;
    }

    internal static bool IsActive => Ambient.Value != null;
    internal static CancellationToken Token => Ambient.Value?._token ?? CancellationToken.None;

    internal static void Checkpoint()
    {
        if (Token.IsCancellationRequested)
            throw new NormalEndException("云模式切换，结束一键宏");
    }

    // 原始等待：不进入 TaskControl，防止焦点恢复的重试递归。
    internal static void Sleep(TimeSpan delay, CancellationToken ct = default)
    {
        if (!IsActive)
        {
            Thread.Sleep(delay);
            return;
        }

        Checkpoint();
        using var linked = ct.CanBeCanceled && ct != Token
            ? CancellationTokenSource.CreateLinkedTokenSource(Token, ct) : null;
        var token = linked?.Token ?? Token;
        if (token.WaitHandle.WaitOne(delay))
            throw new NormalEndException("取消自动任务");
        Checkpoint();
    }

    internal static async Task Delay(int milliseconds, CancellationToken ct)
    {
        if (!IsActive)
        {
            await Task.Delay(milliseconds, ct).ConfigureAwait(false);
            return;
        }

        Checkpoint();
        using var linked = ct.CanBeCanceled && ct != Token
            ? CancellationTokenSource.CreateLinkedTokenSource(Token, ct) : null;
        try
        {
            await Task.Delay(milliseconds, linked?.Token ?? Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new NormalEndException("取消自动任务");
        }
        Checkpoint();
    }

    /// <summary>暂停/失焦共用的可唤醒循环；只归还本次进入时取得的资源。</summary>
    internal static void WaitWhile(Func<bool> waiting, Action? enter = null, Action? step = null,
        Action? exit = null, int intervalMilliseconds = 1000)
    {
        var entered = false;
        try
        {
            Checkpoint();
            while (waiting())
            {
                Checkpoint();
                if (!entered)
                {
                    enter?.Invoke();
                    entered = true;
                }
                step?.Invoke();
                Sleep(TimeSpan.FromMilliseconds(intervalMilliseconds));
            }
            Checkpoint();
        }
        finally
        {
            if (entered) exit?.Invoke();
        }
    }

    /// <summary>在创建后台任务前登记，根宏退出时必须等待，包括派生工作再派生的任务。</summary>
    internal static Task RunChild(Action action)
    {
        var execution = Ambient.Value?._execution;
        return execution != null ? execution.RunChild(action) : Task.Run(action);
    }

    /// <summary>取消检查只在按下前进行；抬起永远不受取消影响。</summary>
    internal static void Hold(Action down, Action body, Action up)
    {
        Checkpoint();
        try
        {
            down();
            body();
        }
        finally
        {
            up();
        }
    }

    public void Dispose() => Ambient.Value = _previous;
}
