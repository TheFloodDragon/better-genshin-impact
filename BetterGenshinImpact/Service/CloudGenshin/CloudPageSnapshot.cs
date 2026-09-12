using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;

namespace BetterGenshinImpact.Service.CloudGenshin;

[JsonConverter(typeof(StringEnumConverter))]
public enum CloudPageState
{
    Unknown, Navigating, WaitingForLogin, AgreementRequired, Lobby, QueueSelection, Queuing,
    Connecting, Streaming, TimeExhausted, Maintenance, Disconnected
}

[JsonConverter(typeof(StringEnumConverter))]
public enum CloudPageAction
{
    None, StartGame, SelectNormalQueue, ConfirmEnter, DismissReward
}

public sealed record CloudPageRect(double X, double Y, double Width, double Height)
{
    [JsonIgnore]
    public bool IsValid => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width)
                           && double.IsFinite(Height) && X >= 0 && Y >= 0 && Width > 0 && Height > 0;

    public bool IsInside(double width, double height) => IsValid && double.IsFinite(width) && double.IsFinite(height)
        && X + Width <= width + 0.01 && Y + Height <= height + 0.01;
}

public sealed class CloudPageSnapshot
{
    public CloudPageState State { get; init; }
    public CloudPageAction Action { get; init; }
    public bool ActionVerified { get; init; }
    public string Message { get; init; } = "";
    public CloudPageRect? ActionBounds { get; init; }
    public CloudPageRect? GameBounds { get; init; }
    public double ViewportWidth { get; init; }
    public double ViewportHeight { get; init; }
    public double DevicePixelRatio { get; init; }
    public bool StreamReady { get; init; }
    public double? StreamTime { get; init; }
}
