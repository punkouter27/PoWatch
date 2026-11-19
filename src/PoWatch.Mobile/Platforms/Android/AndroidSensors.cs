using Android.Content;
using Android.Graphics;
using Android.Hardware;
using Android.Media;
using PoWatch.Mobile.Services.Sensing;
using AndroidApp = Android.App.Application;

namespace PoWatch.Mobile;

/// <summary>Microphone loudness from AudioRecord: RMS per 100 ms, reported once a second as mean and peak. Nothing is recorded.</summary>
public sealed class AndroidMicrophone : IMicrophone, IDisposable
{
    private const int Rate = 16_000;
    private AudioRecord? _record;
    private CancellationTokenSource? _stop;

    public Task<bool> StartAsync(Action<double, double> onLevel)
    {
        int min = AudioRecord.GetMinBufferSize(Rate, ChannelIn.Mono, Encoding.Pcm16bit);
        try
        {
            _record = new AudioRecord(AudioSource.Mic, Rate, ChannelIn.Mono, Encoding.Pcm16bit, Math.Max(min, Rate / 5));
            if (_record.State != State.Initialized)
            {
                return Task.FromResult(false);
            }

            _record.StartRecording();
        }
        catch (Java.Lang.Exception)
        {
            return Task.FromResult(false);
        }

        _stop = new CancellationTokenSource();
        CancellationToken ct = _stop.Token;
        AudioRecord record = _record;
        _ = Task.Run(() =>
        {
            short[] chunk = new short[Rate / 10];
            List<double> levels = [];
            while (!ct.IsCancellationRequested)
            {
                int read = record.Read(chunk, 0, chunk.Length);
                if (read <= 0)
                {
                    continue;
                }

                double sum = 0;
                for (int i = 0; i < read; i++)
                {
                    sum += (double)chunk[i] * chunk[i];
                }

                double dbfs = 20 * Math.Log10(Math.Max(1e-9, Math.Sqrt(sum / read) / short.MaxValue));
                levels.Add(Math.Clamp((dbfs + 60) / 60, 0, 1));
                if (levels.Count >= 10)
                {
                    onLevel(levels.Average(), levels.Max());
                    levels.Clear();
                }
            }
        }, ct);
        return Task.FromResult(true);
    }

    public void Stop()
    {
        _stop?.Cancel();
        try
        {
            _record?.Stop();
        }
        catch (Java.Lang.IllegalStateException)
        {
        }

        _record?.Release();
        _record = null;
    }

    public void Dispose()
    {
        Stop();
        _stop?.Dispose();
    }
}

/// <summary>The ambient light sensor (lux), which most Android phones have.</summary>
public sealed class AndroidLightSensor : Java.Lang.Object, ILightSensor, ISensorEventListener
{
    private static SensorManager Manager => (SensorManager)AndroidApp.Context.GetSystemService(Context.SensorService)!;
    private Action<double>? _onLux;

    public bool IsSupported => Manager.GetDefaultSensor(SensorType.Light) is not null;

    public void Start(Action<double> onLux)
    {
        _onLux = onLux;
        if (Manager.GetDefaultSensor(SensorType.Light) is { } sensor)
        {
            Manager.RegisterListener(this, sensor, SensorDelay.Normal);
        }
    }

    public void Stop()
    {
        Manager.UnregisterListener(this);
        _onLux = null;
    }

    public void OnAccuracyChanged(Sensor? sensor, SensorStatus accuracy)
    {
    }

    public void OnSensorChanged(SensorEvent? e)
    {
        if (e?.Values is { Count: > 0 } values)
        {
            _onLux?.Invoke(values[0]);
        }
    }
}

public sealed class AndroidImageEncoder : IImageEncoder
{
    public byte[] Jpeg(RgbaFrame frame, int maxSide, int quality)
    {
        using Bitmap full = Bitmap.CreateBitmap(frame.Width, frame.Height, Bitmap.Config.Argb8888!)!;
        full.CopyPixelsFromBuffer(Java.Nio.ByteBuffer.Wrap(frame.Pixels)!); // ARGB_8888 is RGBA in memory
        double scale = Math.Min(1, maxSide / (double)Math.Max(frame.Width, frame.Height));
        using Bitmap sized = scale < 1 ? Bitmap.CreateScaledBitmap(full, (int)(frame.Width * scale), (int)(frame.Height * scale), true)! : full.Copy(Bitmap.Config.Argb8888!, false)!;
        using MemoryStream stream = new();
        sized.Compress(Bitmap.CompressFormat.Jpeg!, quality, stream);
        return stream.ToArray();
    }
}
