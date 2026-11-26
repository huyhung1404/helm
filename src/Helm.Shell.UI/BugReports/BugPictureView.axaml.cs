using Avalonia.Controls;
using Avalonia.Interactivity;
using Helm.Shell.BugReports;

namespace Helm.BugReports.Ui;

/// <summary>The picture sheet's body: the picture big, "Open full size" where the platform has a viewer, Done.</summary>
public partial class BugPictureView : UserControl
{
    private readonly IBugPictureSource? _host;
    private readonly Action? _close;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public BugPictureView() => InitializeComponent();

    public BugPictureView(BugImageItem image, IBugPictureSource host, Action close)
    {
        _host = host;
        _close = close;
        DataContext = image;
        InitializeComponent();
        // Without a viewer, Done fills the row alone.
        OpenButton.IsVisible = host.CanOpenPicture;
        Grid.SetColumn(DoneButton, host.CanOpenPicture ? 1 : 0);
        Grid.SetColumnSpan(DoneButton, host.CanOpenPicture ? 1 : 2);
    }

    private async void Open_Click(object? sender, RoutedEventArgs e)
    {
        if (_host is null || DataContext is not BugImageItem image) return;
        try
        {
            await _host.OpenPictureAsync(await image.LoadAsync(), image.Name);
        }
        catch (Exception ex)
        {
            Problem.Text = $"This picture cannot be opened now: {ex.Message}";
            Problem.IsVisible = true;
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => _close?.Invoke();
}
