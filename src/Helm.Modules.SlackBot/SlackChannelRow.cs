using CommunityToolkit.Mvvm.ComponentModel;

namespace Helm.Modules.SlackBot;

/// <summary>A channel or DM in the "Channels" list of the settings page, with what Slack Bot may do there.</summary>
public sealed partial class SlackChannelRow : ObservableObject
{
    private readonly Action<string, SlackChannelMode> _changed;

    [ObservableProperty] private SlackChannelMode _mode;

    public SlackChannelRow(SlackConversation conversation, SlackChannelMode mode, Action<string, SlackChannelMode> changed)
    {
        Id = conversation.Id;
        Name = conversation.Kind switch
        {
            SlackConversationKind.DirectMessage => "DM: " + conversation.Name,
            SlackConversationKind.GroupMessage => "Group DM: " + conversation.Name,
            SlackConversationKind.PrivateChannel => "🔒 " + conversation.Name,
            _ => "#" + conversation.Name,
        };
        _mode = mode;
        _changed = changed;
    }

    public static IReadOnlyList<SlackBotChoice<SlackChannelMode>> Modes { get; } =
    [
        new(SlackChannelMode.Off, "Off"),
        new(SlackChannelMode.Read, "Read"),
        new(SlackChannelMode.Reply, "Read and reply"),
    ];

    public string Id { get; }

    public string Name { get; }

    partial void OnModeChanged(SlackChannelMode value) => _changed(Id, value);
}
