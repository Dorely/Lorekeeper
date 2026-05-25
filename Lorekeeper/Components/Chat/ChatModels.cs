using System.Text;
using System.Text.Json;

namespace Lorekeeper.Components.Chat;

public enum ChatMessageRole
{
    User,
    Assistant,
}

public enum ChatMessageStatus
{
    Pending,
    Completed,
    Failed,
    Cancelled,
}

public sealed record ChatRenderableMessage(
    Guid Id,
    ChatMessageRole Role,
    ChatMessageStatus Status,
    List<ChatMessagePart> Parts);

public abstract class ChatMessagePart;

public sealed class ChatTextPart : ChatMessagePart
{
    public ChatTextPart(string text) => Text.Append(text);

    public StringBuilder Text { get; } = new();

    public void Append(string text) => Text.Append(text);
}

public sealed class ChatToolPart(ChatToolChip chip) : ChatMessagePart
{
    public ChatToolChip Chip { get; } = chip;
}

public sealed class ChatToolChip
{
    private readonly StringBuilder _arguments = new();

    public ChatToolChip(string callId, string name, string argumentsJson, bool argumentsComplete = true)
    {
        CallId = callId;
        Name = name;
        SetArguments(argumentsJson);
        ArgumentsComplete = argumentsComplete;
    }

    public string CallId { get; }

    public string Name { get; private set; }

    public string ArgumentsJson => _arguments.ToString();

    public string? Result { get; set; }

    public string? Error { get; set; }

    public ChatToolProgress? Progress { get; set; }

    public double? DurationMs { get; set; }

    public bool Completed { get; set; }

    public bool ArgumentsComplete { get; private set; }

    public bool HasArguments => !string.IsNullOrWhiteSpace(ArgumentsJson) && ArgumentsJson != "{}";

    public void Rename(string name) => Name = name;

    public void SetArguments(string argumentsJson)
    {
        _arguments.Clear();
        if (!string.IsNullOrEmpty(argumentsJson))
            _arguments.Append(argumentsJson);
    }

    public void AppendArguments(string argumentsDelta, bool argumentsComplete)
    {
        if (!string.IsNullOrEmpty(argumentsDelta))
            _arguments.Append(argumentsDelta);
        ArgumentsComplete = argumentsComplete || ArgumentsComplete;
    }

    public void MarkArgumentsComplete(string? argumentsJson = null)
    {
        if (argumentsJson is not null)
            SetArguments(argumentsJson);
        ArgumentsComplete = true;
    }
}

public sealed class ChatToolProgress
{
    public Guid? JobId { get; set; }

    public int Version { get; set; }

    public string Status { get; set; } = string.Empty;

    public DateTime? UpdatedAt { get; set; }

    public int TotalCount { get; set; }

    public int QueuedCount { get; set; }

    public int RunningCount { get; set; }

    public int CompletedCount { get; set; }

    public int FailedCount { get; set; }

    public int InvalidCount { get; set; }

    public int CancelledCount { get; set; }

    public List<ChatToolProgressRow> Rows { get; set; } = [];
}

public sealed record ChatToolProgressRow(
    Guid Id,
    int Order,
    string Title,
    string Status,
    string Summary,
    string? ErrorMessage);

public sealed class ChatLiveTurn
{
    private bool _startNewMessageOnNextPart;

    public List<ChatLiveMessage> Messages { get; } = [];

    public bool HasContent => Messages.Any(message => message.Parts.Count > 0);

    public bool IsThinking { get; private set; } = true;

    public void AppendText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        IsThinking = false;
        CurrentMessage().AppendText(text);
    }

    public void StartToolCall(string callId, string name, string argumentsJson, bool argumentsComplete)
    {
        IsThinking = false;
        var chip = FindToolChip(callId);
        if (chip is null)
        {
            chip = new ChatToolChip(callId, name, argumentsJson, argumentsComplete);
            ToolMessage().Parts.Add(new ChatToolPart(chip));
            return;
        }

        chip.Rename(name);
        if (!string.IsNullOrEmpty(argumentsJson))
            chip.SetArguments(argumentsJson);
        if (argumentsComplete)
            chip.MarkArgumentsComplete();
    }

    public void AppendToolArguments(string callId, string argumentsDelta, bool argumentsComplete)
    {
        IsThinking = false;
        var chip = FindToolChip(callId);
        if (chip is null)
        {
            chip = new ChatToolChip(callId, callId, string.Empty, argumentsComplete: false);
            ToolMessage().Parts.Add(new ChatToolPart(chip));
        }

        chip.AppendArguments(argumentsDelta, argumentsComplete);
    }

    public void CompleteToolCall(string callId, string? result, string? error, double? durationMs = null)
    {
        var chip = FindToolChip(callId);
        if (chip is not null)
        {
            chip.Result = result;
            chip.Error = error;
            chip.DurationMs = durationMs;
            chip.Completed = true;
            chip.MarkArgumentsComplete();
        }

        IsThinking = true;
        _startNewMessageOnNextPart = true;
    }

    public void UpdateToolProgress(string callId, string toolName, ChatToolProgress progress)
    {
        IsThinking = false;
        var chip = FindToolChip(callId);
        if (chip is null)
        {
            chip = new ChatToolChip(callId, toolName, string.Empty);
            ToolMessage().Parts.Add(new ChatToolPart(chip));
        }
        else
        {
            chip.Rename(toolName);
        }

        progress.Version = (chip.Progress?.Version ?? 0) + 1;
        chip.Progress = progress;
    }

    private ChatToolChip? FindToolChip(string callId) => Messages
        .SelectMany(message => message.Parts)
        .OfType<ChatToolPart>()
        .Select(part => part.Chip)
        .FirstOrDefault(candidate => candidate.CallId == callId);

    private ChatLiveMessage CurrentMessage()
    {
        if (_startNewMessageOnNextPart || Messages.Count == 0)
        {
            var message = new ChatLiveMessage();
            Messages.Add(message);
            _startNewMessageOnNextPart = false;
            return message;
        }

        return Messages[^1];
    }

    private ChatLiveMessage ToolMessage()
    {
        if (_startNewMessageOnNextPart
            && Messages.LastOrDefault() is { } lastMessage
            && lastMessage.Parts.Count > 0
            && lastMessage.Parts.All(part => part is ChatToolPart))
        {
            _startNewMessageOnNextPart = false;
            return lastMessage;
        }

        return CurrentMessage();
    }
}

public sealed class ChatLiveMessage
{
    public List<ChatMessagePart> Parts { get; } = [];

    public void AppendText(string text)
    {
        if (Parts.LastOrDefault() is ChatTextPart textPart)
            textPart.Append(text);
        else
            Parts.Add(new ChatTextPart(text));
    }
}

public sealed record ChatPersistedToolCall(string CallId, string Name, string ArgumentsJson, int? TextOffset = null);

public static class ChatTranscriptHelpers
{
    public static List<ChatPersistedToolCall> ReadPersistedCalls(string toolCallsJson)
    {
        if (string.IsNullOrWhiteSpace(toolCallsJson) || toolCallsJson == "[]") return [];

        try
        {
            return JsonSerializer.Deserialize<List<ChatPersistedToolCall>>(toolCallsJson) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static void AppendLiveTurnForTokenCount(StringBuilder sb, ChatLiveTurn? live)
    {
        if (live is null) return;

        foreach (var message in live.Messages)
        {
            foreach (var part in message.Parts)
            {
                switch (part)
                {
                    case ChatTextPart textPart:
                        var text = textPart.Text.ToString();
                        if (!string.IsNullOrEmpty(text))
                            sb.AppendLine(text);
                        break;
                    case ChatToolPart toolPart:
                        AppendToolChipForTokenCount(sb, toolPart.Chip);
                        break;
                }
            }
        }
    }

    public static void AppendToolChipForTokenCount(StringBuilder sb, ChatToolChip chip)
    {
        sb.Append("Tool: ").Append(chip.Name).Append(' ').AppendLine(chip.CallId);
        if (chip.HasArguments)
            sb.Append("Args: ").AppendLine(chip.ArgumentsJson);
        if (!string.IsNullOrWhiteSpace(chip.Result))
            sb.Append("Result: ").AppendLine(chip.Result);
        if (!string.IsNullOrWhiteSpace(chip.Error))
            sb.Append("Error: ").AppendLine(chip.Error);
    }

    public static string TruncateInline(string value, int max)
    {
        value = value.Replace('\n', ' ').Replace('\r', ' ');
        return value.Length <= max ? value : value[..max] + "...";
    }
}
