using OpenCvSharp;
using System;

namespace BetterGenshinImpact.Service.CloudGenshin;

/// <summary>与一张截图绑定的不可变坐标信息，不使用桌面位置计算 CDP 输入。</summary>
public sealed record CloudViewportTransform(CloudPageRect ContentBounds, double ViewportWidth, double ViewportHeight, Rect SourceRect)
{
    public const int ImageWidth = 1920;
    public const int ImageHeight = 1080;

    public static CloudViewportTransform Create(CloudPageSnapshot snapshot, int imageWidth, int imageHeight)
    {
        var bounds = snapshot.GameBounds;
        if (imageWidth <= 0 || imageHeight <= 0 || bounds?.IsInside(snapshot.ViewportWidth, snapshot.ViewportHeight) != true)
            throw new ArgumentException("云游戏画面区域或浏览器截图尺寸无效。");
        // 不把非 16:9 画面强行拉伸，避免模板与输入一起发生变形。
        if (Math.Abs(bounds.Width / bounds.Height - 16d / 9) > 0.05)
            throw new ArgumentException("首版网页云原神识别需要 16:9 游戏画面，请调整浏览器窗口或游戏显示设置。");
        var sx = imageWidth / snapshot.ViewportWidth;
        var sy = imageHeight / snapshot.ViewportHeight;
        var left = Math.Clamp((int)Math.Floor(bounds.X * sx), 0, imageWidth - 1);
        var top = Math.Clamp((int)Math.Floor(bounds.Y * sy), 0, imageHeight - 1);
        var right = Math.Clamp((int)Math.Ceiling((bounds.X + bounds.Width) * sx), left + 1, imageWidth);
        var bottom = Math.Clamp((int)Math.Ceiling((bounds.Y + bounds.Height) * sy), top + 1, imageHeight);
        return new CloudViewportTransform(bounds, snapshot.ViewportWidth, snapshot.ViewportHeight, new Rect(left, top, right - left, bottom - top));
    }

    public (double X, double Y) ToCss(double imageX, double imageY)
    {
        if (!double.IsFinite(imageX) || !double.IsFinite(imageY) || imageX < 0 || imageY < 0 || imageX >= ImageWidth || imageY >= ImageHeight)
            throw new ArgumentOutOfRangeException(nameof(imageX));
        return (ContentBounds.X + imageX * ContentBounds.Width / ImageWidth,
                ContentBounds.Y + imageY * ContentBounds.Height / ImageHeight);
    }

    public bool Matches(CloudPageSnapshot snapshot)
    {
        return snapshot.GameBounds is { } rect && Math.Abs(ViewportWidth - snapshot.ViewportWidth) < 0.1
            && Math.Abs(ViewportHeight - snapshot.ViewportHeight) < 0.1 && Math.Abs(ContentBounds.X - rect.X) < 0.5
            && Math.Abs(ContentBounds.Y - rect.Y) < 0.5 && Math.Abs(ContentBounds.Width - rect.Width) < 0.5
            && Math.Abs(ContentBounds.Height - rect.Height) < 0.5;
    }
}
