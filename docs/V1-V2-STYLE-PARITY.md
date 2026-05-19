# v1 → v2 style parity (pilot cutover)

> **Status.** Sprint 60 — design tokens in v2 realigned to v1 NSCIM
> `ICUMSTheme.PaletteLight` + `enhanced-theme.css` so users moving from v1
> to v2 at the pilot site see the same Government-Executive blue, the same
> dark Domiex left rail, and the same Ghana flag accents.
> **Scope.** Tokens only + the inspection sidenav. v2 layout shapes
> (Razor + scoped CSS) are NOT being replaced by MudBlazor.

## 1. Why

The pilot cutover at Tema swaps the active URL/host but keeps the same
analysts in front of the same monitors. If the v2 chrome looks
unfamiliar — different blue, light sidebar, different button shapes —
operators stall on "is this the right app?" instead of acting on the
case in front of them. The token swap removes the visual delta without
giving up v2's lighter component-level architecture.

## 2. Source of truth

- v1 ICUMSTheme:
  [`C:/Shared/NSCIM_PRODUCTION/src/NickScanWebApp.New/Theme/ICUMSTheme.cs`](../../NSCIM_PRODUCTION/src/NickScanWebApp.New/Theme/ICUMSTheme.cs)
- v1 enhanced-theme:
  [`C:/Shared/NSCIM_PRODUCTION/src/NickScanWebApp.New/wwwroot/css/enhanced-theme.css`](../../NSCIM_PRODUCTION/src/NickScanWebApp.New/wwwroot/css/enhanced-theme.css)
- v1 custom branding (Ghana, gradients):
  [`C:/Shared/NSCIM_PRODUCTION/src/NickScanWebApp.New/wwwroot/css/custom.css`](../../NSCIM_PRODUCTION/src/NickScanWebApp.New/wwwroot/css/custom.css)
- v2 tokens (target):
  [`platform/NickERP.Platform.Web.Shared/wwwroot/tokens.css`](../platform/NickERP.Platform.Web.Shared/wwwroot/tokens.css)

Per `PLAN.md` §4 standing rule, v1 stays read-only — we ported values,
not files.

## 3. Palette mapping

| Token | Old v2 value | New v2 value | v1 source |
|---|---|---|---|
| `--nickerp-color-primary` | `#1d4ed8` (indigo-700) | `#2563EB` (blue-600) | `ICUMSTheme.PaletteLight.Primary` |
| `--nickerp-color-primary-hover` | `#1e40af` | `#1D4ED8` | `PrimaryDarken` |
| `--nickerp-color-primary-light` | _new_ | `#60A5FA` | `PrimaryLighten` |
| `--nickerp-color-accent` | `#06b6d4` (cyan-500) | `#334155` (slate-700) | `Secondary` — v1 has no cyan |
| `--nickerp-color-accent-hover` | `#0891b2` | `#1E293B` | `SecondaryDarken` |
| `--nickerp-color-tertiary` | _new_ | `#0369A1` | `Tertiary` (sky-700) |
| `--nickerp-color-bg-page` | `#f8fafc` (slate-50) | `#F1F5F9` (slate-100) | `Background` |
| `--nickerp-color-bg-topnav` | `#0f172a` (slate-900) | `#1B2537` (Domiex dark) | `DrawerBackground` |
| `--nickerp-color-bg-drawer` | _new_ alias | `#1B2537` | same |
| `--nickerp-color-text` | `#0f172a` (slate-900) | `#020617` (slate-950) | `TextPrimary` |
| `--nickerp-color-text-muted` | `#475569` (slate-600) | `#64748B` (slate-500) | `TextSecondary` |
| `--nickerp-color-text-on-drawer` | _new_ | `rgba(255,255,255,0.55)` | `DrawerText` |
| `--nickerp-color-warning` | `#ca8a04` (yellow-600) | `#D97706` (amber-600) | `Warning` |
| `--nickerp-color-info` | `#0284c7` (sky-600) | `#0369A1` (sky-700) | `Info` |

## 4. New tokens added

The following families are net-new on v2 — v1 has them, v2 didn't.

- **Ghana flag accents** — `--nickerp-color-gh-red` `#CE1126`,
  `--nickerp-color-gh-gold` `#FCD116`, `--nickerp-color-gh-green` `#006B3F`.
  Used in v1 for login splash, dashboard stripes, decorative icon
  backgrounds. Available on v2 pages that want a Ghana flourish.
- **Gradients** — `--nickerp-gradient-primary` /
  `-primary-hover` / `-success` / `-warning` / `-info` / `-danger`.
  Mirrors v1 `enhanced-theme.css`. Stat-card-modern-gradient and login
  splash use these.
- **Extended radius scale** — `--nickerp-radius-xl` (16 px),
  `--nickerp-radius-2xl` (24 px). v1 stat-card-modern uses 16 px.
- **Extended shadow scale** — `--nickerp-shadow-xl`, `-2xl`, `-inner`.
  v1 hover-elevation patterns use these.
- **Transitions** — `--nickerp-transition-fast` /`-base` / `-slow`
  with the v1 cubic-bezier curve `(0.4, 0, 0.2, 1)`.

## 5. Layout-level changes

- **Inspection sidenav** is now the Domiex dark drawer (background
  `var(--nickerp-color-bg-drawer)` = `#1B2537`, text
  `rgba(255,255,255,0.55)`, active state primary-blue background). This
  is the single most recognizable v1 visual signature; matching it
  means a v1 analyst lands on the v2 inspection page and immediately
  sees the layout they know. File:
  [`modules/inspection/src/NickERP.Inspection.Web/wwwroot/inspection.css`](../modules/inspection/src/NickERP.Inspection.Web/wwwroot/inspection.css).
- **SharedHeader / TopNav** inherit `--nickerp-color-bg-topnav` so they
  also pick up the Domiex dark hex. No component-level change needed.

## 6. NOT changed (deliberately)

- **MudBlazor not added.** v2 uses hand-rolled Razor + scoped CSS by
  design. Adding MudBlazor for pilot would add a multi-MB bundle, a
  licensing surface (MudBlazor is MIT — fine — but bundle size +
  binding-shape divergence isn't worth it), and a second component
  library to maintain. The visual parity goal is met by the token swap.
- **Shell shape.** v1 uses MudLayout/AppBar/Drawer; v2 uses a CSS grid
  with header + sidenav + main. We did not collapse v2 onto MudLayout.
- **Stat card / modern-card visual flourishes.** v1's
  `stat-card-modern` and `modern-card-gradient` patterns aren't ported.
  v2 pages that want them can build them against the new tokens — the
  ingredients (gradients, shadow-xl, radius-xl) are now available.
- **Dark mode.** v1 has a dark-mode toggle with a Security-Command palette.
  v2 doesn't have dark mode tokens yet; out of scope for cutover.

## 7. Verification

- Tokens still consumed by existing CSS — no broken `var(--...)`
  references. All names preserved; only values changed.
- Inspection sidenav legibility: white-on-`#1B2537` is contrast-ratio
  4.5+ for the active-link (white on primary-blue) and 3.5+ for the
  muted body text (rgba(255,255,255,0.55) on `#1B2537`). The
  `rgba(255,255,255,0.55)` is the same opacity v1 uses; if a future
  audit insists on AA-strict (4.5+ for body too), bump to 0.7.
- Build: `dotnet build NickERP.Tests.slnx` clean on the changed files
  (CSS isn't compiled; the Razor still references tokens by name).

## 8. Open follow-ups

- **Dark-mode parity** — port v1's PaletteDark (Security Command) into
  a v2 `@media (prefers-color-scheme: dark)` block. Post-pilot.
- **Stat-card components** — if pilot dashboards want v1-style stat
  cards, port `stat-card-modern` + `modern-card-gradient` as v2 scoped
  CSS components against the new tokens.
- **Login splash** — v1 has a gradient login background. v2 portal
  splash currently doesn't. If pilot users hit the v2 login page they
  may notice the absence. Sprint-after-pilot scope.
