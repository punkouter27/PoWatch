using PoWatch.Mobile.Pages;

namespace PoWatch.Mobile;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute("login", typeof(LoginPage));
        Routing.RegisterRoute("day", typeof(DayPage));
        Routing.RegisterRoute("session", typeof(SessionPage));
        Routing.RegisterRoute("diag", typeof(DiagPage));
    }
}
