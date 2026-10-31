using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using NorthCommander.UI.ViewModels;

namespace NorthCommander.UI.Views;

public partial class ListerView : UserControl
{
    public ListerView()
    {
        InitializeComponent();

        // Tunnel so the scroll bar or other children never swallow the viewer keys.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        Viewport.SizeChanged += (_, _) => UpdateViewport();
        Viewport.PointerWheelChanged += OnPointerWheelChanged;
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && IsVisible)
            {
                UpdateViewport();
            }
        };
    }

    private ListerViewModel? ViewModel => DataContext as ListerViewModel;

    public void FocusViewer() => Focus(NavigationMethod.Pointer);

    private void UpdateViewport()
    {
        if (ViewModel is not { } viewModel || Viewport.Bounds.Height <= 0)
        {
            return;
        }

        var sample = new FormattedText(
            "0123456789",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(ListerText.FontFamily),
            ListerText.FontSize,
            Brushes.White);
        var charWidth = Math.Max(1, sample.WidthIncludingTrailingWhitespace / 10);
        var rows = (int)((Viewport.Bounds.Height - ListerText.Margin.Top - ListerText.Margin.Bottom) / ListerText.LineHeight);
        var columns = (int)((Viewport.Bounds.Width - ListerText.Margin.Left - ListerText.Margin.Right) / charWidth);
        viewModel.SetViewport(rows, columns);
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        ViewModel?.ScrollLines((int)Math.Round(-e.Delta.Y * 3));
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { IsOpen: true } viewModel)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                viewModel.Close();
                break;
            case Key.D1 or Key.NumPad1:
                viewModel.SetMode(ListerMode.Text);
                break;
            case Key.D2 or Key.NumPad2:
                viewModel.SetMode(ListerMode.Hex);
                break;
            case Key.D3 or Key.NumPad3:
                viewModel.SetMode(ListerMode.Image);
                break;
            case Key.W:
                viewModel.ToggleWrap();
                break;
            case Key.Down:
                viewModel.ScrollLines(1);
                break;
            case Key.Up:
                viewModel.ScrollLines(-1);
                break;
            case Key.PageDown or Key.Space:
                viewModel.PageDown();
                break;
            case Key.PageUp:
                viewModel.PageUp();
                break;
            case Key.Home:
                viewModel.GoToStart();
                break;
            case Key.End:
                viewModel.GoToEnd();
                break;
            default:
                return;
        }

        e.Handled = true;
    }
}
