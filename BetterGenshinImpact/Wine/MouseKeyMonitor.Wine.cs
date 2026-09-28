using BetterGenshinImpact.Platform.Wine;

namespace BetterGenshinImpact.Core.Monitor;

public partial class MouseKeyMonitor
{
    private WinePlatformAddon? _wineAddon;

    /// <summary>Wine 本地轮询与 Windows 事件源互斥；云安全监听不调用此入口。</summary>
    private void TrySubscribeWinePolling(long session)
    {
        if (!_environment.IsWine()) return;
        _wineAddon = new WinePlatformAddon(null);
        _wineAddon.StartPolling(() => PollingLoop(session), 15);
    }

    private void PollingLoop(long session)
    {
        lock (_stateLock)
        {
            // 已排队的 Wine 回调同样不能在切回本地后复活。
            if (!IsCurrent(session) || _mode != ListeningMode.Local || _environment.IsInputDisabled()) return;
            UpdateRepeat(WinePlatformAddon.IsKeyDown(_pickUpKey), ref _firstFKeyDownTime,
                _fTimer, 200, _environment.FRepeatEnabled);
            UpdateRepeat(WinePlatformAddon.IsKeyDown(_releaseControlKey), ref _firstSpaceKeyDownTime,
                _spaceTimer, 300, _environment.SpaceRepeatEnabled);
        }
    }

    private void DisposeWineAddon()
    {
        var addon = _wineAddon;
        _wineAddon = null;
        addon?.Dispose();
    }
}
