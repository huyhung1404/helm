using Avalonia.Controls;
using Avalonia.Layout;
using Helm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.Modules.Habits;

/// <summary>
/// The Habits pop-up (see <see cref="HabitsViewModel.HasPopup"/>), shown over the whole screen by
/// <see cref="HabitsContentPage"/>. A tap on the scrim around the card closes it when the view model allows; the card
/// sits near the top while a reason is typed, so the keyboard does not cover it.
/// </summary>
public partial class HabitsPopup : UserControl
{
    private readonly HabitsViewModel _viewModel;

    /// <summary>For the XAML runtime loader and the designer; the page creates it with its view model.</summary>
    public HabitsPopup()
        : this(HelmAndroidServices.Current.GetRequiredService<HabitsViewModel>())
    {
    }

    public HabitsPopup(HabitsViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        // A tap on the card itself must not reach the scrim.
        Card.PointerPressed += (_, e) => e.Handled = true;
        Scrim.PointerPressed += (_, _) => _viewModel.DismissPopupCommand.Execute(null);
    }

    /// <summary>Places the card for what it shows: centred, or near the top for a reason to type.</summary>
    public void Place()
    {
        Card.VerticalAlignment = _viewModel.ShowLapse ? VerticalAlignment.Top : VerticalAlignment.Center;
        Card.Margin = _viewModel.ShowLapse ? new Avalonia.Thickness(16, 48, 16, 16) : new Avalonia.Thickness(16);
    }

    public void PlayConfetti(bool big) => Confetti.Play(big);
}
