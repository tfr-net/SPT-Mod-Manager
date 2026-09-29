using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SptModManager.App.Services;
using SptModManager.App.ViewModels;
using SptModManager.App.Views;
using SptModManager.Core;
using SptModManager.Core.Logging;

namespace SptModManager.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            var services = new AppServices(AppSettings.Load(), new DialogService(() => window));
            var viewModel = new MainViewModel(services);

            window.DataContext = viewModel;
            window.Opened += async (_, _) =>
            {
                try
                {
                    await viewModel.InitializeAsync();
                }
                catch (Exception e)
                {
                    services.Log.Error($"Startup failed: {e.Message}");
                }
            };

            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
