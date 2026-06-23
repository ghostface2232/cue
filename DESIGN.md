---
version: alpha
name: Cue
description: >-
  The visual and interaction design system for Cue, a calm, Korean-first task
  manager built on WinUI 3 / Windows App SDK with a Mica backdrop. The direction
  fuses Things 3's quiet hierarchy, whitespace, and satisfying completion moments
  with Todoist's fast capture and priority queue — held to the polish bar of a
  native Windows app (microinteractions, theme parity, Fluent finish). The
  defining rule is that nothing hardcodes a color: every interactive, text, and
  state color aliases one of WinUI's alpha-based theme tokens, so the whole
  surface inverts automatically between Light and Dark.

# NOTE — Color tokens below are ALIASES, not literal hex.
# Cue is a native WinUI 3 app. Each color value names the WinUI theme token it resolves from (e.g. `SubtleFillColorSecondary`).
# These tokens are alpha-based and theme-aware: they flip between Light/Dark automatically, so there is no single fixed hex per token.
# The strings are intentionally non-CSS — they are the normative source of truth for this design system. See the "Colors" section.
colors:
  # Brand / accent — the system accent is Cue's only "brand" color, used sparingly.
  primary: AccentFillColorDefault
  accent: AccentFillColorDefault
  on-accent: TextOnAccentFillColorPrimary
  # Surfaces
  page-surface: LayerFillColorDefault
  card-surface: CardBackgroundFillColorDefault
  input-surface: ControlFillColorDefault
  input-surface-hover: ControlFillColorSecondary
  # Text hierarchy
  text-primary: TextFillColorPrimary
  text-secondary: TextFillColorSecondary
  text-tertiary: TextFillColorTertiary
  # Interaction (shared hover/press recipe)
  hover-fill: SubtleFillColorSecondary
  pressed-fill: SubtleFillColorTertiary
  # Strokes / separation
  card-stroke: CardStrokeColorDefault
  divider-stroke: DividerStrokeColorDefault
  control-stroke: ControlStrokeColorDefault
  elevation-stroke: CircleElevationBorderBrush
  # Priority queue (P1–P4)
  priority-p1: SystemFillColorCritical
  priority-p2: SystemFillColorCaution
  priority-p3: SystemAccentColor
  priority-p4: TextFillColorTertiary
  # Semantic state
  success: SystemFillColorSuccess
  error: SystemFillColorCritical

# NOTE — Weight is expressed by FAMILY, not FontWeight.
# Pretendard JP ships as static OTFs, so SemiBold is a separate family (`Pretendard JP SemiBold`).
# In WinUI the hierarchy switches family rather than setting FontWeight. The `fontWeight` values below (400/600) are the semantic equivalent for non-WinUI consumers (Figma / Tailwind / web export).
typography:
  page-title:
    fontFamily: Pretendard JP SemiBold
    fontSize: 28px
    fontWeight: 600
  detail-title:
    fontFamily: Pretendard JP SemiBold
    fontSize: 27px
    fontWeight: 600
  section-header:
    fontFamily: Pretendard JP SemiBold
    fontSize: 16px
    fontWeight: 600
  card-header:
    fontFamily: Pretendard JP SemiBold
    fontSize: 14px
    fontWeight: 600
  row:
    fontFamily: Pretendard JP
    fontSize: 15px
    fontWeight: 400
  row-sub:
    fontFamily: Pretendard JP
    fontSize: 14px
    fontWeight: 400
  secondary:
    fontFamily: Pretendard JP
    fontSize: 12px
    fontWeight: 400
  pill:
    fontFamily: Pretendard JP
    fontSize: 11px
    fontWeight: 400

rounded:
  sm: 4px   # buttons, checks, child rows, small surfaces
  md: 8px   # task rows, detail inner cards, timeline bars
  lg: 12px  # detail panel, timeline canvas
  pill: 9999px

# NOTE — Spacing is currently applied inline in XAML; there is no centralized spacing token resource yet (only radius, type, and color tokens live in `Styles/DesignTokens.xaml`).
# The scale below documents the observed rhythm and is the intended target for future centralization. See "Known Gaps".
spacing:
  xxs: 2px
  xs: 4px
  sm: 8px
  md: 12px
  lg: 16px
  xl: 20px
  page-x: 28px
  page-y: 20px

components:
  # --- Task row (flat + grouped lists) ---
  task-row:
    backgroundColor: transparent
    rounded: "{rounded.md}"
    padding: 12px 8px
  task-row-hover:
    backgroundColor: "{colors.hover-fill}"
  task-row-pressed:
    backgroundColor: "{colors.pressed-fill}"
  task-row-child:
    backgroundColor: transparent
    rounded: "{rounded.sm}"
    padding: 12px 6px
  selection-bar:
    backgroundColor: "{colors.accent}"
    width: 3px
    rounded: 1.5px
  # --- Completion check (custom circular template) ---
  completion-check:
    size: 20px
    rounded: "{rounded.pill}"
  completion-check-checked:
    backgroundColor: "{colors.accent}"
    textColor: "{colors.on-accent}"
  # --- Priority pill (importance label) ---
  priority-pill:
    backgroundColor: "{colors.priority-p1}"   # rendered at ~17% alpha tint (PriorityToTint)
    textColor: "{colors.priority-p1}"          # saturated text via PriorityToBrush
    typography: "{typography.pill}"
    rounded: 9px
    padding: 7px 1px
  # --- Quick-add (omnibar pill) ---
  quick-add:
    backgroundColor: "{colors.input-surface}"
    typography: "{typography.row}"
    rounded: 24px
    height: 48px
    padding: 11px 14px 11px 46px
  # --- Detail panel + inner cards ---
  detail-panel:
    backgroundColor: "{colors.page-surface}"
    rounded: "{rounded.lg}"
    width: 460px       # default; user-resizable 320–680 (abs. min 260), see "Detail panel"
    padding: 16px 6px 20px 18px
  detail-card:
    backgroundColor: "{colors.card-surface}"
    rounded: "{rounded.md}"
    padding: 16px
  # --- Timeline ---
  timeline-canvas:
    backgroundColor: "{colors.card-surface}"
    rounded: "{rounded.lg}"
  timeline-bar:
    backgroundColor: "{colors.card-surface}"
    rounded: "{rounded.md}"
    height: 54px
    padding: 0 12px
  timeline-day-header:
    textColor: "{colors.text-secondary}"
    height: 58px
  today-marker:
    backgroundColor: "{colors.accent}"
    textColor: "{colors.on-accent}"
    rounded: 14px
    size: 28px
  today-line:
    backgroundColor: "{colors.accent}"
    width: 1px
  # --- Navigation ---
  nav-item-selected:
    textColor: "{colors.text-primary}"
  # --- Buttons ---
  icon-button:
    backgroundColor: transparent
    rounded: "{rounded.sm}"
    size: 34px
  subtle-text-button:
    backgroundColor: transparent
    textColor: "{colors.text-secondary}"
    rounded: "{rounded.sm}"
    padding: 8px 4px
  # --- Inputs ---
  text-input:
    backgroundColor: "{colors.input-surface}"
    textColor: "{colors.text-primary}"
    typography: "{typography.row}"
  text-input-focused:
    backgroundColor: "{colors.input-surface}"
  text-input-dark-well:
    backgroundColor: "#18000000"   # Dark theme only: a recessed black-overlay well
  text-input-dark-well-hover:
    backgroundColor: "#24000000"
---

# Cue Design System

This is the single source of truth for Cue's visual and interaction design. Build new screens and components against it, and fix existing ones to match.

The direction fuses **Things 3** (calm hierarchy, whitespace, satisfying completion) with **Todoist** (fast capture, priority queue), held to the polish bar of a native Windows app. Stack: **WinUI 3 / Windows App SDK**, Mica backdrop. The product is **Korean-first** — see "UX Writing".

## Overview

Cue should feel quiet, native, and considered. Reading order comes before information density: hierarchy is built from weight, size, and tone, and secondary elements stay subdued. The interaction vocabulary is small and repeated — one hover/press recipe, one color-transition timing — so the same action looks and moves the same way everywhere.

**Defining principles**

1. **No hardcoded ARGB.** Every interactive, text, and state color references a WinUI alpha-based theme token (`SubtleFillColor*`, `ControlFillColor*`, `TextFillColor*`, `SystemFillColor*`, `CardStrokeColor*`, …). These invert automatically across Light/Dark. When a literal color is unavoidable, wrap the brush's `Color` in `{ThemeResource …Color}` or split Light/Dark via `ThemeDictionaries`.
2. **Thin layer over stock.** Override theme resource keys rather than re-templating controls. Custom templates only where necessary (e.g. the completion check) and minimal in scope.
3. **Shadows only on truly floating surfaces.** Flyouts, popups, slide-overs get elevation. In-flow cards, list rows, and the detail panel use zero shadow and a 1px stroke for separation.
4. **Restrained accent.** Never fill a surface with the accent. The accent is for selection indicators, focus rings, tag dots, and the completion check only.
5. **Tokens are truth.** Sizes, radii, colors, and timings are consumed from the tokens in `Styles/DesignTokens.xaml` — never sprinkled as literals in XAML.

**Quality baseline**

- The same intent must hold in **both** Light and Dark. A color or contrast that only reads in one theme is a defect.
- Surface separation reads from stroke / tonal difference, not shadow.
- Every clickable element shows hover, press, and focus. If the focus rectangle is suppressed, focus must be re-expressed via background/stroke (accessibility).
- Nested radii align to `inner ≤ outer − padding`.
- Alignment and spacing follow the token scale; no one-off arbitrary values.

## Colors

Cue defines no fixed palette of its own. Color is delegated to WinUI's alpha-based theme tokens so the entire surface flips correctly between Light and Dark. The names below are semantic roles; the value is the WinUI source token.

### Brand & Accent
- **Accent** (`{colors.accent}` → `AccentFillColorDefault`): Cue's only brand color. Used sparingly — selection bar, focus ring, completion-check fill, today marker/line. Never as a surface fill.

### Surfaces
- **Page surface** (`{colors.page-surface}` → `LayerFillColorDefault`): page and detail-panel background.
- **Card surface** (`{colors.card-surface}` → `CardBackgroundFillColorDefault`): detail inner cards, timeline bars.
- **Input surface** (`{colors.input-surface}` → `ControlFillColorDefault`): the floating quick-add and text inputs.

### Text
- **Primary** (`{colors.text-primary}` → `TextFillColorPrimary`): titles and primary text.
- **Secondary** (`{colors.text-secondary}` → `TextFillColorSecondary`): metadata and secondary labels.
- **Tertiary** (`{colors.text-tertiary}` → `TextFillColorTertiary`): the quietest labels (group/tag headers, unchecked check outline).

### Interaction
- Transparent at rest → hover `{colors.hover-fill}` (`SubtleFillColorSecondary`) → press `{colors.pressed-fill}` (`SubtleFillColorTertiary`). This **one shared recipe** is used by main list rows, child rows, timeline bars, subtle buttons, and detail-panel controls alike, so Light mode never drifts between surfaces.

  > Implementation note: hover/press is unified through `CueHoverFillBrush` / `CuePressedFillBrush`. The detail panel previously used a one-step-stronger custom overlay; it now aliases the same shared brushes for both themes.

- **Input tone is theme-split.** Light keeps the standard control fill and holds that tone on focus (avoiding the default bright white-out). Dark replaces the framework's translucent-white fill — too bright on dark cards — with a subtle recessed **well**: `#18000000` (~9%) at rest, `#24000000` (~14%) on hover. These are the only literal colors in the system, defined per-theme in `ThemeDictionaries`.

### Priority queue (P1–P4)
- **P1** (`{colors.priority-p1}` → `SystemFillColorCritical`) — 매우 중요 / Critical
- **P2** (`{colors.priority-p2}` → `SystemFillColorCaution`) — 중요 / High
- **P3** (`{colors.priority-p3}` → `SystemAccentColor`) — 보통 / Normal
- **P4** (`{colors.priority-p4}` → `TextFillColorTertiary`) — 사소 / Low

The importance pill paints its background as a ~17% alpha tint of the priority color (`PriorityToTint`, alpha `0x2B`) with the saturated color as the label text (`PriorityToBrush`).

### Semantic state
- **Success** (`{colors.success}` → `SystemFillColorSuccess`): the detail Save glyph.
- **Error** (`{colors.error}` → `SystemFillColorCritical`): the detail Close glyph, error `InfoBar`.

  > Semantic glyphs keep their color through hover/press — they are **never** covered by a gray fill; pressing only drops opacity to 0.6.

## Typography

The typeface is **Pretendard JP** (Korean-first). Because the static OTFs ship as two families, **weight hierarchy switches family, not FontWeight**: SemiBold is its own family (`Pretendard JP SemiBold`). `ContentControlThemeFontFamily` is overridden to Pretendard so templated controls (buttons, lists, inputs, nav) inherit it; plain `TextBlock`s inherit via the window root's `FontFamily`.

### Hierarchy

| Token | Family | Size | Use |
|---|---|---|---|
| `{typography.page-title}` | SemiBold | 28 | Page title (`CuePageTitleTextStyle`) |
| `{typography.detail-title}` | SemiBold | 27 | Detail-pane editable title |
| `{typography.section-header}` | SemiBold | 16 | Group / priority-bucket headers (`CueSectionHeaderTextStyle`) |
| `{typography.row}` | Regular | 15 | Task-row title |
| `{typography.card-header}` | SemiBold | 14 | Detail card headers (`CueCardHeaderTextStyle`) |
| `{typography.row-sub}` | Regular | 14 | Child task rows, checklist items |
| `{typography.secondary}` | Regular | 12 | Metadata, secondary labels (`MetadataTextStyle`) |
| `{typography.pill}` | Regular | 11 | Priority pill label |

### Color hierarchy
Primary text `{colors.text-primary}`, metadata `{colors.text-secondary}`, quietest labels (group/tag headers) `{colors.text-tertiary}`.

### Principles
- Weight = family. Titles/headers use SemiBold; body/metadata use Regular.
- Consume text styles from the central styles (`CuePageTitleTextStyle`, `CueSectionHeaderTextStyle`, `CueCardHeaderTextStyle`, `MetadataTextStyle`) — never create inline font literals.

## Layout

### Shell — `MainWindow.xaml`
- A `TitleBar` (48px, the WinUI control with `ExtendsContentIntoTitleBar`) + left `NavigationView` (stock, thinly overridden) + content `Frame`. Mica backdrop.
- Navigation is **flat** — no back history between lists; each selection clears the back stack.
- Caption (min/max/close) buttons are drawn by the system, so XAML theming does not reach them. Their glyph colors and hover/press backgrounds are set in code and reapplied on every `ActualThemeChanged` (hover bg `#14000000` light / `#20FFFFFF` dark; pressed `#28000000` / `#40FFFFFF`).

### List page — `TaskListPage.xaml`
- Rows: page title + caption → (error `InfoBar`) → quick-add → list (+ detail panel). Body padding `28,20` (`page-x` × `page-y`).
- Two-column body: left list (flexible) + right detail panel (resizable, default 460px). When the detail closes, the list reclaims the width.
- The list takes **two forms**: a **flat list** (`ItemsRepeater`) and a **grouped list** (`ListView`, group header + rows). The grouped form is used only by the Priority (P1–P4) view (`IsGroupedList`); every other list, Project included, is flat.

### Timeline page — `TimelinePage.xaml`
- A horizontally-scrolling month view. One framed canvas (`{rounded.lg}`, 1px card stroke) holds a row of day-column headers above the task cards.
- Single-point, not a gantt range: each task has one date (When), so it is drawn as a card positioned on that day's column — there are no span bars. Only tasks with a concrete When (OnDate) appear.
- Header row: title + range caption, with prev-month / 오늘 (Today) / next-month controls (`subtle-text-button`, 34px icon buttons with chevron glyphs).
- Panning: pointer drag and mouse-wheel both scroll the timeline horizontally; left/right arrow keys pan the view in predictable steps (the ScrollViewer is a tab stop).
- Shares the **same detail panel** as the list page (resizable, identical card stack and behavior).

### Detail panel
- Radius 12, no shadow, 1px `CardStrokeColorDefault`, `InnerBorderEdge`, slides in and slides out on close (see "Motion").
- A vertical stack of cards (radius 8, 1px stroke, no shadow): task info (notes · importance · group) / date (the single When, + optional time / 종일) / tags / checklist.
- **Resizable.** A 10px transparent grab strip on the panel's left edge drag-resizes it. Width is clamped to 320–680px (absolute min 260px) and further capped so the primary list keeps ≥340px — the panel never starves the list. On hover or while dragging, the strip reveals a slim vertical pill handle (4×58, radius 2, tertiary text brush at ~72% opacity) with the standard 83ms opacity transition.
- **Responsive.** Below a compact width (~390px), paired side-by-side fields (importance + group, date + time) reflow to stack vertically so nothing is squeezed.
- **Conditional text fade for clipped content** (see "Elevation & Depth"): only overflowing inline text inside padded content, such as long tag names, fades at the right edge instead of hard-clipping. The panel scroll body itself does not get a bottom fade because it clips at the panel boundary.
- The timeline's detail panel is the same component with the same behavior.

### Spacing
The page rhythm is body padding `28,20` and card internal padding `16`. Spacing is currently applied inline in XAML (there is no centralized spacing token yet — see "Known Gaps"); the `spacing` scale in the frontmatter documents the intended rhythm.

## Elevation & Depth

Separation is carried by **stroke and tone, not shadow.**

| Level | Treatment | Use |
|---|---|---|
| In-flow | No shadow, 1px stroke | Cards, list rows, detail panel, timeline bars |
| Pseudo-float | No shadow, gradient stroke | Quick-add (`CircleElevationBorderBrush`) |
| True overlay | Elevation / shadow | Flyouts, popups, menus |

- In-flow surfaces use **zero shadow**. A 1px `CardStrokeColorDefault` (cards) or `DividerStrokeColorDefault` (inner dividers) does the separating. Stroked cards set `BackgroundSizing="InnerBorderEdge"` so the 1px sits inside the radius.
- When something needs to feel lifted (the quick-add omnibar) it uses `CircleElevationBorderBrush` — a top-light / bottom-dark gradient stroke — for subtle dimension **instead of** a drop shadow.
- Shadows belong only to true overlays (flyouts, popups).
- **Edge fades are conditional and local.** Use a short gradient only when content is clipped before it reaches an actual window/card/panel edge — for example, inline text ending inside padded content. Do not add fades where the container boundary already explains the clipping, such as the main list, sidebar, timeline canvas edge, or detail-panel scroll body.
- The fade appears only when the text actually overflows. Short labels must render without a gradient overlay. Overflowing tag names fade at the right to `CardBackgroundFillColorDefault`; timeline bar titles fade at the right to the bar's current surface, switching to the shared hover fill while hovered. The opaque stop should arrive late enough to feel like the text disappears naturally, not like a translucent veil over readable text.

## Shapes

### Radius scale
Use only `{rounded.sm}` (4) / `{rounded.md}` (8) / `{rounded.lg}` (12) plus pills. Arbitrary radii are forbidden. Nested radii align to `inner ≤ outer − padding`.

| Token | Value | Use |
|---|---|---|
| `{rounded.sm}` | 4px | Buttons, checks, child rows, small surfaces |
| `{rounded.md}` | 8px | Task rows, detail inner cards, timeline bars |
| `{rounded.lg}` | 12px | Detail panel, timeline canvas |
| `{rounded.pill}` | height/2 | Pills |

Pill instances are explicit half-height radii: priority pill `9`, quick-add `24`, today marker circle `14`.

### Focus & stroke
- System focus visuals by default.
- Text-input border thickness is flattened to 1px in every state (`TextControlBorderThemeThickness(Focused)=1`) with the **bottom accent bar removed**; on focus the border color shifts to the accent instead.
- Where a selectable row suppresses the focus rectangle, focus is re-expressed via background/stroke.

## Components

### Task row
- Columns: `[3px selection bar][circular check][title … priority pill]`, with a one-line metadata row (schedule) below.
- Selected → left 3px accent bar (`selection-bar`, radius 1.5, a dedicated column so it never shifts content). Background hover transitions over 83ms. Radius `{rounded.md}`.
- Subtasks render as an indented nested list under the parent (with a 1px divider). Their presence is self-evident, so there is no "N subtasks" caption. Child rows reuse the same circular check, row-sub font, and spacing.

### Completion state
- Completing does not remove the row: it stays in place at **opacity 0.48** and sinks to the bottom of the list. This persists across views (the active query includes completed items, sorted last). Only the sidebar count badge counts open tasks.
- **Completing a parent completes its whole checklist (subtasks).** A parent is never left complete with open subtasks — except a repeating task that has rolled to its next cycle (the work continues).

### Priority pill
- Rendered **directly beside** the row title as a text pill (not a leading dot). The title is width-capped and truncates, so the pill hugs the title's trailing edge rather than being pushed to the far right. Labels: 매우 중요 / 중요 / 보통 / 사소.
- Background is a ~17% tint of the priority color; text is the saturated tone. Radius 9. Color mapping per the priority tokens; text via `PriorityToLabel`, tint via `PriorityToTint`, saturated color via `PriorityToBrush`.

### Circular completion check — `CueCircleCheckBoxStyle`
- 20×20. Unchecked = 1.6px outline circle (`TextFillColorTertiary`, → secondary on hover). Checked = accent fill + white check glyph + overshoot pop.
- For completion toggles only — never for multi-select / general checkboxes.

### Quick-add (omnibar)
- A floating pill (min height 48, radius 24). Background `ControlFillColorDefault` + `CircleElevationBorderBrush` for subtle dimension (not a shadow). Leading glyph and text are vertically centered.
- Lifts one tone on hover; gains an accent ring on focus.
- A natural-language date in the input defaults to the **due date** (the start date is added explicitly in detail).

### Start + due date card
- Start and due dates live in **one card**. Adding a start date places it **above** the due date (natural start→due order), separated by a divider; before that, a "+ 시작일 추가" (Add start date) button holds the slot.
- The start date can never exceed the due date (calendar `MaxDate` cap; pulling the due date earlier pulls the start with it).
- Each date shows date + time on one line; "종일" (All day) hides the time (expires at 23:59).

### Tags
- The detail tag card is a **checkbox-free list**. Tapping a row confirms selection with a trailing check (color glyph + name + trailing check).
- A "태그 없음" (No tags) item at the top makes the untagged state explicit (checked by default); it is mutually exclusive with real tags and is not written as a tag id.

### Inputs
- Flat 1px boxes with no bottom accent bar. Theme-split tone (Dark = recessed well). On focus only the border turns accent.

### Sidebar / navigation
- Stock `NavigationView` + thin override. Selected text is flattened to `TextFillColorPrimary` (calm, not accent); selection reads from the fill + the stock left accent pill.
- Fixed items: 모든 할 일 (All) · 오늘 할 일 (Today) · 앞으로 할 일 (Upcoming) · **타임라인 (Timeline)** · 언젠가 할 일 (Anytime) · 완료한 일 (Logbook) · 중요도 (Priority). Below them: **그룹 (Groups / Projects)** and **태그 (Tags / Labels)** sections.
- Group/Tag headers are 12 SemiBold `TextFillColorTertiary` (quiet hierarchy).
- An `InfoBadge` with the open-task count sits at the end of each item (restrained).
- **Glyph click = instant picker.** Clicking a project glyph opens the icon picker; clicking a tag glyph opens the color picker — directly, no depth (right-click context menu is the fallback).
- **Sidebar right-click = show/hide menu.** A checkable list toggles the fixed views (오늘 / 앞으로 / 타임라인 / 언젠가 / 완료 / 중요도) on and off (name left, accent check right). Saved to app-local settings. "모든 할 일" is always shown.

### Selection popup (icon / color)
- Icon/color only, no names; a 4-column grid flyout anchored to the nav item.
- **The current selection is ringed** (accent ring for icons, high-contrast ring for colors).
- Color swatches only **brighten slightly** on hover (white-blend) rather than being covered by a theme fill; the ring persists through hover/press.
- Project icons are chosen not to clash with the fixed sidebar glyphs; the star uses an outline glyph (tonally matched to the other outline icons).

### Timeline
- **Day-column header** (`timeline-day-header`, 58px tall): day number over weekday label, secondary text; the cell carries a right + bottom 1px divider. Today's number sits in a 28px accent circle (`today-marker`, radius 14) with on-accent text.
- **Task bar** (`timeline-bar`): a card-surface bar (radius 8, 1px card stroke, `InnerBorderEdge`), min width 140, min height 54, positioned on a canvas by start offset and width. Carries the title + priority pill and a date caption. Hover uses the shared `CueHoverFillBrush`; the title fade appears only when the measured title overflows.
- **Now line** (`today-line`): a precise 1px accent rectangle at opacity 0.8, positioned by a `TranslateTransform` using the current time-of-day fraction, shown only when today falls in range.

### Dialogs / inline buttons
- Inline secondary actions (rename / delete / + add) share one style: transparent background + subtle hover + secondary text (`CueSubtleTextButtonStyle`). At most one true primary is emphasized per context.
- The shared icon button is `CueIconButtonStyle` (34×34 `HyperlinkButton`, transparent at rest, semantic meaning via glyph color only).

## Do's and Don'ts

- **Do** consume color from WinUI theme tokens; **don't** hardcode ARGB. Where a literal is unavoidable, use a `ThemeResource` Color or `ThemeDictionaries`.
- **Do** verify both Light and Dark — a one-theme-only color is a defect.
- **Do** separate in-flow surfaces with a 1px stroke + `InnerBorderEdge`; **don't** add a shadow unless the surface is a true overlay.
- **Do** reuse the one hover/press recipe and the 83ms color transition; **don't** invent per-surface interaction colors.
- **Do** keep the accent restrained (small indicators); **don't** fill a surface with it.
- **Do** build hierarchy from family-weight, size, and tone; keep secondary elements quiet.
- **Do** show hover/press/focus on every clickable element; if the focus rectangle is off, re-express focus with background/stroke.
- **Do** align nested radii to `inner ≤ outer − padding`; use only the 4/8/12 + pill scale.

---

# Appendix (Cue-specific, beyond the standard schema)

> The sections below capture Cue conventions that the standard DESIGN.md schema does not model (motion, copy, process). They are preserved here as additional, non-normative guidance.

## Motion & Microinteractions

- **Color transitions are unified at 83ms, ease-out.** Task-row backgrounds hover via a declarative `BrushTransition` (`0:0:0.083`), not a code-behind swap.
- **Completion (celebration moment):** empty outline → filled accent circle + check glyph, with a scale overshoot 0.6 → 1.15 → 1.0 (~280ms; spline `KeySpline 0.1,0.9 0.2,1.0`). The completed row then fades to opacity 0.48 in place.
- **Detail-panel entrance:** Composition `Translation` (28→0) + `Opacity`, slide-in over ~350ms on the signature cubic-bezier `(0.1,0.9)(0.2,1.0)`, run on the compositor thread.
- **Detail-panel close:** the reverse — `Translation` (0→24) + `Opacity` fade out, faster (~180ms slide / ~160ms fade) on an accelerating ease `(0.4,0)(1,1)`. The actual close is deferred ~170ms so the panel animates out before it is removed.
- **Page transition:** subtle `Opacity` + `Scale` settle (≈0.99→1.0) on navigation.
- **List reposition:** rows carry `RepositionThemeTransition`. To avoid virtualization conflicts, motion is attached **only to realized containers** (`ElementPrepared` / `ElementClearing`); Storyboards are never keyed to virtual items. Drag-reorder surfaces use their own motion with default transitions off.
- **Cross-cutting:** for virtualized rows, prefer Composition implicit animations over Storyboards. `ConnectedAnimation` is not used.
- **Respect reduced motion.** Motion is gated on the system `UISettings.AnimationsEnabled`; when off, transitions are skipped and the end state is applied directly (e.g. the detail panel closes immediately rather than animating out).

## UX Writing

- **Korean-first.** Write natural Korean where intent lands immediately.
- **Domain term mapping:** Project → **그룹 (Group)**, Label → **태그 (Tag)**, Priority → **중요도 (Importance)** (P1–P4 = 매우 중요 / 중요 / 보통 / 사소), Subtask → **체크리스트 (Checklist)**, the task's single date → **날짜 (Date)**, Parent task → **할 일 (Task)**. (There is no separate deadline; a task has one date.)
- **Time views:** 오늘 할 일 (Today) / 앞으로 할 일 (Upcoming) / 언젠가 할 일 (Anytime) / 완료한 일 (Logbook).
- Drop redundant labels (e.g. omit self-evident card titles).

## Adding a New Element — Checklist

1. **Tokens first.** Before inventing a color/radius/font/timing, check for a fit in the existing tokens. If none, add a token and consume that (no literals).
2. **Both themes.** Confirm the intent holds in Light and Dark. Literals go through `ThemeResource` Color or `ThemeDictionaries`.
3. **Separate with stroke.** If you want a shadow, ask whether it is a true overlay. If in-flow, use a 1px stroke + `InnerBorderEdge`.
4. **Reuse the interaction vocabulary.** Hover/press from the shared recipe; color transition at 83ms; new motion follows the token timings/splines.
5. **Restrain the accent.** No accent-filled surfaces; emphasize with small indicators.
6. **Hierarchy** from family-weight, size, and tone; keep secondary elements quiet.
7. **Focus & accessibility.** Clickable elements show hover/press/focus; provide a replacement when the focus rectangle is suppressed.
8. **Radius alignment.** Nested: `inner ≤ outer − padding`.
9. **Copy** in natural Korean per the UX Writing terms and tone.
10. **Verify.** Build + tests pass; check the real screen in Light and Dark where possible.

## Known Gaps

- **No centralized spacing token.** `Styles/DesignTokens.xaml` defines radius, type, and color tokens, but spacing/padding is still applied inline in XAML. The `spacing` scale in the frontmatter documents the intended rhythm (page padding `28,20`, card padding `16`) but is not yet a consumed resource. This is the main unmet part of the "tokens are truth" principle.
- **Pretendard JP** ships as static OTFs, so weight hierarchy is family-switched (Regular vs. SemiBold) rather than `FontWeight`-driven. The frontmatter encodes the semantic weight (400/600) for non-WinUI export.
- **Caption (window) buttons** are system-drawn and themed in code-behind (`ApplyCaptionButtonColors`), reapplied on `ActualThemeChanged` — they are not reachable as XAML tokens.
- The Dark text-input **well** (`#18000000` / `#24000000`) is the only set of literal colors in the system; it is intentionally theme-scoped in `ThemeDictionaries`.
- Color tokens in this document are **aliases** of WinUI theme tokens, not literal hex; they cannot be WCAG-contrast-checked statically because they resolve to different values per theme.
