# NickERP.Platform.Web.Shared

> Status: A.6 shipped — `tokens.css`, `TopNav`, `UserMenu`, `NotificationBell`, `AppSwitcher`. Demo + cross-app federated search wiring on the backlog.
>
> See `ROADMAP.md §A.6`.

---

## What this layer does

A Razor Class Library every NickERP Blazor app references so the suite feels like one product:

- **`tokens.css`** — single source of truth for colors, typography, spacing, shadows. Sub-apps reference this in their host `<head>` and use `var(--nickerp-color-primary)` everywhere instead of literal values.
- **`TopNav`** — the dark header bar (brand on the left, search in the middle, app-switcher + notifications + user menu on the right). Composes the rest of the components but accepts overrides for any slot.
- **`UserMenu`** — circle avatar + dropdown showing the current user's display name, email, granted scopes, and a sign-out link (defaults to CF Access logout).
- **`NotificationBell`** — bell icon with an unread-count badge. Today shows a placeholder "inbox not wired yet" dropdown; lights up once the audit-events projection ships.
- **`AppSwitcher`** — 3×3 grid of NickERP sub-apps with one-click hops between hostnames. CF Access shares the SSO session so users never re-login.

## Sub-app integration

`Program.cs` of any consuming Blazor app:

```csharp
builder.Services.AddCascadingAuthenticationState();
```

`App.razor` `<head>`:

```html
<link rel="stylesheet" href="_content/NickERP.Platform.Web.Shared/tokens.css" />
```

`MainLayout.razor`:

```razor
@using NickERP.Platform.Web.Shared.Components

<TopNav AppName="NickHR" AppHomeHref="/" SearchEnabled="false" />

<main class="container">
    @Body
</main>
```

That's it. Identity (auth handler from `NickERP.Platform.Identity`) is already populating `HttpContext.User` with the claims the user menu reads.

## Customisation

`TopNav` accepts `RenderFragment` overrides for any slot — pass a real search component, a custom user menu, or a different app switcher when you need to:

```razor
<TopNav AppName="Inspection v2"
        SearchSlot="@searchAutocomplete"
        AppSwitcherSlot="@customSwitcher" />

@code {
    private RenderFragment searchAutocomplete = ...;
    private RenderFragment customSwitcher = ...;
}
```

`AppSwitcher.Apps` accepts a custom list when an app needs to add or remove entries (e.g. a customer-specific build).

## Naming conventions

CSS classes are prefixed `nickerp-` to avoid collisions with sub-app styles or third-party libraries. Razor components live under `NickERP.Platform.Web.Shared.Components`. CSS custom properties are prefixed `--nickerp-`.

## What's NOT here (deferred)

- **Federated search wiring.** `TopNav` exposes a search slot but no concrete autocomplete component yet — that needs the cross-app search API which lands when Portal v2 (Track B.2) is built.
- **Live notification stream.** `NotificationBell` reads from a future inbox endpoint; the AUDIT layer's events projection is the source. Wiring lands in B.2 too.
- **Demo Blazor host.** Right now this library is only validated by builds. A small `platform/demos/web-shared/` consuming app would prove the chrome end-to-end. Backlog.

## Related

- `IDENTITY.md` — claims the user menu reads (`nickerp:display_name`, `nickerp:email`, `nickerp:scope`).
- `AUDIT.md` — events that drive the notification bell's unread count.
- `ROADMAP.md §A.6` — task list and acceptance criteria.
