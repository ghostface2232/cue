namespace Cue.Services;

internal enum ToastAction
{
    Open,
    Complete,
}

/// <summary>Parsed form of action=open|complete&amp;taskId=...&amp;occurrenceId=...</summary>
internal sealed record ToastActivationPayload(ToastAction Action, Guid TaskId, Guid? OccurrenceId)
{
    public string ToArguments()
    {
        var action = Action == ToastAction.Complete ? "complete" : "open";
        var result = $"action={action}&taskId={TaskId:D}";
        return OccurrenceId is { } occurrenceId
            ? $"{result}&occurrenceId={occurrenceId:D}"
            : result;
    }

    public ToastActivationPayload AsOpen() => this with { Action = ToastAction.Open };

    public static bool TryParse(string? arguments, out ToastActivationPayload? payload)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(arguments))
            return false;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in arguments.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
                continue;

            var key = Uri.UnescapeDataString(pair[..separator]);
            var value = Uri.UnescapeDataString(pair[(separator + 1)..]);
            values[key] = value;
        }

        if (!values.TryGetValue("action", out var actionText) ||
            !values.TryGetValue("taskId", out var taskIdText) ||
            !Guid.TryParse(taskIdText, out var taskId))
        {
            return false;
        }

        var action = actionText.ToLowerInvariant() switch
        {
            "open" => ToastAction.Open,
            "complete" => ToastAction.Complete,
            _ => (ToastAction?)null,
        };
        if (action is null)
            return false;

        Guid? occurrenceId = null;
        if (values.TryGetValue("occurrenceId", out var occurrenceIdText))
        {
            if (!Guid.TryParse(occurrenceIdText, out var parsedOccurrenceId))
                return false;
            occurrenceId = parsedOccurrenceId;
        }

        payload = new ToastActivationPayload(action.Value, taskId, occurrenceId);
        return true;
    }
}
