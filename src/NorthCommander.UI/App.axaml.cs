using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using NorthCommander.Core.Services;
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
            desktop.MainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainWindowViewModel>(),
            };

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
