using BetterGenshinImpact.Service.CloudGenshin;
using BetterGenshinImpact.Service.CloudGenshin.Browser;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.UnitTest.ServiceTests.CloudGenshin;

public class CloudPageDetectorTests
{
    [Theory]
    [InlineData(CloudPageState.Lobby, CloudPageAction.StartGame, true)]
    [InlineData(CloudPageState.QueueSelection, CloudPageAction.SelectNormalQueue, true)]
    [InlineData(CloudPageState.Connecting, CloudPageAction.ConfirmEnter, true)]
    [InlineData(CloudPageState.Unknown, CloudPageAction.StartGame, false)]
    [InlineData(CloudPageState.WaitingForLogin, CloudPageAction.StartGame, false)]
    [InlineData(CloudPageState.AgreementRequired, CloudPageAction.ConfirmEnter, false)]
    [InlineData(CloudPageState.Queuing, CloudPageAction.StartGame, false)]
    [InlineData(CloudPageState.Streaming, CloudPageAction.StartGame, false)]
    [InlineData(CloudPageState.QueueSelection, CloudPageAction.StartGame, false)]
    public void OnlyVerifiedExpectedActionsAreAllowed(CloudPageState state, CloudPageAction action, bool expected)
    {
        Assert.Equal(expected, CloudPageDetector.CanAutoClick(Snapshot(state, action)));
    }

    [Theory]
    [InlineData(double.NaN, 0, 100, 100)]
    [InlineData(-1, 0, 100, 100)]
    [InlineData(1900, 0, 100, 100)]
    [InlineData(0, 0, 0, 100)]
    [InlineData(0, double.PositiveInfinity, 100, 100)]
    public void InvalidBoundsAreRejected(double x, double y, double width, double height)
    {
        var snapshot = new CloudPageSnapshot
        {
            State = CloudPageState.Lobby, Action = CloudPageAction.StartGame, ActionVerified = true,
            ActionBounds = new(x, y, width, height), ViewportWidth = 1920, ViewportHeight = 1080
        };
        Assert.False(CloudPageDetector.CanAutoClick(snapshot));
    }

    [Fact]
    public async Task StateChangeBeforeClickDoesNotClick()
    {
        await using var browser = new FakeCloudBrowser(Snapshot(CloudPageState.Queuing, CloudPageAction.None));
        var clicked = await new CloudPageDetector().TryClickActionAsync(browser,
            Snapshot(CloudPageState.Lobby, CloudPageAction.StartGame), CancellationToken.None);
        Assert.False(clicked);
        Assert.Empty(browser.Clicks);
    }

    [Fact]
    public async Task NormalQueueUsesFreshBounds()
    {
        await using var browser = new FakeCloudBrowser(Snapshot(CloudPageState.QueueSelection, CloudPageAction.SelectNormalQueue));
        Assert.True(await new CloudPageDetector().TryClickActionAsync(browser,
            Snapshot(CloudPageState.QueueSelection, CloudPageAction.SelectNormalQueue), CancellationToken.None));
        Assert.Equal((150d, 225d), Assert.Single(browser.Clicks));
    }

    [Fact]
    public void MissingVerificationDoesNotClick()
    {
        Assert.False(CloudPageDetector.CanAutoClick(new CloudPageSnapshot
        {
            State = CloudPageState.Lobby, Action = CloudPageAction.StartGame,
            ActionBounds = new(0, 0, 100, 100), ViewportWidth = 1920, ViewportHeight = 1080
        }));
    }

    // 真机复现：浏览器刚启动时页面仍是 about:blank，被误判为“页面已离开官方云原神”。
    // 这里断言生产脚本真的先处理导航中状态，而不是另写一份 C# 副本自证。
    [Fact]
    public void ProbeScriptTreatsBlankPageAsNavigatingBeforeOffsiteCheck()
    {
        var script = CloudPageDetector.ProbeScript;
        var navigating = script.IndexOf("result.state='Navigating'", StringComparison.Ordinal);
        var offsite = script.IndexOf("页面已离开官方云原神", StringComparison.Ordinal);

        Assert.True(navigating >= 0, "探测脚本必须保留导航中状态，否则启动瞬间会被判为离站。");
        Assert.True(offsite > navigating, "导航中判定必须早于离站判定。");
        Assert.Contains("location.protocol === 'about:'", script);
        Assert.Contains("!location.hostname", script);
        // 登录会跳转到米哈游账号域，不能当作会话中断。
        Assert.Contains(@"mihoyo\.com|miyoushe\.com", script);
    }

    // 导航中不得触发任何自动点击：此时 DOM 还不是云原神页面。
    [Theory]
    [InlineData(CloudPageAction.StartGame)]
    [InlineData(CloudPageAction.SelectNormalQueue)]
    [InlineData(CloudPageAction.ConfirmEnter)]
    public void NavigatingStateNeverClicks(CloudPageAction action)
    {
        Assert.False(CloudPageDetector.CanAutoClick(Snapshot(CloudPageState.Navigating, action)));
    }

    internal static CloudPageSnapshot Snapshot(CloudPageState state, CloudPageAction action) => new()
    {
        State = state, Action = action, ActionVerified = true,
        ActionBounds = new(100, 200, 100, 50), ViewportWidth = 1920, ViewportHeight = 1080,
        DevicePixelRatio = 1
    };
}

internal sealed class FakeCloudBrowser(CloudPageSnapshot snapshot) : ICloudBrowser
{
    public List<(double X, double Y)> Clicks { get; } = [];
    public TaskCompletionSource Inspected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Disposed { get; private set; }
    public nint WindowHandle => 1;
    public int ProcessId => Environment.ProcessId;
    public bool IsConnected => !Disposed;
    public Task StartAsync(CloudBrowserOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
    public Task<JToken?> EvaluateAsync(string expression, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Inspected.TrySetResult();
        return Task.FromResult<JToken?>(JObject.FromObject(snapshot));
    }
    public Task<byte[]> CaptureScreenshotAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("此测试不应请求截图");
    public Task ClickAsync(double cssX, double cssY, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Clicks.Add((cssX, cssY));
        return Task.CompletedTask;
    }
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}
