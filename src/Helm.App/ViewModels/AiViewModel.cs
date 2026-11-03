using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Ai;

namespace Helm.App.ViewModels;

/// <summary>
/// The AI page: the models Helm calls with the user's API keys (Models &amp; keys), and the AI agents that use Helm's
/// tools over MCP (Agents &amp; MCP).
/// </summary>
internal sealed partial class AiViewModel(AiKeysViewModel keys, McpViewModel agents) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsKeysTab))]
    private bool _isAgentsTab;

    public AiKeysViewModel Keys { get; } = keys;
    public McpViewModel Agents { get; } = agents;

    public bool IsKeysTab => !IsAgentsTab;

    [RelayCommand]
    private void ShowKeys() => IsAgentsTab = false;

    [RelayCommand]
    private void ShowAgents() => IsAgentsTab = true;
}
