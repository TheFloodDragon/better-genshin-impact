using OpenCvSharp;
using Vanara.PInvoke;

namespace Fischless.GameCapture;

public sealed class GameCaptureFrame : IDisposable
{
    public GameCaptureFrame(Mat frame, RECT? captureRect = null, bool isDesktopCapture = true, long sequenceNumber = 0, DateTimeOffset? capturedAt = null)
    {
        Frame = frame;
        CaptureRect = captureRect;
        IsDesktopCapture = isDesktopCapture;
        SequenceNumber = sequenceNumber;
        CapturedAt = capturedAt ?? DateTimeOffset.UtcNow;
    }

    /// <summary>逻辑视口截图不能沿用桌面点击坐标链。</summary>
    public bool IsDesktopCapture { get; }
    public long SequenceNumber { get; }
    public DateTimeOffset CapturedAt { get; }

    public Mat Frame { get; }

    public RECT? CaptureRect { get; }

    public void Dispose()
    {
        Frame.Dispose();
        GC.SuppressFinalize(this);
    }
}
