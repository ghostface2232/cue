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
  # Chips (task-row group/tag). Group chip = neutral overlay (theme-split literal); tag chip = a
  # tint of the tag's own color (HexToTint) with the saturated color (HexToBrush) on top.
  chip-neutral-fill: CueChipNeutralFillBrush  # Light #14000000 / Dark #18FFFFFF
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
# Pretendard JP ships as static OTFs, so each weight is a separate family: Regular (`Pretendard JP`),
# Medium (`Pretendard JP Medium`), SemiBold (`Pretendard JP SemiBold`). Medium is the emphasis weight
# for important content text that would otherwise read as Regular (task titles, checklist item titles).
# In WinUI the hierarchy switches family rather than setting FontWeight. The `fontWeight` values below (400/500/600) are the semantic equivalent for non-WinUI consumers (Figma / Tailwind / web export).
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
  list-title:            # main list row title (larger/clearer than the base row)
    fontFamily: Pretendard JP SemiBold
    fontSize: 16.5px
    fontWeight: 600
  list-meta:             # main list row meta line (date · time) and right-edge group/tag chips
    fontFamily: Pretendard JP
    fontSize: 13px
    fontWeight: 400
  row-sub:
    fontFamily: Pretendard JP
    fontSize: 14px
    fontWeight: 400
  checklist-item-title:  # checklist item title (a checklist item is title + check only — no memo)
    fontFamily: Pretendard JP Medium
    fontSize: 14px
    fontWeight: 500
  secondary:
    fontFamily: Pretendard JP
    fontSize: 12px
    fontWeight: 400
  pill:
    fontFamily: Pretendard JP
    fontSize: 11px
    fontWeight: 400

rounded:
  sm: 4px   # buttons, checks, checklist rows, small surfaces
  md: 8px   # task rows, detail inner cards
  lg: 12px  # detail panel
  chip: 14px # group / tag / priority chips on a task row
  pill: 9999px

# NOTE — Spacing is tokenized in `Styles/DesignTokens.xaml` and consumed from there: gaps
# (Spacing / ColumnSpacing / RowSpacing) use the `CueGap*` x:Double tokens; padding/margin uses the
# `CuePad*` Thickness tokens. Off-scale literals were snapped onto this scale. A few structural/optical
# values stay inline by design (see "Known Gaps").
spacing:
  xxs: 2px    # CueGap2
  xs: 4px     # CueGap4
  sm: 8px     # CueGap8
  md: 12px    # CueGap12
  lg: 16px    # CueGap16  (also CuePadCard, uniform card padding)
  xl: 20px    # CueGap20 / CuePadPage — uniform body padding on both axes (list / settings)
  xxl: 24px   # CueGap24  (settings label↔control column gap)

components:
  # --- Task row (flat + grouped lists) ---
  task-row:
    backgroundColor: transparent
    rounded: "{rounded.md}"
    padding: 12px 12px
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
  # --- Settings (section nav + form rows) ---
  settings-nav:                # left section list (시간 / 파싱 / 외관 / 알림)
    width: 200px               # {CueSettingsNavWidth}
    item-rounded: "{rounded.md}"   # hover/selection pill matches the sidebar
    item-min-height: 38px
  settings-content:            # right column form
    width: fill                # fills the space beside the nav (variable, no cap), like the main content area beside the sidebar
  settings-row:                # one label/caption + trailing control row inside a card
    minHeight: 52px            # {CueSettingsRowMinHeight}
    columnGap: 24px
    label-maxWidth: 380px      # {CueSettingsLabelMaxWidth}; caps the text column so captions wrap
  settings-control:            # trailing combo/etc.
    width: 200px               # {CueSettingsControlWidth}
    rounded: "{rounded.sm}"    # 4px inside the 8px card (inner ≤ outer)
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
- Alignment and spacing follow the token scale; no one-off arbitrary values. Gaps use the `CueGap` 2/4/8/12/16/20/24 scale and padding uses `CuePad*`; off-scale values are snapped, with only the documented structural/optical exceptions left inline.

## Colors

Cue defines no fixed palette of its own. Color is delegated to WinUI's alpha-based theme tokens so the entire surface flips correctly between Light and Dark. The names below are semantic roles; the value is the WinUI source token.

### Brand & Accent
- **Accent** (`{colors.accent}` → `AccentFillColorDefault`): Cue's only brand color. Used sparingly — selection bar, focus ring, completion-check fill, today marker/line. Never as a surface fill.

### Surfaces
- **Page surface** (`{colors.page-surface}` → `LayerFillColorDefault`): page and detail-panel background.
- **Card surface** (`{colors.card-surface}` → `CardBackgroundFillColorDefault`): detail inner cards.
- **Input surface** (`{colors.input-surface}` → `ControlFillColorDefault`): the floating quick-add and text inputs.

### Text
- **Primary** (`{colors.text-primary}` → `TextFillColorPrimary`): titles and primary text.
- **Secondary** (`{colors.text-secondary}` → `TextFillColorSecondary`): metadata and secondary labels.
- **Tertiary** (`{colors.text-tertiary}` → `TextFillColorTertiary`): the quietest labels (group/tag headers, unchecked check outline).

### Interaction
- Transparent at rest → hover `{colors.hover-fill}` (`SubtleFillColorSecondary`) → press `{colors.pressed-fill}` (`SubtleFillColorTertiary`). This **one shared recipe** is used by main list rows, checklist rows, subtle buttons, and detail-panel controls alike, so Light mode never drifts between surfaces.

  > Implementation note: hover/press is unified through `CueHoverFillBrush` / `CuePressedFillBrush`. The detail panel previously used a one-step-stronger custom overlay; it now aliases the same shared brushes for both themes.

- **Input tone is theme-split.** Light keeps the standard control fill and holds that tone on focus (avoiding the default bright white-out). Dark replaces the framework's translucent-white fill — too bright on dark cards — with a subtle recessed **well**: `#18000000` (~9%) at rest, `#24000000` (~14%) on hover. These are the only literal colors in the system, defined per-theme in `ThemeDictionaries`.

### Priority queue (P1–P4)
- **P1** (`{colors.priority-p1}` → `SystemFillColorCritical`) — 매우 중요 / Critical
- **P2** (`{colors.priority-p2}` → `SystemFillColorCaution`) — 중요 / High
- **P3** (`{colors.priority-p3}` → `SystemAccentColor`) — 보통 / Normal
- **P4** (`{colors.priority-p4}` → `TextFillColorTertiary`) — 사소 / Low

The importance pill paints its background as a ~17% alpha tint of the priority color (`PriorityToTint`, alpha `0x2B`) with the saturated color as the label text (`PriorityToBrush`).

The 중요도 view groups every task into these four buckets in P1→P4 order, followed by a trailing **없음** bucket for tasks with no priority. Each bucket header is a muted gray, slightly-smaller-than-title caption (`{typography.bucket-header}`) so it reads clearly apart from the task titles beneath it. A bucket is shown only when it has rows.

### Semantic state
- **Success** (`{colors.success}` → `SystemFillColorSuccess`): the detail Save glyph.
- **Error** (`{colors.error}` → `SystemFillColorCritical`): the detail Close glyph, error `InfoBar`.

  > Semantic glyphs keep their color through hover/press — they are **never** covered by a gray fill; pressing only drops opacity to 0.6.

## Typography

The typeface is **Pretendard JP** (Korean-first). Because the static OTFs ship as separate families, **weight hierarchy switches family, not FontWeight**: Medium (`Pretendard JP Medium`, via `CueFontFamilyMedium`) and SemiBold (`Pretendard JP SemiBold`) are each their own family. Medium is the emphasis weight for important content that would otherwise read as Regular — list task titles and checklist item titles — sitting between Regular body text and SemiBold headers. `ContentControlThemeFontFamily` is overridden to Pretendard so templated controls (buttons, lists, inputs, nav) inherit it; plain `TextBlock`s inherit via the window root's `FontFamily`.

### Hierarchy

| Token | Family | Size | Use |
|---|---|---|---|
| `{typography.page-title}` | SemiBold | 28 | Page title (`CuePageTitleTextStyle`) |
| `{typography.detail-title}` | SemiBold | 27 | Detail-pane editable title |
| `{typography.section-header}` | SemiBold | 16 | Section headers — settings groups (`CueSectionHeaderTextStyle`) |
| `{typography.bucket-header}` | SemiBold | 13 | 중요도 view priority-bucket headers, muted gray (`CuePriorityGroupHeaderTextStyle`) |
| `{typography.row}` | Regular | 15 | Task-row title |
| `{typography.card-header}` | SemiBold | 14 | Detail card headers (`CueCardHeaderTextStyle`) |
| `{typography.row-sub}` | Regular | 14 | Checklist item rows |
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
- The TitleBar's **pane-toggle (hamburger) button** is restyled (`TitleBarPaneToggleButtonStyle` override) to a 34px button matching the app's icon buttons, sharing the sidebar's hover/press fills; its left margin is tuned so the centered glyph lands on the nav rail's natural icon column, aligned in both the open and compact (icon-only) pane states.
- Caption (min/max/close) buttons are drawn by the system, so XAML theming does not reach them. Their glyph colors and hover/press backgrounds are set in code and reapplied on every `ActualThemeChanged` (hover bg `#14000000` light / `#20FFFFFF` dark; pressed `#28000000` / `#40FFFFFF`).

### List page — `TaskListPage.xaml`
- Rows: page title + caption → (error `InfoBar`) → quick-add → list (+ detail panel). Body padding `20` (uniform on all sides).
- Two-column body: left list (flexible) + right detail panel (resizable, default 460px). When the detail closes, the list reclaims the width.
- The list takes **two forms**: a **flat list** (`ItemsRepeater`) and a **sectioned list** (`ListView`, section header + rows). The sectioned form is used only by the Priority (P1–P4) view (`IsPrioritySectioned`); every other list, the TaskGroup list included, is flat.

### Settings page — `SettingsPage.xaml`
- Body padding is the same uniform `20` as the list page; the page title (`CuePageTitleTextStyle`) and the nav/content grid share the `4` inner inset so the screen lines up with the rest of the app.
- Two columns: a left **section nav** (`settings-nav`, fixed `{CueSettingsNavWidth}` = 200px) listing 시간 / 파싱 / 외관 / 알림, and a right **form column** holding one section's cards at a time (sections are visibility-toggled, with the shared fade+slide entrance — see "Motion").
- The section nav is a `ListView` whose item is **retemplated to read exactly like the main sidebar/list**: a calm subtle-fill hover/selection **pill** rounded to `{rounded.md}`, inset 4px so its corners float (icon + label, primary text/glyph on selection, no accent text), with a left **accent bar** marking the selected item. The fill fades on the same `83ms` `BrushTransition` as the main list, and a selected item **deepens** on hover/press (Secondary → Tertiary), following the cross-surface deepen rule. Items are 38px min-height with a small gap so each pill reads separately.
- The form column **fills the space beside the nav** (variable width, no cap), the same way the main content area fills the space beside the sidebar — a wider window gives the cards more room rather than leaving the right side empty. The per-row measure is held by the row itself, not a column cap: the label column is width-capped (`{CueSettingsLabelMaxWidth}`) so captions wrap, and the trailing control is a fixed width flush right, so a wide card simply widens the gap between label and control.
- A settings **row** (`settings-row`) is `[label + caption *] [control Auto, flush right]`: the label/caption column is width-capped (`{CueSettingsLabelMaxWidth}`) so captions wrap at a comfortable measure, and the trailing control sits at a shared fixed width (`{CueSettingsControlWidth}`) so controls align across rows. Rows are 52px min-height, separated inside a card by full-bleed `DividerStrokeColorDefault` dividers.
- Controls consume the shared tokens: combos/inputs round to `{rounded.sm}` (4px, inner ≤ the 8px card), the toggle is stripped of its On/Off content and right-aligned, and list rows (custom date meanings) carry the row-sub font with an even vertical rhythm.
- Cards, typography (`CueSectionHeaderTextStyle` / `CueCardHeaderTextStyle` / `SettingsCaptionStyle` = `MetadataTextStyle`), and strokes all match the rest of the app — the settings screen carries no bespoke look. There is **no accent-color customization**: Cue consumes the system accent token directly (see "Restrained accent"), so there is no app-level accent override and no swatch picker.
- A **back-arrow** button sits immediately left of the 설정 title (`CueIconButtonStyle`); it returns to the view shown before Settings was opened (navigation is flat, so it re-selects the remembered nav item rather than walking a Frame back stack). The arrow is a line-art `Path` (Segoe Fluent's back glyph has no bold weight), nudged up via `RenderTransform` for optical centering against the SemiBold title.
- **Restyled stock controls must `BasedOn` the framework default.** The settings combo / toggle styles set `BasedOn="{StaticResource DefaultComboBoxStyle}"` / `DefaultToggleSwitchStyle`. An explicit `Style` with no `BasedOn` replaces the default wholesale and strips the WinUI 3 template (rounded dropdown popup, Fluent toggle + hover states), leaving a flat Windows-10-looking control.
- **Stacked-row card padding exception.** A card holding multiple rows separated by full-bleed dividers (the 시간 card) sets vertical padding to **0** and gives each row its own vertical padding; the divider splits the inter-row gap into two halves, so a non-zero card padding would make the top/bottom edges read as double the inter-item gap. With card vertical padding 0, every visible gap equals the row padding (22, matching the single-row cards' centered inset).

### Detail panel
- Radius 12, no shadow, 1px `CardStrokeColorDefault`, `InnerBorderEdge`, slides in and slides out on close (see "Motion").
- A vertical stack of cards (radius 8, 1px stroke, no shadow): task info (notes · importance · group) / **일시 (the single When, + optional time / 종일)** / tags / checklist. The date card is titled **일시** (date + time); a date added with no explicit time defaults to 종일 (all-day). In the one-column (compact) layout the time dropdowns stretch to the card width instead of staying fixed-width.
- **Resizable.** A 10px transparent grab strip on the panel's left edge drag-resizes it. Width is clamped to 320–680px (absolute min 260px) and further capped so the primary list keeps ≥340px — the panel never starves the list. On hover or while dragging, the strip reveals a slim vertical pill handle (4×58, radius 2, tertiary text brush at ~72% opacity) with the standard 83ms opacity transition.
- **Responsive.** Below a compact width (~390px), paired side-by-side fields (importance + group, date + time) reflow to stack vertically so nothing is squeezed.
- **Conditional text fade for clipped content** (see "Elevation & Depth"): only overflowing inline text inside padded content, such as long tag names, fades at the right edge instead of hard-clipping. The panel scroll body itself does not get a bottom fade because it clips at the panel boundary.

### Spacing
The page rhythm is `CuePadPage` body padding (uniform `20` on all sides) and `CuePadCard` (`16`) card internal padding. Spacing is tokenized: gaps consume the `CueGap*` scale (2/4/8/12/16/20/24) and padding/margin consume the `CuePad*` Thickness tokens, both from `Styles/DesignTokens.xaml`. Off-scale literals were snapped to the nearest rung. Structural/optical exceptions stay inline — negative full-bleed margins, the quick-add omnibar's optical padding, empty-state centering offsets, the priority-pill inset, the detail-card margin *rhythm* (per-axis, see Components), and sub-2px nudges; the `CueNav*` offsets are optical corrections, not part of the scale (see "Known Gaps").

## Elevation & Depth

Separation is carried by **stroke and tone, not shadow.**

| Level | Treatment | Use |
|---|---|---|
| In-flow | No shadow, 1px stroke | Cards, list rows, detail panel |
| Pseudo-float | No shadow, gradient stroke | Quick-add (`CircleElevationBorderBrush`) |
| True overlay | Elevation / shadow | Flyouts, popups, menus |

- In-flow surfaces use **zero shadow**. A 1px `CardStrokeColorDefault` (cards) or `DividerStrokeColorDefault` (inner dividers) does the separating. Stroked cards set `BackgroundSizing="InnerBorderEdge"` so the 1px sits inside the radius.
- When something needs to feel lifted (the quick-add omnibar) it uses `CircleElevationBorderBrush` — a top-light / bottom-dark gradient stroke — for subtle dimension **instead of** a drop shadow.
- Shadows belong only to true overlays (flyouts, popups).
- **Edge fades are conditional and local.** Use a short gradient only when content is clipped before it reaches an actual window/card/panel edge — for example, inline text ending inside padded content. Do not add fades where the container boundary already explains the clipping, such as the main list, sidebar, or detail-panel scroll body.
- The fade appears only when the text actually overflows. Short labels must render without a gradient overlay. Overflowing tag names fade at the right to `CardBackgroundFillColorDefault`. The opaque stop should arrive late enough to feel like the text disappears naturally, not like a translucent veil over readable text.

## Shapes

### Radius scale
Use only `{rounded.sm}` (4) / `{rounded.md}` (8) / `{rounded.lg}` (12) plus pills. Arbitrary radii are forbidden. Nested radii align to `inner ≤ outer − padding`.

| Token | Value | Use |
|---|---|---|
| `{rounded.sm}` | 4px | Buttons, checks, checklist rows, small surfaces |
| `{rounded.md}` | 8px | Task rows, detail inner cards |
| `{rounded.lg}` | 12px | Detail panel |
| `{rounded.chip}` | 14px | Group / tag / priority chips on a task row (full pill at chip height) |
| `{rounded.pill}` | height/2 | Pills |

Pill instances are explicit half-height radii: chips (group/tag/priority) `{rounded.chip}` (14), quick-add `24`, today marker circle `14`.

### Focus & stroke
- System focus visuals by default.
- Text-input border thickness is flattened to 1px in every state (`TextControlBorderThemeThickness(Focused)=1`) with the **bottom accent bar removed**; on focus the border color shifts to the accent instead.
- Where a selectable row suppresses the focus rectangle, focus is re-expressed via background/stroke.

## Components

### Task row
- Layout: `[circular check] [title … priority pill] … [group / tag chips]`. Three columns (check Auto · title+meta star · trailing chips Auto), `{gap.lg}` (16) apart, with a one-line schedule row below the title. The trailing chips are **right-aligned and stacked vertically** (group on top, then each tag) in the wide layout; the schedule shows the date, plus the time (e.g. `오후 3:00`) for a task with a specific time, while an all-day (종일) task shows the date alone.
- **Group and tag are both rendered as squircle chips** (rounded rectangles, radius `{rounded.chip}` = 8, a 12px glyph + 13px name), so the two read as one consistent family rather than two ad-hoc layouts. A fixed squircle radius (not a half-height pill) keeps a short chip — icon + 2 chars — reading as a clean rounded rect; a pill radius would make the rounded ends meet on a narrow chip and look like an oval.
- **All chip/pill elements on a row share one fixed height** (`CueChipHeight` = 26) with **horizontal-only padding** (`CuePadChip` = `9,0`) and content vertically centered. The group chip, every tag chip, and the priority pill therefore line up exactly and never distort, regardless of glyph vs. text or differing font sizes — height is purely the token, not a function of content. (`VerticalAlignment="Center"` on each chip keeps it from being stretched by a taller sibling.) The **group chip** is a neutral gray pill (`CueChipNeutralFillBrush`, a theme-split overlay) with the group's own glyph + name in secondary text. A **tag chip** is tinted with the tag's *own* color (`HexToTint`, ~14%) and carries the tag glyph (`E8EC`) + name in that saturated color (`HexToBrush`). Each chunk hides itself when the task has no group / no tags.
- A **repeating task** carries a small repeat glyph (Segoe Fluent `RepeatAll`, `E8EE`, secondary text tone) in the schedule row, after the date. It is a quiet informational mark — no accent, no fill — mirroring the recurrence flag the index derives from the task's rule (the rule itself stays in the file).
- Selected → a **persistent row fill in the hover tone** (`{colors.hover-fill}`) spanning the row + checklist, so the open task reads as selected even when the pointer is elsewhere — the highlight area alone carries the selection, with **no separate accent bar/pill**. Background hover transitions over 83ms. Radius `{rounded.md}`.
- Rows are **uniform height regardless of content**: a `MinHeight` of 60 with vertically-centered content means a bare title row keeps the same presence as one with a schedule and chips, and the title centers when there is no second line. Generous padding (`16,12`) and a SemiBold title (`{typography.list-title}`, 16.5) over a 13px meta line sharpen the hierarchy. **No row separators** — rows are divided by whitespace (the per-row margin) alone, not lines.
- **Compact reflow:** when the list column is too narrow for the right-edge chips, the same group/tag chips drop to their own line **under the title** (horizontal, left-aligned), driven by `IsCompact` → `ShowRightMeta` / `ShowInlineMeta`.
- A task's checklist items render as an indented nested list under it, set in further from the task body (with a 1px vertical guide line) so they read as belonging to the task rather than as peers. Their presence is self-evident, so there is no "N items" caption. These rows reuse the same circular check and row-sub font; they are not tasks, so they cannot be dragged or carry a date/priority/group — just a checkbox and a title. The checkbox toggles the item in place; **tapping the rest of the row opens the parent task's detail** (a checklist item has no detail of its own). The parent's **hover and selection highlight spans the checklist rows too** — the highlight surface (hover fill, persistent selected fill) wraps the task row and its checklist as one region, so hovering or selecting the task lights up the whole group (the row's inner padding and the checklist indent are unchanged).

### Completion state
- Completing does not remove the row: it stays in place at **opacity 0.48** and sinks to the bottom of the list. This persists across views (the active query includes completed items, sorted last). Only the sidebar count badge counts open tasks.
- **Checklist items are independent of the task's completion.** Completing a task leaves its checklist as-is; an item's checked state is its own. A repeating task resets all its checklist items to unchecked when it rolls to the next cycle (the procedure repeats), while the cycle just finished keeps its ticked state on the Logbook copy.

### Priority pill
- Rendered **directly beside** the row title as a text pill (not a leading dot). The title is width-capped and truncates, so the pill hugs the title's trailing edge rather than being pushed to the far right. Labels: 매우 중요 / 중요 / 보통 / 사소.
- Background is a ~17% tint of the priority color; text is the saturated tone. Radius `{rounded.chip}` (a full pill, matching the group/tag chips beside it). Color mapping per the priority tokens; text via `PriorityToLabel`, tint via `PriorityToTint`, saturated color via `PriorityToBrush`.

### Circular completion check — `CueCircleCheckBoxStyle`
- 20×20. Unchecked = 1.6px outline circle (`TextFillColorTertiary`, → secondary on hover). Checked = accent fill + white check glyph + overshoot pop.
- For completion toggles only — never for multi-select / general checkboxes.

### Quick-add (omnibar)
- A floating pill (min height 48, radius 24). Background `ControlFillColorDefault` + `CircleElevationBorderBrush` for subtle dimension (not a shadow). Leading glyph and text are vertically centered.
- Lifts one tone on hover; gains an accent ring on focus.
- A natural-language date in the input fills the task's single **일시 (When)** date. A dateless line stays Unscheduled (lands in 언젠가 / Anytime), except on the Today list, which pins it to today.

### 일시 (When) card
- A task has a **single date** (When) — there is no separate start date or deadline. Before a date is set, a "+ 일시 추가" button holds the card; tapping it adds a date and reveals the editor (headed **일시**), with a "제거" button to clear it.
- The editor is one date picker plus an optional time (hour : minute dropdowns). A "종일" (All day) checkbox marks the date as all-day — carried as an explicit flag on the When (`ScheduledWhen.AllDay`), not a sentinel time — which hides the time; a date added with no explicit time defaults to 종일 until the user unchecks it to set a time.
- In the one-column (compact) layout the time dropdowns stretch to the card width instead of staying fixed-width.
- **반복 (Recurrence)** lives in this card as a plain labeled field (a `반복` label like 중요도 / 그룹 — not a card header, no decorative glyph) below the date editor, and is shown **only when a concrete 일시 is set** — a recurrence with no date to anchor it doesn't read as meaningful, so removing the date (제거) hides the field and clears the rule (`ClearWhen`). The picker offers common presets (반복 안 함 / 매일 / 매주 / 평일 (월–금) / 매월 / 매년) whose RRULEs match what the parser emits; a loaded rule with no preset (e.g. a parsed 격주 / 특정 요일 rule) appears as its own summarized entry so it round-trips. Editing recurrence stays a domain concern — the view model only holds the RRULE string and builds the `RecurrenceRule` (with an anchor) on save; RRULE *evaluation* stays in the storage layer (invariant 9).

### Tags
- The detail tag card is a **checkbox-free list**. Tapping a row confirms selection with a trailing check (color glyph + name + trailing check).
- A "태그 없음" (No tags) item at the top makes the untagged state explicit (checked by default); it is mutually exclusive with real tags and is not written as a tag id.

### Inputs
- Flat 1px boxes with no bottom accent bar. Theme-split tone (Dark = recessed well). On focus only the border turns accent.
- **One consistent interaction recipe, app-wide** (the floating quick-add is the one exception — it keeps its elevated pill look). The accent focus border is **global** (`TextControlBorderBrushFocused` → `AccentFillColorDefault`), so every text input — notes, inline new-tag, settings fields, sidebar inline group/tag create+rename — shows the same accent focus ring in both themes, not just the quick-add. Rest/hover keep the flat 1px `ControlStrokeColorDefault`; hover lifts the fill one tone.
- **Inline-editable text.** Checklist item titles are borderless and transparent at rest so they read as content, but adopt the **same fill + border on hover/focus** as the boxed inputs (a transparent rest border that resolves to `ControlStroke` on hover and the accent on focus) — so editing them matches every other field. The **detail-panel title is the one heading exception**: it stays fully borderless in every state (its accent focus border is suppressed locally) so it reads as a title rather than a field.
- **Enter commits inline edits.** The detail title and checklist item titles save immediately on Enter, not only on focus-out — matching the sidebar inline group/tag create + rename and the inline new-tag field, which already commit on Enter.

### Sidebar / navigation
- Stock `NavigationView` + thin override. Selected text is flattened to `TextFillColorPrimary` (calm, not accent); selection reads from the fill + the stock left accent pill.
- Fixed items, in order: 모든 할 일 (All) · 오늘 할 일 (Today) · 앞으로 할 일 (Upcoming) · 언젠가 할 일 (Anytime) · 완료한 일 (Logbook) · 중요도 (Priority). Below them: **그룹 (TaskGroup)** and **태그 (Tag)** sections. **앞으로 할 일 (Upcoming)** and **언젠가 할 일 (Anytime)** start hidden by default (shown via the right-click show/hide menu).
- Group/Tag headers are 12 SemiBold `TextFillColorTertiary` (quiet hierarchy). The **new group / new tag action is a `+` icon button on the section header**, immediately left of the expand/collapse chevron — not a list row. Its glyph is the stock Add (`E710`) sized to match the sibling nav glyphs (15) — Segoe Fluent Icons has **no weight variants**, so an icon's visual weight comes from its render size, not a heavier glyph.
- Each section **ends with** a **그룹 없음 / 태그 없음** catch-all row (always sorted last, below the real groups/tags) for unfiled items, marked with a **faded** (`CueNavUnfiledIconOpacity` = 0.4) copy of the real group/tag glyph rather than a distinct funnel icon — it reads as the section's empty bucket, not a different kind of filter. A **newly created** group/tag is inserted at the **top** of its section (`PrependRank`), above the existing rows.
- **Creating and renaming are both inline.** The `+` action opens an inline name field at the foot of the section; renaming (via the row's context menu) swaps the row's label for the same inline editor in place — neither uses a modal dialog. Enter or blur-with-text commits, Escape or blur-empty cancels.
- The per-group / per-tag open-task count reads as a **plain number** at the end of the row (`TextFillColorTertiary`, no pill/circle background) — a quiet count, not an alert badge. It is implemented as a WinUI `InfoBadge` deliberately re-templated (`CueCountInfoBadgeStyle`: transparent background + a tertiary `TextBlock`) so it shows the digit alone rather than the default notification-pill look. The number is inset from the row's right edge (`CueNavBadgeRight`) so it doesn't hug it.
- Top-level rows start from the stock 4px hover/selection pill gutter (`CueNavPillLeft`); rows **nested** under a Groups/Tags section indent deeper (`CueNavChildPillLeft` = 12) so the hierarchy reads at a glance. The right gutter has a 1px optical compensation against the content separator. The `CueNav*` values in `DesignTokens.xaml` set the pill / label offsets directly on the stock presenter parts (`LayoutRoot` / `ContentGrid` / `ContentPresenter` / `PresenterContentRootGrid`) over the NavigationView's nesting indent — optical corrections, not page spacing tokens. **The icon, highlight, and accent bar are pinned to the collapsed (compact) geometry — a zero left inset on `PresenterContentRootGrid` / `ContentGrid` — in both pane states, so they sit the same distance from the window's left edge and don't shift when the pane toggles open/closed.** Right-edge spacing is tokenized the same way: the count badge inset (`CueNavBadgeRight`) and the chevron's gap from the edge (`CueNavChevronRight`, layered on top of the framework's −14 flush margin so the token reads directly as px-from-edge, 0 = flush).
- **Hover deepens selection, everywhere.** Hovering an already-selected row darkens it (selected + hover stacks toward `{colors.pressed-fill}`), matching the main list — the stock NavigationView's lighter selected-hover is overridden per theme so the two surfaces behave the same.
- **Glyph click = instant picker.** Clicking a group glyph opens the icon picker; clicking a tag glyph opens the color picker — directly, no depth (right-click context menu is the fallback).
- **Sidebar right-click = show/hide menu.** A checkable list toggles the fixed views (오늘 / 앞으로 / 언젠가 / 완료 / 중요도) on and off (name left, accent check right). Saved to app-local settings. "모든 할 일" is always shown.
- **Delete key deletes the focused group/tag**, the same way it deletes a focused task row — through the anchored confirm popover (not the right-click menu's only path). A tag asks a one-line confirm; a group, which must decide its tasks' fate, asks a two-action popover (그룹만 제거 / 할 일까지 삭제), with the task-deleting action in the red destructive tone and 그룹만 제거 as the focused default.

### Selection popup (icon / color)
- Icon/color only, no names; a 4-column grid flyout anchored to the nav item.
- **The current selection is ringed** (accent ring for icons, high-contrast ring for colors).
- Color swatches only **brighten slightly** on hover (white-blend) rather than being covered by a theme fill; the ring persists through hover/press.
- Group icons are chosen not to clash with the fixed sidebar glyphs; the star uses an outline glyph (tonally matched to the other outline icons).

### Dialogs / inline buttons
- Inline secondary actions (rename / delete / + add) share one style: transparent background + subtle hover + secondary text (`CueSubtleTextButtonStyle`). At most one true primary is emphasized per context.
- The shared icon button is `CueIconButtonStyle` (34×34 `HyperlinkButton`, transparent at rest, semantic meaning via glyph color only).
- **Confirm popover** (`ConfirmPopover` + `CueConfirmPopoverPresenterStyle`): one-line confirmations (e.g. 삭제) use a light `Flyout` anchored to the row/button that triggered them, not a centered `ContentDialog`. Layout is a wrapped message over a right-aligned `취소`(subtle) + one-or-more affirmative buttons; the focused default or Enter confirms, while the cancel button, Esc, or a click outside dismisses to "no". `ShowChoiceAsync` extends this to a few affirmative actions (e.g. a group delete's 그룹만 제거 / 할 일까지 삭제) that resolve by index, each able to opt into the destructive tone independently. A destructive button reuses `AccentButtonStyle` recolored to `CueDangerFillBrush` (red fill, on-accent text, stock hover/press) — the recolor overrides the `AccentButtonBackground*` keys in that button's own scope, since the stock template aliases them as `StaticResource` (overriding `AccentFillColor*` would not reach them). Chrome + rhythm are tokens (`CuePadPopover`, `CuePopoverMinWidth/MaxWidth`, `{rounded.md}`). Reserve a `ContentDialog` for choices that genuinely need a modal (blocking, or more than a couple of options).

### Scrollbars
- **Auto-hide + tuned thumb** (`Behaviors/ScrollBarAutoHide`): scrollbars stay hidden at rest, fade in on scroll or hover, and fade back out ~1s after the last activity — independent of the OS "auto-hide scrollbars" setting (which otherwise leaves them permanently visible). The visual thumb is a quieter 4px globally (`ScrollBarVerticalThumbMinWidth`, `ScrollBarHorizontalThumbMinHeight`) with theme-token color plus opacity, so light/dark stay balanced. By default the behavior reserves a 12px right/bottom gutter in `ScrollViewer.Padding`, so the bar appears in padding instead of overlaying content. NavigationView disables that gutter with `ScrollBarAutoHide.ReserveGutter="False"` because its internal menu scroller otherwise creates an oversized right sidebar gap. Applied as an attached behavior (`behaviors:ScrollBarAutoHide.IsEnabled="True"`) on each scroll surface — and on hosts like `ListView`/`NavigationView` to reach their inner scrollers — rather than by retemplating `ScrollBar` (the stock template ships as compiled XBF). Fade timing is motion, kept in the behavior; thickness and color are tokens.

### Settings form
- **Section nav** (`settings-nav`): a `ListView` styled to match the sidebar — a `{rounded.md}` subtle-fill pill on hover/selection, primary text, icon + label. Fixed width `{CueSettingsNavWidth}`.
- **Form column** (`settings-content`): fills the space beside the nav (variable, no cap), like the main content area beside the sidebar. The per-row measure is held by the width-capped label column + fixed-width control, not a column cap, so captions still wrap rather than over-widening.
- **Row** (`settings-row`): `[label + caption *][control flush right]`. The label column caps at `{CueSettingsLabelMaxWidth}` (captions wrap); the control sits at `{CueSettingsControlWidth}` and rounds to `{rounded.sm}` (4px inside the 8px card). Dimensions are tokens in `DesignTokens.xaml` (`CueSettings*`), not inline literals.

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
- **Domain term mapping:** TaskGroup → **그룹 (Group)**, Tag → **태그 (Tag)**, Priority → **중요도 (Importance)** (P1–P4 = 매우 중요 / 중요 / 보통 / 사소), ChecklistItem → **체크리스트 항목 (Checklist item)** (an embedded title + check on a 할 일 — no memo), the task's single date → **일시 (When)** (the detail card is titled 일시; the picker placeholder reads 날짜 선택), Recurrence → **반복 (Repeat)** (presets: 반복 안 함 / 매일 / 매주 / 평일 / 매월 / 매년). (There is no separate deadline; a task has one date.)
- **Time views:** 오늘 할 일 (Today) / 앞으로 할 일 (Upcoming) / 언젠가 할 일 (Anytime) / 완료한 일 (Logbook).
- Drop redundant labels (e.g. omit self-evident card titles).

## Adding a New Element — Checklist

1. **Tokens first.** Before inventing a color/radius/font/timing/spacing, check for a fit in the existing tokens — gaps → `CueGap*`, padding → `CuePad*`, radius → `CueRadius*`, type → `CueFont*`. If none fits, add a token and consume that (no literals); only documented structural/optical values stay inline.
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

- **Inline spacing exceptions.** Gaps and padding are tokenized (`CueGap*` / `CuePad*`), but a `Thickness` resource is a fixed 4-tuple, so values that vary per axis or are purely optical stay inline by design: negative full-bleed margins, the quick-add omnibar's optical padding, empty-state centering offsets, the priority-pill inset, the detail-card margin rhythm, and sub-2px nudges. The `CueNav*` offsets also sit outside the scale — they correct for the NavigationView's nesting indent (optical tuning, not rhythm). One framework gotcha they ride on: the expand/collapse chevron is a 40px grid centering a 12px glyph, which the stock template pulls flush with a −14 right margin (`NavigationViewItemExpandChevronMargin`); any override of that margin must keep the −14 base or the chevron drifts ~14px off the edge.
- **Pretendard JP** ships as static OTFs, so weight hierarchy is family-switched (Regular / Medium / SemiBold) rather than `FontWeight`-driven. The frontmatter encodes the semantic weight (400/500/600) for non-WinUI export.
- **Caption (window) buttons** are system-drawn and themed in code-behind (`ApplyCaptionButtonColors`), reapplied on `ActualThemeChanged` — they are not reachable as XAML tokens.
- The literal colors in the system are intentionally theme-scoped in `ThemeDictionaries`: the Dark text-input **well** (`#18000000` / `#24000000`), and the **Dark danger red** (`#D13438`, `CueDangerFillBrush`) — the system critical color reads as a washed-out pink on dark surfaces, so the destructive delete/confirm button uses this literal red instead (with forced white label text); Light keeps the system critical red.
- Color tokens in this document are **aliases** of WinUI theme tokens, not literal hex; they cannot be WCAG-contrast-checked statically because they resolve to different values per theme.
