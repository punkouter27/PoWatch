using PoWatch.Mobile.Services;
using PoWatch.Shared.Models;

namespace PoWatch.Mobile.Pages;

public partial class SentryPage : ContentPage
{
    private readonly PoWatchMobileClient _client;
    private readonly OfflineOutboxService _outbox;
    private Guid? _activeSessionId;

    public SentryPage(PoWatchMobileClient client, OfflineOutboxService outbox)
    {
        _client = client;
        _outbox = outbox;
        InitializeComponent();
        _client.StatsChanged += OnStatsChanged;
        Connectivity.Current.ConnectivityChanged += OnConnectivityChanged;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        UpdateDeviceReadout();
        OutboxCountLabel.Text = $"Pending offline batches: {await _outbox.CountAsync()}";
    }

    private void UpdateDeviceReadout()
    {
        var charge = Battery.Default.ChargeLevel;
        var power = Battery.Default.EnergySaverStatus;
        BatteryLabel.Text = $"Battery: {(charge >= 0 ? $"{charge:P0}" : "AC/Unknown")} · Saver: {power}";
        NetworkLabel.Text = $"Network: {Connectivity.Current.NetworkAccess}";
    }

    private void OnWakeLockToggled(object? sender, ToggledEventArgs e)
    {
        if (_activeSessionId.HasValue)
        {
            DeviceDisplay.Current.KeepScreenOn = e.Value;
        }
    }

    private async void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(UpdateDeviceReadout);
        if (e.NetworkAccess == NetworkAccess.Internet)
        {
            var flushed = await _client.FlushOutboxAsync();
            var remaining = await _outbox.CountAsync();
            MainThread.BeginInvokeOnMainThread(() =>
            {
                OutboxCountLabel.Text = $"Pending offline batches: {remaining}";
                if (flushed > 0)
                {
                    StatusLabel.Text = $"Reconnected — flushed {flushed} queued batch(es).";
                }
            });
        }
    }

    private void OnStatsChanged(StatsChangedDto dto)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            HubStatusLabel.Text = $"SignalR Hub: tick #{dto.Ticks} landed at {dto.AtUtc:HH:mm:ss}";
        });
    }

    private async void OnStartClicked(object? sender, EventArgs e)
    {
        try
        {
            StartButton.IsEnabled = false;
            var session = await _client.StartSessionAsync();
            if (session is null)
            {
                StatusLabel.Text = "Failed to start session.";
                StartButton.IsEnabled = true;
                return;
            }

            _activeSessionId = session.Id;
            DeviceDisplay.Current.KeepScreenOn = WakeLockSwitch.IsToggled;
            StopButton.IsEnabled = true;
            SendTickButton.IsEnabled = true;
            StatusLabel.Text = $"Observing session {SessionDto.Tag(session.Id)}";
            await _client.ConnectHubAsync();
            HubStatusLabel.Text = "SignalR Hub: connected";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Start failed: {ex.Message}";
            StartButton.IsEnabled = true;
        }
    }

    private async void OnStopClicked(object? sender, EventArgs e)
    {
        if (_activeSessionId is not { } id)
        {
            return;
        }

        try
        {
            await _client.StopSessionAsync(id);
            StatusLabel.Text = $"Stopped session {SessionDto.Tag(id)}";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Stop queued/failed: {ex.Message}";
        }
        finally
        {
            _activeSessionId = null;
            DeviceDisplay.Current.KeepScreenOn = false;
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            SendTickButton.IsEnabled = false;
        }
    }

    private async void OnCapturePhotoClicked(object? sender, EventArgs e)
    {
        try
        {
            if (!MediaPicker.Default.IsCaptureSupported)
            {
                StatusLabel.Text = "Camera capture is not supported on this device.";
                return;
            }

            var photo = await MediaPicker.Default.CapturePhotoAsync();
            if (photo is null)
            {
                return;
            }

            SnapshotPreview.Source = ImageSource.FromFile(photo.FullPath);
            SnapshotPreview.IsVisible = true;
            StatusLabel.Text = $"Captured snapshot: {photo.FileName}";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Camera error: {ex.Message}";
        }
    }

    private async void OnSendTickClicked(object? sender, EventArgs e)
    {
        if (_activeSessionId is not { } id)
        {
            return;
        }

        var batch = new IngestBatchDto
        {
            BatchKey = Guid.NewGuid(),
            Ticks =
            [
                new TickDto
                {
                    StartUtc = DateTimeOffset.UtcNow.AddSeconds(-10),
                    DurationSeconds = 10,
                    PixelSamples = 10,
                    DetectorSamples = 5,
                    MotionMean = 0.12,
                    MotionMax = 0.35,
                    LuminanceMean = 0.55,
                    SoundMean = 0.08,
                    SoundMax = 0.22,
                },
            ],
        };

        var sent = await _client.SendOrQueueBatchAsync(id, batch);
        var pending = await _outbox.CountAsync();
        OutboxCountLabel.Text = $"Pending offline batches: {pending}";
        StatusLabel.Text = sent ? "10s telemetry batch delivered." : "Offline — batch saved to durable outbox.";
    }
}
