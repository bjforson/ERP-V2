# NickERP.Inspection — v2 (greenfield)

> **Status:** design phase. Nothing is built yet. No production traffic.
> **Repo:** standalone — this folder (`C:\Shared\ERP V2\`) is its own git repo, independent of v1.
> **Parent roadmap:** `C:\Shared\NSCIM_PRODUCTION\ROADMAP.md` → Phase 6. (Lives in the v1 repo; absolute path because v1 and v2 are siblings, not parent/child.)
> **Design of record:** [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)

---

## What this is

A vendor-neutral, location-federated, plugin-driven rebuild of the NSCIM scan / analysis / authority-submission pipeline. Runs in parallel with the current production system (`C:\Shared\NSCIM_PRODUCTION\src\NickScanCentralImagingPortal.*`) until cutover.

The v1 system stays untouched. v2 borrows decoders, rules, and viewer components from v1 as it ports them, but does not share code, schema, services, or git history during the rebuild.

## Why v2

Current system is flat, vendor-entangled, and single-location. See `C:\Shared\NSCIM_PRODUCTION\ROADMAP.md` gaps #1, #3, #14, #17–#22. Rewriting in place would bleed into live ops for a year. A parallel build gives us a clean domain and a real cutover moment.

## Where things live

```
C:\Shared\ERP V2\                ← this repo, separate from NSCIM_PRODUCTION
├── README.md                    ← this file
├── docs\
│   ├── ARCHITECTURE.md          ← the design of record
│   └── MIGRATION-FROM-V1.md     ← cutover plan (stub, grows over time)
└── src\                         ← (not yet — created in Phase 6.0 Skeleton)
```

## How to join in

1. Read `docs/ARCHITECTURE.md` top to bottom — the domain vocabulary and plugin contracts are the load-bearing decisions.
2. Read `C:\Shared\NSCIM_PRODUCTION\ROADMAP.md` Phase 6 for schedule context.
3. Check the "Open questions to settle later" section at the end of `ARCHITECTURE.md` before proposing a change.

## Rules of engagement

- No code in this tree yet. Phase 6.0 (Skeleton) creates the `src/` projects.
- **Never reach into `C:\Shared\NSCIM_PRODUCTION\src\NickScanCentralImagingPortal.*`** from here. v2 is standalone — different folder, different repo.
- Ports from v1 are **line-by-line copies into new files with rename**, not shared references or submodule imports. v1 keeps moving; v2 takes a point-in-time snapshot of the logic it needs.
- Every vendor name (FS6000, ICUMS, BOE, CMR, regime codes) belongs in an adapter or country module — **never in core domain**.

## Note on the path

The folder name `ERP V2` contains a space. Quote every shell reference: `"C:\Shared\ERP V2"`. Some scripts and CI tools handle this poorly; if it becomes a recurring papercut, we'll discuss renaming to `erp-v2` or similar.
