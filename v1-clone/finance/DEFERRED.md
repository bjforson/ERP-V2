# NickFinance — pinned items awaiting external credentials

> Status (2026-04-28): the NickFinance suite is feature-complete and
> deployed to the live `nickhr` database. **All adapter code for the
> external integrations now ships in the build.** Each integration uses
> a *routing* DI registration that activates the real adapter the moment
> its environment variable holds a non-`PLACEHOLDER` value — restart
> the service and live calls flow. No code change required to flip any
> integration on.
>
> The five rows below were previously pinned as "engineering can't
> proceed without credentials". That's no longer accurate: engineering
> shipped the adapters; only the credential rotation remains. The rows
> are kept as a runbook for the operators who will land each credential.
>
> Item #6 (PWA-offline) is a different shape — it's a multi-week WebAssembly
> port, not a credential swap. It is genuinely deferred until the border-site
> network reliability becomes a hard business requirement.

---

## 1. GRA e-VAT — adapter shipped, credential pending

**Status:** `HubtelEvatProvider` ships in `NickFinance.AR`. The DI
registration in `Program.cs` is a `RoutingEvatProvider` that uses
Hubtel when `NICKFINANCE_EVAT_API_KEY` is real, falls through to
`StubEvatProvider` (sandbox IRN) when the env var holds a placeholder.

**Activation steps when the contract lands:**

1. Pick the partner — Hubtel is wired by default; Persol or Blue Skies
   would be ~30 LoC to drop into `NickFinance.AR/<Partner>EvatProvider.cs`
   following the `HubtelEvatProvider` shape (HTTP client + Issue mapping).
2. Replace the placeholder env var with the real merchant key:
   ```powershell
   [Environment]::SetEnvironmentVariable('NICKFINANCE_EVAT_API_KEY', '<real>', 'Machine')
   ```
3. If using a non-default base URL or a non-Hubtel partner, set
   `NickFinance:Evat:BaseUrl` in `appsettings.json` (or via env var
   `NickFinance__Evat__BaseUrl`).
4. Restart `NickFinance_WebApp` Windows Service.
5. Acceptance: issue a sandbox invoice, confirm IRN comes back without
   the `SANDBOX-IRN-` prefix, GRA iTaPS view reflects the post.

---

## 2. MoMo disbursement — adapter shipped, gateway endpoint pending

**Status:** `NickCommsMomoChannel` ships in `NickFinance.PettyCash.Disbursement`.
The DI registration is conditional on `NICKCOMMS_API_KEY_NICKFINANCE`
being non-placeholder; `OfflineCashChannel` is the fallback (custodian
hands cash physically, journal posts immediately).

**Activation steps when NickComms ships `/api/disburse/momo`:**

1. Wait for NickComms team to deploy the endpoint and issue the per-app
   key. Verify reachability: `curl -H "X-Api-Key: <key>" https://comms.nickerp.local/api/disburse/momo`.
2. Replace the placeholder env var:
   ```powershell
   [Environment]::SetEnvironmentVariable('NICKCOMMS_API_KEY_NICKFINANCE', '<real>', 'Machine')
   ```
3. Optional: override `NickFinance:Momo:GatewayBaseUrl` if NickComms
   moves off `http://localhost:5220`.
4. Restart `NickFinance_WebApp`.
5. Acceptance: a voucher with `PayeeMomoNumber + PayeeMomoNetwork` set
   disburses to a sandbox MTN MoMo number, the rail reference returned
   by Hubtel lands on `voucher.disbursement_reference`.

---

## 3. Receipt OCR — adapter shipped, Azure subscription pending

**Status:** `AzureFormRecognizerOcrEngine` ships in
`NickFinance.PettyCash.Receipts`. Implemented as a thin REST client over
Document Intelligence's 2024-11-30 API (no SDK dependency, keeps the
image lean). The DI registration is a `RoutingOcrEngine` that activates
on a non-placeholder `AZURE_DOCUMENT_INTELLIGENCE_KEY`.

**Activation steps when the Azure subscription lands:**

1. Provision Azure AI Document Intelligence (S0 tier covers the
   ~2,000 receipts/month projection).
2. Replace placeholder env vars:
   ```powershell
   [Environment]::SetEnvironmentVariable('AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT', '<your-resource-endpoint>', 'Machine')
   [Environment]::SetEnvironmentVariable('AZURE_DOCUMENT_INTELLIGENCE_KEY',      '<primary-key>', 'Machine')
   ```
3. Optional: override `NickFinance:Ocr:Endpoint` in `appsettings.json`.
4. Restart `NickFinance_WebApp`.
5. Acceptance: uploading a Ghana-format receipt photo populates
   `OcrAmountMinor` and `OcrDate` with ≥80% confidence, dashboard
   "Receipt OCR" pinned-item banner clears.

---

## 4. Production rollout against the live `nickhr` database — DONE 2026-04-28

Cleared. Bootstrap CLI ran clean against the live `nickhr` database
(10/10 steps green). WebApp listens on `http://localhost:5500`, public
URL `https://finance.nickscan.net` gated by Cloudflare Access (Email-OTP,
`@nickscan.com`). Windows Service `NickFinance_WebApp` runs as
LocalSystem, AutoStart, with recovery actions configured. Identity
retrofit (Track C.2) deployed: `Cf-Access-Jwt-Assertion` validated
against CF's JWKS, claims map deterministically to the audit columns.
Smoke runner ships in the bootstrap CLI for re-verification on every
deploy: `NickFinance.Database.Bootstrap --conn "$conn" --skip-migrations --smoke-test`.

---

## 5. WhatsApp approval flow — adapter shipped, Meta credentials pending

**Status:** `IApprovalNotifier` interface + `WhatsAppApprovalNotifier`
implementation ship in `NickFinance.PettyCash.Approvals`. Uses Meta's
WhatsApp Business Cloud API (`graph.facebook.com/v20.0/.../messages`)
with template messages; default template is `nickerp_voucher_approval`.
The DI registration is a `RoutingApprovalNotifier` — activates when
`WHATSAPP_CLOUD_API_TOKEN` is non-placeholder.

**Activation steps when Meta Business assets land:**

1. Complete Meta Business Verification for Nick TC-Scan Ltd.
2. Provision a WhatsApp Cloud API phone number; capture the phone-number-id.
3. Submit and get approval for the `nickerp_voucher_approval` template
   in Meta Business Manager. Three body parameters (voucher_no,
   amount_with_currency, purpose).
4. Replace placeholder env vars:
   ```powershell
   [Environment]::SetEnvironmentVariable('WHATSAPP_CLOUD_API_TOKEN',  '<bearer>', 'Machine')
   [Environment]::SetEnvironmentVariable('WHATSAPP_PHONE_NUMBER_ID', '<id>',     'Machine')
   ```
   - Webhook URL for Meta: `https://finance.nickscan.net/api/whatsapp/webhook`
   - Also set `WHATSAPP_WEBHOOK_SECRET` (HMAC validation, any 32+ char random string) and `WHATSAPP_WEBHOOK_VERIFY_TOKEN` (any string you also paste in the Meta dashboard during webhook setup)
5. Optional: override `NickFinance:WhatsApp:TemplateName` and
   `:LanguageCode` in `appsettings.json`.
6. Restart `NickFinance_WebApp`.
7. _(done 2026-04-28: engine fires `IApprovalNotifier.NotifyAsync` for vouchers ≥ `NickFinance:WhatsApp:NotifyThresholdMinor`)_
8. Acceptance: CFO receives a WhatsApp template message for any voucher
   above the policy-defined threshold, can tap Approve in the chat,
   webhook handler maps the reply back to `IPettyCashService.ApproveVoucherAsync`.

---

## 6. Mobile / PWA offline mode (border sites) — pinned by design

**Pin:** Aflao + Paga + Elubo lose Starlink frequently. The Blazor
Server UI requires a live SignalR connection, so it goes down with the
network. A true offline-first PWA needs Blazor WebAssembly with
IndexedDB-backed queueing — that's a multi-week port (server-side DI /
DbContext don't transfer to WASM directly, the auth path needs reworking
for token-bearer, the disbursement service needs a queue interface).

**Why not "ship adapter + flip env var" like the others:** because
there is no adapter — it's a different rendering host. The work needed
is a new project (`NickFinance.WebApp.Wasm`), a service worker, a
separate API surface (`NickFinance.WebApp.Api`), a JS-side IndexedDB
queue, and a sync protocol. Realistic estimate: 3-4 weeks of
engineering, contingent on the WASM render-mode story being settled
in the .NET runtime we ship on (.NET 10 has it; we'd be exercising it).

**Workaround in place:** custodians at border sites use the WebApp
during the daily Starlink window (typically 2-3 hours); voucher
submissions outside that window are recorded on paper and entered the
next online morning.

**To kick this off when the business decides it's worth the spend:**

1. Stand up `finance/NickFinance.WebApp.Wasm` companion project.
2. Tag the Submit and Approve pages with `@rendermode InteractiveAuto`
   so they survive a SignalR disconnect.
3. Service worker (`wwwroot/service-worker.js`) caches submit-page assets.
4. `IPettyCashOfflineQueue` interface — POST to `NickFinance.WebApp.Api`
   when online, IndexedDB-queue otherwise.
5. Voucher carries a `queued_offline=true` flag for audit.
6. Acceptance: custodian submits a voucher mid-blackout, page confirms
   "queued, will submit when online", queued voucher posts on next
   online window with the `queued_offline=true` audit flag.

---

## Tracker

| # | Item | Owner | Status |
|---|---|---|---|
| 1 | GRA e-VAT | CFO + CTO (partner contract) | **Adapter shipped 2026-04-28** — set `NICKFINANCE_EVAT_API_KEY` to activate |
| 2 | MoMo disbursement | NickComms team | **Adapter shipped 2026-04-28** — set `NICKCOMMS_API_KEY_NICKFINANCE` to activate |
| 3 | Receipt OCR | CTO (Azure subscription) | **Adapter shipped 2026-04-28** — set `AZURE_DOCUMENT_INTELLIGENCE_KEY` to activate |
| 4 | Live `nickhr` rollout | DBA + ops | **Done 2026-04-28** |
| 5 | WhatsApp approvals | CFO + CTO (Meta verification) | **Adapter shipped 2026-04-28** — set `WHATSAPP_CLOUD_API_TOKEN` to activate; webhook URL `/api/whatsapp/webhook` ready for Meta config |
| 6 | PWA offline (border sites) | CTO greenlight | **Deferred by design** — multi-week WASM port, see §6 |

When rows 1–3, 5 activate (env var swap), the system is in full
production-cutover state with one residual eng task on row 5
(approval-engine hook + inbound WhatsApp webhook handler).

Delete this file when row 6 is the only remaining row.
