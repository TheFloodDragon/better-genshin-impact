using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Vanara.PInvoke;

namespace BetterGenshinImpact.Service.CloudGenshin.Browser;

public sealed class CloudBrowserSession(ILogger<CloudBrowserSession> logger) : ICloudBrowser
{
    private readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };
    private Process? _process;
    private FileStream? _profileLock;
    private CdpClient? _client;
    private int _disposed;
    private int _viewportWidth;
    private int _viewportHeight;
    private bool _mouseDown;

    public nint WindowHandle { get; private set; }
    public int ProcessId => _process?.Id ?? 0;
    public bool IsConnected => Volatile.Read(ref _disposed) == 0 && _client?.IsConnected == true && _process is { HasExited: false };

    public static string ResolveBrowserPath(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var path = Path.GetFullPath(configuredPath.Trim().Trim('"'));
            if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
                throw new FileNotFoundException("请配置有效的 Edge 或 Chrome 浏览器可执行文件。", path);
            return path;
        }
        var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) };
        foreach (var relative in new[] { @"Microsoft\Edge\Application\msedge.exe", @"Google\Chrome\Application\chrome.exe" })
        {
            var candidate = roots.Where(x => !string.IsNullOrEmpty(x)).Select(x => Path.Combine(x, relative)).FirstOrDefault(File.Exists);
            if (candidate != null) return candidate;
        }
        throw new FileNotFoundException("未找到 Edge 或 Chrome，请在网页云原神设置中指定浏览器路径。");
    }

    public static bool IsGameUrl(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https"
               && uri.Host.Equals("ys.mihoyo.com", StringComparison.OrdinalIgnoreCase)
               && (uri.AbsolutePath == "/cloud" || uri.AbsolutePath.StartsWith("/cloud/", StringComparison.Ordinal))
               && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo);
    }

    public static IReadOnlyList<string> BuildArguments(CloudBrowserOptions options)
    {
        return new[]
        {
            $"--app={CloudBrowserOptions.GameUrl}", $"--user-data-dir={Path.GetFullPath(options.ProfileDirectory)}",
            "--remote-debugging-port=0", "--remote-debugging-address=127.0.0.1", "--no-first-run",
            "--no-default-browser-check", "--lang=zh-CN", "--force-device-scale-factor=1", "--window-size=1920,1120",
            "--disable-backgrounding-occluded-windows", "--disable-renderer-backgrounding"
        };
    }

    public async Task StartAsync(CloudBrowserOptions options, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_process != null) throw new InvalidOperationException("浏览器会话已经启动。");
        if (options.ViewportWidth <= 0 || options.ViewportHeight <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        _viewportWidth = options.ViewportWidth;
        _viewportHeight = options.ViewportHeight;
        var executable = ResolveBrowserPath(options.BrowserPath);
        Directory.CreateDirectory(options.ProfileDirectory);
        try
        {
            try
            {
                _profileLock = new FileStream(Path.Combine(options.ProfileDirectory, "bettergi.session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex)
            {
                throw new IOException("网页云原神 Profile 正由另一个 BetterGI 会话使用，请先停止该会话。", ex);
            }
            var portFile = Path.Combine(options.ProfileDirectory, "DevToolsActivePort");
            if (File.Exists(portFile)) File.Delete(portFile);
            var info = new ProcessStartInfo(executable) { UseShellExecute = false };
            foreach (var argument in BuildArguments(options)) info.ArgumentList.Add(argument);
            cancellationToken.ThrowIfCancellationRequested();
            _process = Process.Start(info) ?? throw new IOException("无法启动云游戏浏览器。");
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(TimeSpan.FromSeconds(45));
            int port = 0;
            while (port == 0)
            {
                startup.Token.ThrowIfCancellationRequested();
                if (_process.HasExited) throw new IOException("独立浏览器启动失败或 Profile 已被其他浏览器占用。");
                try
                {
                    if (File.Exists(portFile))
                    {
                        var lines = await File.ReadAllLinesAsync(portFile, startup.Token).ConfigureAwait(false);
                        if (lines.Length >= 2 && int.TryParse(lines[0], out var parsed) && parsed is > 0 and <= 65535
                            && lines[1].StartsWith("/devtools/browser/", StringComparison.Ordinal)) port = parsed;
                    }
                }
                catch (IOException) { /* 浏览器可能正在写文件，等待完整内容。 */ }
                if (port == 0) await Task.Delay(200, startup.Token).ConfigureAwait(false);
            }
            Uri? targetEndpoint = null;
            while (targetEndpoint == null || WindowHandle == 0)
            {
                startup.Token.ThrowIfCancellationRequested();
                if (_process.HasExited) throw new IOException("云游戏浏览器已退出。");
                try
                {
                    var json = await _http.GetStringAsync($"http://127.0.0.1:{port}/json/list", startup.Token).ConfigureAwait(false);
                    var target = JArray.Parse(json).OfType<JObject>().FirstOrDefault(t => t["type"]?.Value<string>() == "page" && IsGameUrl(t["url"]?.Value<string>()));
                    if (Uri.TryCreate(target?["webSocketDebuggerUrl"]?.Value<string>(), UriKind.Absolute, out var endpoint)
                        && CdpClient.IsLoopbackEndpoint(endpoint) && endpoint.Port == port) targetEndpoint = endpoint;
                }
                catch (HttpRequestException) { /* 调试服务尚未完成启动。 */ }
                WindowHandle = FindOwnedWindow(_process.Id);
                if (targetEndpoint == null || WindowHandle == 0) await Task.Delay(200, startup.Token).ConfigureAwait(false);
            }
            _client = new CdpClient();
            await _client.ConnectAsync(targetEndpoint, startup.Token).ConfigureAwait(false);
            await _client.SendAsync("Emulation.setDeviceMetricsOverride", new JObject
            {
                ["width"] = options.ViewportWidth, ["height"] = options.ViewportHeight,
                ["deviceScaleFactor"] = 1, ["mobile"] = false
            }, startup.Token).ConfigureAwait(false);
            logger.LogInformation("独立云游戏浏览器已连接，进程 {ProcessId}", _process.Id);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await DisposeAsync().ConfigureAwait(false);
            throw new TimeoutException("启动网页云原神浏览器超时。");
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static nint FindOwnedWindow(int processId)
    {
        nint handle = 0;
        long largestArea = 0;
        User32.EnumWindows((window, _) =>
        {
            User32.GetWindowThreadProcessId(window, out var pid);
            if (pid == processId && User32.IsWindowVisible(window))
            {
                User32.GetClientRect(window, out var rect);
                var area = (long)rect.Width * rect.Height;
                if (area > largestArea)
                {
                    largestArea = area;
                    handle = (nint)window;
                }
            }
            return true;
        }, IntPtr.Zero);
        return handle;
    }

    public async Task<JToken?> EvaluateAsync(string expression, CancellationToken cancellationToken)
    {
        var client = _client ?? throw new IOException("浏览器尚未连接。");
        var result = await client.SendAsync("Runtime.evaluate", new JObject
        {
            ["expression"] = expression, ["returnByValue"] = true, ["awaitPromise"] = true
        }, cancellationToken).ConfigureAwait(false);
        if (result["exceptionDetails"] != null) throw new IOException("云游戏页面探测脚本执行失败，页面结构可能已变化。");
        return result["result"]?["value"];
    }

    public async Task<byte[]> CaptureScreenshotAsync(CancellationToken cancellationToken)
    {
        if (_client == null) throw new IOException("浏览器尚未连接。");
        if (User32.IsIconic(WindowHandle))
            throw new InvalidOperationException("网页云原神窗口已最小化，请恢复窗口后重试；最小化可能暂停视频渲染。");
        var result = await _client.SendAsync("Page.captureScreenshot", new JObject
        {
            ["format"] = "jpeg", ["quality"] = 100, ["fromSurface"] = true, ["captureBeyondViewport"] = false
        }, cancellationToken).ConfigureAwait(false);
        var data = result["data"]?.Value<string>();
        if (string.IsNullOrEmpty(data)) throw new IOException("云游戏浏览器返回空截图。");
        return Convert.FromBase64String(data);
    }

    public async Task ClickAsync(double cssX, double cssY, CancellationToken cancellationToken)
    {
        if (!double.IsFinite(cssX) || !double.IsFinite(cssY) || cssX < 0 || cssY < 0 || cssX >= _viewportWidth || cssY >= _viewportHeight)
            throw new ArgumentOutOfRangeException(nameof(cssX), "云游戏点击位置超出浏览器视口。");
        if (_client == null) throw new IOException("浏览器尚未连接。");
        var currentUrl = await EvaluateAsync("location.href", cancellationToken).ConfigureAwait(false);
        if (!IsGameUrl(currentUrl?.Value<string>())) throw new InvalidOperationException("页面已离开云原神，拒绝发送输入。");
        await DispatchMouseAsync("mouseMoved", cssX, cssY, 0, cancellationToken).ConfigureAwait(false);
        try
        {
            _mouseDown = true;
            await DispatchMouseAsync("mousePressed", cssX, cssY, 1, cancellationToken).ConfigureAwait(false);
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (_mouseDown)
            {
                try
                {
                    using var release = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await DispatchMouseAsync("mouseReleased", cssX, cssY, 0, release.Token).ConfigureAwait(false);
                }
                finally { _mouseDown = false; }
            }
        }
    }

    private Task<JObject> DispatchMouseAsync(string type, double x, double y, int buttons, CancellationToken cancellationToken)
    {
        return _client!.SendAsync("Input.dispatchMouseEvent", new JObject
        {
            ["type"] = type, ["x"] = x, ["y"] = y, ["button"] = type == "mouseMoved" ? "none" : "left",
            ["buttons"] = buttons, ["clickCount"] = type == "mouseMoved" ? 0 : 1, ["pointerType"] = "mouse"
        }, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (_process is { HasExited: false })
            {
                _process.CloseMainWindow();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                    using var killed = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    try { await _process.WaitForExitAsync(killed.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { logger.LogWarning("云游戏浏览器尚未完全退出。"); }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning(ex, "关闭自建云游戏浏览器时发生异常。");
        }
        finally
        {
            if (_client != null) await _client.DisposeAsync().ConfigureAwait(false);
            _http.Dispose();
            _process?.Dispose();
            _profileLock?.Dispose();
            _process = null;
            WindowHandle = 0;
        }
    }
}
