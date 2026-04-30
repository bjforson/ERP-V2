# Custodian Guide — NickFinance Petty Cash

A field guide for the people who actually move cash at our customs sites.
If anything in here doesn't match what you see on screen, contact your finance lead
or the NICKSCAN IT helpdesk — the screens may have shifted slightly.

## What is NickFinance?

NickFinance is the NICKSCAN finance suite. The piece you'll use day to day is
the **Petty Cash module** — it's where every cedi handed out at a site is
recorded with proper approvals and an audit trail. Custodians at the customs
sites (Tema HQ, Kotoka, Takoradi, Aflao, Paga, Elubo) submit vouchers, get
them approved, and disburse cash. Behind the scenes the system writes the
journal entries, tracks the float, and keeps the receipts. If you get stuck,
contact your finance lead first; otherwise raise a ticket with the
NICKSCAN IT helpdesk.

[screenshot: NickFinance home page with site logo and metric tiles]

## Logging in

1. Open `https://finance.nickscan.net` in your browser (Chrome, Edge, or Firefox).
2. You'll be redirected to a Cloudflare Access login page (this is the NICKSCAN
   gate — not NickFinance itself).
3. Type your `@nickscan.com` email address and submit.
4. Cloudflare emails you a **one-time code (OTP)** within a few seconds.
   Paste it on the next screen.
5. After that you land on the NickFinance home page. Your name and email
   show in the top-right corner — that's how you know you're signed in as
   yourself.

[screenshot: Cloudflare Access OTP entry page]

If the OTP doesn't arrive within a minute, check your spam folder, then
ask the helpdesk to verify your email is in the access group.

## Submitting your first voucher

1. Click **Petty cash** in the top nav, then **New voucher** (or hit `/petty-cash/new`).
2. Pick a **Category**: Transport, Office, Fuel, Snacks, Repair, or Other.
3. Enter a **Purpose** — keep it concrete. "Cab from Tema port back to
   office" is good. "Travel" is too vague.
4. Enter the **Amount** in GHS. The form takes whole cedis and pesewas
   (e.g. 80.00).
5. Fill in **Description per line** — one row per item that adds up to
   the total. The line items must sum to the amount or the voucher will
   be rejected.
6. Optional **Payee** — the person or vendor receiving the cash. Useful
   for audit, especially for amounts over GHS 200.
7. Click **Submit**.

[screenshot: New voucher form filled out for a cab fare]

The voucher gets a number that looks like `PC-76FD5C-2026-00042`. The
hex prefix is your site's short hash; the suffix counts up per site per
year. You can refer to this number in WhatsApp, email, or audit
discussions later.

## Voucher statuses

A voucher moves through a small set of states:

- **Submitted** — you've sent it. The approver (configured per site/amount
  band) sees it in their queue. Larger vouchers may need two approvals.
- **Approved** — green light to hand over the cash. The general ledger
  has NOT moved yet — it only posts on disbursement.
- **Disbursed** — done. Cash handed over, journal posted, audit row
  written. From here it's permanent record.
- **Rejected** — the approver said no. Read the reason. The original
  voucher stays in the system as audit; you submit a NEW one if needed.

[screenshot: Petty cash list page showing mixed statuses with colored pills]

## What to do if it's rejected

Open the rejected voucher and read the **rejection note**. Common causes:

1. **Line items don't sum to total** — fix the math.
2. **Missing receipt photo** — for vouchers over GHS 100 (or per your
   site policy) a receipt is mandatory. Re-take the photo, make sure the
   total is legible.
3. **Category doesn't match the actual purpose** — "Transport" with
   description "lunch for visitors" will get bounced. Pick the right
   category, or use "Other" with a clear purpose.
4. **Duplicate of an earlier voucher** — same payee, same amount, same
   day. The fraud-detection layer catches these. If it's NOT a duplicate,
   note that explicitly in the new voucher's purpose.

Fix the issue and submit a NEW voucher. The rejected one stays where it
is — that's intentional, so audit can see what was tried.

## Floats and replenishment

Each custodian holds a **float** — a working balance of cash in their
drawer or safe. Your float starts at GHS 5,000 (or whatever amount was
provisioned for your site at setup). Every approved disbursement
decreases the float by the voucher amount.

When the float drops below **25%** of the original (e.g. below GHS 1,250
on a GHS 5,000 float), the home page shows a yellow **"needs replenish"**
banner, and finance is automatically notified. Finance tops the float
back up to the original amount and the banner clears.

You don't request a replenishment — the system does it for you. But if
you're approaching a busy day, it's reasonable to ping your finance lead
ahead of time.

[screenshot: home page with the yellow "float needs replenish" banner]

## A day in the life

A quick chronological example for Tema HQ:

- **08:00** — float at GHS 4,200 (was 5,000 yesterday; couple of items
  yesterday afternoon).
- **09:30** — cab driver paid GHS 80 to bring an inspector back from the
  port. Voucher `PC-A1B2C3-2026-00018`, submitted, site manager
  approves, custodian disburses cash.
- **12:30** — lunch supplier GHS 60 for visiting auditors. Voucher
  `PC-A1B2C3-2026-00019`, same flow.
- **End of day** — float at GHS 4,060, two new audit rows in the GL.
  Evening cash count by the supervisor matches the system count.

That's it. No spreadsheets, no shoe-boxes of receipts. Everything
reconciles itself.

## What does NOT go through here

A few things that look like petty cash but aren't:

- **Salary disbursement** — that's the **NickHR** module. Don't process
  salary advances or wages as petty-cash vouchers; they have their own
  approval flow and tax handling.
- **Customer invoices** — if you're billing a customer, that's **AR**
  (Accounts Receivable), not petty cash. AR has its own page in the
  top nav.
- **Big capital purchases** — a new vehicle, a generator, anything over
  the petty-cash ceiling for your site (typically GHS 5,000). Those go
  through **AP** (Accounts Payable) with PO + invoice + payment run.

When in doubt, ask finance before submitting. A wrong-module voucher
slows everyone down and creates audit cleanup.

[screenshot: top nav with Petty cash, AR, AP modules highlighted]
