using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Service.CloudGenshin.Browser;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.Service.CloudGenshin;

public sealed record CloudSessionStatus(string Message, bool Ready, bool Ended);

/// <summary>串行拥有页面探测、输入与截图，不占用 WPF UI 线程或任务触发器的锁。</summary>
public sealed class CloudGenshinService : IHostedService, IDisposable, IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly ILogger<CloudGenshinService> _logger;
    private readonly Func<ICloudBrowser> _browserFactory;
    private readonly CloudPageDetector _detector = new();
    private CancellationTokenSource? _cancellation;
    private Task _runTask = Task.CompletedTask;
    private string _lastStatus = "";
    private volatile bool _ready;

    public CloudGenshinService(ILogger<CloudGenshinService> logger, ILogger<CloudBrowserSession> browserLogger)
        : this(logger, () => new CloudBrowserSession(browserLogger)) { }

    public CloudGenshinService(ILogger<CloudGenshinService> logger, Func<ICloudBrowser> browserFactory)
    {
        _logger = logger;
        _browserFactory = browserFactory;
    }

    public CloudGameCapture Capture { get; } = new();
    public bool IsRunning { get { lock (_sync) return !_runTask.IsCompleted; } }
    public bool IsReady => _ready;
    public nint WindowHandle { get; private set; }
    public event EventHandler<CloudSessionStatus>? StatusChanged;

    public Task StartSessionAsync(GenshinStartConfig config, Func<nint, int, Task> attach, CancellationToken cancellationToken = default)
    {
        var timeouts = new CloudEntryTimeouts(TimeSpan.FromMinutes(config.CloudLoginTimeoutMinutes),
            TimeSpan.FromMinutes(config.CloudQueueTimeoutMinutes), TimeSpan.FromSeconds(config.CloudConnectTimeoutSeconds),
            TimeSpan.FromMinutes(config.CloudEnterTimeoutMinutes));
        timeouts.Validate();
        var options = new CloudBrowserOptions(config.CloudBrowserPath,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BetterGI", "CloudGenshin", "Profile"));
        var autoEnter = config.AutoEnterGameEnabled;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            if (!_runTask.IsCompleted) throw new InvalidOperationException("网页云原神会话正在运行或停止中，请勿重复启动。");
            _ready = false;
            _lastStatus = "";
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var cancellation = _cancellation;
            _runTask = Task.Run(() => RunAsync(options, timeouts, autoEnter, attach, ready, cancellation));
        }
        return ready.Task;
    }

    public void RequestStop()
    {
        lock (_sync)
        {
            _cancellation?.Cancel();
        }
    }

    public async Task StopSessionAsync(CancellationToken cancellationToken = default)
    {
        Task run;
        lock (_sync)
        {
            _cancellation?.Cancel();
            run = _runTask;
        }
        await run.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunAsync(CloudBrowserOptions options, CloudEntryTimeouts timeouts, bool autoEnter,
        Func<nint, int, Task> attach, TaskCompletionSource ready, CancellationTokenSource cancellation)
    {
        var ct = cancellation.Token;
        ICloudBrowser? browser = null;
        var finalMessage = "网页云原神已停止";
        try
        {
            Report("正在启动独立云游戏浏览器");
            browser = _browserFactory();
            await browser.StartAsync(options, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            WindowHandle = browser.WindowHandle;
            Capture.Start(WindowHandle);
            await attach(WindowHandle, browser.ProcessId).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var deadline = new CloudEntryDeadline(timeouts);
            var lastAction = DateTimeOffset.MinValue;
            var lastFrameTime = DateTimeOffset.UtcNow;
            double? streamTime = null;
            var lastStreamAdvance = DateTimeOffset.UtcNow;
            var stableMainFrames = 0;
            var offsiteObservations = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (!browser.IsConnected) throw new IOException("网页云原神浏览器已关闭或连接中断。");
                var snapshot = await _detector.InspectAsync(browser, ct).ConfigureAwait(false);
                if (snapshot.State is CloudPageState.TimeExhausted or CloudPageState.Maintenance)
                    throw new InvalidOperationException(snapshot.Message);
                // 页面跳转期间可能瞬时离站，连续多次才判定会话真的断了。
                if (snapshot.State == CloudPageState.Disconnected)
                {
                    if (++offsiteObservations >= 3) throw new InvalidOperationException(snapshot.Message);
                }
                else offsiteObservations = 0;
                if (!_ready) deadline.Observe(snapshot.State);
                Report(_ready && snapshot.State == CloudPageState.Streaming
                    ? "云原神已就绪：支持截图与图像识别，旧自动任务暂未适配" : snapshot.Message);

                if (!_ready && CloudPageDetector.CanAutoClick(snapshot) && DateTimeOffset.UtcNow - lastAction >= TimeSpan.FromSeconds(5))
                {
                    if (await _detector.TryClickActionAsync(browser, snapshot, ct).ConfigureAwait(false))
                        lastAction = DateTimeOffset.UtcNow;
                }

                if (snapshot.State == CloudPageState.Streaming && snapshot.StreamReady && snapshot.GameBounds != null)
                {
                    if (snapshot.StreamTime is { } playbackTime)
                    {
                        if (streamTime == null || playbackTime != streamTime) lastStreamAdvance = DateTimeOffset.UtcNow;
                        streamTime = playbackTime;
                        if (DateTimeOffset.UtcNow - lastStreamAdvance > TimeSpan.FromSeconds(20))
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
                        var normalized = new Mat();
                        try
                        {
                            Cv2.Resize(crop, normalized, new Size(CloudViewportTransform.ImageWidth, CloudViewportTransform.ImageHeight));
                        }
                        catch { normalized.Dispose(); throw; }
                        Capture.Publish(normalized, transform);
                        lastFrameTime = DateTimeOffset.UtcNow;
                        if (!_ready)
                        {
                            using var frame = Capture.CaptureWithTransform();
                            if (frame != null)
                            {
                                // 区域拥有自己的克隆，不与帧缓存共享可释放的 Mat。
                                using var region = new GameCaptureRegion(frame.Image.Clone(), 0, 0);
                                if (Bv.IsInMainUi(region))
                                {
                                    stableMainFrames++;
                                    if (stableMainFrames >= 2)
                                    {
                                        _ready = true;
                                        ready.TrySetResult();
                                        Report("云原神已就绪：支持截图与图像识别，旧自动任务暂未适配");
                                    }
                                }
                                else
                                {
                                    stableMainFrames = 0;
                                    if (autoEnter && DateTimeOffset.UtcNow - lastAction >= TimeSpan.FromSeconds(3))
                                    {
                                        var point = FindEnterPoint(region);
                                        if (point is { } position)
                                        {
                                            var current = await _detector.InspectAsync(browser, ct).ConfigureAwait(false);
                                            if (current.State == CloudPageState.Streaming && current.StreamReady && transform.Matches(current))
                                            {
                                                var css = transform.ToCss(position.X, position.Y);
                                                await browser.ClickAsync(css.X, css.Y, ct).ConfigureAwait(false);
                                                lastAction = DateTimeOffset.UtcNow;
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
                    lastStreamAdvance = DateTimeOffset.UtcNow;
                }
                if (_ready && DateTimeOffset.UtcNow - lastFrameTime > TimeSpan.FromSeconds(20))
                    throw new IOException("超过 20 秒未取得有效游戏画面，已停止会话，请人工检查浏览器。");
                await Task.Delay(snapshot.State == CloudPageState.Queuing ? 5000 : snapshot.State == CloudPageState.Streaming ? 300 : 1000, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            finalMessage = "网页云原神已取消";
            ready.TrySetCanceled(ct);
        }
        catch (Exception ex)
        {
            finalMessage = $"网页云原神已停止：{ex.Message}";
            _logger.LogWarning(ex, "网页云原神会话结束");
            ready.TrySetException(ex);
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
            lock (_sync)
            {
                if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
                cancellation.Dispose();
            }
            Report(finalMessage, ended: true);
        }
    }

    private static (double X, double Y)? FindEnterPoint(ImageRegion region)
    {
        foreach (var name in new[] { "ChooseEnterGame", "EnterGame" })
        {
            using var match = region.Find(RecognitionAssets.Get("GameLoading", name, region));
            if (match.Width > 0 && match.Height > 0) return (match.X + match.Width / 2d, match.Y + match.Height / 2d);
        }
        // 月卡奖励是已知游戏内阻挡，不处理未知确认、协议或费用弹窗。
        if (Bv.IsInBlessingOfTheWelkinMoon(region)) return (100, 100);
        return null;
    }

    private void Report(string message, bool ended = false)
    {
        if (!ended && _lastStatus == message) return;
        _lastStatus = message;
        _logger.LogInformation("{CloudStatus}", message);
        try { StatusChanged?.Invoke(this, new CloudSessionStatus(message, _ready, ended)); }
        catch (Exception ex) { _logger.LogDebug(ex, "云游戏状态通知失败"); }
    }

    Task IHostedService.StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    Task IHostedService.StopAsync(CancellationToken cancellationToken) => StopSessionAsync(cancellationToken);
    public void Dispose() => RequestStop();
    public async ValueTask DisposeAsync() => await StopSessionAsync().ConfigureAwait(false);
}
