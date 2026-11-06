namespace PoWatch.Client.Services;

/// <summary>
/// Which side drawer is open: at most one at a time (opening Settings closes the bell and back), and
/// any navigation closes it (MainLayout).
/// </summary>
public sealed class DrawerState
{
    public const string Settings = "settings";
    public const string Tray = "tray";

    public string? Open { get; private set; }

    /// <summary>The Settings tab to open on (recording, people, alerts, advanced), for links that mean one; null for the last one used.</summary>
    public string? Section { get; private set; }

    public event Action? Changed;

    public bool IsOpen(string name) => Open == name;

    public void Show(string name) => Set(name);

    /// <summary>Opens Settings on one tab: the bell's "watch rules" and "name everyone" links.</summary>
    public void ShowSettings(string section)
    {
        Section = section;
        if (Open == Settings) Changed?.Invoke();
        else Set(Settings);
    }

    public void Toggle(string name) => Set(Open == name ? null : name);

    public void Close() => Set(null);

    private void Set(string? name)
    {
        if (Open == name) return;
        Open = name;
        if (name != Settings) Section = null;
        Changed?.Invoke();
    }
}
