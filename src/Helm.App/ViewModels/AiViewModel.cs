using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Ai;

namespace Helm.App.ViewModels;

/// <summary>
/// The AI page: the models Helm calls with the user's API keys (Models &amp; keys), the agents on this PC that take
/// Helm's AI jobs first (Proxy), and the AI agents that use Helm's tools over MCP (Agents &amp; MCP).
/// </summary>
internal sealed partial class AiViewModel(AiKeysViewModel keys, AiProxyViewModel proxy, McpViewModel agents) : ObservableObject
{
    internal enum AiTab
    {
        Keys,
        Proxy,
        Agents,
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsKeysTab), nameof(IsProxyTab), nameof(IsAgentsTab))]
    private AiTab _tab;

    public AiKeysViewModel Keys { get; } = keys;
    public AiProxyViewModel Proxy { get; } = proxy;
    public McpViewModel Agents { get; } = agents;

    public bool IsKeysTab => Tab == AiTab.Keys;
    public bool IsProxyTab => Tab == AiTab.Proxy;
    public bool IsAgentsTab => Tab == AiTab.Agents;

    [RelayCommand]
    private void ShowKeys() => Tab = AiTab.Keys;

    [RelayCommand]
    private void ShowProxy()
    {
        Tab = AiTab.Proxy;
        Proxy.Refresh();
    }

    [RelayCommand]
    private void ShowAgents() => Tab = AiTab.Agents;
}
