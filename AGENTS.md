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
 
Domain types are pure C# with zero knowledge of persistence: `TaskItem`, `Project`, `Section`, `Label`. The hierarchy is Project contains TaskItems (optionally grouped under a Section), and Label is cross-cutting. A task's container is a single nullable `ProjectId`; null means unclassified (free). An Area level above Project is intentionally deferred for the foundation phase — it can be added later as an additive record type plus an optional `Project.AreaId`, with no rewrite.
 
Every record carries these fields from day one:

- `Id` (GUID)
- `CreatedAt`
- `UpdatedAt`
- `DeletedAt` (tombstone for soft delete; null means alive)
- `SchemaVersion` (int)

Records compare by **identity**: two instances are equal when they are the same concrete type with the same `Id` (exact `GetType()` match), and the hash is over the immutable, init-only `Id` only. Because equality is Id-only it cannot tell whether content changed — a local record and an incoming synced one with the same Id are "equal" even if every other field differs. Index refresh and sync reconciliation must therefore compare `UpdatedAt` (or specific fields), never `old.Equals(new)`.

Timestamps and user dates are stored differently:

- System timestamps — `CreatedAt`, `UpdatedAt`, `CompletedAt` — are UTC instants only; a timezone carries no meaning for them.
- Every user-chosen scheduled date — a task's When date, a task's and a project's `Deadline`, a recurrence anchor — stores UTC plus the original timezone, rendered in local time only at display. This protects DST correctness now and feeds last-write-wins reconciliation later.

Per-type specifics:

- **Completion is a nullable `CompletedAt`, not a boolean.** `IsCompleted` is a derived read-only property (true once `CompletedAt` is set), on both `TaskItem` and `Project`.
- **Two optional date fields.** `Deadline` is the hard due date (the default workflow uses only this). `When` is the scheduled date that drives Today/Upcoming, with three stored states: unscheduled, a concrete (zoned) date, or Someday. "Today" and "This Evening" are not stored states — the caller stamps the current day in the user's timezone as a concrete date, "This Evening" adding an evening flag. Whether something is today / upcoming / overdue is view logic comparing the stored date to the current day, never a stored flag.
- **Recurrence** is an RFC 5545 RRULE string plus a zoned anchor. Computing the next occurrence — and advancing a completed task's When to the next cycle — is deferred to the store/logic layer; the domain only holds the rule and anchor.
- **Priority** is an explicit enum: `None`, `P1`, `P2`, `P3`, `P4` (P1 most urgent), default `None`.
- **Sub-tasks** are full `TaskItem` records linked by `ParentTaskId`, not a lightweight checklist, so each carries its own dates, labels, priority, and recurrence.
- **`SortOrder`** on every manually-ordered entity (tasks, projects, sections, labels) is a LexoRank-style fractional string rank, so a new rank always fits between two neighbors without renumbering. The store/rank service assigns it.

Domain types are pure data holders with no clock or persistence access: they never self-stamp `UpdatedAt` — that is the store's job on save.
 
## Storage layer
 
- `ITaskStore` exposes GetAll, Get(id), Save(record), Delete(id). Every data mutation in the app goes through this single interface. This is what makes sync additive later.
- Folder layout uses type-based subfolders: `tasks/`, `projects/`, `sections/`, `labels/`, and `meta/` for settings and schema version. Each file is named `{guid}.json`, so the filename equals the record Id and records are never renamed (which minimizes the sync conflict surface).
- The root folder path is a configuration value, never hardcoded. The v1 default is the local app data path; in a later phase it becomes a cloud-synced folder.
- Save always updates `UpdatedAt`. Delete is a soft delete: set `DeletedAt`, then re-save the record. Never hard-delete a file.
- Use atomic writes: write to a temp file, then swap, so a crash mid-write cannot leave a half-written file. All file IO is async. The temp name carries a unique token (not a fixed `{id}.tmp`) so concurrent saves of one record don't collide, and the writer deletes its own temp on failure.
- Reads are corruption-isolated: `GetAll` skips a file that fails to deserialize (a half-written or partially-synced file) and returns the rest rather than throwing, so one bad file can't break a listing or the index rebuild that runs on it.
## Index layer
 
- `Microsoft.Data.Sqlite`. Rebuild the index from the file folder on startup, then update it incrementally on each Save and Delete.
- The index must hold nothing that cannot be reconstructed from the files. Deleting the index file and restarting must fully rebuild it from the folder. Treat passing this test as a hard requirement.
- Tombstoned records (non-null `DeletedAt`) are excluded from default queries.
- Implementation: `SqliteTaskIndex` (raw SQL) holds the query surface, and `IndexedTaskStore` composes a `FileTaskStore` with it so there is exactly one write path — Save/Delete write the file first, then reflect that one record into the index. `OpenAsync` wires both and rebuilds at startup. By-id and GetAll reads come from the files; all *list* reads come from the index (`ITaskIndex`), which returns the lightweight `TaskListItem` projection and never scans the folder.
- The index database path is configured separately from the data root (`FileTaskStoreOptions.IndexPath`, defaulting to `{RootPath}/index.db`). Because the index is a per-device disposable cache, it must stay on local storage and never be synced: when the data `RootPath` later points at a cloud folder, set `IndexPath` to a local path so the database is never swept into sync (which would spawn per-device conflict copies).
- Queries split on two axes. Classification: by project, by section, by label, and the project-less Inbox. Time: Today (an open task whose When day is today-or-earlier, or whose Deadline is today-or-earlier — so a deadline-only task that is due or overdue still surfaces; overdue rolls forward), This Evening (the evening-flagged subset of the OnDate part of Today), Upcoming (a future When day or a future Deadline), Anytime (Unscheduled and open), Someday, Logbook (completed). The two axes overlap intentionally (e.g. a deadline-only future task is both Anytime and Upcoming).
- Time views are never stored. The current day is computed at query time from an injected `TimeProvider` and time zone and compared against each task's pinned calendar date, which is what makes overdue items carry into Today and a task move Upcoming → Today as the clock advances.
- Project, Section, and Label navigation records are indexed alongside tasks and rebuilt from their per-record files at startup. Active navigation queries exclude tombstones and, where the domain supports them, archived/completed records. No left-nav list scans the folders.
- Container deletion policy (resolved): deleting a Project or Section preserves its live and completed tasks by clearing both `ProjectId` and `SectionId`, moving open tasks to Inbox; deleting a Project also soft-deletes its Section records. This intentionally favors the least destructive behavior over cascading task deletion. Deleting a Label only removes that Label id from tasks and soft-deletes the Label record.
- Deferred: (a) Cross-zone "today": a task's `when_date`/`deadline_date` is its calendar day in its own zone, while "today" is computed in the index's zone, so a task pinned in another zone (travel) can be a day off at the boundary — accepted foundation-phase edge; the UTC+zone data needed to refine it later is already retained. (b) File→index writes are not one transaction; a reflect failure leaves the file ahead until the next startup rebuild heals it (the reflect runs in its own SQLite transaction, so the index is never half-written). A persisted dirty-marker, not a retry, is the right future mitigation.
## Parser
 
- `IDateParser` (in `Cue.Parsing`) takes one line of user text plus a reference `now` and the user's time zone, and returns a `ParsedQuickAdd`: the cleaned `Title` (recognized phrases removed) plus `When` (`ScheduledWhen`), optional `Deadline`, and optional `Recurrence`. Pure service — no UI/storage dependency, reads no clock (caller supplies `now`), never throws.
- **Library reality:** at the pinned version, `Microsoft.Recognizers.Text.DateTime` has *no* working Korean DateTime model — `Culture.Korean` returns zero matches for everything, down to bare `내일`/`3월 15일` (the English model works fine). So the package can't be the core recognizer for Korean today.
- Design: `KoreanDateParser` is an ordered pipeline of `IQuickAddRule`s (the boost seam, modeled on chrono's parser list). The built-in rules do the Korean recognition — Someday, Recurrence, RelativeHour, Deadline (까지/마감/안에), WhenDate (relative/weekday/absolute + time/evening), TimeOfDay — each consuming the span it recognizes so the leftover becomes the title. Custom rules passed to the constructor run *first*, so they override/extend without touching the defaults. The Recognizers library is still wired in (per spec, with `Culture.Korean`) as a final fallback stage; it contributes nothing for Korean now but lights up automatically if the library gains support.
- Misrecognition guards: rules use Hangul word-boundary lookarounds (`(?<![가-힣])` / `(?![가-힣])`), so date-like fragments glued into nouns stay in the title — `내일로`, `오늘의집`, `3월의`, `일요일의` don't match, and `24시` is rejected as an impossible clock time.
- If nothing is recognized (or input is ambiguous), the whole line becomes the title with empty scheduling. Representative expressions across all PARSING.md categories are covered by unit tests.
- Known boost-layer gaps (library can't help; add rules as needed): bare day-parts like 점심때/새벽 carry no precise time; some Korean-specific forms (아침저녁 twice-daily, 말일/월급날 semantic dates) aren't mapped yet. PARSING.md is the regression corpus — add a row, add a rule, re-test.
## UI
 
- MVVM with `CommunityToolkit.Mvvm` source generators (`[ObservableProperty]` on partial properties — the WinUI-recommended form, not fields).
- Current state — minimal data-loop UI (craft deferred; this stage proves the loop end to end): one reusable `TaskListPage` serves Inbox, all time views, Project, and Label modes from `ITaskIndex`. Today separates the indexed This Evening subset; Project mode groups tasks by indexed Section records. The left `NavigationView` shows live indexed Project and Label lists and offers basic create/rename/soft-delete actions, while Section actions live on the Project page. Every mutation goes through `ITaskStore`; quick-add places a task in the current Project or attaches the current Label when applicable. Services are wired through `Microsoft.Extensions.DependencyInjection`, the store/index is rebuilt before the window is created, and view models are resolved through DI. Visuals remain stock Fluent/Mica — colors, spacing, motion intentionally not set yet.
- Interface and interaction references: Things 3 and Todoist. Take the calm hierarchy, restraint, and list interactions from Things 3 (the Project to Section to Task layout, the satisfying complete interaction, generous whitespace), and the fast capture and organization patterns from Todoist (one-line quick-add, filters and saved views, priority cues). These define what the layout and interactions are.
- Quality bar: the files-community Files app. The interface references above define what the layout is; the Files app defines how finished it must feel. Match its native Fluent polish and microinteraction quality. Use Mica/Acrylic materials, native composition animations, and `ItemsRepeater`-based virtualization so long lists scroll smoothly. Native virtualization is the foundation of Files-grade smoothness.
- Light and dark come from the template defaults. Color, spacing, and motion details are intentionally not fixed yet. Leave them adjustable and do not lock them in early.
## Invariants (do not violate)
 
1. Files are the source of truth; SQLite is a disposable index that is always rebuildable from the files.
2. Never sync or treat a monolithic database file as the truth.
3. One file per record, and the filename equals the record GUID.
4. All mutations go through `ITaskStore`.
5. Soft delete only via `DeletedAt`. Never hard-delete.
6. Every record carries Id, CreatedAt, UpdatedAt, DeletedAt, and SchemaVersion.
7. Every user-chosen scheduled date (task When/Deadline, project Deadline, recurrence anchor) is stored as UTC plus original timezone; system timestamps (CreatedAt/UpdatedAt/CompletedAt) are UTC instants only.
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
