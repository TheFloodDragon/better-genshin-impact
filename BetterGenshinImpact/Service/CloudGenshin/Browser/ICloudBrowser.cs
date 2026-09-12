using Newtonsoft.Json.Linq;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.Service.CloudGenshin.Browser;

/// <summary>
/// 只控制独立 Profile 中由 BetterGI 启动的网页云游戏，不接管日常浏览器。
/// </summary>
public interface ICloudBrowser : IAsyncDisposable
{
    nint WindowHandle { get; }
    int ProcessId { get; }
    bool IsConnected { get; }

    Task StartAsync(CloudBrowserOptions options, CancellationToken cancellationToken);
    Task<JToken?> EvaluateAsync(string expression, CancellationToken cancellationToken);
    Task<byte[]> CaptureScreenshotAsync(CancellationToken cancellationToken);
    Task ClickAsync(double cssX, double cssY, CancellationToken cancellationToken);
}

public sealed record CloudBrowserOptions(
    string BrowserPath,
    string ProfileDirectory,
    int ViewportWidth = 1920,
    int ViewportHeight = 1080)
{
    public const string GameUrl = "https://ys.mihoyo.com/cloud/";
}
