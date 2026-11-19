using PoWatch.Mobile.Services;
using PoWatch.Mobile.Services.Sensing;

namespace PoWatch.Mobile.Pages;

/// <summary>Live: point the phone at a room, press Start, walk away. Everything is measured on the phone.</summary>
public sealed class LivePage : ContentPage
{
    private readonly SensingEngine _engine;
    private readonly Outbox _outbox;
    private readonly Session _session;
    private readonly IDeviceHost _host;
    private readonly Image _preview = new() { HeightRequest = 260, Aspect = Aspect.AspectFit, BackgroundColor = Colors.Black };
    private readonly Button _start;
    private readonly Label _status = Ui.Muted(null);
    private readonly Grid _stats = new() { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)], ColumnSpacing = 10, RowSpacing = 6 };
    private IDispatcherTimer? _timer;
    private byte[]? _shown;

    public LivePage(SensingEngine engine, Outbox outbox, Session session, IDeviceHost host)
    {
        _engine = engine;
        _outbox = outbox;
        _session = session;
        _host = host;
        Title = "Live";
        _start = Ui.Primary("Start observing", ToggleAsync);

        Switch keepOn = Ui.Toggle(DeviceDisplay.Current.KeepScreenOn, on => { DeviceDisplay.Current.KeepScreenOn = on; return Task.CompletedTask; });
        Content = new ScrollView
        {
            Content = Ui.Stack(10,
                _preview,
                _start,
                _status,
                Ui.Card(_stats),
                Ui.Card(Ui.Stack(6,
                    Ui.H2("Sensor mode"),
                    Ui.Muted(DeviceInfo.Current.Platform == DevicePlatform.Android
                        ? "Plug an old phone in, prop it up and start: PoWatch keeps observing with the screen off (a notification shows while it runs; Stop is on it)."
                        : "iOS stops the camera when the screen locks, so keep the phone plugged in with the screen on — dimmed to near black."),
                    Ui.Field("Keep the screen on", keepOn),
                    Ui.Secondary("Dim screen (tap to wake)", DimAsync))),
                Ui.Secondary("Share the current frame", ShareFrameAsync)),
            Padding = 16,
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (!await _session.RefreshAsync())
        {
            await Shell.Current.GoToAsync("login");
            return;
        }

        _timer ??= Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += Refresh;
        _timer.Start();
        Refresh(null, EventArgs.Empty);
        _ = _outbox.SendAsync();
        _ = _engine.RetryPendingStopsAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= Refresh;
        }
    }

    private async Task ToggleAsync()
    {
        if (_engine.IsRunning)
        {
            await _engine.StopAsync();
            _status.Text = $"Stopped. {_outbox.Pending} batch(es) still waiting to send.";
        }
        else
        {
            _status.Text = "Starting…";
            Progress<double> progress = new(p => _status.Text = $"Downloading the detector model (one time, ~29 MB): {p:P0}");
            if (await _engine.StartAsync(progress) is { } problem)
            {
                _status.Text = problem;
            }
        }

        Refresh(null, EventArgs.Empty);
    }

    private void Refresh(object? sender, EventArgs e)
    {
        LiveState l = _engine.Live;
        _start.Text = _engine.IsRunning ? "Stop observing" : "Start observing";
        if (l.Thumbnail is { } jpeg && !ReferenceEquals(jpeg, _shown))
        {
            _shown = jpeg;
            _preview.Source = ImageSource.FromStream(() => new MemoryStream(jpeg));
        }

        string battery = Battery.Default.ChargeLevel >= 0 ? $"{Battery.Default.ChargeLevel:P0}{(Battery.Default.PowerSource == BatteryPowerSource.Battery ? " · on battery" : " · charging")}" : "—";
        (string, string)[] rows =
        [
            ("Motion", $"{l.Motion:P0}"),
            ("Light (camera)", $"{l.Luminance:P0}"),
            ("Light meter", l.Lux is { } lux ? $"{lux:N0} lux" : "—"),
            ("Sound", $"{l.Sound:P0}"),
            ("Air pressure", l.PressureHpa is { } hpa ? $"{hpa:N1} hPa" : "—"),
            ("Phone moving", l.DeviceMoving ? "yes" : "no"),
            ("In frame", l.InFrame.Count == 0 ? "nothing" : string.Join(", ", l.InFrame.GroupBy(x => x).Select(g => g.Count() > 1 ? $"{g.Count()} {g.Key}" : g.Key))),
            ("Detector", $"{l.ModelStatus}{(l.DetectorMs > 0 ? $" · {l.DetectorMs:F0} ms" : string.Empty)}"),
            ("Ticks", $"{l.Ticks}"),
            ("Waiting to send", $"{_outbox.Pending}"),
            ("Sent / refused / dropped", $"{_outbox.Sent} / {_outbox.Refused} / {_outbox.Dropped}"),
            ("Battery", battery),
        ];

        _stats.Clear();
        _stats.RowDefinitions.Clear();
        for (int i = 0; i < rows.Length; i++)
        {
            _stats.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            _stats.Add(Ui.Muted(rows[i].Item1, 13), 0, i);
            _stats.Add(Ui.Text(rows[i].Item2, 14), 1, i);
        }

        string? note = l.LastError ?? _outbox.LastProblem ?? l.Throttle ?? (l.LastHighlight is { } h ? $"Highlight: {h}" : null);
        if (_engine.IsRunning && note is not null)
        {
            _status.Text = note;
        }
    }

    private async Task DimAsync()
    {
        _host.SetDim(true);
        DeviceDisplay.Current.KeepScreenOn = true;
        await Navigation.PushModalAsync(new DimPage(_engine, _outbox, () => _host.SetDim(false)));
    }

    private async Task ShareFrameAsync()
    {
        if (_engine.CurrentJpeg() is not { } jpeg)
        {
            await Ui.Toast("Start observing first.");
            return;
        }

        string file = Path.Combine(FileSystem.CacheDirectory, $"powatch-{DateTime.Now:yyyyMMdd-HHmmss}.jpg");
        await File.WriteAllBytesAsync(file, jpeg);
        await Share.Default.RequestAsync(new ShareFileRequest("PoWatch frame", new ShareFile(file, "image/jpeg")));
    }
}

/// <summary>Sensor mode's screen: near-black, one dim line of status, tap anywhere to wake.</summary>
public sealed class DimPage : ContentPage
{
    private readonly Label _line = new() { TextColor = Color.FromArgb("#333333"), FontSize = 12, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.End, Margin = 24 };

    public DimPage(SensingEngine engine, Outbox outbox, Action wake)
    {
        BackgroundColor = Colors.Black;
        Shell.SetNavBarIsVisible(this, false);
        Content = new Grid { Children = { _line } };
        TapGestureRecognizer tap = new();
        tap.Tapped += async (_, _) =>
        {
            wake();
            await Navigation.PopModalAsync();
        };
        Content.GestureRecognizers.Add(tap);

        IDispatcherTimer timer = Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(5);
        timer.Tick += (_, _) => _line.Text = engine.IsRunning
            ? $"observing · {engine.Live.Ticks} ticks · {outbox.Pending} waiting · tap to wake"
            : "not observing · tap to wake";
        timer.Start();
        Disappearing += (_, _) => timer.Stop();
    }
}
