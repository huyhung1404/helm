using System.Windows.Controls;

namespace Helm.App.Views.Ai;

/// <summary>The Skill Hub card of the AI page's Features tab; its DataContext is a <see cref="Helm.Ai.Prompts.SkillHubViewModel"/>.</summary>
internal partial class SkillHubView : UserControl
{
    public SkillHubView() => InitializeComponent();
}
