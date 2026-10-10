using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Helm.Core.Services;
using Helm.Core.Ui;
using Microsoft.Win32;

namespace Helm.Modules.NovelReader;

/// <summary>Novel Reader's settings (Home → Utilities). The library and the reader are on <see cref="NovelReaderContentPage"/>.</summary>
public partial class NovelReaderPage : ModulePageBase
{
    private readonly NovelReaderViewModel _viewModel;
    private readonly IShellNavigation _navigation;

    public NovelReaderPage(NovelReaderModule module, NovelReaderViewModel viewModel, IShellNavigation navigation)
    {
        _viewModel = viewModel;
        _navigation = navigation;
        DataContext = viewModel;
        InitializeComponent();
        // The view model is platform-neutral and knows nothing about the Windows module type.
        Module = module;
    }

    private void Open_Click(object sender, RoutedEventArgs e) => _navigation.ShowPage(typeof(NovelReaderContentPage));

    /// <summary>View glue: the file dialog is Windows UI.</summary>
    private async void ImportDictionaries_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import dictionary files",
            Filter = "QuickTranslator dictionaries (*.txt)|*.txt",
            Multiselect = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) await _viewModel.ImportDictionariesAsync(dialog.FileNames);
    }

    /// <summary>View glue: the folder dialog is Windows UI.</summary>
    private void ChooseLocalVoiceFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "The VieNeu-TTS folder (with .venv inside)" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) _viewModel.SetLocalVoiceFolder(dialog.FolderName);
    }

    private void Sources_LostFocus(object sender, RoutedEventArgs e) => _viewModel.ApplyDictionarySources(SourcesBox.Text);

    /// <summary>View glue: the key goes from the password box to the view model once, and the box is emptied.</summary>
    private void SaveClaudeKey_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SetClaudeKey(ClaudeKeyBox.Password);
        ClaudeKeyBox.Password = "";
    }

    private void ExportNames_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "Export names for all novels", FileName = "Names.txt", Filter = "Text (*.txt)|*.txt" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) _viewModel.ExportEntriesTo(null, EntryKind.Name, dialog.FileName);
    }

    private void ImportNames_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Import names for all novels", Filter = "Text (*.txt)|*.txt" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) _viewModel.ImportEntries(null, EntryKind.Name, dialog.FileName);
    }
}

/// <summary>A file size such as "27.3 MB".</summary>
public sealed class SizeText : IValueConverter
{
    public static SizeText Instance { get; } = new();

    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture) =>
        value is long bytes ? NovelFormat.Size(bytes) : "";

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
