using Android.Content;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.Media;
using Android.OS;
using Java.Util.Concurrent;
using PoWatch.Mobile.Services.Sensing;
using AndroidApp = Android.App.Application;
using Size = Android.Util.Size;

namespace PoWatch.Mobile;

/// <summary>
/// The rear camera through Camera2 — no CameraX dependency, and it keeps running inside the foreground
/// service with the screen off. YUV frames (~640×480) become upright RGBA at up to 5 fps.
/// </summary>
public sealed class AndroidCamera : ICameraSource, IDisposable
{
    private const long MinFrameGapMs = 200;
    private HandlerThread? _thread;
    private Handler? _handler;
    private CameraDevice? _device;
    private CameraCaptureSession? _session;
    private ImageReader? _reader;
    private Action<RgbaFrame>? _onFrame;
    private int _rotation;
    private long _lastFrameMs;
    private int _busy;

    public async Task<bool> StartAsync(Action<RgbaFrame> onFrame)
    {
        _onFrame = onFrame;
        var manager = (CameraManager)AndroidApp.Context.GetSystemService(Context.CameraService)!;
        string? id = manager.GetCameraIdList().FirstOrDefault(c => (manager.GetCameraCharacteristics(c).Get(CameraCharacteristics.LensFacing) as Java.Lang.Integer)?.IntValue() == (int)LensFacing.Back)
            ?? manager.GetCameraIdList().FirstOrDefault();
        if (id is null)
        {
            return false;
        }

        CameraCharacteristics info = manager.GetCameraCharacteristics(id);
        int sensor = (info.Get(CameraCharacteristics.SensorOrientation) as Java.Lang.Integer)?.IntValue() ?? 90;
        int display = await MainThread.InvokeOnMainThreadAsync(() => DeviceDisplay.Current.MainDisplayInfo.Rotation switch
        {
            DisplayRotation.Rotation90 => 90,
            DisplayRotation.Rotation180 => 180,
            DisplayRotation.Rotation270 => 270,
            _ => 0,
        });
        _rotation = (sensor - display + 360) % 360;

        var map = (StreamConfigurationMap)info.Get(CameraCharacteristics.ScalerStreamConfigurationMap)!;
        Size size = map.GetOutputSizes((int)ImageFormatType.Yuv420888)!
            .Where(s => s.Width * s.Height >= 480 * 360)
            .OrderBy(s => Math.Abs(s.Width - 640) + Math.Abs(s.Height - 480))
            .FirstOrDefault() ?? new Size(640, 480);

        _thread = new HandlerThread("powatch-camera");
        _thread.Start();
        _handler = new Handler(_thread.Looper!);
        _reader = ImageReader.NewInstance(size.Width, size.Height, ImageFormatType.Yuv420888, 2);
        _reader.SetOnImageAvailableListener(new FrameListener(this), _handler);

        TaskCompletionSource<CameraDevice?> opened = new();
        try
        {
            manager.OpenCamera(id, new DeviceCallback(opened), _handler);
        }
        catch (Exception ex) when (ex is CameraAccessException or Java.Lang.SecurityException)
        {
            return false;
        }

        _device = await opened.Task;
        if (_device is null)
        {
            return false;
        }

        TaskCompletionSource<CameraCaptureSession?> configured = new();
        var output = new OutputConfiguration(_reader.Surface!);
        var config = new SessionConfiguration((int)SessionType.Regular, [output], Executors.NewSingleThreadExecutor()!, new SessionCallback(configured));
        _device.CreateCaptureSession(config);
        _session = await configured.Task;
        if (_session is null)
        {
            return false;
        }

        CaptureRequest.Builder request = _device.CreateCaptureRequest(CameraTemplate.Preview);
        request.AddTarget(_reader.Surface!);
        _session.SetRepeatingRequest(request.Build(), null, _handler);
        return true;
    }

    public void Stop()
    {
        _session?.Close();
        _device?.Close();
        _reader?.Close();
        _thread?.QuitSafely();
        _session = null;
        _device = null;
        _reader = null;
        _thread = null;
        _onFrame = null;
    }

    public void Dispose()
    {
        Stop();
        _handler?.Dispose();
        _handler = null;
    }

    private void OnImage(ImageReader reader)
    {
        using Android.Media.Image? image = reader.AcquireLatestImage();
        long now = SystemClock.ElapsedRealtime();
        if (image is null || _onFrame is null || now - _lastFrameMs < MinFrameGapMs || Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        _lastFrameMs = now;
        try
        {
            _onFrame(ToRgba(image, _rotation));
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    // YUV_420_888 → RGBA (BT.601), rotated so the scene is upright.
    private static RgbaFrame ToRgba(Android.Media.Image image, int rotation)
    {
        int w = image.Width, h = image.Height;
        Android.Media.Image.Plane[] planes = image.GetPlanes()!;
        byte[] y = Bytes(planes[0]), u = Bytes(planes[1]), v = Bytes(planes[2]);
        int yRow = planes[0].RowStride, uvRow = planes[1].RowStride, uvPixel = planes[1].PixelStride;
        bool swap = rotation is 90 or 270;
        int ow = swap ? h : w, oh = swap ? w : h;
        byte[] rgba = new byte[ow * oh * 4];

        for (int sy = 0; sy < h; sy++)
        {
            for (int sx = 0; sx < w; sx++)
            {
                int Y = y[(sy * yRow) + sx] & 0xFF;
                int uv = ((sy >> 1) * uvRow) + ((sx >> 1) * uvPixel);
                int U = (u[uv] & 0xFF) - 128, V = (v[uv] & 0xFF) - 128;
                (int dx, int dy) = rotation switch
                {
                    90 => (h - 1 - sy, sx),
                    180 => (w - 1 - sx, h - 1 - sy),
                    270 => (sy, w - 1 - sx),
                    _ => (sx, sy),
                };
                int o = ((dy * ow) + dx) * 4;
                rgba[o] = Clamp(Y + (1.402 * V));
                rgba[o + 1] = Clamp(Y - (0.344 * U) - (0.714 * V));
                rgba[o + 2] = Clamp(Y + (1.772 * U));
                rgba[o + 3] = 255;
            }
        }

        return new RgbaFrame(rgba, ow, oh, DateTimeOffset.UtcNow);
    }

    private static byte Clamp(double v) => (byte)Math.Clamp((int)v, 0, 255);

    private static byte[] Bytes(Android.Media.Image.Plane plane)
    {
        Java.Nio.ByteBuffer buffer = plane.Buffer!;
        byte[] bytes = new byte[buffer.Remaining()];
        buffer.Get(bytes);
        return bytes;
    }

    private sealed class FrameListener(AndroidCamera owner) : Java.Lang.Object, ImageReader.IOnImageAvailableListener
    {
        public void OnImageAvailable(ImageReader? reader)
        {
            if (reader is not null)
            {
                owner.OnImage(reader);
            }
        }
    }

    private sealed class DeviceCallback(TaskCompletionSource<CameraDevice?> opened) : CameraDevice.StateCallback
    {
        public override void OnOpened(CameraDevice camera) => opened.TrySetResult(camera);

        public override void OnDisconnected(CameraDevice camera)
        {
            camera.Close();
            opened.TrySetResult(null);
        }

        public override void OnError(CameraDevice camera, CameraError error)
        {
            camera.Close();
            opened.TrySetResult(null);
        }
    }

    private sealed class SessionCallback(TaskCompletionSource<CameraCaptureSession?> configured) : CameraCaptureSession.StateCallback
    {
        public override void OnConfigured(CameraCaptureSession session) => configured.TrySetResult(session);

        public override void OnConfigureFailed(CameraCaptureSession session) => configured.TrySetResult(null);
    }
}
