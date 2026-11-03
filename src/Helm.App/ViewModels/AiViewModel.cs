using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Ai;

namespace Helm.App.ViewModels;

/// <summary>
/// The AI page: the models Helm calls with the user's API keys (Models &amp; keys), the agents on this PC that take
/// Helm's AI jobs first (Proxy), the AI agents that use Helm's tools over MCP (Agents &amp; MCP), and each AI feature's
/// settings (Features).
/// </summary>
internal sealed partial class AiViewModel(AiKeysViewModel keys, AiProxyViewModel proxy, McpViewModel agents, AiFeaturesViewModel features)
    : ObservableObject
{
    internal enum AiTab
    {
        Keys,
        Proxy,
        Agents,
        Features,
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsKeysTab), nameof(IsProxyTab), nameof(IsAgentsTab), nameof(IsFeaturesTab))]
    private AiTab _tab;

    public AiKeysViewModel Keys { get; } = keys;
    public AiProxyViewModel Proxy { get; } = proxy;
    public McpViewModel Agents { get; } = agents;
    public AiFeaturesViewModel Features { get; } = features;

    public bool IsKeysTab => Tab == AiTab.Keys;
    public bool IsProxyTab => Tab == AiTab.Proxy;
    public bool IsAgentsTab => Tab == AiTab.Agents;
    public bool IsFeaturesTab => Tab == AiTab.Features;

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

    [RelayCommand]
    private void ShowFeatures()
    {
        Tab = AiTab.Features;
        Features.Refresh();
    }
}
