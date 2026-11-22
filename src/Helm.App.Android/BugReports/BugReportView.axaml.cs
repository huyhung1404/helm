using Avalonia.Controls;
using Avalonia.Interactivity;
using Helm.Shell.BugReports;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.App.Android.BugReports;

public partial class BugReportView : UserControl
{
    public BugReportView() => InitializeComponent();

    private BugReportFormViewModel? Form => DataContext as BugReportFormViewModel;

    private static AndroidBugReporter Reporter => App.Services.GetRequiredService<AndroidBugReporter>();

    private async void Gallery_Click(object? sender, RoutedEventArgs e)
    {
        if (Form is not { } form) return;
        foreach (var image in await Reporter.PickAsync(BugStore.MaxImages - form.Images.Count))
            if (!form.AddImage(image)) break;
    }

    private async void Camera_Click(object? sender, RoutedEventArgs e)
    {
        if (Form is { } form && await Reporter.TakePhotoAsync() is { } photo) form.AddImage(photo);
    }

    private async void Paste_Click(object? sender, RoutedEventArgs e)
    {
        if (Form is not { } form) return;
        if (await Reporter.PasteAsync() is { } image) form.AddImage(image);
        else form.Error = "There is no picture on the clipboard.";
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Reporter.CloseReport();

    private void Open_Click(object? sender, RoutedEventArgs e)
    {
        var id = Form?.SentId;
        Reporter.CloseReport();
        if (id is not null) Reporter.ShowPage(id);
    }
}
