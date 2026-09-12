using BetterGenshinImpact.Service.CloudGenshin.Browser;

namespace BetterGenshinImpact.UnitTest.ServiceTests.CloudGenshin;

public class CloudBrowserTests
{
    [Theory]
    [InlineData("ws://127.0.0.1:9222/devtools/page/test", true)]
    [InlineData("ws://192.168.1.1:9222/devtools/page/test", false)]
    [InlineData("ws://example.com/devtools/page/test", false)]
    [InlineData("https://127.0.0.1:9222/", false)]
    [InlineData("ws://user:pass@127.0.0.1:9222/devtools/page/test", false)]
    public void RemoteDebuggingOnlyAcceptsLoopback(string url, bool allowed)
    {
        Assert.Equal(allowed, CdpClient.IsLoopbackEndpoint(new Uri(url)));
    }

    [Theory]
    [InlineData("https://ys.mihoyo.com/cloud/", true)]
    [InlineData("https://ys.mihoyo.com/cloud", true)]
    [InlineData("https://ys.mihoyo.com/cloud/#/", true)]
    [InlineData("https://ys.mihoyo.com.evil.example/cloud/", false)]
    [InlineData("https://ys.mihoyo.com/cloud-other/", false)]
    [InlineData("http://ys.mihoyo.com/cloud/", false)]
    [InlineData("https://user:pass@ys.mihoyo.com/cloud/", false)]
    public void OnlyOfficialGamePageCanReceiveInput(string url, bool allowed)
    {
        Assert.Equal(allowed, CloudBrowserSession.IsGameUrl(url));
    }

    [Fact]
    public void LaunchArgumentsUsePrivateProfileWithoutDisablingSandbox()
    {
        var profile = Path.Combine(Path.GetTempPath(), "cloud profile");
        var args = CloudBrowserSession.BuildArguments(new CloudBrowserOptions("", profile));
        Assert.Contains("--remote-debugging-port=0", args);
        Assert.Contains("--remote-debugging-address=127.0.0.1", args);
        Assert.Contains($"--user-data-dir={Path.GetFullPath(profile)}", args);
        Assert.DoesNotContain("--no-sandbox", args);
        Assert.DoesNotContain(args, x => x.Contains("AutomationControlled"));
    }

    [Fact]
    public async Task DisposeUnconnectedClientIsIdempotent()
    {
        var client = new CdpClient();
        await Task.WhenAll(client.DisposeAsync().AsTask(), client.DisposeAsync().AsTask());
        Assert.False(client.IsConnected);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.SendAsync("Page.enable", null, CancellationToken.None));
    }
}
