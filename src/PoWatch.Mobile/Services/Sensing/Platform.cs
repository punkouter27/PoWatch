namespace PoWatch.Mobile.Services.Sensing;

/// <summary>The rear camera as a stream of upright RGBA frames (Android Camera2 / iOS AVCapture).</summary>
public interface ICameraSource
{
    Task<bool> StartAsync(Action<RgbaFrame> onFrame);

    void Stop();
}

/// <summary>Microphone loudness only — mean and peak in [0, 1] (−60 dBFS → 0, 0 dBFS → 1) about once a second. No audio is kept.</summary>
public interface IMicrophone
{
    Task<bool> StartAsync(Action<double, double> onLevel);

    void Stop();
}

/// <summary>The ambient light sensor in lux; unsupported on iOS (no public API) and on phones without one.</summary>
public interface ILightSensor
{
    bool IsSupported { get; }

    void Start(Action<double> onLux);

    void Stop();
}

/// <summary>JPEG encoding for snapshots and the preview thumbnail.</summary>
public interface IImageEncoder
{
    byte[] Jpeg(RgbaFrame frame, int maxSide, int quality);
}

/// <summary>What the phone itself offers the observer: keep-alive while observing, notifications, heat, screen.</summary>
public interface IDeviceHost
{
    /// <summary>Android: a camera + microphone foreground service, so observing continues with the screen off. iOS: the screen stays on.</summary>
    void SetKeepAlive(bool observing, string status);

    void Notify(string title, string body);

    /// <summary>0 none, 1 light, 2 moderate, 3 severe, 4+ critical (Android thermal status); 0 where unknown.</summary>
    int ThermalLevel { get; }

    /// <summary>Sensor mode's dim screen: near-black brightness, or the user's own.</summary>
    void SetDim(bool dim);

    Task<bool> RequestNotificationsAsync();
}
