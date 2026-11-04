namespace Cue.Storage;

public enum SkippedFileReason
{
    /// <summary>Named as a record, but its content couldn't be read or isn't that record.</summary>
    Unreadable,

    /// <summary>Not named <c>{guid}.json</c>: a cloud provider's conflict copy, or a stray file.</summary>
    NotARecord,
}

/// <summary>A file in a record folder that was left out of the app's data.</summary>
public sealed record SkippedFile(string Path, SkippedFileReason Reason);
