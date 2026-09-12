using BetterGenshinImpact.Service.CloudGenshin;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.ServiceTests.CloudGenshin;

public class CloudEntryDeadlineTests
{
    private static CloudEntryTimeouts Timeouts => new(TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(60), TimeSpan.FromSeconds(90), TimeSpan.FromMinutes(5));

    [Fact]
    public void QueueTimeDoesNotConsumeEnterTimeout()
    {
        var clock = new FakeTimeProvider();
        var deadline = new CloudEntryDeadline(Timeouts, clock);
        deadline.Observe(CloudPageState.Queuing);
        clock.Advance(TimeSpan.FromMinutes(30));
        deadline.Observe(CloudPageState.Streaming);
        clock.Advance(TimeSpan.FromMinutes(4));
        deadline.Observe(CloudPageState.Streaming);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Throws<TimeoutException>(() => deadline.Observe(CloudPageState.Streaming));
    }

    [Fact]
    public void UnknownAndStateFluctuationsDoNotResetQueueBudget()
    {
        var clock = new FakeTimeProvider();
        var deadline = new CloudEntryDeadline(Timeouts, clock);
        deadline.Observe(CloudPageState.Queuing);
        clock.Advance(TimeSpan.FromMinutes(40));
        deadline.Observe(CloudPageState.Unknown);
        clock.Advance(TimeSpan.FromMinutes(19));
        deadline.Observe(CloudPageState.Lobby);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Throws<TimeoutException>(() => deadline.Observe(CloudPageState.Queuing));
    }

    [Fact]
    public void ConnectionBudgetDoesNotIncludeLoginOrQueueTime()
    {
        var clock = new FakeTimeProvider();
        var deadline = new CloudEntryDeadline(Timeouts, clock);
        deadline.Observe(CloudPageState.Connecting);
        clock.Advance(TimeSpan.FromSeconds(10));
        deadline.Observe(CloudPageState.WaitingForLogin);
        clock.Advance(TimeSpan.FromMinutes(10));
        deadline.Observe(CloudPageState.Queuing);
        clock.Advance(TimeSpan.FromMinutes(20));
        deadline.Observe(CloudPageState.Connecting);
        clock.Advance(TimeSpan.FromSeconds(70));
        deadline.Observe(CloudPageState.Connecting);
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Throws<TimeoutException>(() => deadline.Observe(CloudPageState.Connecting));
    }

    // 导航中若不计入任何预算，页面卡在 about:blank 时会无限等待。
    [Fact]
    public void NavigatingIsBoundedByConnectTimeout()
    {
        var clock = new FakeTimeProvider();
        var deadline = new CloudEntryDeadline(Timeouts, clock);
        deadline.Observe(CloudPageState.Navigating);
        clock.Advance(TimeSpan.FromSeconds(80));
        deadline.Observe(CloudPageState.Navigating);
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Throws<TimeoutException>(() => deadline.Observe(CloudPageState.Navigating));
    }

    [Fact]
    public void InvalidTimeoutIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CloudEntryDeadline(Timeouts with { Queue = TimeSpan.Zero }));
    }
}
