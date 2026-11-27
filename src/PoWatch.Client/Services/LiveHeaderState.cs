using PoWatch.Shared.Services;

namespace PoWatch.Client.Services;

/// <summary>
/// Shares the Live page's session label, panel layout controls, and camera-start error with the top
/// nav bar (<see cref="PoWatch.Client.Layout.PoHeader"/>) so every Live control lives in one row.
/// </summary>
public sealed class LiveHeaderState
{
    public bool IsLiveActive { get; private set; }
    public string WindowLabel { get; private set; } = string.Empty;
    public IReadOnlyList<string> Order { get; private set; } = LivePanels.DefaultOrder;
    public IReadOnlySet<string> Hidden { get; private set; } = new HashSet<string>();
    public IReadOnlyList<string> VisiblePanels { get; private set; } = LivePanels.DefaultOrder;
    public bool IsDefaultLayout { get; private set; } = true;
    public string? StartError { get; private set; }

    public Func<Task>? OnShowAll { get; private set; }
    public Func<Task>? OnHideAll { get; private set; }
    public Func<string, bool, Task>? OnSetHidden { get; private set; }
    public Func<string, int, Task>? OnStep { get; private set; }
    public Func<Task>? OnResetLayout { get; private set; }

    public event Action? Changed;

    public void Update(
        string windowLabel,
        IReadOnlyList<string> order,
        IReadOnlyList<string> hidden,
        bool isDefaultLayout,
        Func<Task> onShowAll,
        Func<Task> onHideAll,
        Func<string, bool, Task> onSetHidden,
        Func<string, int, Task> onStep,
        Func<Task> onResetLayout)
    {
        IsLiveActive = true;
        WindowLabel = windowLabel;
        Order = [.. order];
        var hiddenSet = new HashSet<string>(hidden, StringComparer.Ordinal);
        Hidden = hiddenSet;
        VisiblePanels = [.. order.Where(id => !hiddenSet.Contains(id))];
        IsDefaultLayout = isDefaultLayout;
        OnShowAll = onShowAll;
        OnHideAll = onHideAll;
        OnSetHidden = onSetHidden;
        OnStep = onStep;
        OnResetLayout = onResetLayout;
        Changed?.Invoke();
    }

    public void UpdateWindowLabel(string windowLabel)
    {
        if (!IsLiveActive || WindowLabel == windowLabel) return;
        WindowLabel = windowLabel;
        Changed?.Invoke();
    }

    public void SetStartError(string? error)
    {
        if (StartError == error) return;
        StartError = error;
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (!IsLiveActive) return;
        IsLiveActive = false;
        WindowLabel = string.Empty;
        OnShowAll = null;
        OnHideAll = null;
        OnSetHidden = null;
        OnStep = null;
        OnResetLayout = null;
        Changed?.Invoke();
    }
}
