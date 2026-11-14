using Microsoft.Extensions.Logging;
using Splitly.Mobile.Pages;
using Splitly.Mobile.Services;

namespace Splitly.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        // One session for the whole app.
        builder.Services.AddSingleton<ApiClient>();
        builder.Services.AddTransient<LoginPage>();
        builder.Services.AddTransient<GroupsPage>();
        builder.Services.AddTransient<GroupPage>();
        builder.Services.AddTransient<ReceiptPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
