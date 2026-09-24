using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Service.CloudGenshin;
using BetterGenshinImpact.Service.CloudGenshin.Browser;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Newtonsoft.Json.Linq;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.ServiceTests.CloudGenshin;

/// <summary>
/// 离线端到端流程：用脚本化的假浏览器覆盖登录、验证码、授权、排队、连接与游戏内主界面确认。
/// 不接触网络、真实浏览器或账号；主界面识别通过注入替换，保证结果可重复。
/// </summary>
public class CloudEntryFlowTests
{
    private static GenshinStartConfig Config => new()
    {
        CloudLoginTimeoutMinutes = 20,
        CloudQueueTimeoutMinutes = 60,
        CloudConnectTimeoutSeconds = 90,
        CloudEnterTimeoutMinutes = 5,
        CloudMaxRetryAttempts = 2,
        AutoEnterGameEnabled = true
    };

    /// <summary>成功路径：只有连续两帧确认游戏主界面后才算进入完成。</summary>
    [Fact]
    public async Task ReadyOnlyAfterGameMainUiIsConfirmed()
    {
        var browser = ScriptedCloudBrowser.FullEntryFlow();
        using var clock = new ClockPump();
        var service = CreateService(browser, clock.Clock, () => browser.InGameMainUi);

        await service.StartSessionAsync(Config, (_, _) => Task.CompletedTask).WaitAsync(TestTimeout);

        Assert.True(service.IsReady);
        // 登录页、大厅、排队和播放器出现都不算成功，必须落到游戏内主界面。
        Assert.Equal(CloudPageState.Streaming, browser.CurrentState);
        Assert.True(browser.InGameMainUi);

        // 会话循环仍在运行，先停止再读取输入记录，避免断言与后台循环竞争。
        await service.StopSessionAsync().WaitAsync(TestTimeout);

        // 平台外壳依次点击：打开登录、进入游戏、普通队列、确认开始，最后是游戏内开门。
        // 进入完成后不再追加任何输入，也不继续执行其他游戏任务。
        Assert.Equal(
        [
            CloudPageState.LoginRequired, CloudPageState.Lobby, CloudPageState.QueueSelection,
            CloudPageState.Connecting, CloudPageState.Streaming
        ], browser.ClickedStates);
        Assert.True(browser.Disposed);
        Assert.False(service.Capture.IsCapturing);
    }

    /// <summary>验证码、授权、协议阶段必须完全交给人工，不得自动点击或重复提交。</summary>
    [Fact]
    public async Task ManualVerificationStagesNeverReceiveInput()
    {
        var browser = ScriptedCloudBrowser.FullEntryFlow();
        using var clock = new ClockPump();
        var service = CreateService(browser, clock.Clock, () => browser.InGameMainUi);

        await service.StartSessionAsync(Config, (_, _) => Task.CompletedTask).WaitAsync(TestTimeout);
        await service.StopSessionAsync().WaitAsync(TestTimeout);

        Assert.DoesNotContain(CloudPageState.WaitingForLogin, browser.ClickedStates);
        Assert.DoesNotContain(CloudPageState.VerificationRequired, browser.ClickedStates);
        Assert.DoesNotContain(CloudPageState.AuthorizationRequired, browser.ClickedStates);
        Assert.DoesNotContain(CloudPageState.AgreementRequired, browser.ClickedStates);
        // 这些阶段确实出现过，断言才有意义。
        Assert.Contains(CloudPageState.VerificationRequired, browser.ObservedStates);
        Assert.Contains(CloudPageState.AuthorizationRequired, browser.ObservedStates);
    }

    /// <summary>页面跳转导致执行上下文销毁属于暂态错误，应退避重试而不是结束会话。</summary>
    [Fact]
    public async Task TransientProbeFailureRecoversWithoutEndingSession()
    {
        var browser = ScriptedCloudBrowser.FullEntryFlow();
        browser.TransientFailuresBeforeFirstProbe = 2;
        using var clock = new ClockPump();
        var service = CreateService(browser, clock.Clock, () => browser.InGameMainUi);

        await service.StartSessionAsync(Config, (_, _) => Task.CompletedTask).WaitAsync(TestTimeout);

        Assert.True(service.IsReady);
        Assert.Equal(0, browser.TransientFailuresBeforeFirstProbe);
        await service.StopSessionAsync().WaitAsync(TestTimeout);
    }

    /// <summary>暂态错误超过配置上限后必须停止，不能无限重试。</summary>
    [Fact]
    public async Task TransientFailureBeyondRetryLimitStopsSession()
    {
        var browser = ScriptedCloudBrowser.FullEntryFlow();
        browser.TransientFailuresBeforeFirstProbe = 99;
        using var clock = new ClockPump();
        var service = CreateService(browser, clock.Clock, () => browser.InGameMainUi);

        var start = service.StartSessionAsync(Config, (_, _) => Task.CompletedTask);
        // 超过上限后向上抛出原始暂态异常，而不是继续无限重试。
        await Assert.ThrowsAsync<TimeoutException>(() => start.WaitAsync(TestTimeout));

        Assert.False(service.IsReady);
        Assert.True(browser.Disposed);
        Assert.False(service.Capture.IsCapturing);
    }

    /// <summary>时长不足需要人工处理，不得自动充值或继续排队。</summary>
    [Fact]
    public async Task TimeExhaustedStopsWithoutPurchase()
    {
        var browser = new ScriptedCloudBrowser(
        [
            ScriptedStep.AfterProbes(Snapshot(CloudPageState.Navigating), 1),
            ScriptedStep.Final(Snapshot(CloudPageState.TimeExhausted, message: "云游戏可用时长不足，请人工处理；不会自动充值"))
        ]);
        using var clock = new ClockPump();
        var service = CreateService(browser, clock.Clock, () => false);

        var start = service.StartSessionAsync(Config, (_, _) => Task.CompletedTask);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => start.WaitAsync(TestTimeout));

        Assert.Contains("时长不足", failure.Message);
        Assert.Empty(browser.Clicks);
        Assert.True(browser.Disposed);
    }

    /// <summary>停止请求必须释放浏览器与帧缓存，且启动调用方收到取消而不是虚假成功。</summary>
    [Fact]
    public async Task StopReleasesBrowserAndReportsCancellation()
    {
        var browser = new ScriptedCloudBrowser(
        [
            ScriptedStep.Final(Snapshot(CloudPageState.WaitingForLogin,
                message: "请在官方登录窗口完成密码/短信/扫码登录；验证码和手机授权须人工处理"))
        ]);
        using var clock = new ClockPump();
        var service = CreateService(browser, clock.Clock, () => false);

        var start = service.StartSessionAsync(Config, (_, _) => Task.CompletedTask);
        await browser.FirstProbe.Task.WaitAsync(TestTimeout);
        service.RequestStop();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(TestTimeout));
        await service.StopSessionAsync().WaitAsync(TestTimeout);
        Assert.True(browser.Disposed);
        Assert.False(service.IsReady);
        Assert.False(service.Capture.IsCapturing);
        Assert.Equal(0, service.WindowHandle);
    }

    /// <summary>浏览器启动即失败时不应残留会话状态，允许立即安全重启。</summary>
    [Fact]
    public async Task FailedStartupAllowsImmediateRestart()
    {
        var failing = new ScriptedCloudBrowser([ScriptedStep.Final(Snapshot(CloudPageState.Navigating))])
        {
            StartupFailure = () => new IOException("独立浏览器启动失败或 Profile 已被其他浏览器占用。")
        };
        using var clock = new ClockPump();
        var service = CreateService(failing, clock.Clock, () => false);

        await Assert.ThrowsAsync<IOException>(() => service.StartSessionAsync(Config, (_, _) => Task.CompletedTask).WaitAsync(TestTimeout));
        Assert.True(failing.Disposed);
        Assert.False(service.IsRunning);

        // 失败通知在资源释放之后才发出，所以这里可以直接重启而不会抛"会话正在运行"。
        var working = ScriptedCloudBrowser.FullEntryFlow();
        var restarted = CreateService(working, clock.Clock, () => working.InGameMainUi);
        await restarted.StartSessionAsync(Config, (_, _) => Task.CompletedTask).WaitAsync(TestTimeout);
        Assert.True(restarted.IsReady);
        await restarted.StopSessionAsync().WaitAsync(TestTimeout);
    }

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(60);

    private static CloudGenshinService CreateService(ICloudBrowser browser, TimeProvider clock, Func<bool> isInMainUi)
    {
        return new CloudGenshinService(NullLogger<CloudGenshinService>.Instance, () => browser, clock,
            _ => isInMainUi(), _ => (960, 540));
    }

    internal static CloudPageSnapshot Snapshot(CloudPageState state, CloudPageAction action = CloudPageAction.None,
        bool streamReady = false, double? streamTime = null, string message = "", bool hasLoginFrame = false)
    {
        return new CloudPageSnapshot
        {
            State = state,
            Action = action,
            ActionVerified = action != CloudPageAction.None,
            HasLoginFrame = hasLoginFrame,
            Message = string.IsNullOrEmpty(message) ? state.ToString() : message,
            ActionBounds = action == CloudPageAction.None ? null : new CloudPageRect(100, 200, 120, 48),
            GameBounds = streamReady ? new CloudPageRect(0, 0, 1920, 1080) : null,
            ViewportWidth = 1920,
            ViewportHeight = 1080,
            DevicePixelRatio = 1,
            StreamReady = streamReady,
            StreamTime = streamTime
        };
    }
}

/// <summary>推进假时钟，让服务内部的退避与轮询延时在测试中快速完成。</summary>
internal sealed class ClockPump : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _pump;

    public ClockPump()
    {
        Clock = new FakeTimeProvider();
        _pump = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                // 步进必须远小于帧的 3 秒有效期，否则刚发布的帧会在读取前被判定过期。
                Clock.Advance(TimeSpan.FromMilliseconds(20));
                try { await Task.Delay(1, _stop.Token); }
                catch (OperationCanceledException) { return; }
            }
        });
    }

    public FakeTimeProvider Clock { get; }

    public void Dispose()
    {
        _stop.Cancel();
        try { _pump.Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { }
        _stop.Dispose();
    }
}

internal sealed record ScriptedStep(CloudPageSnapshot Snapshot, bool AdvanceOnClick, int ProbesBeforeAdvance, bool MainUiAfterClick = false)
{
    public static ScriptedStep AfterProbes(CloudPageSnapshot snapshot, int probes) => new(snapshot, false, probes);
    public static ScriptedStep OnClick(CloudPageSnapshot snapshot, bool mainUiAfterClick = false) => new(snapshot, true, int.MaxValue, mainUiAfterClick);
    public static ScriptedStep Final(CloudPageSnapshot snapshot) => new(snapshot, false, int.MaxValue);
}

/// <summary>
/// 按脚本推进的假浏览器：点击或多次探测后进入下一状态，模拟真实云平台的阶段推进。
/// </summary>
internal sealed class ScriptedCloudBrowser : ICloudBrowser
{
    private readonly List<ScriptedStep> _steps;
    private readonly object _sync = new();
    private int _index;
    private int _probesInStep;
    private double _streamTime;

    public ScriptedCloudBrowser(List<ScriptedStep> steps) => _steps = steps;

    public List<(double X, double Y)> Clicks { get; } = [];
    public List<CloudPageState> ClickedStates { get; } = [];
    public List<CloudPageState> ObservedStates { get; } = [];
    public TaskCompletionSource FirstProbe { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool InGameMainUi { get; private set; }
    public bool Disposed { get; private set; }
    public int TransientFailuresBeforeFirstProbe { get; set; }
    public Func<Exception>? StartupFailure { get; set; }

    public nint WindowHandle => 4242;
    public int ProcessId => 4242;
    public bool IsConnected => !Disposed;

    public CloudPageState CurrentState { get { lock (_sync) return _steps[_index].Snapshot.State; } }

    /// <summary>完整流程：导航 → 登录入口 → 官方登录 → 验证码 → 授权 → 大厅 → 队列 → 排队 → 连接 → 视频 → 游戏主界面。</summary>
    public static ScriptedCloudBrowser FullEntryFlow()
    {
        return new ScriptedCloudBrowser(
        [
            ScriptedStep.AfterProbes(CloudEntryFlowTests.Snapshot(CloudPageState.Navigating, message: "正在打开云原神页面"), 1),
            ScriptedStep.OnClick(CloudEntryFlowTests.Snapshot(CloudPageState.LoginRequired, CloudPageAction.OpenLogin, message: "正在打开官方账号登录窗口")),
            ScriptedStep.AfterProbes(CloudEntryFlowTests.Snapshot(CloudPageState.WaitingForLogin, hasLoginFrame: true,
                message: "请在官方登录窗口完成密码/短信/扫码登录；验证码和手机授权须人工处理"), 2),
            ScriptedStep.AfterProbes(CloudEntryFlowTests.Snapshot(CloudPageState.VerificationRequired, hasLoginFrame: true,
                message: "请人工完成验证码；不会自动提交或重复发送短信"), 2),
            ScriptedStep.AfterProbes(CloudEntryFlowTests.Snapshot(CloudPageState.AuthorizationRequired, hasLoginFrame: true,
                message: "已扫码或等待授权，请在手机或官方窗口确认登录"), 2),
            ScriptedStep.OnClick(CloudEntryFlowTests.Snapshot(CloudPageState.Lobby, CloudPageAction.StartGame, message: "账号已登录，准备进入云原神普通队列")),
            ScriptedStep.OnClick(CloudEntryFlowTests.Snapshot(CloudPageState.QueueSelection, CloudPageAction.SelectNormalQueue, message: "选择普通队列；时长消耗以官网规则为准")),
            ScriptedStep.AfterProbes(CloudEntryFlowTests.Snapshot(CloudPageState.Queuing, message: "正在排队：预计等待 2 分钟"), 2),
            ScriptedStep.OnClick(CloudEntryFlowTests.Snapshot(CloudPageState.Connecting, CloudPageAction.ConfirmEnter, message: "云端加载完成，准备开始游戏")),
            // 视频已就绪但仍在游戏内加载页，此时才允许识别并点击游戏内的进入按钮。
            ScriptedStep.OnClick(CloudEntryFlowTests.Snapshot(CloudPageState.Streaming, streamReady: true, message: "已连接播放器，等待游戏内主界面确认"), mainUiAfterClick: true),
            ScriptedStep.Final(CloudEntryFlowTests.Snapshot(CloudPageState.Streaming, streamReady: true, message: "已连接播放器，等待游戏内主界面确认"))
        ]);
    }

    public Task StartAsync(CloudBrowserOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (StartupFailure != null) throw StartupFailure();
        return Task.CompletedTask;
    }

    public Task<JToken?> EvaluateAsync(string expression, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FirstProbe.TrySetResult();
        lock (_sync)
        {
            if (TransientFailuresBeforeFirstProbe > 0)
            {
                TransientFailuresBeforeFirstProbe--;
                // 与真实路径一致：CdpClient 把执行上下文销毁映射为暂态异常，仅这类才允许重试。
                throw new TimeoutException("浏览器正在切换页面，等待执行上下文恢复。");
            }
            var step = _steps[_index];
            ObservedStates.Add(step.Snapshot.State);
            var snapshot = step.Snapshot;
            if (snapshot.StreamReady)
            {
                // 视频时间必须持续推进，否则服务会判定画面停止更新。
                _streamTime += 0.05;
                snapshot = CloudEntryFlowTests.Snapshot(snapshot.State, snapshot.Action, true, _streamTime, snapshot.Message);
            }
            if (!step.AdvanceOnClick && ++_probesInStep >= step.ProbesBeforeAdvance) Advance();
            return Task.FromResult<JToken?>(JObject.FromObject(snapshot));
        }
    }

    public Task<byte[]> CaptureScreenshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // 非黑屏灰度图：服务会据此判定画面有效，再交给注入的主界面识别。
        using var image = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(128, 128, 128));
        Cv2.ImEncode(".jpg", image, out var bytes);
        return Task.FromResult(bytes);
    }

    public Task ClickAsync(double cssX, double cssY, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var step = _steps[_index];
            Clicks.Add((cssX, cssY));
            ClickedStates.Add(step.Snapshot.State);
            if (step.MainUiAfterClick) InGameMainUi = true;
            if (step.AdvanceOnClick || step.MainUiAfterClick) Advance();
        }
        return Task.CompletedTask;
    }

    private void Advance()
    {
        if (_index < _steps.Count - 1) _index++;
        _probesInStep = 0;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
