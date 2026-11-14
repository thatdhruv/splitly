using Splitly.Mobile.Pages;

namespace Splitly.Mobile;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        // The login page is the root; everything else is pushed on top of it by route.
        Routing.RegisterRoute("groups", typeof(GroupsPage));
        Routing.RegisterRoute("group", typeof(GroupPage));
        Routing.RegisterRoute("receipt", typeof(ReceiptPage));
    }
}
