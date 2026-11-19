using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using PoWatch.Mobile.Services.Sensing;
using AndroidApp = Android.App.Application;

namespace PoWatch.Mobile;

/// <summary>Android side of the observer: the keep-alive foreground service, notifications, heat and dimming.</summary>
public sealed class AndroidDeviceHost : IDeviceHost
{
    public const string ObservingChannel = "observing", AlertChannel = "alerts";
    private static Context Ctx => AndroidApp.Context;

    public void SetKeepAlive(bool observing, string status)
    {
        Intent intent = new(Ctx, typeof(ObserverService));
        if (!observing)
        {
            Ctx.StopService(intent);
            return;
        }

        intent.PutExtra(ObserverService.StatusExtra, status);
        Ctx.StartForegroundService(intent);
    }

    public void Notify(string title, string body)
    {
        Channels(Ctx);
        var builder = new NotificationCompat.Builder(Ctx, AlertChannel);
        builder.SetSmallIcon(Resource.Mipmap.appicon);
        builder.SetContentTitle(title);
        builder.SetContentText(body);
        builder.SetPriority(NotificationCompat.PriorityHigh);
        builder.SetAutoCancel(true);
        NotificationManagerCompat.From(Ctx)!.Notify(System.Environment.TickCount & 0x3FFFFFFF, builder.Build()!);
    }

    public int ThermalLevel =>
        OperatingSystem.IsAndroidVersionAtLeast(29) ? (int)((PowerManager)Ctx.GetSystemService(Context.PowerService)!).CurrentThermalStatus : 0;

    public void SetDim(bool dim) => MainThread.BeginInvokeOnMainThread(() =>
    {
        if (Platform.CurrentActivity?.Window is { } window && window.Attributes is { } attributes)
        {
            attributes.ScreenBrightness = dim ? 0.01f : -1f; // -1 = the user's own brightness
            window.Attributes = attributes;
        }
    });

    public async Task<bool> RequestNotificationsAsync()
    {
        Channels(Ctx);
        return !OperatingSystem.IsAndroidVersionAtLeast(33) || await Permissions.RequestAsync<Permissions.PostNotifications>() == PermissionStatus.Granted;
    }

    internal static void Channels(Context context)
    {
        var manager = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
        manager.CreateNotificationChannel(new NotificationChannel(ObservingChannel, "Observing", NotificationImportance.Low) { Description = "Shown while PoWatch is watching." });
        manager.CreateNotificationChannel(new NotificationChannel(AlertChannel, "Watch alerts", NotificationImportance.High) { Description = "Watch rules and new regulars." });
    }
}

/// <summary>
/// Sensor mode (item 2): a camera + microphone foreground service with a partial wake lock, so an old
/// phone on a shelf keeps observing with its screen off. The sensing itself runs in the app process;
/// this service is what Android requires to let the camera and microphone continue in the background.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeCamera | ForegroundService.TypeMicrophone)]
public sealed class ObserverService : Service
{
    public const string StatusExtra = "status", StopAction = "com.punkouter.powatch.STOP";
    private const int NotificationId = 4242;
    private PowerManager.WakeLock? _wake;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == StopAction)
        {
            if (IPlatformApplication.Current?.Services.GetService(typeof(SensingEngine)) is SensingEngine engine)
            {
                _ = engine.StopAsync();
            }

            StopSelf();
            return StartCommandResult.NotSticky;
        }

        AndroidDeviceHost.Channels(this);
        Notification note = Build(intent?.GetStringExtra(StatusExtra) ?? "Observing");
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            StartForeground(NotificationId, note, ForegroundService.TypeCamera | ForegroundService.TypeMicrophone);
        }
        else
        {
            StartForeground(NotificationId, note);
        }

        if (_wake is null)
        {
            _wake = ((PowerManager)GetSystemService(PowerService)!).NewWakeLock(WakeLockFlags.Partial, "powatch:observing");
            _wake!.Acquire();
        }

        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        if (_wake?.IsHeld == true)
        {
            _wake.Release();
        }

        _wake = null;
        base.OnDestroy();
    }

    private Notification Build(string status)
    {
        PendingIntent stop = PendingIntent.GetService(this, 1, new Intent(this, typeof(ObserverService)).SetAction(StopAction), PendingIntentFlags.Immutable)!;
        PendingIntent open = PendingIntent.GetActivity(this, 0, PackageManager!.GetLaunchIntentForPackage(PackageName!)!, PendingIntentFlags.Immutable)!;
        var builder = new NotificationCompat.Builder(this, AndroidDeviceHost.ObservingChannel);
        builder.SetSmallIcon(Resource.Mipmap.appicon);
        builder.SetContentTitle("PoWatch is observing");
        builder.SetContentText(status);
        builder.SetOngoing(true);
        builder.SetOnlyAlertOnce(true);
        builder.SetContentIntent(open);
        builder.AddAction(0, "Stop", stop);
        return builder.Build()!;
    }
}
