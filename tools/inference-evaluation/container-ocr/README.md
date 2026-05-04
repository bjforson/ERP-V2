# OCR evaluation harness (`tools/inference-evaluation/container-ocr/`)

Sprint 19 offline-eval harness for the §6.1 container-OCR replacement
track. Runs an OCR engine against a held-out corpus and emits a JSON
report consumed by the §12.3 acceptance gate.

## Status

- Phase 1 (scaffold + Tesseract baseline + Postgres + directory corpus
  sources + JSON output) — **shipped Sprint 19**.
- Phase 2 (v1 Tesseract baseline measurement) — **shipped Sprint 19**.
  See `results/baseline-2026-05-04.json` and
  `docs/runbooks/08-ocr-baseline-2026-05-04.md`.
- Future engines (`florence2`, `donut`) — wired as enum values; the
  harness rejects them today with a "not yet supported" message. They
  light up once the Florence-2 ONNX export from
  `tools/inference-training/container-ocr/` lands.

## Usage

```powershell
# Tesseract against a held-out v1 sample (Postgres corpus):
dotnet run --project tools/inference-evaluation/container-ocr -- `
    --engine tesseract `
    --corpus-source postgres `
    --corpus-limit 5000 `
    --out tools/inference-evaluation/container-ocr/results/baseline-2026-05-04.json

# Tesseract against a directory of (.png + .json) pairs:
dotnet run --project tools/inference-evaluation/container-ocr -- `
    --engine tesseract `
    --corpus-source directory `
    --corpus-dir C:/Shared/ERP V2/tests/fixtures/ocr-eval `
    --out /tmp/dir-eval.json
```

## Output schema

See `results/baseline-2026-05-04.json` for a real example. The schema is
locked to plan §12.3:

```json
{
  "engine": "tesseract",
  "corpusSize": 5000,
  "exactMatchRate": 0.74,
  "checkDigitPassRate": 0.91,
  "perFailureMode": { "stylizedTypography": 0.43, "weathering": 0.61, "obliqueAngles": 0.55, "falsePositiveSurfaces": 0.18 },
  "latency": { "p50Ms": 280, "p95Ms": 720, "p99Ms": 1450 },
  "ranAt": "2026-05-04T...",
  "host": "iGPU | CPU"
}
```

When failure-mode classification is unreliable on the corpus available
(e.g. owner-prefix metadata is missing), `perFailureMode` is emitted as
`null` and the limitation is documented in the runbook.

## Read-only invariant

Same posture as the sibling Python tools (`tools/v1-label-export/`,
`tools/inference-training/container-ocr/harvest_plates.py`). The harness
opens Postgres connections with `set_session(readonly=true)` and never
writes against `nickscan_production`. Output JSON lives under v2's tree.
