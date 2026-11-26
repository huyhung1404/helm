using Avalonia.Controls;

namespace Helm.Modules.Missions.Ui;

/// <summary>The first run of Missions: three steps, the one button to take the first, and the ready-made missions.</summary>
public partial class MissionsFirstRun : UserControl
{
    public MissionsFirstRun() => InitializeComponent();

    /// <summary>One column on a phone, three side by side on a wider page.</summary>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var narrow = e.NewSize.Width < 640;
        Steps.Columns = narrow ? 1 : 3;
        Columns.Apply(this, "TemplateGrid", narrow ? 1 : 3);
    }
}
