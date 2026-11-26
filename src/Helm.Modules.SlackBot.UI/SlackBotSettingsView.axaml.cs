using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Helm.Core.Modules;

namespace Helm.Modules.SlackBot.Ui;

/// <summary>Slack Report's settings, shown as a sheet over the page (see <see cref="SlackBotView"/>'s gear).</summary>
public partial class SlackBotSettingsView : UserControl
{
    private readonly SlackBotViewModel? _viewModel;
    private readonly ISlackBotPageHost? _host;
    private bool _choosing;

    /// <summary>For the XAML loader and the previewer only.</summary>
    public SlackBotSettingsView() => InitializeComponent();

    /// <param name="openConnection">Open on the Slack app's steps (first run); they are open anyway until Slack is connected.</param>
    public SlackBotSettingsView(SlackBotViewModel viewModel, IModule module, ISlackBotPageHost host, Action close, bool openConnection)
    {
        _viewModel = viewModel;
        _host = host;
        DataContext = viewModel;
        InitializeComponent();
        CloseButton.Click += (_, _) => close();
        DoneButton.Click += (_, _) => close();
        CopyManifestButton.Click += CopyManifest_Click;
        ConnectButton.Click += Connect_Click;
        AppFold.IsChecked = openConnection || !viewModel.HasTokens;

        // The tool's on/off switch (it used to be on a settings page of its own).
        EnableSwitch.IsChecked = module.IsEnabled;
        EnableSwitch.IsCheckedChanged += (_, _) => module.IsEnabled = EnableSwitch.IsChecked == true;
        void OnModule(object? s, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(IModule.IsEnabled)) Dispatcher.UIThread.Post(() => EnableSwitch.IsChecked = module.IsEnabled);
        }

        // The choices are records with a label: shown by label, saved by value.
        Choice(ModelBox, viewModel.Models.Select(m => m.Label), () => IndexOf(viewModel.Models, viewModel.Model), i => viewModel.Model = viewModel.Models[i].Value);
        Choice(EffortBox, viewModel.Efforts.Select(m => m.Label), () => IndexOf(viewModel.Efforts, viewModel.Effort), i => viewModel.Effort = viewModel.Efforts[i].Value);
        Choice(RemindBox, viewModel.RemindChoices.Select(m => m.Label), () => IndexOf(viewModel.RemindChoices, viewModel.RemindEveryMinutes),
            i => viewModel.RemindEveryMinutes = viewModel.RemindChoices[i].Value);

        AttachedToVisualTree += (_, _) =>
        {
            module.PropertyChanged += OnModule;
            viewModel.PropertyChanged += OnViewModel;
        };
        DetachedFromVisualTree += (_, _) =>
        {
            module.PropertyChanged -= OnModule;
            viewModel.PropertyChanged -= OnViewModel;
        };
    }

    private static int IndexOf<T>(IReadOnlyList<SlackBotChoice<T>> choices, T value)
    {
        for (var i = 0; i < choices.Count; i++)
            if (EqualityComparer<T>.Default.Equals(choices[i].Value, value)) return i;
        return 0;
    }

    private void Choice(ComboBox box, IEnumerable<string> labels, Func<int> current, Action<int> pick)
    {
        box.ItemsSource = labels.ToList();
        box.SelectedIndex = current();
        box.SelectionChanged += (_, _) =>
        {
            if (!_choosing && box.SelectedIndex >= 0) pick(box.SelectedIndex);
        };
        box.Tag = current;
    }

    private void OnViewModel(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(SlackBotViewModel.Model) or nameof(SlackBotViewModel.Effort) or nameof(SlackBotViewModel.RemindEveryMinutes))) return;
        _choosing = true;
        try
        {
            foreach (var box in new[] { ModelBox, EffortBox, RemindBox })
                if (box.Tag is Func<int> current) box.SelectedIndex = current();
        }
        finally
        {
            _choosing = false;
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        // A phone sheet: the signature on its own line, the emoji under it.
        var narrow = e.NewSize.Width < 440;
        Grid.SetColumnSpan(SignatureField, narrow ? 2 : 1);
        Grid.SetColumn(EmojiField, narrow ? 0 : 1);
        Grid.SetRow(EmojiField, narrow ? 1 : 0);
        Grid.SetColumnSpan(EmojiField, narrow ? 2 : 1);
        // The app's two buttons, one under the other.
        Grid.SetColumnSpan(CopyManifestButton, narrow ? 2 : 1);
        Grid.SetColumn(OpenAppsButton, narrow ? 0 : 1);
        Grid.SetRow(OpenAppsButton, narrow ? 1 : 0);
        Grid.SetColumnSpan(OpenAppsButton, narrow ? 2 : 1);
    }

    private void CopyManifest_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _host is null) return;
        try
        {
            _host.CopyText(_viewModel.Manifest);
            CopyManifestText.Text = "Copied";
            DispatcherTimer.RunOnce(() => CopyManifestText.Text = "Copy manifest", TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            // The clipboard is busy; the button can be pressed again.
            System.Diagnostics.Debug.WriteLine($"Copying the manifest failed: {ex.Message}");
        }
    }

    /// <summary>The tokens are read here, handed over once and cleared.</summary>
    private async void Connect_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        try
        {
            if (!await _viewModel.ConnectAsync(UserTokenBox.Text ?? "", AppTokenBox.Text ?? "")) return;
            UserTokenBox.Text = "";
            AppTokenBox.Text = "";
            AppFold.IsChecked = false;
        }
        catch (Exception ex)
        {
            _viewModel.TokenProblem = "Could not save the tokens: " + ex.Message;
        }
    }

    /// <summary>The cursor in the user token (first run: the step after making the app).</summary>
    public void FocusUserToken() => UserTokenBox.Focus();
}
