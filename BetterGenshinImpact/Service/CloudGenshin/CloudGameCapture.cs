using Fischless.GameCapture;
using OpenCvSharp;
using System;
using System.Collections.Generic;

namespace BetterGenshinImpact.Service.CloudGenshin;

/// <summary>容量为一的帧缓存。发布转移所有权，读取克隆；不将缓存 Mat 交给消费者释放。</summary>
public sealed class CloudGameCapture : IGameCapture
{
    private readonly object _sync = new();
    private readonly TimeProvider _clock;
    private Mat? _image;
    private CloudViewportTransform? _transform;
    private long _sequence;
    private long _timestamp;
    private DateTimeOffset _capturedAt;
    private bool _capturing;

    public CloudGameCapture(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;
    public bool IsCapturing { get { lock (_sync) return _capturing; } }

    public void Start(nint hWnd, Dictionary<string, object>? settings = null)
    {
        lock (_sync) _capturing = true;
    }

    public void Publish(Mat image, CloudViewportTransform transform)
    {
        lock (_sync)
        {
            if (!_capturing) { image.Dispose(); return; }
            _image?.Dispose();
            _image = image;
            _transform = transform;
            _sequence++;
            _timestamp = _clock.GetTimestamp();
            _capturedAt = _clock.GetUtcNow();
        }
    }

    private bool HasFreshFrame => _capturing && _image != null && _transform != null
        && _clock.GetElapsedTime(_timestamp) <= TimeSpan.FromSeconds(3);

    public GameCaptureFrame? Capture()
    {
        lock (_sync)
        {
            return HasFreshFrame ? new GameCaptureFrame(_image!.Clone(), isDesktopCapture: false,
                sequenceNumber: _sequence, capturedAt: _capturedAt) : null;
        }
    }

    public CloudCapturedFrame? CaptureWithTransform()
    {
        lock (_sync)
        {
            return HasFreshFrame ? new CloudCapturedFrame(_image!.Clone(), _transform!, _sequence) : null;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _image?.Dispose();
            _image = null;
            _transform = null;
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            _capturing = false;
            Clear();
        }
    }

    public void Dispose() => Stop();
}

public sealed class CloudCapturedFrame(Mat image, CloudViewportTransform transform, long sequenceNumber) : IDisposable
{
    public Mat Image { get; } = image;
    public CloudViewportTransform Transform { get; } = transform;
    public long SequenceNumber { get; } = sequenceNumber;
    public void Dispose() => Image.Dispose();
}
