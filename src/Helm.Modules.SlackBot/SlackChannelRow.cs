using System.Collections.ObjectModel;
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
    private readonly Action<string, IReadOnlyList<string>>? _projectsChanged;
    private readonly Action<string, SlackAiChoice>? _aiChanged;
    private SlackChannelMode _mode;
    private string _model;
    private string _effort;

    public SlackChannelRow(SlackConversation conversation, SlackChannelMode mode, Action<string, SlackChannelMode> changed,
        IEnumerable<string>? projects = null, Action<string, IReadOnlyList<string>>? projectsChanged = null,
        SlackAiChoice? ai = null, Action<string, SlackAiChoice>? aiChanged = null)
    {
        Projects = new ObservableCollection<string>(projects ?? []);
        _projectsChanged = projectsChanged;
        _model = ai?.Model ?? "";
        _effort = ai?.Effort ?? "";
        _aiChanged = aiChanged;
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

    /// <summary>This conversation's own Claude model ("" = the default on the settings page).</summary>
    public string Model
    {
        get => _model;
        set => SetAi(value, _effort);
    }

    /// <summary>This conversation's own effort ("" = the default on the settings page).</summary>
    public string Effort
    {
        get => _effort;
        set => SetAi(_model, value);
    }

    /// <summary>"Default", or what is set: "Fable · High".</summary>
    public string AiLabel => _model.Length == 0 && _effort.Length == 0
        ? "AI: default"
        : $"AI: {SlackAiOptions.Short(_model, "default model")} · {SlackAiOptions.Short(_effort, "default effort")}";

    private void SetAi(string model, string effort)
    {
        if (_model == model && _effort == effort) return;
        (_model, _effort) = (model, effort);
        OnPropertyChanged(nameof(Model));
        OnPropertyChanged(nameof(Effort));
        OnPropertyChanged(nameof(AiLabel));
        _aiChanged?.Invoke(Id, new SlackAiChoice { Model = model, Effort = effort });
    }

    /// <summary>The project folders Lumi may read before answering here (none: the chat only).</summary>
    public ObservableCollection<string> Projects { get; }

    public bool HasProjects => Projects.Count > 0;

    public void AddProject(string folder)
    {
        folder = folder.Trim().Trim('"');
        if (folder.Length == 0 || Projects.Contains(folder, StringComparer.OrdinalIgnoreCase)) return;
        Projects.Add(folder);
        ProjectsChanged();
    }

    public void RemoveProject(string folder)
    {
        if (Projects.Remove(folder)) ProjectsChanged();
    }

    private void ProjectsChanged()
    {
        OnPropertyChanged(nameof(HasProjects));
        _projectsChanged?.Invoke(Id, [.. Projects]);
    }

    /// <summary>Slack Bot may also write there: its replies, and AI agents' reports.</summary>
    public bool CanWrite
    {
        get => _mode == SlackChannelMode.Reply;
        set => Mode = value ? SlackChannelMode.Reply : _mode == SlackChannelMode.Off ? SlackChannelMode.Off : SlackChannelMode.Read;
    }
}
