using System.Text.Json;

namespace Cue.Services;

/// <summary>
/// A small key-value settings file for the unpackaged channel (see <c>LocalSettingsStore</c>): one
/// flat JSON object holding the primitive shapes the preference classes store (string, bool, double).
/// Values live in memory once loaded; every <see cref="Set"/> writes through atomically (temp file +
/// move) so a crash mid-write can't corrupt the previous settings. All IO is best-effort — an
/// unreadable or corrupt file starts from defaults and a failed write keeps the value for the session
/// — because a preference must never take the app down. No WinUI dependency, so the test project
/// compiles this file directly.
/// </summary>
public sealed class JsonSettingsFile
{
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, object>? _values;

    public JsonSettingsFile(string path) => _path = path;

    public bool TryGetValue(string key, out object? value)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (_values!.TryGetValue(key, out var stored))
            {
                value = stored;
                return true;
            }
            value = null;
            return false;
        }
    }

    public void Set(string key, object value)
    {
        lock (_gate)
        {
            EnsureLoaded();
            _values![key] = value;
            Persist();
        }
    }

    private void EnsureLoaded()
    {
        if (_values is not null)
            return;

        var values = new Dictionary<string, object>();
        try
        {
            if (File.Exists(_path))
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(_path));
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    object? parsed = property.Value.ValueKind switch
                    {
                        JsonValueKind.String => property.Value.GetString(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Number => property.Value.GetDouble(),
                        _ => null, // no preference stores arrays/objects/null — drop anything else
                    };
                    if (parsed is not null)
                        values[property.Name] = parsed;
                }
            }
        }
        catch
        {
            // An unreadable or corrupt settings file must never block startup; start from defaults.
            // The next successful Set rewrites the file wholesale.
            values.Clear();
        }
        _values = values;
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(_values));
            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
            // Best-effort: the in-memory value still applies for this session.
        }
    }
}
