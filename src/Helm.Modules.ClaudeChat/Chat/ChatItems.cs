using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Helm.Modules.ClaudeChat.Chat;

/// <summary>One row in the conversation. Each subtype has its own DataTemplate in ClaudeChatView.xaml.</summary>
public abstract class ChatItem : ObservableObject;

public sealed class UserChatItem(string text) : ChatItem
{
    public string Text { get; } = text;
}

/// <summary>Assistant text; grows while it streams.</summary>
public sealed partial class AssistantChatItem : ChatItem
{
    [ObservableProperty] private string _text = string.Empty;

    [ObservableProperty] private bool _isStreaming = true;

    public void Append(string delta) => Text += delta;
}

/// <summary>A tool call and, once it arrives, its result.</summary>
public sealed partial class ToolChatItem(string id, string name, string summary) : ChatItem
{
    public string Id { get; } = id;
    public string Name { get; } = name;

    /// <summary>The one detail worth showing inline: a path, a command, a pattern.</summary>
    public string Summary { get; } = summary;

    [ObservableProperty] private string? _result;

    [ObservableProperty] private bool _isError;

    [ObservableProperty] private bool _isRunning = true;

    public void Complete(string result, bool isError)
    {
        Result = result;
        IsError = isError;
        IsRunning = false;
    }

    public static string Summarize(string name, JsonElement input)
    {
        foreach (var key in (string[])["file_path", "command", "pattern", "path", "url", "query", "description", "prompt"])
        {
            if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = (v.GetString() ?? string.Empty).ReplaceLineEndings(" ");
                return s.Length <= 160 ? s : s[..160] + "…";
            }
        }
        return name;
    }
}

public enum NoticeKind
{
    Info,
    Warning,
    Error,
}

/// <summary>Something Helm or the CLI says about the conversation (interrupted, denied, crashed...).</summary>
public sealed class NoticeChatItem(string text, NoticeKind kind = NoticeKind.Info) : ChatItem
{
    public string Text { get; } = text;
    public NoticeKind Kind { get; } = kind;
}
