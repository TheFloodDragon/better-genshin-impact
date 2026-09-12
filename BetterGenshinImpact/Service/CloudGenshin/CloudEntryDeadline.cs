using System;
using System.Collections.Generic;

namespace BetterGenshinImpact.Service.CloudGenshin;

public sealed record CloudEntryTimeouts(TimeSpan Login, TimeSpan Queue, TimeSpan Connect, TimeSpan Enter)
{
    public void Validate()
    {
        foreach (var value in new[] { Login, Queue, Connect, Enter })
            if (value <= TimeSpan.Zero || value > TimeSpan.FromHours(24))
                throw new ArgumentOutOfRangeException(nameof(value), "云游戏超时必须大于零且不超过 24 小时。");
    }
}

/// <summary>每阶段独立累积耗时；DOM 抖动不重置计时，排队不会消耗开门时间。</summary>
public sealed class CloudEntryDeadline
{
    private readonly CloudEntryTimeouts _timeouts;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, TimeSpan> _elapsed = new();
    private long _lastObservation;
    private string _phase = "连接";

    public CloudEntryDeadline(CloudEntryTimeouts timeouts, TimeProvider? timeProvider = null)
    {
        timeouts.Validate();
        _timeouts = timeouts;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lastObservation = _timeProvider.GetTimestamp();
    }

    public void Observe(CloudPageState state)
    {
        _elapsed[_phase] = _elapsed.GetValueOrDefault(_phase) + _timeProvider.GetElapsedTime(_lastObservation);
        _lastObservation = _timeProvider.GetTimestamp();
        CheckTimeout(_phase);
        var phase = state switch
        {
            CloudPageState.WaitingForLogin or CloudPageState.AgreementRequired => "登录/人工确认",
            CloudPageState.Lobby or CloudPageState.QueueSelection or CloudPageState.Queuing => "排队",
            CloudPageState.Streaming => "游戏内进入",
            CloudPageState.Connecting => "云端加载",
            _ => _phase
        };
        _phase = phase;
        CheckTimeout(phase);
    }

    private void CheckTimeout(string phase)
    {
        var timeout = phase switch
        {
            "登录/人工确认" => _timeouts.Login,
            "排队" => _timeouts.Queue,
            "游戏内进入" => _timeouts.Enter,
            _ => _timeouts.Connect
        };
        if (_elapsed.GetValueOrDefault(phase) >= timeout)
            throw new TimeoutException($"云原神{phase}超时，已停止自动操作。");
    }
}
