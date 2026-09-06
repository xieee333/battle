using OpenCvSharp;

namespace BattlegroundsVisionAgent.Vision.Capture;

public sealed class CapturedFrame : IDisposable
{
    public CapturedFrame(Mat image, DateTimeOffset capturedAt)
    {
        Image = image ?? throw new ArgumentNullException(nameof(image));
        if (image.Empty())
            throw new ArgumentException("Captured frame cannot be empty.", nameof(image));
        CapturedAt = capturedAt;
    }

    public Mat Image { get; }

    public DateTimeOffset CapturedAt { get; }

    public void Dispose() => Image.Dispose();
}

public interface IFrameSource : IDisposable
{
    IntPtr WindowHandle { get; }

    Task<CapturedFrame> CaptureAsync(CancellationToken cancellationToken = default);
}
