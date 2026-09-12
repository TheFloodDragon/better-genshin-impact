using BetterGenshinImpact.Service.CloudGenshin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.UnitTest.ServiceTests.CloudGenshin;

/// <summary>
/// 真机复现：登录后官网弹出"每日登陆奖励"弹窗，此前被归入 AgreementRequired 导致自动流程停在大厅。
/// 这里锁定关闭该弹窗所需的三个环节：枚举可序列化、白名单放行、探测脚本包含匹配规则。
/// </summary>
public class DailyRewardDismissTests
{
    [Fact]
    public void DismissRewardIsAllowedOnlyInLobby()
    {
        Assert.True(CloudPageDetector.CanAutoClick(CloudPageDetectorTests.Snapshot(CloudPageState.Lobby, CloudPageAction.DismissReward)));
        // 弹窗只会出现在大厅；其他阶段即使探测返回同名动作也不得点击。
        Assert.False(CloudPageDetector.CanAutoClick(CloudPageDetectorTests.Snapshot(CloudPageState.Queuing, CloudPageAction.DismissReward)));
        Assert.False(CloudPageDetector.CanAutoClick(CloudPageDetectorTests.Snapshot(CloudPageState.Streaming, CloudPageAction.DismissReward)));
        Assert.False(CloudPageDetector.CanAutoClick(CloudPageDetectorTests.Snapshot(CloudPageState.AgreementRequired, CloudPageAction.DismissReward)));
    }

    [Fact]
    public void DismissRewardRoundTripsThroughJson()
    {
        // 探测脚本以字符串返回动作名，StringEnumConverter 必须能还原为枚举，否则会静默落回 None。
        var json = JObject.Parse("""{"state":"Lobby","action":"DismissReward","actionVerified":true}""");
        var snapshot = json.ToObject<CloudPageSnapshot>();
        Assert.NotNull(snapshot);
        Assert.Equal(CloudPageAction.DismissReward, snapshot!.Action);
        Assert.Equal("\"DismissReward\"", JsonConvert.SerializeObject(CloudPageAction.DismissReward));
    }

    [Fact]
    public void ProbeScriptMatchesDailyRewardDialogBeforeGenericFallback()
    {
        var script = CloudPageDetector.ProbeScript;
        var reward = script.IndexOf("DismissReward", StringComparison.Ordinal);
        var fallback = script.IndexOf("页面弹窗需要人工确认", StringComparison.Ordinal);

        Assert.True(reward >= 0, "探测脚本必须包含每日奖励弹窗的处理分支。");
        Assert.True(fallback > reward, "每日奖励分支必须早于通用弹窗回退，否则仍会被判为需要人工确认。");
        // 真机弹窗标题为"每日登陆奖励"，官网文案历史上"登陆/登录"混用，两种写法都要覆盖。
        Assert.Contains("每日登[陆录]奖励", script);
        Assert.Contains("我知道了", script);
    }

    [Fact]
    public void DailyRewardIsNotConfusedWithTimeExhausted()
    {
        // 真机文案："免费时长已达到上限，本次领取 11 分钟"——包含"免费时长"但不是时长耗尽。
        // 时长耗尽判定必须先于奖励判定，且不能被"已达到上限"误触发。
        var script = CloudPageDetector.ProbeScript;
        var exhausted = script.IndexOf("TimeExhausted", StringComparison.Ordinal);
        var reward = script.IndexOf("DismissReward", StringComparison.Ordinal);
        Assert.True(exhausted >= 0 && exhausted < reward, "时长耗尽判定应先于奖励弹窗判定。");
        Assert.DoesNotContain("已达到上限", script);
    }
}
