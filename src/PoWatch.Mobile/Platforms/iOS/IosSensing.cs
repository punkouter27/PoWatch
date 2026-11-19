using AVFoundation;
using CoreFoundation;
using CoreGraphics;
using CoreMedia;
using CoreVideo;
using Foundation;
using PoWatch.Mobile.Services.Sensing;
using UIKit;
using UserNotifications;

namespace PoWatch.Mobile;

/// <summary>The rear camera through AVCapture: BGRA frames at 640×480, upright, at up to 5 fps.</summary>
public sealed class IosCamera : ICameraSource
{
    private AVCaptureSession? _session;
    private FrameDelegate? _delegate;

    public async Task<bool> StartAsync(Action<RgbaFrame> onFrame)
    {
        if (!await AVCaptureDevice.RequestAccessForMediaTypeAsync(AVAuthorizationMediaType.Video)
            || AVCaptureDevice.GetDefaultDevice(AVMediaTypes.Video) is not { } device)
        {
            return false;
        }

        _session = new AVCaptureSession { SessionPreset = AVCaptureSession.Preset640x480 };
        AVCaptureDeviceInput? input = AVCaptureDeviceInput.FromDevice(device, out NSError? error);
        if (input is null || error is not null || !_session.CanAddInput(input))
        {
            return false;
        }

        _session.AddInput(input);
        AVCaptureVideoDataOutput output = new()
        {
            AlwaysDiscardsLateVideoFrames = true,
            WeakVideoSettings = new CVPixelBufferAttributes { PixelFormatType = CVPixelFormatType.CV32BGRA }.Dictionary,
        };
        _delegate = new FrameDelegate(onFrame);
        output.SetSampleBufferDelegate(_delegate, new DispatchQueue("powatch-camera"));
        _session.AddOutput(output);
        if (output.ConnectionFromMediaType(AVMediaTypes.Video.GetConstant()!) is { } connection && connection.SupportsVideoOrientation)
        {
            connection.VideoOrientation = AVCaptureVideoOrientation.Portrait;
        }

        _session.StartRunning();
        return true;
    }

    public void Stop()
    {
        _session?.StopRunning();
        _session?.Dispose();
        _session = null;
    }

    private sealed class FrameDelegate(Action<RgbaFrame> onFrame) : AVCaptureVideoDataOutputSampleBufferDelegate
    {
        private DateTime _last;

        public override void DidOutputSampleBuffer(AVCaptureOutput captureOutput, CMSampleBuffer sampleBuffer, AVCaptureConnection connection)
        {
            try
            {
                if ((DateTime.UtcNow - _last).TotalMilliseconds < 200 || sampleBuffer.GetImageBuffer() is not CVPixelBuffer pixels)
                {
                    return;
                }

                _last = DateTime.UtcNow;
                pixels.Lock(CVPixelBufferLock.ReadOnly);
                try
                {
                    int w = (int)pixels.Width, h = (int)pixels.Height, stride = (int)pixels.BytesPerRow;
                    byte[] bgra = new byte[stride * h];
                    System.Runtime.InteropServices.Marshal.Copy(pixels.BaseAddress, bgra, 0, bgra.Length);
                    byte[] rgba = new byte[w * h * 4];
                    for (int y = 0; y < h; y++)
                    {
                        for (int x = 0; x < w; x++)
                        {
                            int s = (y * stride) + (x * 4), o = ((y * w) + x) * 4;
                            rgba[o] = bgra[s + 2];
                            rgba[o + 1] = bgra[s + 1];
                            rgba[o + 2] = bgra[s];
                            rgba[o + 3] = 255;
                        }
                    }

                    onFrame(new RgbaFrame(rgba, w, h, DateTimeOffset.UtcNow));
                }
                finally
                {
                    pixels.Unlock(CVPixelBufferLock.ReadOnly);
                }
            }
            finally
            {
                sampleBuffer.Dispose();
            }
        }
    }
}

/// <summary>Microphone loudness from an AVAudioEngine input tap; nothing is recorded.</summary>
public sealed class IosMicrophone : IMicrophone
{
    private AVAudioEngine? _engine;

    public Task<bool> StartAsync(Action<double, double> onLevel)
    {
        AVAudioSession.SharedInstance().SetCategory(AVAudioSessionCategory.PlayAndRecord, AVAudioSessionCategoryOptions.MixWithOthers, out _);
        AVAudioSession.SharedInstance().SetActive(true, out _);
        _engine = new AVAudioEngine();
        AVAudioInputNode input = _engine.InputNode;
        List<double> levels = [];
        input.InstallTapOnBus(0, 1600, input.GetBusOutputFormat(0), (buffer, _) =>
        {
            unsafe
            {
                float* samples = ((float**)buffer.FloatChannelData)[0];
                int n = (int)buffer.FrameLength;
                double sum = 0;
                for (int i = 0; i < n; i++)
                {
                    sum += samples[i] * samples[i];
                }

                double dbfs = 20 * Math.Log10(Math.Max(1e-9, Math.Sqrt(sum / Math.Max(1, n))));
                levels.Add(Math.Clamp((dbfs + 60) / 60, 0, 1));
            }

            if (levels.Count >= 10)
            {
                onLevel(levels.Average(), levels.Max());
                levels.Clear();
            }
        });
        _engine.Prepare();
        return Task.FromResult(_engine.StartAndReturnError(out NSError? error) && error is null);
    }

    public void Stop()
    {
        _engine?.InputNode.RemoveTapOnBus(0);
        _engine?.Stop();
        _engine = null;
    }
}

/// <summary>iOS exposes no ambient light sensor to apps; light comes from the camera's luminance instead.</summary>
public sealed class IosLightSensor : ILightSensor
{
    public bool IsSupported => false;

    public void Start(Action<double> onLux)
    {
    }

    public void Stop()
    {
    }
}

public sealed class IosImageEncoder : IImageEncoder
{
    public byte[] Jpeg(RgbaFrame frame, int maxSide, int quality)
    {
        using CGColorSpace space = CGColorSpace.CreateDeviceRGB();
        using CGBitmapContext context = new(frame.Pixels, frame.Width, frame.Height, 8, frame.Width * 4, space, CGImageAlphaInfo.NoneSkipLast);
        using CGImage image = context.ToImage()!;
        double scale = Math.Min(1, maxSide / (double)Math.Max(frame.Width, frame.Height));
        using UIImage full = UIImage.FromImage(image);
        using UIImage sized = scale < 1 ? Resize(full, new CGSize(frame.Width * scale, frame.Height * scale)) : full;
        return sized.AsJPEG(quality / 100f)?.ToArray() ?? [];
    }

    private static UIImage Resize(UIImage image, CGSize size) =>
        new UIGraphicsImageRenderer(size).CreateImage(_ => image.Draw(new CGRect(CGPoint.Empty, size)));
}

/// <summary>iOS can't run the camera in the background, so sensor mode keeps the screen awake (dimmed) instead.</summary>
public sealed class IosDeviceHost : IDeviceHost
{
    private static nfloat s_brightness = -1;

    public void SetKeepAlive(bool observing, string status) =>
        MainThread.BeginInvokeOnMainThread(() => UIApplication.SharedApplication.IdleTimerDisabled = observing);

    public void Notify(string title, string body)
    {
        UNMutableNotificationContent content = new() { Title = title, Body = body, Sound = UNNotificationSound.Default };
        UNUserNotificationCenter.Current.AddNotificationRequest(UNNotificationRequest.FromIdentifier(Guid.NewGuid().ToString(), content, null), _ => { });
    }

    public int ThermalLevel => NSProcessInfo.ProcessInfo.ThermalState switch
    {
        NSProcessInfoThermalState.Serious => 3,
        NSProcessInfoThermalState.Critical => 4,
        NSProcessInfoThermalState.Fair => 1,
        _ => 0,
    };

    public void SetDim(bool dim) => MainThread.BeginInvokeOnMainThread(() =>
    {
        UIScreen screen = UIScreen.MainScreen;
        if (dim)
        {
            s_brightness = screen.Brightness;
            screen.Brightness = 0.01f;
        }
        else if (s_brightness >= 0)
        {
            screen.Brightness = s_brightness;
        }
    });

    public async Task<bool> RequestNotificationsAsync() =>
        (await UNUserNotificationCenter.Current.RequestAuthorizationAsync(UNAuthorizationOptions.Alert | UNAuthorizationOptions.Sound)).Item1;
}
