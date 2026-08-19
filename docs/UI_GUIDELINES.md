# HinoGateway UI guidelines

The browser UI is an **operator HMI for an industrial PC**, not a marketing site. Every rule below
exists because of that: an operator glances at the screen from a metre away, in a control room that
may be bright or dark, and has to answer "is anything wrong?" before reading a single word.

Stack: Blazor WebAssembly + **MudBlazor 9.7**, hosted by `HinoGateway.Server`.
There is **no Node, no npm, no Tailwind, no SCSS** — and none is to be added. Everything is
`dotnet build` / `dotnet publish`.

Two files hold the entire design system:

| File | Holds |
| --- | --- |
| `src/HinoGateway.Web/Layout/MainLayout.razor` | the `MudTheme` (palettes, radius, typography) and the app shell |
| `src/HinoGateway.Web/wwwroot/css/app.css` | tokens and the industrial layer on top of MudBlazor |

---

## 1. Colour

### Theme palette (MudBlazor)

Defined once, at the bottom of `MainLayout.razor`. Dark is the default — a control room is usually
dim, and a white screen at night is a complaint waiting to happen. The choice is persisted in
`localStorage["hino-theme"]`.

| Role | Light | Dark |
| --- | --- | --- |
| Primary | `#1d4ed8` | `#3b82f6` |
| Background | `#eef1f6` | `#0b1220` |
| Surface | `#ffffff` | `#111c2f` |
| Text primary | `#0f172a` | `#e2e8f0` |
| Lines | `#d7dde6` | `#1e2c44` |

Everything else must consume the MudBlazor variables (`--mud-palette-surface`,
`--mud-palette-lines-default`, `--mud-palette-text-secondary`, …) so it follows both themes for free.

### State colours

Defined in `app.css` as the only custom colour tokens:

| Token | Value | Means |
| --- | --- | --- |
| `--hino-good` | `#22c55e` | quality Good, device connected, alarm normal |
| `--hino-warn` | `#f59e0b` | quality **Uncertain**, degraded, tag-level scan override |
| `--hino-bad` | `#ef4444` | quality Bad, disconnected, active alarm |
| `--hino-idle` | `#64748b` | no data yet, disabled, not applicable |

**Colour carries state, and nothing else.** Do not colour something to make it look nice, to brand
it, or to separate two neutral things. If a new screen needs a colour that is not on this list, the
answer is almost always a shade of the theme palette, not a new hue.

**Colour is never the only channel.** Every coloured element also carries text or a shape: the LED
sits next to the state word, the severity pill contains the severity name, the quality cell prints
`GOOD`/`BAD`. A colour-blind operator, or a screenshot printed in mono, must lose nothing.

---

## 2. Text: three axes, kept apart

Family, size and emphasis are three independent classes; compose them.

```html
<span class="mono txt-sm hino-dim">192.168.1.50:502</span>
```

| Axis | Classes |
| --- | --- |
| family | `.mono` — `--hino-mono` (Cascadia/Consolas/Roboto Mono). Every machine value: tag ids, addresses, numbers, timestamps, states. |
| size | `.txt-sm` (.80rem) for data cells · `.txt-xs` (.72rem) for a sub-line under a value. Nothing else. |
| emphasis | `.hino-dim` (.70) · `.hino-muted` (.55) · `.hino-faint` (.40, **icons only**) |

Never put text below `.55` opacity — it stops passing contrast in the light theme. `.hino-faint` is
for a decorative empty-state icon and nothing else (`.hino-empty-icon` already bundles it).

Do not write `Style="opacity:…"` or `Style="font-size:…"` on a component. That is how the UI drifted
into nine different "small grey" styles in the first place. Sizes that *are* layout — `min-width`,
`max-width`, `width` on a `MudTh` — stay inline; they belong to that one table, not to the system.

---

## 3. Page structure

Every routed page opens the same way:

```razor
@page "/example"

<PageTitle>HinoGateway — Example</PageTitle>

<div class="page-head">
    <h1>Example</h1>
    @* optional: live/offline chip, status chip *@
    <MudSpacer />
    @* optional: primary action button, or a meta counter *@
</div>
```

The `<h1>` is not decoration: `App.razor` does `<FocusOnNavigate Selector="h1" />`, so a page without
one leaves keyboard focus stranded after navigation. `.page-head` is flex — that is what makes
`MudSpacer` work.

Then, in order: alerts → KPI row → content.

| Block | Class | Notes |
| --- | --- | --- |
| KPI tiles | `.kpi-row` > `.kpi` + `.kpi-accent\|good\|warn\|bad\|neutral` | label / value / sub; the left border carries the state colour |
| Device cards | `.dev-row` > `.dev-card` (`.dev-head`, `.dev-stats`, `.dev-error`) | one card per troubled connection |
| Detail editor | `MudPaper` + `.hino-panel-accent` | the primary left bar means "this is the row you selected" |
| Empty state | `.alarm-empty` + `.hino-empty-icon` | icon, one line of what is missing, one line of what to do |
| Live table | `MudTable Dense` + `.hino-table` | tabular numerals so digits don't jitter as values update |

---

## 4. Data presentation

- **Values are monospace and right-aligned** (`.tag-value`). A column of numbers must line up on the
  decimal point; proportional digits make a changing value visibly twitch.
- **A changed value flashes** — `.tag-value.changed` paints a blue wash that fades over 1.2 s. It is
  the cheapest way to see which of 200 rows just moved.
- **LEDs** (`.led` + `.led-good|warn|bad|idle`) are 9 px dots with a soft glow; `.led-bad` blinks.
  Blinking is reserved for *bad* — if everything blinks, nothing does.
- **Severity** uses the filled pill `.sev-badge`; journal event types use the outlined `.evt-badge`,
  deliberately different so the two never compete in the same row.
- **Tables are `Dense`.** Operators want more rows on screen, not more air. Long lists get
  `Virtualize` + `FixedHeader`.

---

## 5. Time

Show **local time, never UTC** — the operator reads the wall clock behind them.

- Display: `DateTime.Now` / `.ToLocalTime()`.
- The UTC offset is printed once, next to the appbar clock (`UTC+07:00`), so a screenshot sent to
  head office is still unambiguous. Column headers do **not** carry a "(UTC)" suffix.
- Internal arithmetic — durations, query windows, retention — stays on `DateTimeOffset.UtcNow`.
  That is timezone-independent and correct; only the display layer converts.

---

## 6. Actions

**Put the action on the row that owns the data.** A write button on every tag row
(`Components/WriteValueDialog.razor`), an Ack button on every alarm row, edit/delete at the end of
each configuration row. Never park a "manual write" panel at the bottom of a long table — on a
1920×1080 screen with 200 tags, the operator never scrolls to it.

Anything that touches a device or the running engine confirms what it is about to do. The write
dialog restates tag id, name and current value before it accepts a new one.

---

## 7. Offline first

The gateway runs on an industrial PC with **no internet**. Therefore:

- No Google Fonts, no webfont, no CDN, no external stylesheet or script in `index.html`. MudBlazor's
  CSS/JS is served from `_content/`, self-contained, and it stays that way.
- No remote images or icon fonts. Icons come from `Icons.Material.Filled.*`, which MudBlazor inlines
  as SVG.
- Typography is the system stack (Segoe UI / system-ui / Roboto / Arial) plus the monospace stack.

---

## 8. Charts

There are no charts in the UI yet. When trend/historian screens arrive, use the `dataviz` skill in
Claude Code for palette, axes, legend and dark/light rules instead of inventing chart colours. Chart
series colours are *not* the state colours from §1 — green/amber/red must keep meaning good/warn/bad.

---

## Checklist before merging a new screen

- [ ] `<PageTitle>` and a single `<h1>` inside `.page-head`
- [ ] Reads correctly in **both** themes (toggle in the appbar) at 1920×1080
- [ ] Text contrast holds — nothing below `.55` opacity, no grey-on-grey
- [ ] No colour without a second channel (text, icon or shape)
- [ ] No new inline `opacity:` / `font-size:` — use `.hino-dim/muted`, `.txt-sm/xs`
- [ ] No new CSS class that is not defined in `app.css` (a ghost class silently renders as nothing)
- [ ] Timestamps are local time
- [ ] Row-level actions, not a panel below the fold
- [ ] Table is `Dense`; long lists `Virtualize`
- [ ] No new external asset, font or CDN reference
- [ ] `dotnet build` clean, `dotnet test` still green
