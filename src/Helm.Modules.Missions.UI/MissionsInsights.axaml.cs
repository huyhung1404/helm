using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace Helm.Modules.Missions.Ui;

/// <summary>The Insights tab: statistics and badges across all missions.</summary>
public partial class MissionsInsights : UserControl
{
    public MissionsInsights() => InitializeComponent();

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Columns.Apply(this, "BadgeGrid", e.NewSize.Width < 640 ? 1 : e.NewSize.Width < 900 ? 2 : 3);
    }
}

/// <summary>Sets the columns of a named UniformGrid made by an ItemsPanelTemplate (it is not in the view's name scope).</summary>
internal static class Columns
{
    public static void Apply(Control root, string name, int columns)
    {
        foreach (var grid in root.GetVisualDescendants().OfType<UniformGrid>().Where(g => g.Name == name)) grid.Columns = columns;
    }
}
