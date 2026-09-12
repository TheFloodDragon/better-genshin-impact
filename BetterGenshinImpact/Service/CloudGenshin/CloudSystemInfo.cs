using BetterGenshinImpact.GameTask.Model;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;
using System;
using System.Diagnostics;
using Vanara.PInvoke;
using Size = System.Drawing.Size;

namespace BetterGenshinImpact.Service.CloudGenshin;

/// <summary>识别使用标准逻辑画面，不冒充浏览器 HWND 的物理客户区。</summary>
public sealed class CloudSystemInfo : ISystemInfo, IDisposable
{
    public CloudSystemInfo(int processId)
    {
        GameProcess = Process.GetProcessById(processId);
        GameProcessId = processId;
        GameProcessName = GameProcess.ProcessName;
    }

    public Size DisplaySize => new(1920, 1080);
    public RECT GameScreenSize => new(0, 0, 1920, 1080);
    public double AssetScale => 1;
    public double ZoomOutMax1080PRatio => 1;
    public double ScaleTo1080PRatio => 1;
    public RECT CaptureAreaRect { get; set; } = new(0, 0, 1920, 1080);
    public Rect ScaleMax1080PCaptureRect { get; set; } = new(0, 0, 1920, 1080);
    public Process GameProcess { get; }
    public string GameProcessName { get; }
    public int GameProcessId { get; }
    public DesktopRegion DesktopRectArea { get; } = new(1920, 1080);
    public void Dispose() => GameProcess.Dispose();
}
