using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Service.CloudGenshin.Browser;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.Service.CloudGenshin;

public sealed record CloudSessionStatus(string Message, bool Ready, bool Ended);

/// <summary>串行拥有页面探测、输入与截图；仅在确认游戏主界面后完成启动任务。</summary>
public sealed class CloudGenshinService : IHostedService, IDisposable, IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly ILogger<CloudGenshinService> _logger;
    private readonly Func<ICloudBrowser> _browserFactory;
    private readonly CloudPageDetector _detector = new();
    private readonly TimeProvider _clock;
    private readonly Func<ImageRegion, bool> _isInMainUi;
    private readonly Func<ImageRegion, (double X, double Y)?> _findEnterPoint;
    private CancellationTokenSource? _cancellation;
    private CancellationToken _sessionOwner;
    private Task _runTask = Task.CompletedTask;
    private string _lastStatus = "";
    private bool _lastReportedReady;
    private volatile bool _ready;
    private bool _disposed;

    public CloudGenshinService(ILogger<CloudGenshinService> logger, ILogger<CloudBrowserSession> browserLogger)
        : this(logger, () => new CloudBrowserSession(browserLogger)) { }

    public CloudGenshinService(ILogger<CloudGenshinService> logger, Func<ICloudBrowser> browserFactory)
        : this(logger, browserFactory, TimeProvider.System, region => Bv.IsInMainUi(region), FindEnterPoint) { }

    // 注入时钟与画面识别便于完整离线流程测试；正式入口始终使用真实模板识别。
    internal CloudGenshinService(ILogger<CloudGenshinService> logger, Func<ICloudBrowser> browserFactory,
        TimeProvider clock, Func<ImageRegion, bool> isInMainUi, Func<ImageRegion, (double X, double Y)?> findEnterPoint)
    {
        _logger = logger;
        _browserFactory = browserFactory;
        _clock = clock;
        _isInMainUi = isInMainUi;
        _findEnterPoint = findEnterPoint;
        Capture = new CloudGameCapture(clock);
    }

    public CloudGameCapture Capture { get; }
    public bool IsRunning { get { lock (_sync) return !_runTask.IsCompleted; } }
    public bool IsReady => _ready;
    public nint WindowHandle { get; private set; }
    public event EventHandler<CloudSessionStatus>? StatusChanged;

    public Task StartSessionAsync(GenshinStartConfig config, Func<nint, int, Task> attach, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(attach);
        cancellationToken.ThrowIfCancellationRequested();
        var timeouts = new CloudEntryTimeouts(TimeSpan.FromMinutes(config.CloudLoginTimeoutMinutes),
            TimeSpan.FromMinutes(config.CloudQueueTimeoutMinutes), TimeSpan.FromSeconds(config.CloudConnectTimeoutSeconds),
            TimeSpan.FromMinutes(config.CloudEnterTimeoutMinutes));
        timeouts.Validate();
        if (config.CloudMaxRetryAttempts is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(config.CloudMaxRetryAttempts), "云游戏重试次数必须在 0～5 之间。");
        var options = new CloudBrowserOptions(config.CloudBrowserPath,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BetterGI", "CloudGenshin", "Profile"));
        var autoEnter = config.AutoEnterGameEnabled;
        var retries = config.CloudMaxRetryAttempts;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_runTask.IsCompleted) throw new InvalidOperationException("网页云原神会话正在运行或停止中，请勿重复启动。");
            _ready = false;
            _lastStatus = "";
            _lastReportedReady = false;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _sessionOwner = cancellationToken;
            var cancellation = _cancellation;
            _runTask = Task.Run(() => RunAsync(options, timeouts, autoEnter, retries, attach, ready, cancellation));
        }
        return ready.Task;
    }

    public void RequestStop()
    {
        CancellationTokenSource? cancellation;
        lock (_sync) cancellation = _cancellation;
        Cancel(cancellation);
    }

    private static void Cancel(CancellationTokenSource? cancellation)
    {
        // 不持有服务锁执行第三方取消回调；与会话完成时 Dispose 的竞态是正常停止路径。
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public async Task StopSessionAsync(CancellationToken cancellationToken = default)
    {
        Task run;
        CancellationTokenSource? cancellation;
        lock (_sync) { cancellation = _cancellation; run = _runTask; }
        Cancel(cancellation);
        await run.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>迟到的启动收尾只能停止自己拥有的会话，不能取消后续新会话。</summary>
    internal async Task StopSessionForOwnerAsync(CancellationToken owner)
    {
        Task run;
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            if (_sessionOwner != owner) return;
            cancellation = _cancellation;
            run = _runTask;
        }
        Cancel(cancellation);
        await run.ConfigureAwait(false);
    }

    private Task DelayAsync(TimeSpan duration, CancellationToken ct) => Task.Delay(duration, _clock, ct);
    private static TimeSpan RetryDelay(int attempt) => TimeSpan.FromSeconds(Math.Min(2 << Math.Min(attempt, 3), 15));

    private async Task<ICloudBrowser> StartBrowserAsync(CloudBrowserOptions options, int maxRetries, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            Report(attempt == 0 ? "正在启动独立云游戏浏览器与调试服务" : $"正在重试浏览器启动（{attempt}/{maxRetries}）");
            var browser = _browserFactory();
            try
            {
                await browser.StartAsync(options, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (!browser.IsConnected || browser.WindowHandle == 0 || browser.ProcessId <= 0)
                    throw new IOException("云游戏浏览器未通过进程、窗口和连接状态检查。");
                Report("云游戏浏览器服务已就绪，正在检查官方页面");
                return browser;
            }
            catch (Exception ex)
            {
                await browser.DisposeAsync().ConfigureAwait(false);
                if (ct.IsCancellationRequested || attempt >= maxRetries || ex is not (TimeoutException or CloudBrowserTransientException)) throw;
                _logger.LogWarning("云浏览器启动发生暂态错误，第 {Attempt}/{MaxRetries} 次重试", attempt + 1, maxRetries);
                await DelayAsync(RetryDelay(attempt), ct).ConfigureAwait(false);
            }
        }
    }

    private async Task RunAsync(CloudBrowserOptions options, CloudEntryTimeouts timeouts, bool autoEnter, int maxRetries,
        Func<nint, int, Task> attach, TaskCompletionSource ready, CancellationTokenSource cancellation)
    {
        var ct = cancellation.Token;
        ICloudBrowser? browser = null;
        Exception? failure = null;
        var finalMessage = "网页云原神已停止";
        try
        {
            browser = await StartBrowserAsync(options, maxRetries, ct).ConfigureAwait(false);
            WindowHandle = browser.WindowHandle;
            Capture.Start(WindowHandle);
            var attaching = attach(WindowHandle, browser.ProcessId);
            // UI 调度可能仍在队列里。调用方必须在回调内检查取消/代次，不能在停止后重新初始化。
            _ = attaching.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            await attaching.WaitAsync(ct).ConfigureAwait(false);
            var deadline = new CloudEntryDeadline(timeouts, _clock);
            var lastAction = _clock.GetTimestamp();
            var hasActed = false;
            var lastFrame = _clock.GetTimestamp();
            double? streamTime = null;
            var lastStreamAdvance = _clock.GetTimestamp();
            var stableMainFrames = 0;
            var offsiteObservations = 0;
            var transientFailures = 0;
            var entryCompleted = false;
            var actionAttempts = new Dictionary<CloudPageAction, int>();
            CloudPageState? previousState = null;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (!browser.IsConnected) throw new IOException("网页云原神浏览器已关闭或连接中断。");
                CloudPageSnapshot snapshot;
                try
                {
                    snapshot = await _detector.InspectAsync(browser, ct).ConfigureAwait(false);
                    transientFailures = 0;
                }
                catch (Exception ex) when (ex is CloudBrowserTransientException or TimeoutException)
                {
                    Capture.Clear();
                    stableMainFrames = 0;
                    if (!browser.IsConnected || ++transientFailures > maxRetries) throw;
                    if (!entryCompleted) deadline.Observe(CloudPageState.Unknown);
                    Report($"页面导航或探测暂时失败，正在等待恢复（{transientFailures}/{maxRetries}）");
                    await DelayAsync(RetryDelay(transientFailures - 1), ct).ConfigureAwait(false);
                    continue;
                }
                if (snapshot.State is CloudPageState.TimeExhausted or CloudPageState.Maintenance or CloudPageState.UnsupportedBrowser)
                    throw new InvalidOperationException(snapshot.Message);
                if (snapshot.State == CloudPageState.Disconnected)
                {
                    if (++offsiteObservations >= 3) throw new InvalidOperationException(snapshot.Message);
                }
                else offsiteObservations = 0;
                if (!entryCompleted) deadline.Observe(snapshot.State);
                if (previousState != snapshot.State)
                {
                    _logger.LogInformation("云原神流程状态：{Previous} → {Current}", previousState, snapshot.State);
                    previousState = snapshot.State;
                }
                Report(entryCompleted && snapshot.State == CloudPageState.Streaming
                    ? "已确认进入游戏主界面；自动进入已完成，不执行其他游戏任务" : snapshot.Message);

                if (!entryCompleted && CloudPageDetector.CanAutoClick(snapshot))
                {
                    var attempts = actionAttempts.GetValueOrDefault(snapshot.Action);
                    var retryAction = snapshot.Action == CloudPageAction.RetryConnection;
                    var allowed = retryAction ? maxRetries : maxRetries + 1;
                    var interval = attempts == 0 ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(Math.Min(5 * (1 << Math.Min(attempts - 1, 2)), 20));
                    if (!hasActed || _clock.GetElapsedTime(lastAction) >= interval)
                    {
                        if (attempts >= allowed)
                            throw new InvalidOperationException($"云原神步骤 {snapshot.Action} 已达到自动尝试上限，请检查浏览器提示后重新启动。");
                        if (await _detector.TryClickActionAsync(browser, snapshot, ct).ConfigureAwait(false))
                        {
                            actionAttempts[snapshot.Action] = attempts + 1;
                            lastAction = _clock.GetTimestamp();
                            hasActed = true;
                            _logger.LogInformation("云原神执行 {Action}（{Attempt}/{Limit}）", snapshot.Action, attempts + 1, allowed);
                            // 不使用点击前快照判断点击后的画面。
                            await DelayAsync(TimeSpan.FromMilliseconds(300), ct).ConfigureAwait(false);
                            continue;
                        }
                    }
                }

                if (snapshot.State == CloudPageState.Streaming && snapshot.StreamReady && snapshot.GameBounds != null)
                {
                    if (snapshot.StreamTime is { } playbackTime)
                    {
                        if (streamTime == null || playbackTime != streamTime) lastStreamAdvance = _clock.GetTimestamp();
                        streamTime = playbackTime;
                        if (_clock.GetElapsedTime(lastStreamAdvance) > TimeSpan.FromSeconds(20))
                            throw new IOException("云游戏视频帧停止更新，请恢复浏览器或检查网络后重试。");
                    }
                    var bytes = await browser.CaptureScreenshotAsync(ct).ConfigureAwait(false);
                    using var image = Cv2.ImDecode(bytes, ImreadModes.Color);
                    if (image.Empty()) throw new IOException("无法解码云游戏截图。");
                    var transform = CloudViewportTransform.Create(snapshot, image.Width, image.Height);
                    using var crop = new Mat(image, transform.SourceRect);
                    var mean = Cv2.Mean(crop);
                    if (mean.Val0 + mean.Val1 + mean.Val2 > 9)
                    {
                        // 截图期间可能出现登录/支付弹窗或几何变化，复查后才发布和识图。
                        var current = await _detector.InspectAsync(browser, ct).ConfigureAwait(false);
                        if (current.State != CloudPageState.Streaming || !current.StreamReady || !transform.Matches(current))
                        {
                            Capture.Clear();
                            stableMainFrames = 0;
                            await DelayAsync(TimeSpan.FromMilliseconds(300), ct).ConfigureAwait(false);
                            continue;
                        }
                        var normalized = new Mat();
                        try { Cv2.Resize(crop, normalized, new Size(CloudViewportTransform.ImageWidth, CloudViewportTransform.ImageHeight)); }
                        catch { normalized.Dispose(); throw; }
                        Capture.Publish(normalized, transform);
                        lastFrame = _clock.GetTimestamp();
                        if (!entryCompleted)
                        {
                            using var frame = Capture.CaptureWithTransform();
                            if (frame != null)
                            {
                                using var region = new GameCaptureRegion(frame.Image.Clone(), 0, 0);
                                if (_isInMainUi(region))
                                {
                                    if (++stableMainFrames >= 2)
                                    {
                                        ct.ThrowIfCancellationRequested();
                                        entryCompleted = true;
                                        _ready = true;
                                        Report("已确认进入游戏主界面；自动进入已完成，不执行其他游戏任务");
                                        ready.TrySetResult();
                                    }
                                }
                                else
                                {
                                    stableMainFrames = 0;
                                    Report(autoEnter ? "视频已就绪，等待游戏内开门与主界面确认" : "请手动进入游戏主界面，确认后完成启动");
                                    if (autoEnter && (!hasActed || _clock.GetElapsedTime(lastAction) >= TimeSpan.FromSeconds(3)))
                                    {
                                        var point = _findEnterPoint(region);
                                        if (point is { } position)
                                        {
                                            var latest = await _detector.InspectAsync(browser, ct).ConfigureAwait(false);
                                            if (latest.State == CloudPageState.Streaming && latest.StreamReady && transform.Matches(latest))
                                            {
                                                var css = transform.ToCss(position.X, position.Y);
                                                await browser.ClickAsync(css.X, css.Y, ct).ConfigureAwait(false);
                                                lastAction = _clock.GetTimestamp();
                                                hasActed = true;
                                                _logger.LogInformation("云原神执行已识别的游戏内进入操作");
                                                await DelayAsync(TimeSpan.FromMilliseconds(300), ct).ConfigureAwait(false);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        stableMainFrames = 0;
                        Capture.Clear();
                        Report("云游戏画面暂时为黑屏，等待视频加载");
                    }
                }
                else
                {
                    Capture.Clear();
                    stableMainFrames = 0;
                    streamTime = null;
                    lastStreamAdvance = _clock.GetTimestamp();
                }
                if (entryCompleted && _clock.GetElapsedTime(lastFrame) > TimeSpan.FromSeconds(20))
                    throw new IOException("超过 20 秒未取得有效游戏画面，已停止会话，请人工检查浏览器。");
                await DelayAsync(TimeSpan.FromMilliseconds(snapshot.State == CloudPageState.Queuing ? 5000 : snapshot.State == CloudPageState.Streaming ? 300 : 1000), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { finalMessage = "网页云原神已取消"; }
        catch (Exception ex)
        {
            failure = ex;
            finalMessage = $"网页云原神已停止：{ex.Message}";
            _logger.LogWarning(ex, "网页云原神会话结束");
        }
        finally
        {
            _ready = false;
            Capture.Stop();
            if (browser != null)
            {
                try { await browser.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "释放云游戏浏览器资源失败"); }
            }
            WindowHandle = 0;
            lock (_sync) { if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null; }
            cancellation.Dispose();
            Report(finalMessage, ended: true);
            // 失败/取消仅在资源完全清理后通知启动调用方，便于立即安全重试。
            if (failure != null) ready.TrySetException(failure);
            else ready.TrySetCanceled(ct);
        }
    }

    private static (double X, double Y)? FindEnterPoint(ImageRegion region)
    {
        foreach (var name in new[] { "ChooseEnterGame", "EnterGame" })
        {
            using var match = region.Find(RecognitionAssets.Get("GameLoading", name, region));
            if (match.Width > 0 && match.Height > 0) return (match.X + match.Width / 2d, match.Y + match.Height / 2d);
        }
        if (Bv.IsInBlessingOfTheWelkinMoon(region)) return (100, 100);
        return null;
    }

    private void Report(string message, bool ended = false)
    {
        if (!ended && _lastStatus == message && _lastReportedReady == _ready) return;
        _lastStatus = message;
        _lastReportedReady = _ready;
        _logger.LogInformation("{CloudStatus}", message);
        try { StatusChanged?.Invoke(this, new CloudSessionStatus(message, _ready, ended)); }
        catch (Exception ex) { _logger.LogDebug(ex, "云游戏状态通知失败"); }
    }

    Task IHostedService.StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    Task IHostedService.StopAsync(CancellationToken cancellationToken) => StopSessionAsync(cancellationToken);
    public void Dispose() { lock (_sync) _disposed = true; RequestStop(); }
    public async ValueTask DisposeAsync() { Dispose(); await StopSessionAsync().ConfigureAwait(false); }
}
