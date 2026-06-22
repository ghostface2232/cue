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
        "id, title, project_id, section_id, parent_task_id, when_kind, when_date, " +
        "when_is_evening, deadline_date, completed_at, priority, sort_order";

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

    private void EnsureSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS tasks (
                id              TEXT PRIMARY KEY,
                title           TEXT NOT NULL,
                project_id      TEXT NULL,
                section_id      TEXT NULL,
                parent_task_id  TEXT NULL,
                when_kind       TEXT NOT NULL,
                when_date       TEXT NULL,
                when_is_evening INTEGER NOT NULL DEFAULT 0,
                deadline_date   TEXT NULL,
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
                deadline_date TEXT NULL,
                is_archived   INTEGER NOT NULL DEFAULT 0,
                completed_at  TEXT NULL,
                deleted_at    TEXT NULL,
                sort_order    TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS sections (
                id            TEXT PRIMARY KEY,
                project_id    TEXT NOT NULL,
                name          TEXT NOT NULL,
                deadline_date TEXT NULL,
                is_archived   INTEGER NOT NULL DEFAULT 0,
                completed_at  TEXT NULL,
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
            CREATE INDEX IF NOT EXISTS ix_tasks_section ON tasks(section_id);
            CREATE INDEX IF NOT EXISTS ix_tasks_when    ON tasks(when_kind, when_date);
            CREATE INDEX IF NOT EXISTS ix_tasks_active  ON tasks(deleted_at, completed_at);
            CREATE INDEX IF NOT EXISTS ix_labels_label  ON task_labels(label_id);
            CREATE INDEX IF NOT EXISTS ix_projects_active ON projects(deleted_at, is_archived, completed_at);
            CREATE INDEX IF NOT EXISTS ix_sections_project ON sections(project_id, deleted_at, is_archived, completed_at);
            CREATE INDEX IF NOT EXISTS ix_navigation_labels_active ON labels(deleted_at);
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
        IEnumerable<Section> sections,
        IEnumerable<Label> labels,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(labels);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await using (var clear = _connection.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText =
                    "DELETE FROM task_labels; DELETE FROM tasks; DELETE FROM projects; " +
                    "DELETE FROM sections; DELETE FROM labels;";
                await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var task in tasks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await UpsertCoreAsync(task, tx, cancellationToken).ConfigureAwait(false);
            }

            foreach (var project in projects)
                await UpsertProjectCoreAsync(project, tx, cancellationToken).ConfigureAwait(false);
            foreach (var section in sections)
                await UpsertSectionCoreAsync(section, tx, cancellationToken).ConfigureAwait(false);
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

    public Task ReflectAsync(Section section, CancellationToken cancellationToken = default)
        => ReflectRecordAsync(section, UpsertSectionCoreAsync, cancellationToken);

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
                    (id, title, project_id, section_id, parent_task_id, when_kind, when_date,
                     when_is_evening, deadline_date, completed_at, deleted_at, priority, sort_order)
                VALUES
                    ($id, $title, $project, $section, $parent, $whenKind, $whenDate,
                     $evening, $deadline, $completed, $deleted, $priority, $sort)
                ON CONFLICT(id) DO UPDATE SET
                    title           = excluded.title,
                    project_id      = excluded.project_id,
                    section_id      = excluded.section_id,
                    parent_task_id  = excluded.parent_task_id,
                    when_kind       = excluded.when_kind,
                    when_date       = excluded.when_date,
                    when_is_evening = excluded.when_is_evening,
                    deadline_date   = excluded.deadline_date,
                    completed_at    = excluded.completed_at,
                    deleted_at      = excluded.deleted_at,
                    priority        = excluded.priority,
                    sort_order      = excluded.sort_order;
                """;
            Bind(cmd, "$id", task.Id.ToString());
            Bind(cmd, "$title", task.Title);
            Bind(cmd, "$project", task.ProjectId?.ToString());
            Bind(cmd, "$section", task.SectionId?.ToString());
            Bind(cmd, "$parent", task.ParentTaskId?.ToString());
            Bind(cmd, "$whenKind", task.When.Kind.ToString());
            Bind(cmd, "$whenDate", task.When.Kind == WhenKind.OnDate ? LocalDate(task.When.Date) : null);
            Bind(cmd, "$evening", task.When.IsEvening ? 1 : 0);
            Bind(cmd, "$deadline", LocalDate(task.Deadline));
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
            INSERT INTO projects (id, name, deadline_date, is_archived, completed_at, deleted_at, sort_order)
            VALUES ($id, $name, $deadline, $archived, $completed, $deleted, $sort)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name, deadline_date = excluded.deadline_date,
                is_archived = excluded.is_archived, completed_at = excluded.completed_at,
                deleted_at = excluded.deleted_at, sort_order = excluded.sort_order;
            """;
        Bind(cmd, "$id", project.Id.ToString());
        Bind(cmd, "$name", project.Name);
        Bind(cmd, "$deadline", LocalDate(project.Deadline));
        Bind(cmd, "$archived", project.IsArchived ? 1 : 0);
        Bind(cmd, "$completed", Instant(project.CompletedAt));
        Bind(cmd, "$deleted", Instant(project.DeletedAt));
        Bind(cmd, "$sort", project.SortOrder);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UpsertSectionCoreAsync(Section section, SqliteTransaction tx, CancellationToken cancellationToken)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO sections
                (id, project_id, name, deadline_date, is_archived, completed_at, deleted_at, sort_order)
            VALUES ($id, $project, $name, $deadline, $archived, $completed, $deleted, $sort)
            ON CONFLICT(id) DO UPDATE SET
                project_id = excluded.project_id, name = excluded.name,
                deadline_date = excluded.deadline_date, is_archived = excluded.is_archived,
                completed_at = excluded.completed_at, deleted_at = excluded.deleted_at,
                sort_order = excluded.sort_order;
            """;
        Bind(cmd, "$id", section.Id.ToString());
        Bind(cmd, "$project", section.ProjectId.ToString());
        Bind(cmd, "$name", section.Name);
        Bind(cmd, "$deadline", LocalDate(section.Deadline));
        Bind(cmd, "$archived", section.IsArchived ? 1 : 0);
        Bind(cmd, "$completed", Instant(section.CompletedAt));
        Bind(cmd, "$deleted", Instant(section.DeletedAt));
        Bind(cmd, "$sort", section.SortOrder);
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
            "SELECT id, name, deadline_date, sort_order FROM projects " +
            "WHERE deleted_at IS NULL AND is_archived = 0 AND completed_at IS NULL " +
            "ORDER BY sort_order, name;",
            _ => { },
            r => new ProjectListItem(Guid.Parse(r.GetString(0)), r.GetString(1), DateOrNull(r, 2), r.GetString(3)),
            cancellationToken);

    public Task<IReadOnlyList<SectionListItem>> GetSectionsByProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
        => QueryRecordsAsync(
            "SELECT id, project_id, name, deadline_date, sort_order FROM sections " +
            "WHERE project_id = $project AND deleted_at IS NULL AND is_archived = 0 " +
            "AND completed_at IS NULL ORDER BY sort_order, name;",
            cmd => Bind(cmd, "$project", projectId.ToString()),
            r => new SectionListItem(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), r.GetString(2), DateOrNull(r, 3), r.GetString(4)),
            cancellationToken);

    public Task<IReadOnlyList<LabelListItem>> GetLabelsAsync(CancellationToken cancellationToken = default)
        => QueryRecordsAsync(
            "SELECT id, name, color, sort_order FROM labels WHERE deleted_at IS NULL ORDER BY sort_order, name;",
            _ => { },
            r => new LabelListItem(Guid.Parse(r.GetString(0)), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3)),
            cancellationToken);

    internal Task<IReadOnlyList<Guid>> GetTaskIdsByProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
        => QueryIdsAsync(
            "SELECT id FROM tasks WHERE project_id = $id AND deleted_at IS NULL;",
            cmd => Bind(cmd, "$id", projectId.ToString()), cancellationToken);

    internal Task<IReadOnlyList<Guid>> GetTaskIdsBySectionAsync(Guid sectionId, CancellationToken cancellationToken = default)
        => QueryIdsAsync(
            "SELECT id FROM tasks WHERE section_id = $id AND deleted_at IS NULL;",
            cmd => Bind(cmd, "$id", sectionId.ToString()), cancellationToken);

    internal Task<IReadOnlyList<Guid>> GetTaskIdsByLabelAsync(Guid labelId, CancellationToken cancellationToken = default)
        => QueryIdsAsync(
            "SELECT t.id FROM tasks t INNER JOIN task_labels tl ON tl.task_id = t.id " +
            "WHERE tl.label_id = $id AND t.deleted_at IS NULL;",
            cmd => Bind(cmd, "$id", labelId.ToString()), cancellationToken);

    internal Task<IReadOnlyList<Guid>> GetSectionIdsByProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
        => QueryIdsAsync(
            "SELECT id FROM sections WHERE project_id = $id AND deleted_at IS NULL;",
            cmd => Bind(cmd, "$id", projectId.ToString()), cancellationToken);

    // ---- Classification axis -------------------------------------------------

    public Task<IReadOnlyList<TaskListItem>> GetInboxAsync(CancellationToken cancellationToken = default)
        => QueryAsync(
            $"SELECT {Columns} FROM tasks WHERE deleted_at IS NULL AND completed_at IS NULL " +
            "AND project_id IS NULL ORDER BY sort_order;",
            _ => { }, cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetByProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
        => QueryAsync(
            $"SELECT {Columns} FROM tasks WHERE deleted_at IS NULL AND completed_at IS NULL " +
            "AND project_id = $project ORDER BY sort_order;",
            cmd => Bind(cmd, "$project", projectId.ToString()), cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetBySectionAsync(Guid sectionId, CancellationToken cancellationToken = default)
        => QueryAsync(
            $"SELECT {Columns} FROM tasks WHERE deleted_at IS NULL AND completed_at IS NULL " +
            "AND section_id = $section ORDER BY sort_order;",
            cmd => Bind(cmd, "$section", sectionId.ToString()), cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetByLabelAsync(Guid labelId, CancellationToken cancellationToken = default)
        => QueryAsync(
            $"SELECT {Prefixed("t")} FROM tasks t " +
            "INNER JOIN task_labels tl ON tl.task_id = t.id " +
            "WHERE tl.label_id = $label AND t.deleted_at IS NULL AND t.completed_at IS NULL " +
            "ORDER BY t.sort_order;",
            cmd => Bind(cmd, "$label", labelId.ToString()), cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetSubtasksAsync(Guid parentTaskId, CancellationToken cancellationToken = default)
        => QueryAsync(
            $"SELECT {Columns} FROM tasks WHERE deleted_at IS NULL " +
            "AND parent_task_id = $parent ORDER BY sort_order;",
            cmd => Bind(cmd, "$parent", parentTaskId.ToString()), cancellationToken);

    // ---- Time axis (computed against the current day) ------------------------

    public Task<IReadOnlyList<TaskListItem>> GetTodayAsync(CancellationToken cancellationToken = default)
        => QueryAsync(
            $"SELECT {Columns} FROM tasks WHERE deleted_at IS NULL AND completed_at IS NULL AND ( " +
            "(when_kind = 'OnDate' AND when_date IS NOT NULL AND when_date <= $today) OR " +
            "(deadline_date IS NOT NULL AND deadline_date <= $today) ) " +
            "ORDER BY COALESCE(when_date, deadline_date), sort_order;",
            cmd => Bind(cmd, "$today", Today()), cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetThisEveningAsync(CancellationToken cancellationToken = default)
        => QueryAsync(
            $"SELECT {Columns} FROM tasks WHERE deleted_at IS NULL AND completed_at IS NULL " +
            "AND when_kind = 'OnDate' AND when_date IS NOT NULL AND when_date <= $today " +
            "AND when_is_evening = 1 ORDER BY when_date, sort_order;",
            cmd => Bind(cmd, "$today", Today()), cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetUpcomingAsync(CancellationToken cancellationToken = default)
        => QueryAsync(
            $"SELECT {Columns} FROM tasks WHERE deleted_at IS NULL AND completed_at IS NULL AND ( " +
            "(when_kind = 'OnDate' AND when_date > $today) OR " +
            "(deadline_date IS NOT NULL AND deadline_date > $today) ) " +
            "ORDER BY COALESCE(when_date, deadline_date), sort_order;",
            cmd => Bind(cmd, "$today", Today()), cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetAnytimeAsync(CancellationToken cancellationToken = default)
        => QueryAsync(
            $"SELECT {Columns} FROM tasks WHERE deleted_at IS NULL AND completed_at IS NULL " +
            "AND when_kind = 'Unscheduled' ORDER BY sort_order;",
            _ => { }, cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetSomedayAsync(CancellationToken cancellationToken = default)
        => QueryAsync(
            $"SELECT {Columns} FROM tasks WHERE deleted_at IS NULL AND completed_at IS NULL " +
            "AND when_kind = 'SomeDay' ORDER BY sort_order;",
            _ => { }, cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetLogbookAsync(CancellationToken cancellationToken = default)
        => QueryAsync(
            $"SELECT {Columns} FROM tasks WHERE deleted_at IS NULL AND completed_at IS NOT NULL " +
            "ORDER BY completed_at DESC;",
            _ => { }, cancellationToken);

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

    private static TaskListItem Map(SqliteDataReader r) => new(
        Id: Guid.Parse(r.GetString(0)),
        Title: r.GetString(1),
        ProjectId: GuidOrNull(r, 2),
        SectionId: GuidOrNull(r, 3),
        ParentTaskId: GuidOrNull(r, 4),
        WhenKind: Enum.Parse<WhenKind>(r.GetString(5)),
        WhenDate: DateOrNull(r, 6),
        IsEvening: r.GetInt64(7) != 0,
        DeadlineDate: DateOrNull(r, 8),
        IsCompleted: !r.IsDBNull(9),
        Priority: (Priority)r.GetInt64(10),
        SortOrder: r.GetString(11));

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
