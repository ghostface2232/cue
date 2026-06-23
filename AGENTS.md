# Cue
 
Cue is a Windows-native to-do app. Its basic interface takes its references from Things 3 and Todoist, while the bar for interface completion and microinteraction quality is the files-community Files app. Korean is a first-class input language. The name reflects the core mechanic: you type a single line, and it cues the next action, parsing the due date, time, and priority out of plain text.
 
This file is the source of project context for any AI coding agent. Read it before making changes, and treat the Invariants section as non-negotiable.
 
## Project status
 
Foundation phase. The app is single-device and local-first right now. Multi-device sync, reminders, and full visual polish are deliberately out of scope for now, but the architecture is built so they can be added later without a rewrite. Build incrementally and verify each step. Do not try to implement everything at once.
 
## Tech stack
 
- WinUI (Windows App SDK 2.0). This is a native UI framework, not a webview wrapper. Use Fluent Design, Mica/Acrylic materials, and native composition animations.
- C# on .NET 10 (LTS, supported through 2028-11-10). Target framework moniker is of the form `net10.0-windows10.0.26100.0`.
- MVVM via `CommunityToolkit.Mvvm` (use the source generators: `[ObservableProperty]`, `[RelayCommand]`).
- Persistence source of truth: one JSON file per record, serialized with `System.Text.Json`.
- Query index: SQLite via `Microsoft.Data.Sqlite`. Do not use EF Core; the index is a lightweight derived cache.
- Natural-language date parsing: `Microsoft.Recognizers.Text.DateTime` with `Culture.Korean`.
- Recurrence: RFC 5545 RRULE evaluation via `Ical.Net`, used as a calculator only and confined to the store/logic layer (never referenced from the domain or view models, per invariant 9).
- Typography: Pretendard JP is bundled (`Assets/Fonts/*.otf`) and is the app-wide typeface; weight hierarchy switches font family (the static OTFs ship Regular and SemiBold as separate families).
- Packaging: unpackaged or external-location packaged. Rationale: the app must read and write user-chosen cloud folders (OneDrive, Google Drive) in a later phase, so avoid the MSIX virtual filesystem constraints.
## Tooling and commands
 
- Dev loop: `dotnet run` or `winapp run`. Primary editor is VS Code with the WinApp extension. Visual Studio 2026 is optional, not required.
- winapp CLI: `winapp init` (adds package identity and manifest), `winapp run` (build-and-launch, the command-line equivalent of F5), `winapp ui` (inspect and drive the running UI for verification), `winapp pack` (package for distribution).
- For current WinUI APIs, query the Microsoft Learn MCP server rather than relying on training data. WinUI APIs shift between releases, and note the current naming: WinUI 3 is now referred to simply as WinUI, and the old WinUI 2 is WinUI for UWP.
- A Claude Code WinUI plugin is available and recommended: `/plugin install winui@awesome-copilot`.
## Architecture
 
One rule governs everything: files are the source of truth, and SQLite is a disposable, rebuildable index. Never make SQLite the source of truth, and never treat a single monolithic database file as the synced unit. A future cloud-folder sync depends on the truth being granular per-record files.
 
Layers, with dependencies pointing top-down only:
 
```
View (XAML)
   -> ViewModel (MVVM)
        -> Store (ITaskStore, the single write path)
             -> file folder  = source of truth ({guid}.json per record)
             -> SQLite        = derived query index
   -> Parser service (IDateParser), standalone
   -> [future] Sync module that watches the folder
```
 
Write flow: an input line goes through the parser, becomes a record, is saved through `ITaskStore.Save`, which writes `{guid}.json` and updates the SQLite index, after which the ViewModel refreshes.
 
Read flow: every query (Today, Upcoming, by project, by label, completed/incomplete filters, title and notes search) hits the SQLite index. Never scan the file folder directly for queries.
 
## Data model (sync-ready by design)
 
Domain types are pure C# with zero knowledge of persistence: `TaskItem`, `Project`, `Label`. The hierarchy is one level deep — a `Project` contains `TaskItem`s — and `Label` is cross-cutting. A task's container is a single nullable `ProjectId`; null means the task has no group. The home view (`모든 할 일` / All) spans every group — it lists all active tasks regardless of `ProjectId`, not an unclassified-only inbox. A `Project` is a pure group: it carries no date and no completion state, and an unused group is simply soft-deleted. An Area level above Project is intentionally deferred for the foundation phase — it can be added later as an additive record type plus an optional `Project.AreaId`, with no rewrite.
 
Every record carries these fields from day one:

- `Id` (GUID)
- `CreatedAt`
- `UpdatedAt`
- `DeletedAt` (tombstone for soft delete; null means alive)
- `SchemaVersion` (int)

Records compare by **identity**: two instances are equal when they are the same concrete type with the same `Id` (exact `GetType()` match), and the hash is over the immutable, init-only `Id` only. Because equality is Id-only it cannot tell whether content changed — a local record and an incoming synced one with the same Id are "equal" even if every other field differs. Index refresh and sync reconciliation must therefore compare `UpdatedAt` (or specific fields), never `old.Equals(new)`.

Timestamps and user dates are stored differently:

- System timestamps — `CreatedAt`, `UpdatedAt`, `CompletedAt` — are UTC instants only; a timezone carries no meaning for them.
- The one user-chosen date — a task's `When` date — and a recurrence anchor store UTC plus the original timezone, rendered in local time only at display. This protects DST correctness now and feeds last-write-wins reconciliation later.

Per-type specifics:

- **Completion is a nullable `CompletedAt`, not a boolean.** `IsCompleted` is a derived read-only property (true once `CompletedAt` is set), on `TaskItem`. A `Project` has no completion state.
- **One date field: `When`.** A task has a single date — there is no separate deadline. `When` is a `ScheduledWhen` with exactly two stored states: **Unscheduled** (no date) or **OnDate** (a concrete zoned date). It drives Today/Upcoming/Timeline. "Today" is not a stored state — the caller stamps the current day in the user's timezone as an OnDate. Whether something is today / upcoming / overdue is view logic comparing the stored date to the current day, never a stored flag. A dateless task is Unscheduled and lives in the "언젠가" (Anytime) bucket.
- **Recurrence** is an RFC 5545 RRULE string plus a zoned anchor; the domain only holds the rule and anchor. Next-occurrence and complete-on-advance live in the store/logic layer (`Cue.Storage.Recurrence`): `RecurrenceCalculator` evaluates the RRULE with `Ical.Net` in the anchor's own time zone (DST-safe) and returns a `ZonedDateTime`; `RecurringTaskService` implements **method B** — on completing a repeating task it writes a completed one-off copy to the Logbook and advances the original one cycle (When moves forward, the task stays open). An unparseable/exhausted rule degrades to a plain one-off completion.
- **Project** carries an optional sidebar icon (a Fluent glyph string) and accent color; **Label** carries an accent color. These surface in the left nav (and the detail label list) and are edited from the nav context menu.
- **Priority** is an explicit enum: `None`, `P1`, `P2`, `P3`, `P4` (P1 most urgent), default `None`.
- **Sub-tasks** are full `TaskItem` records linked by `ParentTaskId`, not a lightweight checklist, so each carries its own dates, labels, priority, and recurrence.
- **`SortOrder`** on every manually-ordered entity (tasks, projects, labels) is a LexoRank-style fractional string rank, so a new rank always fits between two neighbors without renumbering. The store/rank service assigns it.

Domain types are pure data holders with no clock or persistence access: they never self-stamp `UpdatedAt` — that is the store's job on save.
 
## Storage layer
 
- `ITaskStore` exposes GetAll, Get(id), Save(record), Delete(id). Every data mutation in the app goes through this single interface. This is what makes sync additive later.
- Manual ordering lives in the rank service, not the domain. `FractionalRank` (in `Cue.Storage.Ranking`) is the pure LexoRank-style generator: keys order ordinally (matching SQLite's `BINARY` collation), and `Between(before, after)` always yields a key strictly between two neighbors. `IReorderService`/`ReorderService` assign those keys: `AppendRank` places a new record at the end of a list, and `MoveAsync` re-ranks a moved record between its new neighbors and saves *only that record* through `ITaskStore` (so the store stamps `UpdatedAt` and reflects the index — invariants 4 and 8); neighbors are never rewritten. The one exception is a rare rebalance, confined to the service, that evenly re-ranks a whole list when its keys grow past a length limit or a list is found unranked. Records expose their key via the `ISortable` interface so the service stays generic.
- Folder layout uses type-based subfolders: `tasks/`, `projects/`, `labels/`, and `meta/` for settings and schema version. Each file is named `{guid}.json`, so the filename equals the record Id and records are never renamed (which minimizes the sync conflict surface).
- The root folder path is a configuration value, never hardcoded. The v1 default is the local app data path; in a later phase it becomes a cloud-synced folder.
- Save always updates `UpdatedAt`. Delete is a soft delete: set `DeletedAt`, then re-save the record. Never hard-delete a file.
- Use atomic writes: write to a temp file, then swap, so a crash mid-write cannot leave a half-written file. All file IO is async. The temp name carries a unique token (not a fixed `{id}.tmp`) so concurrent saves of one record don't collide, and the writer deletes its own temp on failure.
- Reads are corruption-isolated: `GetAll` skips a file that fails to deserialize (a half-written or partially-synced file) and returns the rest rather than throwing, so one bad file can't break a listing or the index rebuild that runs on it.
## Index layer
 
- `Microsoft.Data.Sqlite`. Rebuild the index from the file folder on startup, then update it incrementally on each Save and Delete.
- The index must hold nothing that cannot be reconstructed from the files. Deleting the index file and restarting must fully rebuild it from the folder. Treat passing this test as a hard requirement.
- The index carries a schema version (`PRAGMA user_version`). On a version mismatch `EnsureSchema` drops and recreates the (derived) tables, and the startup rebuild repopulates them from the files — so changing the index shape (adding a column, etc.) just needs a version bump, never a hand-written migration.
- Tombstoned records (non-null `DeletedAt`) are excluded from default queries.
- Implementation: `SqliteTaskIndex` (raw SQL) holds the query surface, and `IndexedTaskStore` composes a `FileTaskStore` with it so there is exactly one write path — Save/Delete write the file first, then reflect that one record into the index. `OpenAsync` wires both and rebuilds at startup. By-id and GetAll reads come from the files; all *list* reads come from the index (`ITaskIndex`), which returns the lightweight `TaskListItem` projection and never scans the folder.
- The index database path is configured separately from the data root (`FileTaskStoreOptions.IndexPath`, defaulting to `{RootPath}/index.db`). Because the index is a per-device disposable cache, it must stay on local storage and never be synced: when the data `RootPath` later points at a cloud folder, set `IndexPath` to a local path so the database is never swept into sync (which would spawn per-device conflict copies).
- Queries split on two axes. Classification: by project, by label, the home `모든 할 일` (All) view that spans every group (all active tasks, not project-less only), and the unfiled `그룹 없음` / `태그 없음` views (tasks with no `ProjectId` / no labels) that re-gather quick-capture leftovers the All view would otherwise scatter among already-sorted work. Active list queries return non-deleted rows and intentionally keep completed rows visible/dimmed; open-only counts are used for navigation badges (the unfiled views included). Time, all computed from the single `When` date: Today (a When day today-or-earlier — overdue rolls forward), Upcoming (a future When day), Anytime / "언젠가" (Unscheduled), Logbook (completed). The Timeline view is also When-driven: a task with an OnDate When appears as a single point on that date.
- Time views are never stored. The current day is computed at query time from an injected `TimeProvider` and time zone and compared against each task's pinned calendar date, which is what makes overdue items carry into Today and a task move Upcoming → Today as the clock advances.
- Project and Label navigation records are indexed alongside tasks and rebuilt from their per-record files at startup. Active navigation queries exclude tombstones. No left-nav list scans the folders.
- Container deletion policy: deleting a Project asks the user which disposition to apply to its tasks — `그룹만 제거` (the least-destructive default: clear `ProjectId`, so the tasks stay in the home `모든 할 일` view) or `할 일까지 삭제` (cascade the soft-delete to every task in the group, subtrees included). Both run through the crash-safe deletion saga (the disposition is a `CascadeTasks` flag on the journal entry). The mode-taking path is `IContainerDeletionStore.DeleteProjectAsync`; the generic `ITaskStore.DeleteAsync<Project>` keeps the reparent default. Deleting a Label only removes that Label id from tasks and soft-deletes the Label record. Deleting a TaskItem cascades the soft-delete to its whole subtask subtree (so no orphaned children remain).
- Deferred: (a) Cross-zone "today": a task's `when_date` is its calendar day in its own zone, while "today" is computed in the index's zone, so a task pinned in another zone (travel) can be a day off at the boundary — accepted foundation-phase edge; the UTC+zone data needed to refine it later is already retained. (b) File→index writes are not one transaction; a reflect failure leaves the file ahead until the next startup rebuild heals it (the reflect runs in its own SQLite transaction, so the index is never half-written). A persisted dirty-marker, not a retry, is the right future mitigation.
## Parser
 
- `IDateParser` (in `Cue.Parsing`) takes one line of user text plus a reference `now` and the user's time zone, and returns a `ParsedQuickAdd`: the cleaned `Title` (recognized phrases removed) plus `When` (`ScheduledWhen`) and optional `Recurrence`. Pure service — no UI/storage dependency, reads no clock (caller supplies `now`), never throws.
- **Library reality:** at the pinned version, `Microsoft.Recognizers.Text.DateTime` has *no* working Korean DateTime model — `Culture.Korean` returns zero matches for everything, down to bare `내일`/`3월 15일` (the English model works fine). So the package can't be the core recognizer for Korean today.
- Design: `KoreanDateParser` is an ordered pipeline of `IQuickAddRule`s (the boost seam, modeled on chrono's parser list). The built-in rules do the Korean recognition — SomedayRule, Recurrence, RelativeHour, DeadlineRule (까지/마감/안에), WhenDate (relative/weekday/absolute + time), TimeOfDay — each consuming the span it recognizes so the leftover becomes the title. Note both `SomedayRule` and `DeadlineRule` only recognize-and-strip their phrases now; with a single date there is no separate Someday or Deadline result — SomedayRule resolves to `Unscheduled` and DeadlineRule (and "N일 안에") resolves to an OnDate `When`. Custom rules passed to the constructor run *first*, so they override/extend without touching the defaults. The Recognizers library is still wired in (per spec, with `Culture.Korean`) as a final fallback stage; it contributes nothing for Korean now but lights up automatically if the library gains support.
- Misrecognition guards: rules use Hangul word-boundary lookarounds (`(?<![가-힣])` / `(?![가-힣])`), so date-like fragments glued into nouns stay in the title — `내일로`, `오늘의집`, `3월의`, `일요일의` don't match, and `24시` is rejected as an impossible clock time.
- If nothing is recognized (or input is ambiguous), the whole line becomes the title with empty scheduling. Representative expressions across all PARSING.md categories are covered by unit tests.
- Abstract day-part words resolve to a representative clock hour (새벽 06:00, 아침 08:00, 오전 10:00, 점심 12:00, 오후 15:00, 저녁 18:00, 밤 21:00). The evening flag/"This Evening" concept has been removed entirely (domain, storage, UI). Bare hours with no meridiem are disambiguated to PM (1–6 always; 7–11 only once today's morning reading has passed).
- Quick-add placement (`QuickAddContext`): a typed task with no date/time stays Unscheduled and lands in "언젠가" (Anytime), except on the Today list (pins today). The parser's "언젠가" markers (언젠가/나중에/다음에/담에/시간 나면/여유되면/기회 되면, space-insensitive) are recognized and stripped from the title and resolve to Unscheduled regardless of list.
- Known boost-layer gaps (library can't help; add rules as needed): some Korean-specific forms (아침저녁 twice-daily, 말일/월급날 semantic dates) aren't mapped yet. PARSING.md is the regression corpus — add a row, add a rule, re-test.
## UI
 
- MVVM with `CommunityToolkit.Mvvm` source generators (`[ObservableProperty]` on partial properties — the WinUI-recommended form, not fields).
- Current state — the data loop is proven end to end and a first visual craft pass has been applied (see the design-token notes below and `DESIGN_REF.md`): one reusable `TaskListPage` serves Inbox, all time views, Project, and Label modes from `ITaskIndex`. The 중요도 (priority) view is the only grouped list (P1–P4 buckets); every other list is flat. The left `NavigationView` shows live indexed Project and Label lists and offers basic create/rename/soft-delete actions. Selecting a task opens a detail panel that edits title, Markdown notes, priority, Project, the single zoned `When` date (with optional time / 종일), and multiple Labels; saving goes through `ITaskStore` and re-queries the active index view immediately. The same panel lists direct subtasks from the index and can add, open, complete/reopen, and soft-delete them as full `TaskItem` records linked by `ParentTaskId`. Quick-add places a task in the current Project or attaches the current Label when applicable. Services are wired through `Microsoft.Extensions.DependencyInjection`, the store/index is rebuilt before the window is created, and view models are resolved through DI. Visuals remain stock Fluent/Mica — colors, spacing, motion intentionally not set yet.
- Interface and interaction references: Things 3 and Todoist. Take the calm hierarchy, restraint, and list interactions from Things 3 (the Project to Section to Task layout, the satisfying complete interaction, generous whitespace), and the fast capture and organization patterns from Todoist (one-line quick-add, filters and saved views, priority cues). These define what the layout and interactions are.
- Quality bar: the files-community Files app. The interface references above define what the layout is; the Files app defines how finished it must feel. Match its native Fluent polish and microinteraction quality. Use Mica/Acrylic materials, native composition animations, and `ItemsRepeater`-based virtualization so long lists scroll smoothly. Native virtualization is the foundation of Files-grade smoothness.
- Manual drag-reorder is a single, layout-agnostic `ReorderSurface` (in `Cue.Pages`) attached to a task `ItemsRepeater`, not WinUI's ghost-image drag/drop. It uses native pointer events: the pressed row lifts and tracks the pointer 1:1 via a live `RenderTransform`, the other rows open a gap with `Storyboard`+`CubicEase`, a hysteresis dead-zone keeps the target slot from flickering, and the drop updates the bound collection optimistically before the rank service persists the moved record. It is virtualization-preserving by design — only the *realized* rows animate, geometry is read per frame as `measuredTop − appliedTranslate` (so recycled/newly-realized rows join seamlessly), and dragging to a viewport edge auto-scrolls; the drop computes the new rank from the two real neighbors' ranks (held in memory for every row) and writes only that one record.
- Design tokens live in `Styles/DesignTokens.xaml` (merged in `App.xaml`): a radius scale, type scale (Pretendard), interactive/state brushes, and input-control overrides. The rule is theme-correctness — brushes resolve their `Color` from a `{ThemeResource ...}` (or use stock `SubtleFill`/`ControlFill`/`SystemFill` tokens) so light/dark flip automatically; avoid hardcoded ARGB. `DESIGN_REF.md` is the design analysis distilled from the Files app and the source for the prioritized craft roadmap (P0–P3).
- A first craft pass (DESIGN_REF P0–P2) is applied: dark-mode color correctness, the radius/type token scale, stroke-separated cards (no in-flow shadows), 83ms hover, a Things-style circular completion check with an overshoot pop, the detail-panel slide-in, Todoist-style priority cues, per-label colors and per-project icons, nav count badges, and a Files-path-bar-style quick-add. Color/spacing/motion are still tunable via the tokens — adjust there, not inline.
## Invariants (do not violate)
 
1. Files are the source of truth; SQLite is a disposable index that is always rebuildable from the files.
2. Never sync or treat a monolithic database file as the truth.
3. One file per record, and the filename equals the record GUID.
4. All mutations go through `ITaskStore`.
5. Soft delete only via `DeletedAt`. Never hard-delete.
6. Every record carries Id, CreatedAt, UpdatedAt, DeletedAt, and SchemaVersion.
7. Every user-chosen date (a task's When, a recurrence anchor) is stored as UTC plus original timezone; system timestamps (CreatedAt/UpdatedAt/CompletedAt) are UTC instants only.
8. Domain types have no persistence knowledge, and are pure data holders: they never self-stamp UpdatedAt or read the clock — the store does on save.
9. Dependencies point top-down only: View, ViewModel, Store, files/index.
10. Do not introduce CRDTs or a server backend. The planned sync is cloud-folder plus last-write-wins, not collaborative editing.
## Gotchas
 
- The first successful run must be `dotnet run` or `winapp run` before using F5 or the debugger. The debugger looks for an executable that does not exist until the first run produces it.
- If a build fails showing only MSB3073, the real XAML compile error may be hidden. Read the full `dotnet build` output.
- Cloud sync (future): cloud providers may keep files as on-demand placeholders, so hydrate a file before reading it. FileSystemWatcher can miss or duplicate events over cloud-synced folders, so combine it with a periodic reconcile and a reconcile-on-focus pass rather than trusting events alone.
## Future work (the foundation already accommodates these)
 
- Multi-device sync: point the Store root folder at a OneDrive or Google Drive synced folder, then add a folder watcher and conflict-copy reconciliation (last-write-wins by `UpdatedAt`, honoring tombstones). This is additive because the Store already speaks files.
- Files-grade microinteraction and material polish.
- Reminders via `AppNotifications`. Scheduled reminders while the app is closed require package identity plus a background task or the Windows Task Scheduler.
- Packaging and distribution via `winapp pack`, moving from unpackaged to a direct installer or MSIX as needed.
## Commit discipline
 
Commit after each major step, and keep each change scoped to a single layer where possible.
