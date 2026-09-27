using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace Helm.Modules.ClaudeChat.Chat;

/// <summary>
/// The selected chat: its conversation and the composer with the "/" and "@" menus. DataContext is the
/// <see cref="ChatViewModel"/> on screen; <see cref="Workspace"/> supplies commands, models and chat-level actions.
/// </summary>
public partial class ClaudeChatView : UserControl
{
    private bool _followTail = true;
    private MenuTrigger? _trigger;
    private string? _filesFolder;
    private IReadOnlyList<string> _files = [];
    private ChatWorkspaceViewModel? _workspace;

    public ClaudeChatView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            CloseMenu();
            ShowModel();
            _followTail = true;
            Dispatcher.BeginInvoke(() => Scroller.ScrollToEnd());
        };
    }

    public ChatWorkspaceViewModel? Workspace
    {
        get => _workspace;
        set
        {
            if (_workspace is not null) _workspace.PropertyChanged -= OnWorkspaceChanged;
            _workspace = value;
            if (value is not null) value.PropertyChanged += OnWorkspaceChanged;
            RefreshModels();
        }
    }

    private ChatViewModel? Chat => DataContext as ChatViewModel;

    public void FocusComposer()
    {
        Composer.Focus();
        Keyboard.Focus(Composer);
        Composer.CaretIndex = Composer.Text.Length;
    }

    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatWorkspaceViewModel.ModelChoices)) RefreshModels();
    }

    /// <summary>The CLI's model list arrives after the handshake; keep showing the chat's pick.</summary>
    private void RefreshModels()
    {
        ModelPicker.ItemsSource = _workspace?.ModelChoices;
        ShowModel();
    }

    private bool _showingModel;

    /// <summary>Selects the chat's model in the picker (the default entry when it has none or an unknown id).</summary>
    private void ShowModel()
    {
        if (ModelPicker.ItemsSource is not IReadOnlyList<Cli.ModelChoice> choices || choices.Count == 0) return;
        _showingModel = true;
        try
        {
            ModelPicker.SelectedItem = choices.FirstOrDefault(m => m.Value == (Chat?.SelectedModel ?? string.Empty)) ?? choices[0];
        }
        finally
        {
            _showingModel = false;
        }
    }

    private void OnModelPicked(object sender, SelectionChangedEventArgs e)
    {
        if (_showingModel || Chat is not { } chat || ModelPicker.SelectedItem is not Cli.ModelChoice picked) return;
        chat.SelectedModel = picked.Value;
    }

    // ---- keys -------------------------------------------------------------------------------------------------------

    /// <summary>Enter sends (or picks the highlighted suggestion), Shift+Enter breaks the line; arrows and Esc drive the menu.</summary>
    private void OnComposerKeyDown(object sender, KeyEventArgs e)
    {
        if (MenuPopup.IsOpen && MenuList.Items.Count > 0)
        {
            switch (e.Key)
            {
                case Key.Down:
                    MoveSelection(+1);
                    e.Handled = true;
                    return;
                case Key.Up:
                    MoveSelection(-1);
                    e.Handled = true;
                    return;
                case Key.Tab:
                case Key.Enter when !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift):
                    if (MenuList.SelectedItem is Suggestion picked) _ = PickAsync(picked);
                    e.Handled = true;
                    return;
                case Key.Escape:
                    CloseMenu();
                    e.Handled = true;
                    return;
            }
        }
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        if (Chat is { } vm && vm.SendCommand.CanExecute(null)) vm.SendCommand.Execute(null);
    }

    private void OnComposerTextChanged(object sender, TextChangedEventArgs e) => UpdateMenu();

    private void OnComposerCaretMoved(object sender, RoutedEventArgs e)
    {
        if (MenuPopup.IsOpen) UpdateMenu();
    }

    private void MoveSelection(int delta)
    {
        var count = MenuList.Items.Count;
        MenuList.SelectedIndex = ((MenuList.SelectedIndex + delta) % count + count) % count;
        MenuList.ScrollIntoView(MenuList.SelectedItem);
    }

    // ---- the "/" and "@" menu ---------------------------------------------------------------------------------------

    private void UpdateMenu()
    {
        _trigger = ComposerMenu.FindTrigger(Composer.Text, Composer.CaretIndex);
        if (_trigger is not { } trigger || Chat is not { } chat)
        {
            CloseMenu();
            return;
        }
        IReadOnlyList<Suggestion> items;
        if (trigger.Symbol == '/')
        {
            items = ComposerMenu.SlashItems(trigger.Query, _workspace?.Capabilities.Commands ?? []);
        }
        else
        {
            if (!string.Equals(_filesFolder, chat.Folder, StringComparison.OrdinalIgnoreCase))
            {
                _filesFolder = chat.Folder;
                _files = ProjectFiles.List(chat.Folder); // a few thousand entries at most; fast enough on the UI thread
            }
            items = ComposerMenu.FileItems(trigger.Query, _files);
        }
        if (items.Count == 0)
        {
            CloseMenu();
            return;
        }
        var view = new ListCollectionView(items.ToList());
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Suggestion.Group)));
        MenuList.ItemsSource = view;
        MenuList.SelectedIndex = 0;
        MenuPopup.IsOpen = true;
    }

    private void CloseMenu()
    {
        MenuPopup.IsOpen = false;
        _trigger = null;
    }

    /// <summary>A click (or a screen reader's "activate") on a suggestion row.</summary>
    private void OnSuggestionClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Suggestion picked) _ = PickAsync(picked);
    }

    /// <summary>Opens the menu as if "/" was typed at the start (the + button).</summary>
    private void OnSlashButton(object sender, RoutedEventArgs e)
    {
        if (Chat is not { } chat) return;
        if (!chat.Draft.StartsWith('/')) chat.Draft = "/" + chat.Draft.TrimStart('/');
        FocusComposer();
        Composer.CaretIndex = 1;
        UpdateMenu();
    }

    /// <summary>Types "@" at the caret (the @ button).</summary>
    private void OnMentionButton(object sender, RoutedEventArgs e) => InsertAtCaret("@", spaceBefore: true);

    private async Task PickAsync(Suggestion picked)
    {
        if (_trigger is not { } trigger || Chat is not { } chat) return;
        CloseMenu();
        if (picked.Insert is { } insert)
        {
            var (text, caret) = ComposerMenu.Apply(Composer.Text, trigger, insert);
            chat.Draft = text;
            Composer.CaretIndex = caret;
            FocusComposer();
            Composer.CaretIndex = caret;
            return;
        }

        // Helm's own actions: remove the typed "/..." first.
        var (cleared, at) = ComposerMenu.Apply(Composer.Text, trigger, string.Empty);
        chat.Draft = cleared.TrimStart();
        Composer.CaretIndex = Math.Min(Math.Max(0, at - 1), chat.Draft.Length);
        switch (picked.Action)
        {
            case ComposerAction.AttachFile:
                AttachFile(chat);
                break;
            case ComposerAction.MentionFile:
                InsertAtCaret("@", spaceBefore: true);
                break;
            case ComposerAction.ClearConversation:
                if (_workspace is not null && !chat.IsBusy) await _workspace.ClearChatAsync(chat);
                break;
            case ComposerAction.ExportConversation:
                Export(chat);
                break;
            case ComposerAction.SwitchModel:
                ModelPicker.Focus();
                ModelPicker.IsDropDownOpen = true;
                break;
        }
    }

    private void InsertAtCaret(string text, bool spaceBefore)
    {
        if (Chat is not { } chat) return;
        var caret = Math.Min(Composer.CaretIndex, chat.Draft.Length);
        var needsSpace = spaceBefore && caret > 0 && !char.IsWhiteSpace(chat.Draft[caret - 1]);
        var insert = (needsSpace ? " " : string.Empty) + text;
        chat.Draft = chat.Draft.Insert(caret, insert);
        FocusComposer();
        Composer.CaretIndex = caret + insert.Length;
        UpdateMenu();
    }

    /// <summary>Mentions the picked files: relative to the chat folder when inside it (the CLI reads "@path").</summary>
    private void AttachFile(ChatViewModel chat)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Attach files to the message",
            Multiselect = true,
            InitialDirectory = Directory.Exists(chat.Folder) ? chat.Folder : null,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(chat.Folder)) + Path.DirectorySeparatorChar;
        var mentions = dialog.FileNames.Select(f =>
        {
            var path = f.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(root, f).Replace('\\', '/') : f;
            return path.Contains(' ') ? $"@\"{path}\"" : "@" + path;
        });
        InsertAtCaret(string.Join(' ', mentions) + " ", spaceBefore: true);
        CloseMenu();
    }

    private void Export(ChatViewModel chat)
    {
        var safe = string.Concat(chat.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export conversation",
            FileName = (safe.Length > 0 ? safe : "chat") + ".md",
            DefaultExt = ".md",
            Filter = "Markdown|*.md|All files|*.*",
            InitialDirectory = Directory.Exists(chat.Folder) ? chat.Folder : null,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, chat.ToMarkdown());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---- scrolling --------------------------------------------------------------------------------------------------

    /// <summary>Stay pinned to the newest message while it streams, unless the user scrolled up to read.</summary>
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Only a pure scroll (content and viewport unchanged) says where the user wants to be. Growth of the
        // content or a resize of the viewport must not unpin the tail.
        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
        {
            _followTail = Scroller.VerticalOffset >= Scroller.ScrollableHeight - 2;
            return;
        }
        if (_followTail) Scroller.ScrollToEnd();
    }
}
