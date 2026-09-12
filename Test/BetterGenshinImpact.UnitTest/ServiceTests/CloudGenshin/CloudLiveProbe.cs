using BetterGenshinImpact.Service.CloudGenshin;
using BetterGenshinImpact.Service.CloudGenshin.Browser;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.ServiceTests.CloudGenshin;

/// <summary>
/// 临时真机验证：需要网络与本机 Edge，默认不参与常规测试运行。
/// 通过 BGI_CLOUD_LIVE=1 显式启用。
/// </summary>
public class CloudLiveProbe
{
    [Fact]
    public async Task LaunchInspectAndCapture()
    {
        // 默认跳过，且跳过时不产生任何副作用（不写日志文件）。
        if (Environment.GetEnvironmentVariable("BGI_CLOUD_LIVE") != "1") return;
        Log($"enter temp={Path.GetTempPath()}");

        var profile = Path.Combine(Path.GetTempPath(), "bgi-cloud-live-probe");
        var options = new CloudBrowserOptions("", profile);
        await using var browser = new CloudBrowserSession(NullLogger<CloudBrowserSession>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await browser.StartAsync(options, cts.Token);
        Log($"browser pid={browser.ProcessId} hwnd={browser.WindowHandle}");

        // 直接取原始环境信息，先确认 URL 与视口是否符合预期。
        for (var i = 0; i < 6; i++)
        {
            var raw = await browser.EvaluateAsync(
                "JSON.stringify({href:location.href,proto:location.protocol,host:location.hostname," +
                "path:location.pathname,hash:location.hash,iw:innerWidth,ih:innerHeight," +
                "ow:outerWidth,oh:outerHeight,dpr:devicePixelRatio,ready:document.readyState," +
                "cw:document.documentElement.clientWidth,ch:document.documentElement.clientHeight})",
                cts.Token);
            Log($"#{i} env={raw}");
            await Task.Delay(2000, cts.Token);
        }

        var snapshot = await new CloudPageDetector().InspectAsync(browser, cts.Token);
        Log($"snapshot state={snapshot.State} action={snapshot.Action} verified={snapshot.ActionVerified} " +
            $"viewport={snapshot.ViewportWidth}x{snapshot.ViewportHeight} dpr={snapshot.DevicePixelRatio} " +
            $"streamReady={snapshot.StreamReady} game={snapshot.GameBounds} msg={snapshot.Message}");

        var bytes = await browser.CaptureScreenshotAsync(cts.Token);
        using var image = Cv2.ImDecode(bytes, ImreadModes.Color);
        Log($"shot {image.Width}x{image.Height} bytes={bytes.Length}");
        var shot = Path.Combine(Path.GetTempPath(), "bgi-cloud-live-shot.png");
        Cv2.ImWrite(shot, image);
        Log($"saved {shot}");
    }

    // xUnit 不直接透出 Console，写文件便于读取；同时避免中文控制台编码问题。
    private static void Log(string line)
    {
        var path = Path.Combine(Path.GetTempPath(), "bgi-cloud-live.log");
        File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
    }
}
