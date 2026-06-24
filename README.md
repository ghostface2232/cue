# Cue

A Windows-native to-do app where you capture a task in one line and it works out the rest — the due date, the time, the recurrence — straight out of plain Korean (or English) text. The name is the mechanic: you type a line, and it *cues* the next action.

> **Status — foundation phase.** The core loop (capture → parse → store → query → edit) runs end to end and a first visual craft pass is in. It's single-device and local-first for now; sync, reminders, and final polish are deliberately later. Not packaged for release yet.

## The idea

Most to-do apps send you to a date picker. Cue doesn't. You write the way you'd text yourself a reminder, and the parser lifts the schedule out of the sentence and leaves a clean title behind:

```
내일 오후 3시 회의 준비   →  "회의 준비"        tomorrow, 15:00
매주 월요일 운동          →  "운동"            every Monday  (FREQ=WEEKLY;BYDAY=MO)
금요일까지 보고서         →  "보고서"           this Friday
3월 15일 동창 모임 참석    →  "동창 모임 참석"     March 15
장보기 언젠가            →  "장보기"           someday (Anytime / 언젠가)
```

Korean is the first-class input language, not a bolt-on. The parser is an ordered pipeline of small recognition rules (relative dates, weekdays, absolute dates, times, day-part words, due expressions, recurrence, "someday" markers), with Hangul word-boundary guards so date-like fragments glued into nouns — `오늘의집`, `3월의`, `24시` — stay in the title where they belong.

## What works today

- **One-line quick-add** that drops the new task into whatever list, group, or tag you're currently looking at.
- **Lists that compute themselves.** Today, Upcoming, Anytime, and the Logbook are all derived from a single `When` date, so an overdue task rolls forward into Today on its own — nothing is a stored "is it today" flag.
- **Groups and tags** in a live sidebar, each with its own icon or color, plus the 그룹 없음 / 태그 없음 inboxes that re-gather unfiled quick captures.
- **A priority view** (P1–P4) — the one grouped list; every other list is flat.
- **A detail panel** for title, Markdown notes, priority, group, the zoned date (with an optional time, or 종일/all-day), tags, and an embedded checklist you can add to, tick, edit, and reorder.
- **Drag to reorder** via a hand-rolled, virtualization-preserving surface (native pointer events, gap animation, edge auto-scroll) backed by fractional ranks — moving one task rewrites exactly one record.
- **Recurrence** (RFC 5545 RRULE): completing a repeating task leaves a finished copy in the Logbook and advances the original one cycle.
- **A timeline view** that places each dated task on its day.

## How it works

The one rule everything else follows: **the files are the truth, and SQLite is a throwaway cache.**

Every record — a task, a group, a tag — is a single `{guid}.json` file. All writes go through one `ITaskStore`, which writes the file first and then reflects that one record into a SQLite index. Every list query reads the index; nothing scans the folder. Delete the index, restart, and it rebuilds from the files alone — that round-trip is treated as a hard requirement, not a nice-to-have.

```
View (XAML)
  → ViewModel (MVVM)
      → ITaskStore  ── the single write path
            → folder of {guid}.json   (source of truth)
            → SQLite index            (derived, rebuildable)
  → IDateParser  (standalone, no UI/storage deps)
```

It's built this way on purpose. The plan is for the data folder to eventually live in a synced cloud folder (OneDrive, Google Drive), so the truth has to be granular per-record files that merge cleanly — never one monolithic database that sync would tear apart. When sync arrives it's additive: point the root at a shared folder, add a watcher and last-write-wins reconciliation. No rewrite.

A few deliberate choices fall straight out of that rule:

- Deletes are soft — a `DeletedAt` tombstone, never a removed file — so rebuild and sync still see them.
- File writes are atomic (temp file, then swap) and corruption-isolated: one unreadable file is skipped, not fatal to the whole listing.
- User-chosen dates keep their original time zone (DST-correct, and ready for last-write-wins); system timestamps are plain UTC instants.
- Domain types know nothing about persistence — they don't even stamp their own `UpdatedAt`; the store does that on save.

For the whole story, see [`AGENTS.md`](AGENTS.md) (architecture and the invariants that don't bend), [`DESIGN.md`](DESIGN.md) (the design system), and [`PARSING.md`](PARSING.md) (the parser's test corpus).

## Tech stack

- **WinUI** (Windows App SDK 2.2) — native Fluent UI, not a webview wrapper.
- **C# / .NET 10**, targeting `net10.0-windows10.0.26100.0`.
- **MVVM** with `CommunityToolkit.Mvvm` source generators.
- **System.Text.Json** for the per-record files; **Microsoft.Data.Sqlite** (raw SQL, no EF Core) for the derived index.
- **Ical.Net** for RRULE evaluation, confined to the storage layer and never referenced from the domain or view models.
- **Pretendard JP**, bundled as the app-wide typeface.

The Korean parser is hand-written. `Microsoft.Recognizers.Text` is wired in as a fallback stage, but at the pinned version its Korean DateTime model matches nothing, so the built-in rule pipeline does the real work — and the library lights up automatically if it ever gains Korean support.

## Building and running

You'll need **Windows** (10.0.17763 or newer), the **.NET 10 SDK**, and the Windows App SDK workload.

```bash
# from the repo root
dotnet run      # build and launch   (or: winapp run)
dotnet test     # run the unit tests in Cue.Tests
```

The first launch has to be `dotnet run` (or `winapp run`) before you reach for F5 — the debugger looks for an executable that doesn't exist until the first run produces it. If a build fails with only an `MSB3073`, read the full `dotnet build` output; the real XAML compile error is usually hidden above it.

This is a Windows-only app (`net10.0-windows`) — it won't build or run on macOS or Linux.

## Project layout

```
Cue.Domain     pure domain records (TaskItem, TaskGroup, Tag, ScheduledWhen, ...), zero persistence knowledge
Cue.Storage    ITaskStore, the SQLite index, fractional ranking, recurrence, JSON serialization
Cue.Parsing    IDateParser and the Korean recognition pipeline
ViewModels     MVVM view models
Pages/         XAML pages and the drag-reorder surface
Services/      app-level services (preferences, dialogs, date parsing)
Styles/        design tokens (DesignTokens.xaml)
Cue.Tests      xUnit tests — parser corpus, store, index, recurrence, ranking, time zones
App / MainWindow   the shell, DI wiring, and the navigation sidebar
```

## Roadmap

Accommodated by the architecture, not yet built:

- Multi-device sync over a cloud folder (folder watcher + last-write-wins by `UpdatedAt`, honoring tombstones).
- Reminders via Windows `AppNotifications`.
- Files-grade material and microinteraction polish.
- Packaging and distribution via `winapp pack`.

Out of scope by design: CRDTs, a server backend, real-time collaboration. The planned sync is cloud-folder plus last-write-wins — that's the whole model.

## Credits

Interaction and quality references: [Things 3](https://culturedcode.com/things/), [Todoist](https://todoist.com/), and the [Files](https://github.com/files-community/Files) app for the native-Fluent finish to aim at. The bundled typeface is [Pretendard](https://github.com/orioncactus/pretendard), under the SIL Open Font License (`Assets/Fonts/OFL.txt`).
