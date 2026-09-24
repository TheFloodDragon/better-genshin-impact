using BetterGenshinImpact.Service.CloudGenshin;
using BetterGenshinImpact.Service.CloudGenshin.Browser;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.ServiceTests.CloudGenshin;

/// <summary>
/// 真机探测：启动真实 Edge 与官方页面，只读校验启动健康检查与状态识别。
/// 默认跳过；通过 BGI_CLOUD_LIVE_PROBE=1 显式启用。
///
/// 刻意不使用 CloudGenshinService：本探测绝不自动点击、不进入排队、不消耗云游戏时长，
/// 也不提交任何账号信息。截图只统计尺寸与亮度，不落盘，避免账号信息写入磁盘。
/// </summary>
public class CloudLiveEntryProbe
{
    [Fact]
    public async Task InspectRealCloudPageWithoutEnteringGame()
    {
        if (Environment.GetEnvironmentVariable("BGI_CLOUD_LIVE_PROBE") != "1") return;

        var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BetterGI", "CloudGenshin", "Profile");
        var options = new CloudBrowserOptions("", profile);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var browser = new CloudBrowserSession(NullLogger<CloudBrowserSession>.Instance);

        var startedAt = DateTimeOffset.UtcNow;
        await browser.StartAsync(options, cts.Token);
        Log($"startup ok in {(DateTimeOffset.UtcNow - startedAt).TotalSeconds:F1}s " +
            $"pid={browser.ProcessId} hwnd={browser.WindowHandle} connected={browser.IsConnected}");

        // 启动健康检查：进程、窗口与调试连接必须同时成立。
        Assert.True(browser.IsConnected);
        Assert.True(browser.ProcessId > 0);
        Assert.NotEqual(0, browser.WindowHandle);

        var detector = new CloudPageDetector();
        CloudPageSnapshot? last = null;
        var seen = new List<CloudPageState>();

        // 页面需要时间完成导航与首屏渲染，这里持续观察状态迁移。
        for (var i = 0; i < 20; i++)
        {
            var snapshot = await detector.InspectAsync(browser, cts.Token);
            last = snapshot;
            if (seen.Count == 0 || seen[^1] != snapshot.State)
            {
                seen.Add(snapshot.State);
                Log($"#{i} state={snapshot.State} action={snapshot.Action} verified={snapshot.ActionVerified} " +
                    $"loginFrame={snapshot.HasLoginFrame} viewport={snapshot.ViewportWidth}x{snapshot.ViewportHeight} " +
                    $"dpr={snapshot.DevicePixelRatio} stream={snapshot.StreamReady} msg={snapshot.Message}");
            }
            // 已登录时立刻停止：继续推进会真实进入排队并消耗云游戏时长。
            if (snapshot.State is CloudPageState.LoginRequired or CloudPageState.WaitingForLogin
                or CloudPageState.VerificationRequired or CloudPageState.Lobby
                or CloudPageState.QueueSelection or CloudPageState.Streaming) break;
            await Task.Delay(1500, cts.Token);
        }

        Assert.NotNull(last);
        Log($"states={string.Join(" -> ", seen)}");

        // 关键回归：启动后不得停留在 Unknown/Navigating，否则说明识别不到任何入口（修复前的真机表现）。
        Assert.DoesNotContain(last!.State, new[] { CloudPageState.Unknown, CloudPageState.Navigating });

        // 视口应被 CDP 覆盖为 1920x1080，且缩放为 1，保证后续识图坐标可用。
        Assert.Equal(1920, last.ViewportWidth);
        Assert.Equal(1080, last.ViewportHeight);
        Assert.Equal(1, last.DevicePixelRatio);

        var shot = await browser.CaptureScreenshotAsync(cts.Token);
        using var image = Cv2.ImDecode(shot, ImreadModes.Color);
        var mean = Cv2.Mean(image);
        // 只记录统计值，不保存图像，避免已登录页面的账号信息落盘。
        Log($"screenshot {image.Width}x{image.Height} bytes={shot.Length} " +
            $"mean={mean.Val0 + mean.Val1 + mean.Val2:F1}");
        Assert.Equal(1920, image.Width);
        Assert.Equal(1080, image.Height);
        Assert.False(image.Empty());

        if (last.State == CloudPageState.LoginRequired)
        {
            // 未登录：确认登录入口已被识别并允许自动点击（修复前此处无法识别，流程会卡死）。
            Assert.True(CloudPageDetector.CanAutoClick(last));
            Assert.Equal(CloudPageAction.OpenLogin, last.Action);
            Log("login entry detected and clickable (no click issued in probe)");
        }
        else
        {
            Log($"already-authenticated or manual stage: {last.State}; probe stops without input");
        }
    }

    private static void Log(string line)
    {
        var path = Path.Combine(Path.GetTempPath(), "bgi-cloud-live-probe.log");
        File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
    }
}
