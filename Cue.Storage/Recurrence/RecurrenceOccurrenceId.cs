using System.Security.Cryptography;
using System.Text;

namespace Cue.Storage.Recurrence;

/// <summary>Deterministic identity of one recurrence cycle.</summary>
public static class RecurrenceOccurrenceId
{
    /// <summary>
    /// Derives the stable occurrence id from a series and the cycle's UTC instant. Shared by recurrence
    /// persistence and stale-toast guards so both identify a cycle identically.
    /// </summary>
    public static Guid From(Guid seriesId, DateTimeOffset occurrenceUtc)
    {
        var name = $"cue/recurrence-occurrence/{seriesId:N}/{occurrenceUtc.UtcDateTime.Ticks}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        return new Guid(hash.AsSpan(0, 16));
    }
}
