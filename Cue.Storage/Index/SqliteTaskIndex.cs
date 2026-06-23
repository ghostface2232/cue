using System.Text.Json;
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
    // The contract for the derived `checklist` JSON column. Shared by the serialize (UpsertCore) and
    // deserialize (ChecklistFrom) sides so the index's PascalCase shape stays in lockstep — changing one
    // side without the other (e.g. switching to camelCase) would silently break the round-trip.
    private static readonly JsonSerializerOptions ChecklistJson = new();

    // Base task columns, aliased to the tasks table (t) so list queries can left-join the group and
    // correlate the tag set without column ambiguity.
    private const string TaskColumns =
        "t.id, t.title, t.group_id, t.checklist, t.when_kind, t.when_date, t.when_time, " +
        "t.completed_at, t.priority, t.sort_order";

    // The list projection: base columns + the row's group (name and icon, null when unfiled or the
    // group is deleted) + a packed tag set so a row can show its group and tags without a per-row
    // follow-up query. Tags pack as "name<US>color" each, joined by <RS> — char(31)=US, char(30)=RS,
    // control codes that never appear in user-entered names/colors.
    private static readonly string SelectRows =
        "SELECT " + TaskColumns +
        ", g.name AS group_name, g.icon AS group_icon" +
        ", (SELECT group_concat(tg.name || char(31) || COALESCE(tg.color, ''), char(30)) " +
        "FROM task_tags rt JOIN tags tg ON tg.id = rt.tag_id " +
        "WHERE rt.task_id = t.id AND tg.deleted_at IS NULL) AS tags" +
        " FROM tasks t LEFT JOIN task_groups g ON g.id = t.group_id AND g.deleted_at IS NULL ";

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
    private const long SchemaVersion = 6;

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
                    "DROP TABLE IF EXISTS task_tags; DROP TABLE IF EXISTS tasks; " +
                    "DROP TABLE IF EXISTS task_groups; DROP TABLE IF EXISTS sections; DROP TABLE IF EXISTS tags; " +
                    "DROP TABLE IF EXISTS task_labels; DROP TABLE IF EXISTS projects; DROP TABLE IF EXISTS labels;";
                // (sections and the old projects/labels/task_labels tables are dropped only to clear any
                // stale table from before the model rename.)
                drop.ExecuteNonQuery();
            }
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS tasks (
                id              TEXT PRIMARY KEY,
                title           TEXT NOT NULL,
                group_id        TEXT NULL,
                checklist       TEXT NULL,
                when_kind       TEXT NOT NULL,
                when_date       TEXT NULL,
                when_time       TEXT NULL,
                completed_at    TEXT NULL,
                deleted_at      TEXT NULL,
                priority        INTEGER NOT NULL DEFAULT 0,
                sort_order      TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS task_tags (
                task_id TEXT NOT NULL,
                tag_id  TEXT NOT NULL,
                PRIMARY KEY (task_id, tag_id)
            );
            CREATE TABLE IF NOT EXISTS task_groups (
                id            TEXT PRIMARY KEY,
                name          TEXT NOT NULL,
                icon          TEXT NULL,
                deleted_at    TEXT NULL,
                sort_order    TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS tags (
                id         TEXT PRIMARY KEY,
                name       TEXT NOT NULL,
                color      TEXT NULL,
                deleted_at TEXT NULL,
                sort_order TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS ix_tasks_group  ON tasks(group_id);
            CREATE INDEX IF NOT EXISTS ix_tasks_when   ON tasks(when_kind, when_date);
            CREATE INDEX IF NOT EXISTS ix_tasks_active ON tasks(deleted_at, completed_at);
            CREATE INDEX IF NOT EXISTS ix_task_tags_tag ON task_tags(tag_id);
            CREATE INDEX IF NOT EXISTS ix_task_groups_active ON task_groups(deleted_at);
            CREATE INDEX IF NOT EXISTS ix_tags_active ON tags(deleted_at);
            PRAGMA user_version = 6;
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
        IEnumerable<TaskGroup> taskGroups,
        IEnumerable<Tag> tags,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(taskGroups);
        ArgumentNullException.ThrowIfNull(tags);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await using (var clear = _connection.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText =
                    "DELETE FROM task_tags; DELETE FROM tasks; DELETE FROM task_groups; DELETE FROM tags;";
                await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var task in tasks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await UpsertCoreAsync(task, tx, cancellationToken).ConfigureAwait(false);
            }

            foreach (var taskGroup in taskGroups)
                await UpsertTaskGroupCoreAsync(taskGroup, tx, cancellationToken).ConfigureAwait(false);
            foreach (var tag in tags)
                await UpsertTagCoreAsync(tag, tx, cancellationToken).ConfigureAwait(false);

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

    public Task ReflectAsync(TaskGroup taskGroup, CancellationToken cancellationToken = default)
        => ReflectRecordAsync(taskGroup, UpsertTaskGroupCoreAsync, cancellationToken);

    public Task ReflectAsync(Tag tag, CancellationToken cancellationToken = default)
        => ReflectRecordAsync(tag, UpsertTagCoreAsync, cancellationToken);

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
                    (id, title, group_id, checklist, when_kind, when_date, when_time,
                     completed_at, deleted_at, priority, sort_order)
                VALUES
                    ($id, $title, $group, $checklist, $whenKind, $whenDate, $whenTime,
                     $completed, $deleted, $priority, $sort)
                ON CONFLICT(id) DO UPDATE SET
                    title           = excluded.title,
                    group_id        = excluded.group_id,
                    checklist       = excluded.checklist,
                    when_kind       = excluded.when_kind,
                    when_date       = excluded.when_date,
                    when_time       = excluded.when_time,
                    completed_at    = excluded.completed_at,
                    deleted_at      = excluded.deleted_at,
                    priority        = excluded.priority,
                    sort_order      = excluded.sort_order;
                """;
            Bind(cmd, "$id", task.Id.ToString());
            Bind(cmd, "$title", task.Title);
            Bind(cmd, "$group", task.TaskGroupId?.ToString());
            // The embedded checklist is mirrored as a JSON blob so the list can render its nested
            // rows without a per-row follow-up read; it stays fully rebuildable from the file.
            Bind(cmd, "$checklist", JsonSerializer.Serialize(task.Checklist, ChecklistJson));
            Bind(cmd, "$whenKind", task.When.Kind.ToString());
            Bind(cmd, "$whenDate", task.When.Kind == WhenKind.OnDate ? LocalDate(task.When.Date) : null);
            Bind(cmd, "$whenTime", task.When.Kind == WhenKind.OnDate ? LocalTime(task.When.Date) : null);
            Bind(cmd, "$completed", Instant(task.CompletedAt));
            Bind(cmd, "$deleted", Instant(task.DeletedAt));
            Bind(cmd, "$priority", (int)task.Priority);
            Bind(cmd, "$sort", task.SortOrder);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Replace the tag set for this task wholesale — cheap and keeps it exactly mirroring the file.
        await using (var del = _connection.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM task_tags WHERE task_id = $id;";
            Bind(del, "$id", task.Id.ToString());
            await del.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var tagId in task.TagIds.Distinct())
        {
            await using var ins = _connection.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR IGNORE INTO task_tags (task_id, tag_id) VALUES ($task, $tag);";
            Bind(ins, "$task", task.Id.ToString());
            Bind(ins, "$tag", tagId.ToString());
            await ins.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task UpsertTaskGroupCoreAsync(TaskGroup taskGroup, SqliteTransaction tx, CancellationToken cancellationToken)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO task_groups (id, name, icon, deleted_at, sort_order)
            VALUES ($id, $name, $icon, $deleted, $sort)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name, icon = excluded.icon,
                deleted_at = excluded.deleted_at, sort_order = excluded.sort_order;
            """;
        Bind(cmd, "$id", taskGroup.Id.ToString());
        Bind(cmd, "$name", taskGroup.Name);
        Bind(cmd, "$icon", taskGroup.Icon);
        Bind(cmd, "$deleted", Instant(taskGroup.DeletedAt));
        Bind(cmd, "$sort", taskGroup.SortOrder);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UpsertTagCoreAsync(Tag tag, SqliteTransaction tx, CancellationToken cancellationToken)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO tags (id, name, color, deleted_at, sort_order)
            VALUES ($id, $name, $color, $deleted, $sort)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name, color = excluded.color,
                deleted_at = excluded.deleted_at, sort_order = excluded.sort_order;
            """;
        Bind(cmd, "$id", tag.Id.ToString());
        Bind(cmd, "$name", tag.Name);
        Bind(cmd, "$color", tag.Color);
        Bind(cmd, "$deleted", Instant(tag.DeletedAt));
        Bind(cmd, "$sort", tag.SortOrder);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---- Live navigation records --------------------------------------------

    public Task<IReadOnlyList<TaskGroupListItem>> GetTaskGroupsAsync(CancellationToken cancellationToken = default)
        => QueryRecordsAsync(
            "SELECT id, name, icon, sort_order FROM task_groups " +
            "WHERE deleted_at IS NULL ORDER BY sort_order, name;",
            _ => { },
            r => new TaskGroupListItem(Guid.Parse(r.GetString(0)), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3)),
            cancellationToken);

    public Task<IReadOnlyList<TagListItem>> GetTagsAsync(CancellationToken cancellationToken = default)
        => QueryRecordsAsync(
            "SELECT id, name, color, sort_order FROM tags WHERE deleted_at IS NULL ORDER BY sort_order, name;",
            _ => { },
            r => new TagListItem(Guid.Parse(r.GetString(0)), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3)),
            cancellationToken);

    public Task<IReadOnlyDictionary<Guid, int>> GetOpenTaskCountsByTaskGroupAsync(CancellationToken cancellationToken = default)
        => QueryCountsAsync(
            "SELECT group_id, COUNT(*) FROM tasks " +
            "WHERE deleted_at IS NULL AND completed_at IS NULL AND group_id IS NOT NULL " +
            "GROUP BY group_id;",
            cancellationToken);

    public Task<IReadOnlyDictionary<Guid, int>> GetOpenTaskCountsByTagAsync(CancellationToken cancellationToken = default)
        => QueryCountsAsync(
            "SELECT tt.tag_id, COUNT(*) FROM task_tags tt " +
            "INNER JOIN tasks t ON t.id = tt.task_id " +
            "WHERE t.deleted_at IS NULL AND t.completed_at IS NULL " +
            "GROUP BY tt.tag_id;",
            cancellationToken);

    public Task<int> GetOpenTaskCountWithoutTaskGroupAsync(CancellationToken cancellationToken = default)
        => QueryScalarAsync(
            "SELECT COUNT(*) FROM tasks " +
            "WHERE deleted_at IS NULL AND completed_at IS NULL AND group_id IS NULL;",
            cancellationToken);

    public Task<int> GetOpenTaskCountWithoutTagAsync(CancellationToken cancellationToken = default)
        => QueryScalarAsync(
            "SELECT COUNT(*) FROM tasks " +
            "WHERE deleted_at IS NULL AND completed_at IS NULL " +
            "AND id NOT IN (SELECT task_id FROM task_tags);",
            cancellationToken);

    internal Task<IReadOnlyList<Guid>> GetTaskIdsByTaskGroupAsync(Guid taskGroupId, CancellationToken cancellationToken = default)
        => QueryIdsAsync(
            "SELECT id FROM tasks WHERE group_id = $id AND deleted_at IS NULL;",
            cmd => Bind(cmd, "$id", taskGroupId.ToString()), cancellationToken);

    internal Task<IReadOnlyList<Guid>> GetTaskIdsByTagAsync(Guid tagId, CancellationToken cancellationToken = default)
        => QueryIdsAsync(
            "SELECT t.id FROM tasks t INNER JOIN task_tags tt ON tt.task_id = t.id " +
            "WHERE tt.tag_id = $id AND t.deleted_at IS NULL;",
            cmd => Bind(cmd, "$id", tagId.ToString()), cancellationToken);

    // ---- Classification axis -------------------------------------------------

    // Active lists keep completed tasks (shown dimmed) rather than dropping them on the next load, so
    // finishing an item doesn't make it vanish; they sink below the open rows via the completed-last
    // ordering. Open-task counts (badges) still exclude completed — see GetOpenTaskCounts*.
    // The home "모든 할 일" (AllTasks) list spans every group — no group filter — so a task in a group
    // still surfaces here. Each row carries its embedded checklist for the nested rows under it.
    public Task<IReadOnlyList<TaskListItem>> GetAllActiveAsync(CancellationToken cancellationToken = default)
        => QueryAsync(
            SelectRows + "WHERE t.deleted_at IS NULL " +
            "ORDER BY t.completed_at IS NOT NULL, t.sort_order;",
            _ => { }, cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetByTaskGroupAsync(Guid taskGroupId, CancellationToken cancellationToken = default)
        => QueryAsync(
            SelectRows + "WHERE t.deleted_at IS NULL " +
            "AND t.group_id = $group ORDER BY t.completed_at IS NOT NULL, t.sort_order;",
            cmd => Bind(cmd, "$group", taskGroupId.ToString()), cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetByTagAsync(Guid tagId, CancellationToken cancellationToken = default)
        => QueryAsync(
            SelectRows +
            "INNER JOIN task_tags tt ON tt.task_id = t.id " +
            "WHERE tt.tag_id = $tag AND t.deleted_at IS NULL " +
            "ORDER BY t.completed_at IS NOT NULL, t.sort_order;",
            cmd => Bind(cmd, "$tag", tagId.ToString()), cancellationToken);

    // The 그룹 없음 / 태그 없음 lists re-create the quick-capture inbox: the home list spans every group,
    // so unfiled captures get lost among already-sorted work — these narrow it to just what still needs
    // filing. group_id IS NULL is the unfiled-group test; the NOT IN sub-select is the no-tag test.
    public Task<IReadOnlyList<TaskListItem>> GetWithoutTaskGroupAsync(CancellationToken cancellationToken = default)
        => QueryAsync(
            SelectRows + "WHERE t.deleted_at IS NULL AND t.group_id IS NULL " +
            "ORDER BY t.completed_at IS NOT NULL, t.sort_order;",
            _ => { }, cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetWithoutTagAsync(CancellationToken cancellationToken = default)
        => QueryAsync(
            SelectRows + "WHERE t.deleted_at IS NULL " +
            "AND t.id NOT IN (SELECT task_id FROM task_tags) " +
            "ORDER BY t.completed_at IS NOT NULL, t.sort_order;",
            _ => { }, cancellationToken);

    // ---- Time axis (computed against the current day) ------------------------

    public Task<IReadOnlyList<TaskListItem>> GetTodayAsync(CancellationToken cancellationToken = default)
        => QueryAsync(
            SelectRows + "WHERE t.deleted_at IS NULL " +
            "AND t.when_kind = 'OnDate' AND t.when_date IS NOT NULL AND t.when_date <= $today " +
            "ORDER BY t.completed_at IS NOT NULL, t.when_date, t.sort_order;",
            cmd => Bind(cmd, "$today", Today()), cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetUpcomingAsync(CancellationToken cancellationToken = default)
        => QueryAsync(
            SelectRows + "WHERE t.deleted_at IS NULL " +
            "AND t.when_kind = 'OnDate' AND t.when_date > $today " +
            "ORDER BY t.completed_at IS NOT NULL, t.when_date, t.sort_order;",
            cmd => Bind(cmd, "$today", Today()), cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetAnytimeAsync(CancellationToken cancellationToken = default)
        => QueryAsync(
            SelectRows + "WHERE t.deleted_at IS NULL " +
            "AND t.when_kind = 'Unscheduled' ORDER BY t.completed_at IS NOT NULL, t.sort_order;",
            _ => { }, cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetLogbookAsync(CancellationToken cancellationToken = default)
        => QueryAsync(
            SelectRows + "WHERE t.deleted_at IS NULL AND t.completed_at IS NOT NULL " +
            "ORDER BY t.completed_at DESC;",
            _ => { }, cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetByPriorityAsync(CancellationToken cancellationToken = default)
        => QueryAsync(
            SelectRows + "WHERE t.deleted_at IS NULL AND t.priority <> 0 " +
            "ORDER BY t.priority, t.completed_at IS NOT NULL, t.sort_order;",
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

    private async Task<int> QueryScalarAsync(string sql, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result is null or DBNull ? 0 : Convert.ToInt32(result);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static TaskListItem Map(SqliteDataReader r) => new(
        Id: Guid.Parse(r.GetString(0)),
        Title: r.GetString(1),
        TaskGroupId: GuidOrNull(r, 2),
        WhenKind: Enum.Parse<WhenKind>(r.GetString(4)),
        WhenDate: DateOrNull(r, 5),
        WhenTime: TimeOrNull(r, 6),
        IsCompleted: !r.IsDBNull(7),
        Priority: (Priority)r.GetInt64(8),
        SortOrder: r.GetString(9),
        TaskGroupName: r.IsDBNull(10) ? null : r.GetString(10),
        TaskGroupIcon: r.IsDBNull(11) ? null : r.GetString(11),
        Tags: TagsFrom(r, 12),
        Checklist: ChecklistFrom(r, 3));

    // Deserializes the checklist JSON blob mirrored in the tasks.checklist column (column 3, where
    // the former parent_task_id lived — kept at that ordinal so the rest of Map is unchanged). Only
    // the fields a nested list row needs are projected; the memo is left to the detail view.
    private static IReadOnlyList<TaskListChecklistItem> ChecklistFrom(SqliteDataReader r, int i)
    {
        if (r.IsDBNull(i))
            return Array.Empty<TaskListChecklistItem>();
        var json = r.GetString(i);
        if (json.Length == 0)
            return Array.Empty<TaskListChecklistItem>();
        return JsonSerializer.Deserialize<List<TaskListChecklistItem>>(json, ChecklistJson)
            ?? (IReadOnlyList<TaskListChecklistItem>)Array.Empty<TaskListChecklistItem>();
    }

    // Unpacks the group_concat tag column produced by SelectRows: records split on <RS> (char 30),
    // each "name<US>color" split on <US> (char 31); an empty color field means the tag uses no color.
    private static IReadOnlyList<TaskListTag> TagsFrom(SqliteDataReader r, int i)
    {
        if (r.IsDBNull(i))
            return Array.Empty<TaskListTag>();
        var packed = r.GetString(i);
        if (packed.Length == 0)
            return Array.Empty<TaskListTag>();

        var tags = new List<TaskListTag>();
        foreach (var record in packed.Split((char)30))
        {
            if (record.Length == 0)
                continue;
            var parts = record.Split((char)31);
            var color = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null;
            tags.Add(new TaskListTag(parts[0], color));
        }
        return tags;
    }

    /// <summary>The current calendar day in the configured zone — computed, never stored.</summary>
    private string Today()
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), _zone).DateTime)
            .ToString("yyyy-MM-dd");

    private static string? LocalDate(ZonedDateTime? zoned)
        => zoned is null
            ? null
            : DateOnly.FromDateTime(zoned.Value.ToLocal().DateTime).ToString("yyyy-MM-dd");

    /// <summary>The pinned wall-clock time-of-day, in the task's own zone, as <c>HH:mm</c> — so a list
    /// row can show the time. An all-day item is pinned to 23:59 (the end-of-day marker the UI reads).</summary>
    private static string? LocalTime(ZonedDateTime? zoned)
        => zoned is null
            ? null
            : TimeOnly.FromDateTime(zoned.Value.ToLocal().DateTime).ToString("HH\\:mm");

    private static string? Instant(DateTimeOffset? value)
        => value?.ToUniversalTime().ToString("O");

    private static Guid? GuidOrNull(SqliteDataReader r, int i)
        => r.IsDBNull(i) ? null : Guid.Parse(r.GetString(i));

    private static DateOnly? DateOrNull(SqliteDataReader r, int i)
        => r.IsDBNull(i) ? null : DateOnly.ParseExact(r.GetString(i), "yyyy-MM-dd");

    private static TimeOnly? TimeOrNull(SqliteDataReader r, int i)
        => r.IsDBNull(i) ? null : TimeOnly.ParseExact(r.GetString(i), "HH\\:mm");

    private static void Bind(SqliteCommand cmd, string name, object? value)
        => cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

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
