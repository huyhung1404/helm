using Avalonia.Controls;

namespace Helm.Modules.Missions.Ui;

/// <summary>The step to do now, with its checklist, resources, note and Complete.</summary>
public partial class CurrentStepCard : UserControl
{
    public CurrentStepCard() => InitializeComponent();

    /// <summary>The card, for the short pulse when a step is done here.</summary>
    public Control Card => CardBorder;
}
