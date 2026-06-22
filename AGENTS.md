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
Due dates store UTC plus the original timezone, and are rendered in local time only at display. This protects DST correctness now and feeds last-write-wins reconciliation later.
 
## Storage layer
 
- `ITaskStore` exposes GetAll, Get(id), Save(record), Delete(id). Every data mutation in the app goes through this single interface. This is what makes sync additive later.
- Folder layout uses type-based subfolders: `tasks/`, `projects/`, `sections/`, `labels/`, and `meta/` for settings and schema version. Each file is named `{guid}.json`, so the filename equals the record Id and records are never renamed (which minimizes the sync conflict surface).
- The root folder path is a configuration value, never hardcoded. The v1 default is the local app data path; in a later phase it becomes a cloud-synced folder.
- Save always updates `UpdatedAt`. Delete is a soft delete: set `DeletedAt`, then re-save the record. Never hard-delete a file.
- Use atomic writes: write to a temp file, then swap, so a crash mid-write cannot leave a half-written file. All file IO is async.
## Index layer
 
- `Microsoft.Data.Sqlite`. Rebuild the index from the file folder on startup, then update it incrementally on each Save and Delete.
- The index must hold nothing that cannot be reconstructed from the files. Deleting the index file and restarting must fully rebuild it from the folder. Treat passing this test as a hard requirement.
- Tombstoned records (non-null `DeletedAt`) are excluded from default queries.
## Parser
 
- `IDateParser` wraps `Microsoft.Recognizers.Text.DateTime` configured with `Culture.Korean`. Input is one line of user text; output is an optional recognized date/time plus the cleaned title with the date text removed.
- Korean is partial support in the library. Keep a small refinement layer that can add rules for common Korean expressions the library misses. Core recognition stays in the library; the boost layer plugs in around it.
- If no date is recognized, use the full text as the title and leave the due date empty. Never crash on ambiguous input.
- The parser is a pure service with no UI or storage dependency. Cover representative expressions with unit tests.
## UI
 
- MVVM with `CommunityToolkit.Mvvm` source generators.
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
7. Due dates are stored as UTC plus original timezone.
8. Domain types have no persistence knowledge.
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