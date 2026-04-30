# NickFinance.Pdf — Licensing

## Library: QuestPDF

This project depends on **QuestPDF** (https://www.questpdf.com/) at version
`2026.2.4` (or whichever stable major is currently pinned in
`NickFinance.Pdf.csproj`).

### License chosen

QuestPDF is dual-licensed:

- **Community MIT license** — free for organisations with **annual gross
  revenue under USD 1,000,000**.
- **Professional / Enterprise** — paid tier for organisations above the
  USD 1M threshold, or those wanting commercial support / indemnification.

NickFinance applies the **Community** license at runtime via
`QuestPdfBootstrap.Configure()`, which sets:

```csharp
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
```

### Why Community is appropriate today

Nick TC-Scan Ltd operates as a Ghana customs scanning operator. As of the
2026-04 revenue baseline the company is well below the USD 1,000,000
annual gross revenue threshold that QuestPDF applies to Community usage
(see https://www.questpdf.com/license/community.html). The Community
license therefore covers our use unambiguously.

### When this needs to be re-checked

The orchestrator (CTO + CFO) MUST revisit this choice before any of:

1. Annual gross revenue approaching or exceeding **USD 1,000,000**.
2. A material change to the organisation owning this codebase (acquisition,
   spin-off, white-label resale to a larger operator).
3. A QuestPDF major version bump that changes its licensing model.

If any of those triggers fires, switch to the QuestPDF Professional license
or fall back to the alternative below — and update this file in the same
commit.

### Fallback: PDFsharp (MIT)

If QuestPDF Community licensing turns out to be inappropriate for any
future deployment, the substitution path is:

- Remove `<PackageReference Include="QuestPDF" />` from
  `NickFinance.Pdf.csproj`.
- Add `<PackageReference Include="PDFsharp" />` (MIT-licensed,
  https://github.com/empira/PDFsharp).
- Re-implement the three generators (`InvoicePdfGenerator`,
  `VoucherPdfGenerator`, `ReceiptPdfGenerator`) against PDFsharp's
  lower-level `XGraphics` API. The public interfaces
  (`IInvoicePdfGenerator`, etc.) and the `*PdfModel` records remain
  unchanged so the WebApp side does not need to change.
- Drop the `QuestPdfBootstrap.Configure()` call from `Program.cs`.

PDFsharp is more verbose but its MIT license is unconditional, so it's
a safe long-term fallback regardless of revenue.

### Audit trail

| Date       | Reviewer     | Decision                          |
| ---------- | ------------ | --------------------------------- |
| 2026-04-28 | (initial)    | Apply Community license; flag for |
|            |              | re-review at next revenue close.  |
