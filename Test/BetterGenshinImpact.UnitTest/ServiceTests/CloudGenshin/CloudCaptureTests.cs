using BetterGenshinImpact.Service.CloudGenshin;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.ServiceTests.CloudGenshin;

public class CloudCaptureTests
{
    internal static CloudPageSnapshot Viewport => new()
    {
        State = CloudPageState.Streaming, StreamReady = true,
        GameBounds = new(320, 180, 1280, 720), ViewportWidth = 1920, ViewportHeight = 1080, DevicePixelRatio = 2
    };

    [Fact]
    public void CropAndClickUseSameGeometry()
    {
        var transform = CloudViewportTransform.Create(Viewport, 3840, 2160);
        Assert.Equal(new Rect(640, 360, 2560, 1440), transform.SourceRect);
        Assert.Equal((960d, 540d), transform.ToCss(960, 540));
        Assert.Equal((320d, 180d), transform.ToCss(0, 0));
        Assert.True(transform.Matches(Viewport));
    }

    [Fact]
    public void NonWidescreenContentIsNotStretched()
    {
        var snapshot = new CloudPageSnapshot { GameBounds = new(0, 0, 800, 600), ViewportWidth = 1920, ViewportHeight = 1080 };
        Assert.Throws<ArgumentException>(() => CloudViewportTransform.Create(snapshot, 1920, 1080));
    }

    [Fact]
    public void InvalidCoordinatesAndChangedViewportAreRejected()
    {
        var transform = CloudViewportTransform.Create(Viewport, 1920, 1080);
        Assert.Throws<ArgumentOutOfRangeException>(() => transform.ToCss(double.NaN, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => transform.ToCss(1920, 1080));
        Assert.False(transform.Matches(new CloudPageSnapshot { GameBounds = new(0, 0, 1920, 1080), ViewportWidth = 1920, ViewportHeight = 1080 }));
    }

    [Fact]
    public void ConsumersHaveIndependentOwnershipAndFramesExpire()
    {
        var clock = new FakeTimeProvider();
        using var capture = new CloudGameCapture(clock);
        capture.Start(1);
        var transform = CloudViewportTransform.Create(Viewport, 1920, 1080);
        capture.Publish(new Mat(8, 8, MatType.CV_8UC3, Scalar.White), transform);
        using var first = capture.Capture()!;
        Assert.False(first.IsDesktopCapture);
        Assert.Equal(1, first.SequenceNumber);
        first.Frame.SetTo(Scalar.Black);
        using var second = capture.Capture()!;
        Assert.Equal(255, second.Frame.At<Vec3b>(0, 0).Item0);
        capture.Publish(new Mat(8, 8, MatType.CV_8UC3, Scalar.Black), transform);
        Assert.Equal(255, second.Frame.At<Vec3b>(0, 0).Item0);
        using var third = capture.Capture()!;
        Assert.Equal(2, third.SequenceNumber);
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Null(capture.Capture());
        capture.Stop();
        Assert.False(capture.IsCapturing);
        Assert.Null(capture.CaptureWithTransform());
    }
}
