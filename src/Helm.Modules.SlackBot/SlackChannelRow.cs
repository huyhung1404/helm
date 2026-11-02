using CommunityToolkit.Mvvm.ComponentModel;

namespace Helm.Modules.SlackBot;

/// <summary>
/// A channel or DM in the "Channels and DMs" list of the settings page, with two switches: Read and Write. Both off is
/// <see cref="SlackChannelMode.Off"/>; Write needs Read, so turning Write on turns Read on and turning Read off turns
/// Write off.
/// </summary>
public sealed partial class SlackChannelRow : ObservableObject
{
    private readonly Action<string, SlackChannelMode> _changed;
    private SlackChannelMode _mode;

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

    public string Id { get; }

    public string Name { get; }

    public SlackChannelMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanRead));
            OnPropertyChanged(nameof(CanWrite));
            _changed(Id, value);
        }
    }

    /// <summary>Slack Bot reads it (on the list and in the report).</summary>
    public bool CanRead
    {
        get => _mode != SlackChannelMode.Off;
        set => Mode = value ? (_mode == SlackChannelMode.Off ? SlackChannelMode.Read : _mode) : SlackChannelMode.Off;
    }

    /// <summary>Slack Bot may also write there: its replies, and AI agents' reports.</summary>
    public bool CanWrite
    {
        get => _mode == SlackChannelMode.Reply;
        set => Mode = value ? SlackChannelMode.Reply : _mode == SlackChannelMode.Off ? SlackChannelMode.Off : SlackChannelMode.Read;
    }
}
