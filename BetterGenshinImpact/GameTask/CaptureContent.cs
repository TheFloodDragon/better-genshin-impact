using BetterGenshinImpact.GameTask.Model.Area;
using System;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask;

/// <summary>
/// 捕获的内容
/// 以及一些多个trigger会用到的内容
/// </summary>
public class CaptureContent : IDisposable
{
    public static readonly int MaxFrameIndexSecond = 60;
    public int FrameIndex { get; }
    public double TimerInterval { get; }

    public int FrameRate => (int)(1000 / TimerInterval);

    public ImageRegion CaptureRectArea { get; }
    
    public GameUiCategory CurrentGameUiCategory;

    public CaptureContent(Fischless.GameCapture.GameCaptureFrame frame, int frameIndex, double interval)
        : this(frame.Frame, frameIndex, interval, frame.IsDesktopCapture)
    {
    }

    public CaptureContent(Mat image, int frameIndex, double interval, bool? isDesktopCapture = null)
    {
        FrameIndex = frameIndex;
        TimerInterval = interval;
        if (isDesktopCapture == false || (isDesktopCapture == null && TaskContext.Instance().IsCloudWeb))
        {
            // 保留图像/绘图区域链，但没有 DesktopRegion 父节点，防止旧 Region.Click 误点桌面。
            CaptureRectArea = new GameCaptureRegion(image, 0, 0).DeriveTo1080P();
            return;
        }
        var systemInfo = TaskContext.Instance().SystemInfo;

        var gameCaptureRegion = systemInfo.DesktopRectArea.Derive(image, systemInfo.CaptureAreaRect.X, systemInfo.CaptureAreaRect.Y);
        CaptureRectArea = gameCaptureRegion.DeriveTo1080P();
    }

    /// <summary>
    /// 用于兼容新的 ImageRegion
    /// </summary>
    /// <param name="ra"></param>
    public CaptureContent(ImageRegion ra)
    {
        CaptureRectArea = ra;
    }

    public void Dispose()
    {
        CaptureRectArea.Dispose();
        GC.SuppressFinalize(this);
    }
}
