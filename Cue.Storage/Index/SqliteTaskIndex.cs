using Cue.Domain;
using Microsoft.Data.Sqlite;

namespace Cue.Storage.Index;

/// <summary>
/// A SQLite-backed <see cref="ITaskIndex"/>. The database is a pure <i>cache</i> over the record
/// files: it holds only fields that can be recomputed from those files, so deleting it loses
/// nothing — <see cref="RebuildAsync"/> repopulates it from the source of truth on startup, and
/// the <c>ReflectAsync</c> overloads keep one record in sync after each store write.
/// </summary>
/// <remarks>
/// Raw SQL via Microsoft.Data.Sqlite, no EF Core — the index is a derived, throwaway layer and
/// doesn't warrant an ORM. A single connection is kept open for the lifetime of the instance and
/// access is serialized through a gate, since a SQLite connection is not safe for concurrent
/// commands.
/// <para>
/// Dates are stored as the calendar day the user pinned, in the task's own time zone, as an
/// ISO <c>yyyy-MM-dd</c> string (lexically sortable). The "current day" used by the time views is
/// computed at query time from the injected <see cref="TimeProvider"/> and zone — never stored — so
/// the same row moves between Upcoming → Today → overdue purely as the clock advances.
/// </para>
/// </remarks>
public sealed class SqliteTaskIndex : ITaskIndex, IAsyncDisposable, IDisposable
{
    private const string Columns =
        "id, title, project_id, parent_task_id, when_kind, when_date, " +
        "completed_at, priority, sort_order";

    private readonly SqliteConnection _connection;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _zone;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Opens (creating if absent) the index database at <paramref name="databasePath"/> and ensures
    /// its schema. <paramref name="timeProvider"/> and <paramref name="timeZone"/> define the
    /// "today" the time-axis views compare against; they default to the system clock and local zone.
    /// </summary>
    public SqliteTaskIndex(string databasePath, TimeProvider? timeProvider = null, TimeZoneInfo? timeZone = null)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("Index database path must be set.", nameof(databasePath));

        _clock = timeProvider ?? TimeProvider.System;
        _zone = timeZone ?? TimeZoneInfo.Local;

        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // One long-lived connection per index instance — pooling buys nothing and would keep the
            // OS file handle alive past Dispose, blocking a delete-and-rebuild of the cache on Windows.
            Pooling = false,
        }.ToString());
        _connection.Open();
        EnsureSchema();
    }

    // Bump when the index table shape changes. On a mismatch the (disposable, file-derived) tables
    // are dropped and recreated, then repopulated by the startup RebuildAsync — no data is lost.
    private const long SchemaVersion = 3;

    private void EnsureSchema()
    {
        using (var version = _connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version;";
            var current = Convert.ToInt64(version.ExecuteScalar() ?? 0L);
            if (current != SchemaVersion)
            {
                using var drop = _connection.CreateCommand();
                drop.CommandText =
                    "DROP TABLE IF EXISTS task_labels; DROP TABLE IF EXISTS tasks; " +
                    "DROP TABLE IF EXISTS projects; DROP TABLE IF EXISTS sections; DROP TABLE IF EXISTS labels;";
                // (sections is dropped only to clear any stale table from before the model change.)
                drop.ExecuteNonQuery();
            }
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS tasks (
                id              TEXT PRIMARY KEY,
                title           TEXT NOT NULL,
                project_id      TEXT NULL,
                parent_task_id  TEXT NULL,
                when_kind       TEXT NOT NULL,
                when_date       TEXT NULL,
                completed_at    TEXT NULL,
                deleted_at      TEXT NULL,
                priority        INTEGER NOT NULL DEFAULT 0,
                sort_order      TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS task_labels (
                task_id  TEXT NOT NULL,
                label_id TEXT NOT NULL,
                PRIMARY KEY (task_id, label_id)
            );
            CREATE TABLE IF NOT EXISTS projects (
                id            TEXT PRIMARY KEY,
                name          TEXT NOT NULL,
                icon          TEXT NULL,
                deleted_at    TEXT NULL,
                sort_order    TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS labels (
                id         TEXT PRIMARY KEY,
                name       TEXT NOT NULL,
                color      TEXT NULL,
                deleted_at TEXT NULL,
                sort_order TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS ix_tasks_project ON tasks(project_id);
            CREATE INDEX IF NOT EXISTS ix_tasks_when    ON tasks(when_kind, when_date);
            CREATE INDEX IF NOT EXISTS ix_tasks_active  ON tasks(deleted_at, completed_at);
            CREATE INDEX IF NOT EXISTS ix_labels_label  ON task_labels(label_id);
            CREATE INDEX IF NOT EXISTS ix_projects_active ON projects(deleted_at);
            CREATE INDEX IF NOT EXISTS ix_navigation_labels_active ON labels(deleted_at);
            PRAGMA user_version = 3;
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Rebuilds the entire index from the given tasks (the full contents of the task folder). Wipes
    /// the existing rows first, so a stale or deleted database ends up identical to a freshly built
    /// one. Tombstones are indexed too (with their <c>deleted_at</c>), so queries can exclude them.
    /// </summary>
    public async Task RebuildAsync(
        IEnumerable<TaskItem> tasks,
        IEnumerable<Project> projects,
        IEnumerable<Label> labels,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(labels);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await using (var clear = _connection.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText =
                    "DELETE FROM task_labels; DELETE FROM tasks; DELETE FROM projects; DELETE FROM labels;";
                await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var task in tasks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await UpsertCoreAsync(task, tx, cancellationToken).ConfigureAwait(false);
            }

            foreach (var project in projects)
                await UpsertProjectCoreAsync(project, tx, cancellationToken).ConfigureAwait(false);
            foreach (var label in labels)
                await UpsertLabelCoreAsync(label, tx, cancellationToken).ConfigureAwait(false);

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Reflects a single record's current file state into the index after a store write. Handles
    /// inserts, updates, and tombstones uniformly — the row always mirrors the file, including a set
    /// <c>deleted_at</c>, which is what removes it from the default queries.
    /// </summary>
    public async Task ReflectAsync(TaskItem task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await UpsertCoreAsync(task, tx, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task ReflectAsync(Project project, CancellationToken cancellationToken = default)
        => ReflectRecordAsync(project, UpsertProjectCoreAsync, cancellationToken);

    public Task ReflectAsync(Label label, CancellationToken cancellationToken = default)
        => ReflectRecordAsync(label, UpsertLabelCoreAsync, cancellationToken);

    private async Task ReflectRecordAsync<T>(
        T record,
        Func<T, SqliteTransaction, CancellationToken, Task> upsert,
        CancellationToken cancellationToken) where T : RecordBase
    {
        ArgumentNullException.ThrowIfNull(record);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await upsert(record, tx, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task UpsertCoreAsync(TaskItem task, SqliteTransaction tx, CancellationToken cancellationToken)
    {
        await using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                INSERT INTO tasks
                    (id, title, project_id, parent_task_id, when_kind, when_date,
                     completed_at, deleted_at, priority, sort_order)
                VALUES
                    ($id, $title, $project, $parent, $whenKind, $whenDate,
                     $completed, $deleted, $priority, $sort)
                ON CONFLICT(id) DO UPDATE SET
                    title           = excluded.title,
                    project_id      = excluded.project_id,
                    parent_task_id  = excluded.parent_task_id,
                    when_kind       = excluded.when_kind,
                    when_date       = excluded.when_date,
                    completed_at    = excluded.completed_at,
                    deleted_at      = excluded.deleted_at,
                    priority        = excluded.priority,
                    sort_order      = excluded.sort_order;
                """;
            Bind(cmd, "$id", task.Id.ToString());
            Bind(cmd, "$title", task.Title);
            Bind(cmd, "$project", task.ProjectId?.ToString());
            Bind(cmd, "$parent", task.ParentTaskId?.ToString());
            Bind(cmd, "$whenKind", task.When.Kind.ToString());
            Bind(cmd, "$whenDate", task.When.Kind == WhenKind.OnDate ? LocalDate(task.When.Date) : null);
            Bind(cmd, "$completed", Instant(task.CompletedAt));
            Bind(cmd, "$deleted", Instant(task.DeletedAt));
            Bind(cmd, "$priority", (int)task.Priority);
            Bind(cmd, "$sort", task.SortOrder);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Replace the label set for this task wholesale — cheap and keeps it exactly mirroring the file.
        await using (var del = _connection.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM task_labels WHERE task_id = $id;";
            Bind(del, "$id", task.Id.ToString());
            await del.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var labelId in task.LabelIds.Distinct())
        {
            await using var ins = _connection.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR IGNORE INTO task_labels (task_id, label_id) VALUES ($task, $label);";
            Bind(ins, "$task", task.Id.ToString());
            Bind(ins, "$label", labelId.ToString());
            await ins.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task UpsertProjectCoreAsync(Project project, SqliteTransaction tx, CancellationToken cancellationToken)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO projects (id, name, icon, deleted_at, sort_order)
            VALUES ($id, $name, $icon, $deleted, $sort)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name, icon = excluded.icon,
                deleted_at = excluded.deleted_at, sort_order = excluded.sort_order;
            """;
        Bind(cmd, "$id", project.Id.ToString());
        Bind(cmd, "$name", project.Name);
        Bind(cmd, "$icon", project.Icon);
        Bind(cmd, "$deleted", Instant(project.DeletedAt));
        Bind(cmd, "$sort", project.SortOrder);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UpsertLabelCoreAsync(Label label, SqliteTransaction tx, CancellationToken cancellationToken)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO labels (id, name, color, deleted_at, sort_order)
            VALUES ($id, $name, $color, $deleted, $sort)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name, color = excluded.color,
                deleted_at = excluded.deleted_at, sort_order = excluded.sort_order;
            """;
        Bind(cmd, "$id", label.Id.ToString());
        Bind(cmd, "$name", label.Name);
        Bind(cmd, "$color", label.Color);
        Bind(cmd, "$deleted", Instant(label.DeletedAt));
        Bind(cmd, "$sort", label.SortOrder);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---- Live navigation records --------------------------------------------

    public Task<IReadOnlyList<ProjectListItem>> GetProjectsAsync(CancellationToken cancellationToken = default)
        => QueryRecordsAsync(
            "SELECT id, name, icon, sort_order FROM projects " +
            "WHERE deleted_at IS NULL ORDER BY sort_order, name;",
            _ => { },
            r => new ProjectListItem(Guid.Parse(r.GetString(0)), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3)),
            cancellationToken);

    public Task<IReadOnlyList<LabelListItem>> GetLabelsAsync(CancellationToken cancellationToken = default)
        => QueryRecordsAsync(
            "SELECT id, name, color, sort_order FROM labels WHERE deleted_at IS NULL ORDER BY sort_order, name;",
            _ => { },
            r => new LabelListItem(Guid.Parse(r.GetString(0)), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3)),
            cancellationToken);

    public Task<IReadOnlyDictionary<Guid, int>> GetOpenTaskCountsByProjectAsync(CancellationToken cancellationToken = default)
        => QueryCountsAsync(
            "SELECT project_id, COUNT(*) FROM tasks " +
            "WHERE deleted_at IS NULL AND completed_at IS NULL AND project_id IS NOT NULL " +
            "GROUP BY project_id;",
            cancellationToken);

    public Task<IReadOnlyDictionary<Guid, int>> GetOpenTaskCountsByLabelAsync(CancellationToken cancellationToken = default)
        => QueryCountsAsync(
            "SELECT tl.label_id, COUNT(*) FROM task_labels tl " +
            "INNER JOIN tasks t ON t.id = tl.task_id " +
            "WHERE t.deleted_at IS NULL AND t.completed_at IS NULL " +
            "GROUP BY tl.label_id;",
            cancellationToken);

    internal Task<IReadOnlyList<Guid>> GetTaskIdsByProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
        => QueryIdsAsync(
            "SELECT id FROM tasks WHERE project_id = $id AND deleted_at IS NULL;",
            cmd => Bind(cmd, "$id", projectId.ToString()), cancellationToken);

    internal Task<IReadOnlyList<Guid>> GetTaskIdsByLabelAsync(Guid labelId, CancellationToken cancellationToken = default)
        => QueryIdsAsync(
            "SELECT t.id FROM tasks t INNER JOIN task_labels tl ON tl.task_id = t.id " +
            "WHERE tl.label_id = $id AND t.deleted_at IS NULL;",
            cmd => Bind(cmd, "$id", labelId.ToString()), cancellationToken);

    // ---- Classification axis -------------------------------------------------

    // Active lists keep completed tasks (shown dimmed) rather than dropping them on the next load, so
    // finishing an item doesn't make it vanish; they sink below the open rows via the completed-last
    // ordering. Open-task counts (badges) still exclude completed — see GetOpenTaskCounts*.
    // The home "모든 할 일" (All) list spans every group — no project filter — so a task in a group
    // still surfaces here. Subtasks are included; the view nests them under their parents.
    public Task<IReadOnlyList<TaskListItem>> GetAllActiveAsync(CancellationToken cancellationToken = default)
        => QueryAsync(
            $"SELECT {Columns} FROM tasks WHERE deleted_at IS NULL " +
            "ORDER BY completed_at IS NOT NULL, sort_order;",
            _ => { }, cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetByProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
        => QueryAsync(
            $"SELECT {Columns} FROM tasks WHERE deleted_at IS NULL " +
            "AND project_id = $project ORDER BY completed_at IS NOT NULL, sort_order;",
            cmd => Bind(cmd, "$project", projectId.ToString()), cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetByLabelAsync(Guid labelId, CancellationToken cancellationToken = default)
        => QueryAsync(
            $"SELECT {Prefixed("t")} FROM tasks t " +
            "INNER JOIN task_labels tl ON tl.task_id = t.id " +
            "WHERE tl.label_id = $label AND t.deleted_at IS NULL " +
            "ORDER BY t.completed_at IS NOT NULL, t.sort_order;",
            cmd => Bind(cmd, "$label", labelId.ToString()), cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetSubtasksAsync(Guid parentTaskId, CancellationToken cancellationToken = default)
        => QueryAsync(
            $"SELECT {Columns} FROM tasks WHERE deleted_at IS NULL " +
            "AND parent_task_id = $parent ORDER BY sort_order;",
            cmd => Bind(cmd, "$parent", parentTaskId.ToString()), cancellationToken);

    // ---- Time axis (computed against the current day) ------------------------

    public Task<IReadOnlyList<TaskListItem>> GetTodayAsync(CancellationToken cancellationToken = default)
        => QueryAsync(
            $"SELECT {Columns} FROM tasks WHERE deleted_at IS NULL " +
            "AND when_kind = 'OnDate' AND when_date IS NOT NULL AND when_date <= $today " +
            "ORDER BY completed_at IS NOT NULL, when_date, sort_order;",
            cmd => Bind(cmd, "$today", Today()), cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetUpcomingAsync(CancellationToken cancellationToken = default)
        => QueryAsync(
            $"SELECT {Columns} FROM tasks WHERE deleted_at IS NULL " +
            "AND when_kind = 'OnDate' AND when_date > $today " +
            "ORDER BY completed_at IS NOT NULL, when_date, sort_order;",
            cmd => Bind(cmd, "$today", Today()), cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetAnytimeAsync(CancellationToken cancellationToken = default)
        => QueryAsync(
            $"SELECT {Columns} FROM tasks WHERE deleted_at IS NULL " +
            "AND when_kind = 'Unscheduled' ORDER BY completed_at IS NOT NULL, sort_order;",
            _ => { }, cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetLogbookAsync(CancellationToken cancellationToken = default)
        => QueryAsync(
            $"SELECT {Columns} FROM tasks WHERE deleted_at IS NULL AND completed_at IS NOT NULL " +
            "ORDER BY completed_at DESC;",
            _ => { }, cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetByPriorityAsync(CancellationToken cancellationToken = default)
        => QueryAsync(
            $"SELECT {Columns} FROM tasks WHERE deleted_at IS NULL AND priority <> 0 " +
            "ORDER BY priority, completed_at IS NOT NULL, sort_order;",
            _ => { }, cancellationToken);

    public Task<IReadOnlyList<TimelineTaskItem>> GetTimelineAsync(
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken = default)
    {
        if (end < start)
            throw new ArgumentException("Timeline end date must be on or after the start date.", nameof(end));

        return QueryRecordsAsync(
            $"""
            SELECT id, title, when_date, completed_at, priority
            FROM tasks
            WHERE deleted_at IS NULL
              AND when_kind = 'OnDate' AND when_date IS NOT NULL
              AND when_date >= $start
              AND when_date <= $end
            ORDER BY when_date, completed_at IS NOT NULL, sort_order;
            """,
            cmd =>
            {
                Bind(cmd, "$start", start.ToString("yyyy-MM-dd"));
                Bind(cmd, "$end", end.ToString("yyyy-MM-dd"));
            },
            r => new TimelineTaskItem(
                Guid.Parse(r.GetString(0)),
                r.GetString(1),
                DateOnly.ParseExact(r.GetString(2), "yyyy-MM-dd"),
                !r.IsDBNull(3),
                (Priority)r.GetInt64(4)),
            cancellationToken);
    }

    // ---- Plumbing ------------------------------------------------------------

    private async Task<IReadOnlyList<TaskListItem>> QueryAsync(
        string sql, Action<SqliteCommand> bind, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            bind(cmd);

            var results = new List<TaskListItem>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                results.Add(Map(reader));
            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<T>> QueryRecordsAsync<T>(
        string sql,
        Action<SqliteCommand> bind,
        Func<SqliteDataReader, T> map,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            bind(cmd);
            var results = new List<T>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                results.Add(map(reader));
            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task<IReadOnlyList<Guid>> QueryIdsAsync(
        string sql, Action<SqliteCommand> bind, CancellationToken cancellationToken)
        => QueryRecordsAsync(sql, bind, r => Guid.Parse(r.GetString(0)), cancellationToken);

    private async Task<IReadOnlyDictionary<Guid, int>> QueryCountsAsync(string sql, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            var results = new Dictionary<Guid, int>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                results[Guid.Parse(reader.GetString(0))] = (int)reader.GetInt64(1);
            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static TaskListItem Map(SqliteDataReader r) => new(
        Id: Guid.Parse(r.GetString(0)),
        Title: r.GetString(1),
        ProjectId: GuidOrNull(r, 2),
        ParentTaskId: GuidOrNull(r, 3),
        WhenKind: Enum.Parse<WhenKind>(r.GetString(4)),
        WhenDate: DateOrNull(r, 5),
        IsCompleted: !r.IsDBNull(6),
        Priority: (Priority)r.GetInt64(7),
        SortOrder: r.GetString(8));

    /// <summary>The current calendar day in the configured zone — computed, never stored.</summary>
    private string Today()
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), _zone).DateTime)
            .ToString("yyyy-MM-dd");

    private static string? LocalDate(ZonedDateTime? zoned)
        => zoned is null
            ? null
            : DateOnly.FromDateTime(zoned.Value.ToLocal().DateTime).ToString("yyyy-MM-dd");

    private static string? Instant(DateTimeOffset? value)
        => value?.ToUniversalTime().ToString("O");

    private static Guid? GuidOrNull(SqliteDataReader r, int i)
        => r.IsDBNull(i) ? null : Guid.Parse(r.GetString(i));

    private static DateOnly? DateOrNull(SqliteDataReader r, int i)
        => r.IsDBNull(i) ? null : DateOnly.ParseExact(r.GetString(i), "yyyy-MM-dd");

    private static void Bind(SqliteCommand cmd, string name, object? value)
        => cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

    /// <summary>Column list qualified with a table alias, for the label join query.</summary>
    private static string Prefixed(string alias)
        => string.Join(", ", Columns.Split(',', StringSplitOptions.TrimEntries).Select(c => $"{alias}.{c}"));

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    public void Dispose()
    {
        _connection.Dispose();
        _gate.Dispose();
    }
}
