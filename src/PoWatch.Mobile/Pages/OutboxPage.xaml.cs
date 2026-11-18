using PoWatch.Mobile.Services;
using PoWatch.Shared.Models;

namespace PoWatch.Mobile.Pages;

public partial class OutboxPage : ContentPage
{
    private readonly PoWatchMobileClient _client;
    private readonly OfflineOutboxService _outbox;

    public OutboxPage(PoWatchMobileClient client, OfflineOutboxService outbox)
    {
        _client = client;
        _outbox = outbox;
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await RefreshAsync();
    }

    private async void OnSyncNowClicked(object? sender, EventArgs e)
    {
        try
        {
            var flushed = await _client.FlushOutboxAsync();
            SyncSummaryLabel.Text = $"Synced {flushed} queued batch(es).";
        }
        catch (Exception ex)
        {
            SyncSummaryLabel.Text = $"Sync failed: {ex.Message}";
        }

        await RefreshAsync();
    }

    private async void OnRefreshClicked(object? sender, EventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        var queued = await _outbox.ListAsync();
        OutboxList.Clear();
        if (queued.Count == 0)
        {
            OutboxList.Add(new Label { Text = "No queued batches — all caught up.", Opacity = 0.7 });
        }
        else
        {
            foreach (var item in queued)
            {
                OutboxList.Add(new Label { Text = $"{item.QueuedUtc:HH:mm:ss} · {SessionDto.Tag(item.SessionId)} · {item.Batch.Ticks.Count} ticks" });
            }
        }

        SessionsList.Clear();
        try
        {
            var sessions = await _client.GetRecentSessionsAsync();
            SyncSummaryLabel.Text = $"Queued: {queued.Count} · Online: {Connectivity.Current.NetworkAccess}";
            foreach (var s in sessions)
            {
                SessionsList.Add(new Label { Text = $"{SessionDto.Tag(s.Id)} · {s.StartedUtc:g} · {(s.IsRunning ? "Running" : $"{s.DurationSeconds:F0}s")}" });
            }
        }
        catch (Exception ex)
        {
            SyncSummaryLabel.Text = $"Queued: {queued.Count} · Server unreachable ({ex.Message})";
        }
    }
}
