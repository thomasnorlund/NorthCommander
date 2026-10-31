using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using NorthCommander.Core.Archives;
using NorthCommander.Core.Services;
using NorthCommander.Core.Settings;
using NorthCommander.Core.Transfers;
using NorthCommander.UI.ViewModels;
using NorthCommander.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace NorthCommander.UI;

public partial class App : Application
{
    public ServiceProvider Services { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Services = ConfigureServices();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Portable: settings live next to NorthCommander.exe, never in AppData or the registry.
            var settingsPath = SettingsStore.DefaultPath;
            var viewModel = Services.GetRequiredService<MainWindowViewModel>();
            viewModel.Settings = SettingsStore.Load(settingsPath);
            var window = new MainWindow { DataContext = viewModel };
            window.ApplySettings(viewModel.Settings.Window);
            window.Closing += (_, _) =>
            {
                window.CaptureSettings(viewModel.Settings.Window);
                viewModel.CaptureSettings();
                SettingsStore.TrySave(viewModel.Settings, settingsPath);
            };
            desktop.MainWindow = window;

            // Disposing the provider cancels pending transfers and cleans up partial files.
            desktop.Exit += (_, _) => Services.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton<IDirectoryService, DirectoryService>();

        services.AddSingleton<IFileOperation, CopyFileOperation>();
        services.AddSingleton<IFileOperation, MoveFileOperation>();
        services.AddSingleton<IFileOperation, ExtractFromArchiveOperation>();
        services.AddSingleton<IFileOperation, AddToArchiveOperation>();
        services.AddSingleton(new BackgroundTransferQueueOptions { MaxParallelTransfers = 1 });
        services.AddSingleton<ConflictDialogViewModel>();
        services.AddSingleton<IFileConflictResolver>(sp => sp.GetRequiredService<ConflictDialogViewModel>());
        services.AddSingleton<IBackgroundTransferQueue, BackgroundTransferQueue>();
        services.AddSingleton<TransferQueueViewModel>();

        // Transient: each injection gets its own panel (left and right).
        services.AddTransient<FilePanelViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }
}
