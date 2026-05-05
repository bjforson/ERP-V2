# Runbook 12 — Comms-gateway settings

> Sprint 35 / B8.2 — comms-gateway settings live as plain
> tenant-settings rows. v1 had a separate `NickComms.Gateway` service;
> v2 reuses the existing email infrastructure (Sprint 21) plus the
> generic per-tenant key/value table introduced in Sprint 35.

## Summary

There is no separate "comms gateway" project in v2. Instead:

- The email transport is configured at the host via `Email:Provider`
  (filesystem outbox in dev, noop until opted in elsewhere — see
  `platform/NickERP.Platform.Email`).
- Per-tenant overrides (SMTP host, from-address, reply-to, bounce
  webhook) live in `tenancy.tenant_settings` and are read via
  `ITenantSettingsService.GetAsync`.
- The admin UI for editing them is the portal page at
  `/admin/tenant-settings`.

## Vendor-neutral key catalogue

All keys are lowercase + dot-separated. The portal admin page
surfaces these under the **Comms gateway** option group.

| Key | Type | Default | Notes |
| --- | --- | --- | --- |
| `comms.email.smtp_host` | string | (host) | SMTP host name. Used when `Email:Provider=smtp`. |
| `comms.email.smtp_port` | int | (host) | TCP port. Typically 587 for STARTTLS, 465 for implicit TLS. |
| `comms.email.from_address` | string | (host) | From-address for user-facing email. |
| `comms.email.reply_to_address` | string | (none) | Optional Reply-To header. |
| `comms.email.bounce_handler_url` | string | (none) | Webhook the SMTP relay POSTs delivery failures to. |

Other comms domains (SMS, in-app push, third-party webhook fan-out)
are not built today. When they land, document them here under the
`comms.<channel>.*` key prefix and add catalogue entries to
`apps/portal/Components/Pages/TenantSettings.razor`.

## How callers read settings

Callers depend on `ITenantSettingsService` (registered in the portal
host) and pass an explicit default:

```csharp
public sealed class EmailSenderConfig
{
    private readonly ITenantSettingsService _settings;

    public async Task<EmailEndpoint> ResolveAsync(long tenantId, CancellationToken ct)
    {
        var host = await _settings.GetAsync("comms.email.smtp_host", tenantId, defaultValue: "localhost", ct);
        var port = await _settings.GetIntAsync("comms.email.smtp_port", tenantId, defaultValue: 587, ct);
        var from = await _settings.GetAsync("comms.email.from_address", tenantId, defaultValue: "noreply@nickerp.local", ct);
        return new EmailEndpoint(host, port, from);
    }
}
```

Setting key normalisation: keys are trimmed + lowercased on read and
write, so the catalogue key is the canonical string callers should
use.

## Audit trail

Every `SetAsync` emits a `nickerp.tenancy.setting_changed` event into
`audit.events`. Payload:

```json
{
  "tenantId": 1,
  "settingKey": "comms.email.smtp_host",
  "value": "smtp.example.com",
  "oldValue": "smtp.old.example.com",
  "userId": "00000000-0000-0000-0000-000000000000"
}
```

Audit emission is best-effort — the upsert is the system-of-record
write. If you suspect a silent audit-emit failure (event never lands)
check the host log for "Failed to emit nickerp.tenancy.setting_changed".

## Rotation guidance

- **Rotating SMTP credentials** — credentials never live in
  `tenant_settings`. Keep them in the host's `Email:Smtp` config
  section (`appsettings.json` or env var). Rotate via standard
  config-redeploy.
- **Migrating tenants between SMTP relays** — change
  `comms.email.smtp_host` for the affected tenant rows. The host
  picks up the new value on next read (no restart).
- **Disabling email for a tenant** — set
  `comms.email.smtp_host = "disabled"` and have the email sender check
  the value before opening a connection. (Not built today; document
  the convention here so it stays vendor-neutral when it lands.)
