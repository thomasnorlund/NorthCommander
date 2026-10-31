using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CommanderTom.UI.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    public MainWindowViewModel(FilePanelViewModel leftPanel, FilePanelViewModel rightPanel)
    {
        LeftPanel = leftPanel;
        RightPanel = rightPanel;

        LeftPanel.ActivationRequested += OnPanelActivationRequested;
        RightPanel.ActivationRequested += OnPanelActivationRequested;

        ActivePanel = LeftPanel;
    }

    public FilePanelViewModel LeftPanel { get; }

    public FilePanelViewModel RightPanel { get; }

    [ObservableProperty]
    public partial FilePanelViewModel ActivePanel { get; set; }

    public FilePanelViewModel InactivePanel => ReferenceEquals(ActivePanel, LeftPanel) ? RightPanel : LeftPanel;

    public Task InitializeAsync() => Task.WhenAll(
        LeftPanel.LoadAsync(GetDefaultLeftPath()),
        RightPanel.LoadAsync(GetDefaultRightPath()));

    [RelayCommand]
    private void SwitchActivePanel() => ActivePanel = InactivePanel;

    partial void OnActivePanelChanged(FilePanelViewModel oldValue, FilePanelViewModel newValue)
    {
        // oldValue is null on the first assignment from the constructor.
        oldValue?.IsActive = false;
        newValue.IsActive = true;
        OnPropertyChanged(nameof(InactivePanel));
    }

    private void OnPanelActivationRequested(object? sender, EventArgs e)
    {
        if (sender is FilePanelViewModel panel)
        {
            ActivePanel = panel;
        }
    }

    private static string GetDefaultLeftPath() =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string GetDefaultRightPath() =>
        Path.GetPathRoot(Environment.SystemDirectory) is { Length: > 0 } systemRoot
            ? systemRoot
            : Path.GetPathRoot(GetDefaultLeftPath()) ?? "/";
}
