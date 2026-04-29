# Image Analysis Modernization — Roadmap (v2)

> **Status:** living roadmap. First written 2026-04-28.
> **Partner doc:** [`ARCHITECTURE.md`](ARCHITECTURE.md) — §7.7 (imaging pipeline) is the source of record for the rendering/cache layer.
> **Companion:** [`MIGRATION-FROM-V1.md`](MIGRATION-FROM-V1.md)
> **Cadence:** appended each iteration as we close design questions. New material lands at the bottom of §8 (Iteration log) before being promoted into the appropriate numbered section.

---

## Purpose

This file captures (a) the gap analysis between today's v1 image pipeline and 2024–2026 state-of-the-art for cargo X-ray analysis, (b) the prioritized improvements we will layer into v2, and (c) the concrete specs for each. It is **not** the design of record for the rendering/cache pipeline — that lives in `ARCHITECTURE.md §7.7`. This doc is the design of record for **what we run on top** of the rendered/decoded artifacts: ML models, the inference runtime, and the standards posture.

---

## Principles

1. **Raw channels reach the model. Renderings only reach humans.** Compositing HE+LE+material into 8-bit JPEG before analysis discards the dual-energy Z signal that cargo X-ray AI is built on. v2 keeps `(HE, LE, material)` available alongside the rendered preview.
2. **No cloud round-trip per scan in steady state.** Cloud teachers (Anthropic, hosted VLMs) are acceptable for label generation, evaluation, and shadow comparisons. They are not acceptable as the production critical path for routine decisions.
3. **Models are first-class entities.** Versioned, hashed, registered, hot-swappable. Same posture as scanner/external-system plugins. No `if (model_v3) { ... }` branches in domain code.
4. **One inference contract for the whole module.** Container-split, OCR, anomaly detection, and any future model talk to the same `IInferenceRunner`. Different *runtimes* (CPU, DirectML, CUDA, TensorRT) are plugins. Different *models* are artifacts.
5. **Standards-ready, not standards-blocked.** v2's scanner adapter contract has a DICOS-shaped slot from Phase 7.0. We do not ship a DICOS adapter on day 1 and we do not translate proprietary blobs into DICOS as middleware.

---

## Section tiers

| Tier | Sections | What it covers |
|---|---|---|
| **Image-analysis-direct** | §3, §4, §5, §6.1, §6.2, §6.3, §6.5, §6.6, §6.7, §6.8 | The actual ML/CV that processes images — split student, inference runner, DICOS readiness, OCR, anomaly detection, manifest↔X-ray consistency, threshold calibration, TIP synthetic data, dual-view registration, metal-artifact correction. |
| **Image-analysis-adjacent** | §6.4, §6.9, §6.10 | Training-data infrastructure that feeds the direct tier — active learning loop, threat library capture, HS commodity density reference. |
| **Supporting-platform** | §6.11 | Pipes that carry ML labels in/out — necessary but not image-analysis-itself; inbound post-hoc outcome adapter. |

**How to use this doc.** When deciding what to build next, lead with the image-analysis-direct tier; the adjacent tier is supporting infra; the supporting-platform tier is plumbing.

---

## 1. Gap analysis — current vs SOTA

### 1.1 v1 today (read-only, 2026-04-28)

| Capability | What's there | Anchor |
|---|---|---|
| Decoding | Custom C# + Python parsers for FS6000 (16-bit BE 3-channel) and ASE (16-bit LE, mono / tri-panel). Reverse-engineered from vendor blobs. | `FS6000FormatDecoder.cs:53–250`, `services/image-splitter/inspector/decoders/fs6000.py:187–274`, `…/decoders/ase.py:101–149` |
| Container splitting | 8-strategy consensus including a Claude Sonnet 4.5 vision teacher (~$0.003–0.008 / call, p50 3–5 s, cloud egress per scan). | `services/image-splitter/inspector/pipeline/orchestrator.py:31–57`, `…/strategies/claude_vision.py` |
| Detection | OpenCvSharp edge + contour heuristics. Hardcoded Canny 50/150. | `ContainerObjectDetectionService.cs:69–119` |
| OCR | Tesseract.NET on container plates. | `ContainerNumberOcrService.cs:14–115` |
| Quality assessment | Heuristic. | `ImageQualityAssessmentService.cs` |
| LUT compositor | Vendor-style pseudocolor LUT in C#. Output is 8-bit JPEG served to UI **and used as the input to OCR/detection**. | `FS6000VendorLutCompositor.cs`, `FS6000ImagePipeline.cs:43–200` |
| Format support | FS6000 + ASE proprietary only. No DICOS. No JPEG-2000 lossless. | — |
| On-prem ML | None. No `.onnx` / `.pb` / `.pt` model artifacts found in the tree. | — |
| Per-scanner calibration | None — thresholds are compile-time constants. | various |

### 1.2 SOTA reference points (2024–2026)

| Concern | Reference |
|---|---|
| Format / interchange | NEMA IIC 1 v04 (2023). DCMTK 3.7.0 supports DICOS Storage SOP classes. |
| Dual-energy decoding | HE + LE + Z_eff as 3-channel float input to CNNs beats LUT-rendered RGB by 5–15 mAP across published baggage benchmarks. ([arXiv 2108.12505](https://arxiv.org/abs/2108.12505); limits in [arXiv 2301.05783](https://arxiv.org/abs/2301.05783)) |
| Detection | RT-DETR (CVPR 2024); cargo-tuned YOLOv8/v10/v11 + WIoU/Soft-NMS ([MDPI Logistics 2025](https://www.mdpi.com/2673-7590/5/3/120)). |
| Anomaly (unknown-unknown) | PatchCore, FastFlow, DiffusionAD. **No published cargo X-ray application yet — open opportunity.** |
| Document AI | Donut, Florence-2 (MIT, 0.23B/0.77B), Qwen2.5-VL ([arXiv 2502.13923](https://arxiv.org/abs/2502.13923)). On-prem-friendly. |
| Foundation backbones | DINOv2 (Meta), SAM 2 ([arXiv 2408.00714](https://arxiv.org/abs/2408.00714)). |
| On-prem inference (Windows) | ONNX Runtime + DirectML EP. TensorRT EP optional on NVIDIA hosts. |
| Synthetic data | Threat Image Projection (TIP), Meta-TIP, RWSC-Fusion. |
| Cross-vendor adaptation | Pseudocolor canonicalization (PDSXray, [Sci. Data 2026](https://www.nature.com/articles/s41597-026-07149-8)). |

### 1.3 The single biggest leverage point

**Stop dropping the 16-bit dynamic range and the dual-energy channel before the model sees it.** Today: HE + LE + material → vendor LUT → 8-bit JPEG → OCR/detection. Future: keep `(HE, LE, material)` as float32 tensors all the way to the model; render the LUT-JPEG only for the human reviewer. Without this, every other ML improvement listed below is bounded by an information-bottleneck we created ourselves.

---

## 2. Prioritized improvements

> Tier columns: **T1** = bake into v2 Phase 7.1 / 7.2; **T2** = Phase 7.3+; **T3** = opportunistic, post-cutover.

| # | Phase | Tier (D/A/P) | Item | Effort | Spec section |
|---|---|---|---|---|---|
| 1 | T1 | D | Plumb raw channels through to model input (no LUT-JPEG bottleneck) | M | covered by §7.7 + §3 here |
| 2 | T1 | D | Pick ONNX Runtime + DirectML as inference backbone | S | §4 |
| 3 | T1 | D | Distill local container-split student from accumulated Claude labels | M | §3 |
| 4 | T1 | D | Replace Tesseract container OCR with Donut or Florence-2 | S–M | §6.1 |
| 5 | T2 | D | Anomaly detection conditioned on declared HS (PatchCore + DINOv2) | M–H | §6.2 |
| 6 | T2 | D | Manifest ↔ X-ray consistency scorer (Donut + grounded VLM) | H | §6.3 |
| 7 | T2 | A | Wire ROI/peer-disagreement telemetry into active learning (Label Studio) | M | §6.4 |
| 8 | T2 | D | DICOS export/ingest in `IScannerAdapter` capability flag | S (when needed) | §5 |
| 9 | T2 | D | Per-scanner-instance threshold calibration (DB-keyed, replace hardcodes) | S | §6.5 |
| 10 | T3 | D | Threat Image Projection synthetic data generator | M | §6.6 |
| 11 | T3 | D | Dual-view (top + side) registration | M | §6.7 |
| 12 | T3 | D | Beam-hardening / metal-streak correction for behind-engine regions | M | §6.8 |
| 13 | T1 | A | In-house threat library capture pipeline (unblocks §6.6 sourcing) | M (operational) | §6.9 |
| 14 | T1 | A | HS commodity density reference seed + curation (unblocks §6.3 scorer) | M | §6.10 |
| 15 | T1 | P | Inbound post-hoc outcome adapter (closes §6.4 ARCHITECTURE Q4) | M | §6.11 |

All fifteen items now have specs in this doc (§3 / §4 / §5 / §6.1–6.11). The v2 inference module C# scaffold per §4 landed 2026-04-28 at `modules/inspection/src/NickERP.Inspection.Inference.Abstractions/` + `modules/inspection/plugins/NickERP.Inspection.Inference.{OnnxRuntime,Mock}/` — three projects, 23 source files, registered in `NickERP.Tests.slnx`, builds clean. New work — Tier 2/3 items not yet on this list — lands as additions to this table and a new §6.x stub before being promoted into a full spec.

---

## 3. Container-split student model — spec

### 3.1 Problem

Predict the X-coordinates within a multi-container scan strip where the strip should be split into per-container images. Today's solution makes a network round-trip to Anthropic's API on every scan and depends on a hosted model whose version we do not control. The student replaces the teacher on the production critical path; the teacher remains as a labeling oracle and a periodic drift check.

**Locked acceptance:** 2026-04-28
- p95 inference latency ≤ 100 ms on lane-PC CPU, ≤ 30 ms on lane-PC iGPU (DirectML).
- Mean Absolute Error ≤ 4 px on the held-out test set.
- Catastrophic-fail rate (MAE > 30 px) < 0.5%.
- Zero outbound network calls per scan in production.

### 3.2 Inputs / outputs

**Input.** `(1, H, W)` single-channel float32 tensor. Source: top 25 % crop of the rendered strip (the same view Claude sees today; keeps student aligned with teacher labels). Long-edge resized to 1568 px (matches Claude's downsampled view in `claude_vision.py:83–86`). Normalization: percentile stretch 0.5 %–99.5 % → [0, 1] (matches `fs6000.py:30`).

Final input shape: `(1, 472, 1568)` (FS6000 native 2295×1378 → top 25 % is 2295×344 → resize long-edge to 1568 → 472 height after pad).

**Phase 2 ablation:** retrain with `(3, H, W)` — raw HE, LE, material as channels — to test if dual-energy gives a measurable lift on tough cases (heavy metal occlusion, low-contrast container walls).

**Output.** 1D heatmap `(W,)` of probabilities along the X axis. Split positions = local maxima above threshold (default 0.5). This shape supports variable container counts (1, 2, 3, 4-container scans) end-to-end without an explicit count prediction.

### 3.3 Architecture

| Choice | Decision |
|---|---|
| Family | U-Net with 1D output collapse |
| Encoder | ResNet-18, ImageNet-pretrained init, 4 down-blocks |
| Decoder | Standard U-Net up-blocks with skip connections, 4 up-blocks |
| Head | 1×1 conv → channel-pool over Y axis (mean) → 1D heatmap of length W |
| Total params | ~12 M |
| ONNX opset | 18 |
| Quantization target | post-training INT8 with 256-scan calibration set |

**Why not transformer / DETR.** Labels are sparse and crisp X-coordinates, not sequences. DirectML support for transformer ops on Windows iGPUs is uneven. U-Net is well-understood, easy to debug, easy to quantize, and runs cheaply on CPU.

### 3.4 Training data

| Source | Description | Estimated volume |
|---|---|---|
| **Primary (gold-ish)** | Claude Sonnet 4.5 verifier outputs from production runs, where `verifier_confidence ≥ 0.8` and ≥ 1 other strategy agreed within ±5 px | 50 k+ scans (verify against splitter logs) |
| **Silver** | Multi-strategy consensus where ≥ 4 of the 8 strategies agreed within ±5 px | overlap with primary |
| **Gold** | Human-confirmed splits from analyst review (when the v2 review UI logs split corrections) | accumulates over time |
| **Holdout test** | 5 % stratified by container-count | locked at training time |

**Class balance.** Stratify by container count {1, 2, 3, 4+}. Empty-container scans (cargo absent — floor + walls only) tracked as a separate stratum; over-sample if under-represented.

**Augmentation.** Horizontal flip with split-position remapping; brightness/gamma jitter (±15 %); mild Gaussian blur (σ 0–1 px); simulated metal-plate occlusion (random rectangles, 0–3 per image, max 5 % image area); horizontal stretch ±5 % to handle belt-speed variation.

**Splits.** 80 / 10 / 10 train / val / test, stratified by container count and by source scanner serial.

### 3.5 Loss & evaluation

**Loss.**
- Primary: Gaussian-targeted MSE on the heatmap (σ = 8 px around each true split position).
- Auxiliary (Phase 2, after Claude returns confidence-per-X): KL divergence against teacher heatmap, weight 0.1.

**Eval metrics on holdout test.**
- **MAE in pixels** vs ground truth — primary metric, target ≤ 4 px.
- **F1@5px** of detected split positions — secondary, target ≥ 0.97.
- **% agreement with multi-strategy consensus** — sanity, target ≥ 97 %.
- **Catastrophic-fail rate** (MAE > 30 px) — safety, target < 0.5 %.

**A/B harness.** Route 5–10 % of production traffic to the Claude verifier as a silver-truth source; continuously log MAE per scanner. Trigger an alert if 7-day MAE drift exceeds 2 px.

### 3.6 Inference budget

| Profile | Target |
|---|---|
| p95 CPU latency (i7-1265U class) | ≤ 100 ms |
| p95 iGPU latency (Iris Xe via DirectML) | ≤ 30 ms |
| p95 dGPU latency (RTX A2000 via CUDA) | ≤ 8 ms |
| FP16 model size | ~24 MB |
| INT8 model size | ~12 MB |
| Working memory peak | ≤ 250 MB |
| Cold-load penalty | ≤ 2 s (warm at process start with dummy input) |

### 3.7 Deployment & rollout

**Artifact path.** `<storage_root>/models/container-split/v{N}/model.onnx` plus `model.metadata.json` with `{ sha256, training_set_hash, eval_metrics, exported_at, exported_by, git_commit }`. Loaded via the `IInferenceRunner` contract (§4).

**Rollout phases.**

| Phase | Behavior | Gate to next |
|---|---|---|
| 0. Dev eval | Student runs offline against a frozen test set | MAE ≤ 4 px, F1@5px ≥ 0.97 |
| 1. Shadow | Student runs alongside Claude on every production scan; output logged but unused | ≥ 14 days, observed MAE ≤ 4 px on production traffic |
| 2. Primary + safety net | Student is primary. Fall back to Claude if (heatmap peak < 0.6) OR (student vs `steel_wall_midpoint` disagreement > 30 px) | < 2 % Claude-fallback rate sustained for 7 days |
| 3. Primary + sample audit | Student is primary on 100 %. Claude runs on 1 % sample for ongoing drift detection | SLO: 30-day MAE drift < 2 px |

### 3.8 Failure modes & guards

Carry forward known failure modes from v1's Claude strategy:

- **Center-bias collapse.** Claude returns exactly `width/2` when uncertain (`orchestrator.py:158–169`). The student inherits this risk if the label set is biased that way. **Guard:** if heatmap peak is at `width/2 ± 3 px` AND peak confidence < 0.55, mark `student_unsure` and fall back to ensemble.
- **Wide disagreement.** Keep v1's 50-px disagreement guard (`orchestrator.py:136–150`) as a runtime cross-check against `steel_wall_midpoint`. Student wins by default; > 50-px disagreement with `steel_wall_midpoint` triggers fallback.
- **Single-container scans.** Heatmap should be flat. Define "flat" as `max(heatmap) < 0.3` → predict "no split needed". Validate explicitly on the 1-container stratum during eval.
- **Empty-container scans.** Visually distinct from cargo-bearing scans. Ensure stratum is in train + val + test.

### 3.9 Open questions

| ID | Question | Owner | Status |
|---|---|---|---|
| Q-A1 | Single-channel rendered top-strip vs raw HE+LE+Z 3-channel input. Phase 1 single-channel; ablate in Phase 2. | TBD | Open — recommendation logged |
| Q-A2 | Training-data export pipeline. v1 splitter logs structured outputs — need a one-off Python script to extract `(image_path, split_x, verifier_confidence, agreement_score)` per run. **No v1 code edits** (v1 is read-only). | TBD | Open |
| Q-A3 | Teacher version freeze. Sonnet 4.5 today; if Anthropic rotates the model, label distribution shifts. **Action:** stamp every label with `teacher_model_version`; never mix versions in one training run. | TBD | Locked posture, action TBD |
| Q-A4 | How does the student get retrained? Cron-driven retrain when N new gold labels accumulate? Pull-based? Out of scope for first deployment. | TBD | Deferred to Phase 7.4 |

---

## 4. `IInferenceRunner` plugin contract

### 4.1 Position in v2's plugin system

Sits alongside the existing v2 plugin contracts:

| Existing | Project | Surface |
|---|---|---|
| `IScannerAdapter` | `NickERP.Inspection.Scanners.Abstractions` | Stream + parse vendor scan formats |
| `IExternalSystemAdapter` | `NickERP.Inspection.ExternalSystems.Abstractions` | Fetch documents + submit verdicts |
| `IAuthorityRulesProvider` | `NickERP.Inspection.Authorities.Abstractions` | Country-specific validate + infer |

| **New** | **Project** | **Surface** |
|---|---|---|
| `IInferenceRunner` | `NickERP.Inspection.Inference.Abstractions` | Load + execute models behind a runtime-agnostic API |

**Runners (concrete plugins):**
- `NickERP.Inspection.Inference.OnnxRuntime` — CPU + DirectML EPs (the default, ships with the inspection module).
- `NickERP.Inspection.Inference.OnnxRuntime.Cuda` — optional, NVIDIA hosts only.
- `NickERP.Inspection.Inference.OnnxRuntime.TensorRT` — optional, NVIDIA + TRT-aware deployments.
- `NickERP.Inspection.Inference.Mock` — test double, deterministic noise generator with optional fixture playback.

**Models** are first-class core entities (new):

| Entity | Purpose |
|---|---|
| `InferenceModel` | Registered model: `model_id`, `version`, `family` (e.g. `container-split`), `runner_type_code`, `sha256`, `status` (`registered`, `active`, `retired`), `deployed_at`, `deployed_by`, `metadata jsonb` |
| `LoadedModelInstance` | Runtime singleton wrapping `ILoadedModel`, held by DI. Health-checked. |

### 4.2 Contract — interfaces

```csharp
namespace NickERP.Inspection.Inference.Abstractions;

public interface IInferenceRunner
{
    /// <summary>Stable identifier persisted in InferenceModel.runner_type_code.</summary>
    string TypeCode { get; }

    InferenceRunnerCapabilities Capabilities { get; }

    Task<ConnectionTestResult> TestAsync(
        InferenceRunnerConfig config,
        CancellationToken ct);

    Task<ILoadedModel> LoadAsync(
        ModelArtifact artifact,
        ModelLoadOptions options,
        CancellationToken ct);
}

public interface ILoadedModel : IAsyncDisposable
{
    string ModelId { get; }
    string ModelVersion { get; }
    ModelMetadata Metadata { get; }
    string ExecutionProviderUsed { get; }   // resolved at load (e.g. "DirectML")

    Task<InferenceResult> RunAsync(
        InferenceRequest request,
        CancellationToken ct);
}
```

### 4.3 Contract — records

```csharp
public sealed record InferenceRunnerCapabilities(
    bool SupportsBatch,
    bool SupportsDynamicShapes,
    bool SupportsFp16,
    bool SupportsInt8,
    long MaxModelSizeBytes,
    IReadOnlyList<string> AvailableExecutionProviders);

public sealed class InferenceRunnerConfig
{
    public required string PreferredExecutionProvider { get; init; } // "DirectML"
    public IReadOnlyList<string> FallbackExecutionProviders { get; init; }
        = new[] { "CPU" };
    public int IntraOpThreads { get; init; } = 1;
    public int InterOpThreads { get; init; } = 1;
    public int? GpuDeviceId { get; init; }
    public TimeSpan SessionWarmupTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

public sealed class ModelArtifact
{
    public required string ModelId { get; init; }      // "container-split"
    public required string Version { get; init; }      // "v3.1.0"
    public required string Path { get; init; }         // canonical disk path
    public required string Sha256 { get; init; }       // fail-fast on mismatch
    public IReadOnlyDictionary<string, string>? Tags { get; init; }
}

public sealed record ModelLoadOptions(
    string? PreferredExecutionProvider = null,
    int? GpuDeviceId = null,
    bool UseInt8 = false,
    bool WarmupOnLoad = true);

public sealed class ModelMetadata
{
    public required IReadOnlyList<TensorDescriptor> Inputs { get; init; }
    public required IReadOnlyList<TensorDescriptor> Outputs { get; init; }
    public required string OnnxOpset { get; init; }
    public IReadOnlyDictionary<string, string>? CustomMetadata { get; init; }
}

public sealed record TensorDescriptor(
    string Name,
    TensorElementType ElementType,
    IReadOnlyList<int?> Shape);   // null entry = dynamic dimension

public sealed class InferenceRequest
{
    public required IReadOnlyDictionary<string, ITensor> Inputs { get; init; }
    public required string CorrelationId { get; init; }
    public int? TenantId { get; init; }
    public TimeSpan? Timeout { get; init; }
}

public sealed class InferenceResult
{
    public required IReadOnlyDictionary<string, ITensor> Outputs { get; init; }
    public required InferenceMetrics Metrics { get; init; }
}

public sealed record InferenceMetrics(
    long PreprocessUs,
    long InferenceUs,
    long PostprocessUs,
    string ExecutionProviderUsed,
    long PeakBytesAllocated);
```

### 4.4 Tensor abstraction

```csharp
public interface ITensor : IDisposable
{
    TensorElementType ElementType { get; }
    IReadOnlyList<int> Shape { get; }
    Span<byte> AsBytes();
    Span<T> AsSpan<T>() where T : unmanaged;
}

public enum TensorElementType
{
    Float32, Float16, Int8, UInt8, Int16, Int32, Int64, Bool
}
```

**Why an abstraction over `OrtValue`.** Avoid leaking `Microsoft.ML.OnnxRuntime.OrtValue` through the interface so that `Mock`, future `OpenVINO` runners, or a future managed runtime can implement without an ONNX dependency. The `OnnxRuntime` adapter wraps `OrtValue` behind `ITensor`; ownership of pinned memory is the runner's responsibility.

### 4.5 Model artifact lifecycle

| Stage | Responsibility |
|---|---|
| **Discovery** | At startup, `ModelRegistry` scans `<storage_root>/models/` and reconciles with the `inference_models` table. New artifacts not yet in the table appear as `status='registered'` (admin-promoted to `active`). |
| **Loading** | One `ILoadedModel` per active `(model_id, version)`. Singleton, lazy on first request. Fails fast on sha256 mismatch. |
| **Warmup** | If `WarmupOnLoad`, run a synthetic inference with metadata-derived dummy input within `SessionWarmupTimeout`. |
| **Hot-swap** | Operator promotes a new version → background load of new singleton → atomic ref swap → old singleton `DisposeAsync()` after a 30-second drain. In-flight requests on old version finish; new requests hit new version. |
| **Health** | `IHealthCheck` per loaded model probes with the warmup input every 60 s; emits stale signal if last-success > 5 min. |
| **Unload** | `status='retired'` → unload after 30-second drain. |
| **Tenancy** | Models are tenant-shared by default. Per-tenant variants supported via lookup convention `models/<family>/<version>/<tenant_id>/model.onnx` falling back to `models/<family>/<version>/default/model.onnx`. Use case: country-specific contraband detector. |

### 4.6 Telemetry

**OpenTelemetry spans.**
- `inference.run` — attributes: `model.id`, `model.version`, `runner.type`, `ep.used`, `tenant.id`, `correlation.id`, `inference.preprocess_us`, `inference.run_us`, `inference.postprocess_us`, `outcome` ∈ `{ ok, runtime_error, oom, fallback, timeout }`.
- `inference.load` — attributes: `model.id`, `model.version`, `runner.type`, `ep.requested`, `ep.resolved`, `load.bytes`, `load.duration_ms`.

**Prometheus metrics.**
- `inference_requests_total{model, version, ep, outcome}` (counter)
- `inference_latency_seconds{model, version, ep, phase}` (histogram; phases = preprocess, run, postprocess)
- `inference_loaded_models{model, version, ep, status}` (gauge)
- `inference_load_failures_total{model, version, runner_type, reason}` (counter)

**Logging.** Model-load events with id/version/sha/ep, fallback chain transitions, hot-swap completions, integrity-check failures.

### 4.7 Open questions

| ID | Question | Status |
|---|---|---|
| Q-B1 | Concurrency model for multi-model on one DirectML adapter. Per-adapter mutex? Per-model session-pool? Default to per-model session, single inference at a time per session. | Open |
| Q-B2 | Cold-load latency for ONNX session init can be 2–5 s. Mitigation: warm at process start with metadata-derived dummy input. Confirm budget. | Likely closed (warmup is in spec) |
| Q-B3 | Tenant-keyed model variants — formalize the lookup convention and the admin UI for promoting per-tenant artifacts. | Open |
| Q-B4 | Should `IInferenceRunner` participate in v2's pre-render pipeline (running quality classifiers as part of pre-render) or stay decoupled? Recommend decoupled for v0; add a hook later if needed. | Recommendation logged |

---

## 5. DICOS readiness assessment

### 5.1 Standard landscape (as of 2026-04)

DICOS = **NEMA IIC 1**, the security-imaging analog of DICOM.
- v03 (2017): added Threat Detection Report (TDR), XRD, ETD, Threat Image Projection (TIP), enhanced cargo support.
- **v04 (2023):** current published revision. Adds streaming security image data, enriched threat reporting, explicit air-cargo modality support. Driven primarily by TSA.

Companion standards: **ANSI/IEEE N42.46** (cargo & vehicle imaging performance — under revision for multi-energy and AI-era systems); **IEEE N42.49.1-2024** updates display symbols for screening.

### 5.2 Vendor-side readiness (today)

| Vendor | Cargo product line | DICOS export today |
|---|---|---|
| Smiths Detection | HCV-Mobile, HCV-Multiview | On request; contract-dependent |
| Nuctech | MB1215, MT, NucMind | Limited; vendor-proprietary preferred |
| Rapiscan / OSI | Eagle, Eagle G60 | On request |
| Leidos / SAIC | VACIS family | Available for TSA-procured systems |
| **Current fleet** | FS6000, ASE | **Likely none — vendor-proprietary blobs.** Confirm with vendor reps before any DICOS work. |

The realistic 12–24 month picture: customs-procured cargo systems do not ship DICOS by default. Air-cargo and TSA-procured systems do. Pressure is building from regulators but fleet adoption is uneven.

### 5.3 .NET toolchain options

| Library | Lang | DICOS coverage | License | Verdict |
|---|---|---|---|---|
| **fo-dicom** | C# | DICOM-tag level read/write; no DICOS-specific IOD validation | MS-PL | **Default for in-process .NET.** Add DICOS IOD validation in adapter code. |
| **DCMTK 3.7.0** (Dec 2025) | C++ | DICOS Storage SOP classes since 3.6.2; most complete IOD support | BSD-3-Clause | **Reach for only if** fo-dicom misses a feature (e.g. TDR streaming). Wrap as side process; do not P/Invoke. |
| **GDCM** | C++ + bindings | File-level OK; weaker IOD validation | BSD-style | Skip — sparse maintenance. |
| **pydicom** | Python | File-level OK; partial IOD | MIT | Already in the v1 image-splitter venv. **Don't reach for it from .NET in v2.** |

### 5.4 Adapter implications

Add to `IScannerAdapter.Capabilities`:

```csharp
public sealed record ScannerCapabilities(
    bool SupportsLiveStream,
    bool SupportsHistoricalReplay,
    bool SupportsDicosExport,                 // NEW
    IReadOnlyList<DicosFlavor> DicosFlavors,  // NEW
    IReadOnlyList<string> NativeFormats);     // existing: e.g. "fs6000-v1"

public enum DicosFlavor { Cargo2D, CargoCT, TDR, ATR }
```

Parallel decode path:
- If `SupportsDicosExport && options.PreferDicos`: adapter emits DICOS files; core stores blob hash + metadata; `ParseAsync` decodes via fo-dicom into the same `ParsedArtifact` shape.
- Otherwise: existing vendor path (today's only path).

Map DICOS TDR → existing v2 entities:

| DICOS TDR concept | v2 entity | Notes |
|---|---|---|
| `ThreatRegion` (with bounding geometry, threat type, confidence) | `Finding` | `location_in_image jsonb` carries the box; `severity` derives from threat-category mapping |
| `OverallAssessment` | `Verdict.decision` | TSA-ish set ({Threat, Clear, …}) maps to v2's {Clear, HoldForInspection, Seize, Inconclusive} |
| Algorithm metadata (vendor / version / params) | new `algorithm_metadata jsonb` on `AnalystReview` (or on `Finding`, decide later) | Records that *this* finding came from *that* version of *this* model |

### 5.5 Recommended posture (locked 2026-04-28)

1. **Design-ready, deploy-deferred.** Include `SupportsDicosExport` and `DicosFlavors` in the v2 `IScannerAdapter` contract from Phase 7.0. Ship without an actual DICOS-capable scanner adapter — but the slot exists.
2. **Add fo-dicom as a package reference** to the imaging project **only when the first DICOS-capable scanner enters the fleet.** Not earlier.
3. **Do not translate today's FS6000/ASE blobs into DICOS as middleware.** It's wasted complexity and a source of subtle data loss (16-bit → 8-bit, channel collapse). Keep the proprietary path; add DICOS as a new path.
4. **Track the standard.** Subscribe to NEMA IIC 1 revision activity; the v04 → v05 work will likely add cargo-specific TDR shapes that change the mapping in §5.4.
5. **Switch trigger.** When ≥ 30 % of fleet supports DICOS export natively, switch the adapter contract default to DICOS-first.

### 5.6 Open questions

| ID | Question | Status |
|---|---|---|
| Q-C1 | Does FS6000 firmware (current rev) have a DICOS export option? Need vendor contact. | Open |
| Q-C2 | Storage of DICOS-as-received vs decode-once-and-discard. **Recommend store-as-received** for forensic provenance + signed chain of custody; derive previews. Locked. | Locked posture |
| Q-C3 | If our local ML emits findings, do we emit DICOS TDR for downstream consumers? Decide once a downstream consumer requests it. fo-dicom can write TDR. | Deferred |
| Q-C4 | Companion standards (`N42.46`, `N42.49.1`) — do we need to declare conformance for any tender? Compliance posture not currently required. | Open |

---

## 6. Layered improvements — specs

Each subsection below is a model or capability that builds on the contracts in §3–§5. They share the §3 template (problem → I/O → architecture → training data → loss/eval → inference budget → deployment & rollout → failure modes → open questions), and they integrate via §4's `IInferenceRunner`. Each section's open-question table uses a unique question-ID prefix (Q-D through Q-K) so cross-doc references stay stable; §7 carries the curated cross-cutting view.

### 6.1 Container-number OCR replacement (Donut / Florence-2)

*Tier: image-analysis-direct.*

#### 6.1.1 Problem

Extract the ISO 6346 container identifier (4-letter owner + 6-digit serial + 1-digit check, e.g. `MSCU1234567`) from a cropped plate ROI in the primary scan view. Today's path runs Tesseract.NET on the LUT-rendered 8-bit JPEG (`ContainerNumberOcrService.cs:14–115`). Tesseract is a 1990s-vintage line-OCR engine trained on document text; it degrades sharply on:

- **Stylized owner-prefix typography.** Hapag-Lloyd's serif `HLBU`, MSC's bold-condensed `MSCU`, and TGHU's narrow stencil all sit outside Tesseract's built-in language data.
- **Paint runs and weathering.** Drips bridge characters; sun-bleached digits drop a stroke; over-painted hyphens become noise.
- **Oblique angles.** Plates rotated > 8° from horizontal collapse Tesseract's line segmentation.
- **Mistaken-for-text container surfaces.** Tesseract has no notion of "this is a plate ROI," so it happily reads door-corrugation shadows as letters.

A document-AI VLM with image-conditioned text generation handles all four — that family of model is precisely what Donut, Florence-2, and Qwen2.5-VL were designed for ([Donut, arXiv 2111.15664](https://arxiv.org/abs/2111.15664); [Florence-2, CVPR 2024](https://openaccess.thecvf.com/content/CVPR2024/papers/Xiao_Florence-2_Advancing_a_Unified_Representation_for_a_Variety_of_Vision_CVPR_2024_paper.pdf)). The task is also bounded enough (11 characters, fixed alphabet, computable check digit) that a small distilled model is sufficient — we do not need a 3B-parameter generalist.

**Locked acceptance:** 2026-04-28
- ≥ 95 % exact-match accuracy on the production plate corpus (held-out test set, see §6.1.4).
- p95 latency ≤ 200 ms on lane-PC iGPU (Iris Xe via DirectML or OpenVINO GPU plugin).
- p95 latency ≤ 500 ms on lane-PC CPU.
- Check-digit pass rate ≥ 98 % on accepted predictions (post-process gate, see §6.1.7).
- Zero outbound network calls per scan in production.

#### 6.1.2 Inputs / outputs

**Input.** `(3, 384, 384)` float32 tensor. Source: an upstream plate-ROI detector (carried forward from the existing detection step in §6.5; until that lands, the v1 contour-based ROI is reused unchanged) crops a tight bounding box around the plate, rescaled long-edge to 384 px and zero-padded to square. Normalization: ImageNet mean/std on the rendered 8-bit channels — the plate is on the container exterior, not inside the cargo, so the dual-energy Z signal from §1.3 does not apply here; rendered RGB is the right input.

**Phase 2 ablation:** test a `(1, 384, 384)` mono-channel variant fed from the LE channel directly. Hypothesis: weathered painted plates have higher contrast in raw LE than in the LUT-rendered preview. Out of scope for first deployment.

**Output.** Variable-length token sequence decoded to a UTF-8 string. The model's vocabulary is constrained at decode time to `{A–Z, 0–9, <eos>}` plus a `<unreadable>` sentinel. The post-process — **not the model** — performs ISO 6346 check-digit validation. Returned to the caller as a `Finding` with `finding_type = "ContainerNumber"`, `severity = "Informational"`, and a payload of `{ predicted, confidence, check_digit_passed, decode_path, model.id, model.version }`.

#### 6.1.3 Architecture

| Choice | Decision | Rationale |
|---|---|---|
| Family | **Florence-2-base** (0.23 B params, MIT, Microsoft) | CPU-runnable, OpenVINO-friendly, task-prompt mechanism (`<OCR>`, `<OCR_WITH_REGION>`) is a clean fit for our cropped-ROI input |
| Fallback family | **Donut** (`naver-clova-ix/donut-base`, ~200 M, MIT) | Image-to-text encoder-decoder; battle-tested on document parsing; identical input shape and ONNX export path as a drop-in alt artifact |
| Fine-tune scope | LoRA adapters on the decoder + full fine-tune of the patch embedding | Keeps artifact small (LoRA rank 16, ~20 MB on top of frozen base) and preserves Florence-2's prior on stylized typography |
| Task prompt | Fixed `<CONTAINER_OCR>` synthetic task token added to the tokenizer during fine-tune | Keeps Florence-2's public task palette untouched; one token, one task |
| Decoder constraint | Constrained beam search restricted to `[A-Z]{4}[0-9]{7}` ∪ `<unreadable>` | Hard-codes the ISO 6346 grammar; eliminates malformed outputs without changing the model |
| Quantization | INT8 weight-only on encoder; FP16 decoder | Encoder is the latency floor on iGPU; decoder is short-sequence and benefits from FP16 over INT8 numerics |
| Runtime | OpenVINO via the `IInferenceRunner` OpenVINO runner (new sibling to OnnxRuntime, §4.1) | Florence-2 + OpenVINO is the documented fast path on Intel iGPU; ONNX Runtime + DirectML is the fallback. Both behind the same `IInferenceRunner` contract |

**Rejected.** Qwen2.5-VL-3B (3 B params, > 1 s p95 on iGPU even quantized — does not fit the 200 ms bar); Tesseract LSTM mode (does not address stylized/oblique cases); PaddleOCR/EasyOCR (two-stage detect+recognize is wasted work given upstream ROI); GPT-4o/Claude API (violates principle 2); custom CNN+CTC from scratch (lower ceiling without a much larger curated corpus). All locked 2026-04-28.

#### 6.1.4 Training data

| Source | Description | Estimated volume |
|---|---|---|
| **Synthetic generator** | ISO 6346–valid 11-char generator + plate renderer with 10+ owner-prefix typefaces, weathering masks, occlusion, 6-DoF perspective warps | 200 k+ ROIs, on-demand |
| **Real plates from v1** | `FS6000Images` ROIs paired with v1 Tesseract output where check-digit passed (silver) and analyst-corrected values (gold) | 30–80 k ROIs (estimate; confirm by query result) |
| **Cloud-teacher silver labels** | One-off Florence-2-large or Claude pass over real ROIs that v1 Tesseract failed on or check-digit-rejected | 5–15 k ROIs |
| **Holdout test (locked)** | 5 % stratified by owner-prefix family (top 20 prefixes) and quality tier (clean / weathered / oblique / occluded) | Frozen at training time |

**Harvesting from v1 without v1 edits.** A read-only query against `nickscan_production.public.fs6000images` (joined to `public.containernumbercorrections` for analyst overrides) extracts ROI bytes + v1 prediction + analyst correction + capture timestamp. v1 column casing follows the lowercase convention from memory. Harvest tooling lives in v2 under `tools/v1-plate-harvest/`, sibling to `tools/v1-label-export/` from Q-A2.

**Augmentation.** Mild perspective warp (±5°), brightness/gamma jitter (±20 %), motion blur up to 5 px, plate-edge crop jitter (±8 px), synthetic paint-run overlay. **Class balance:** stratify by owner-prefix family with inverse-frequency loss weighting; over-sample `<unreadable>` to ≥ 10 % of training mix. **Splits:** 80/10/10, no synthetic ROIs sharing a generator seed across splits.

#### 6.1.5 Loss & evaluation

Token-level cross-entropy with label smoothing 0.05; auxiliary eval-only sequence reward = check-digit pass.

| Metric | Target |
|---|---|
| **Exact match (EM)** | ≥ 95 % — primary acceptance bar |
| **Character Error Rate (CER)** | ≤ 1.5 % |
| **Check-digit pass rate** on accepted predictions | ≥ 98 % |
| **`<unreadable>` precision/recall** | precision ≥ 0.85, recall ≥ 0.6 |
| Latency p50 / p95 / p99 by EP | gated to §6.1.6 budget |

Stratified slices by owner-prefix family (top 20), quality tier, and source scanner serial; > 5 % EM regression in any single slice blocks promotion. A/B harness routes 5 % of production scans through both Florence-2 and v1 Tesseract, logs disagreements with analyst-confirmed outcome.

#### 6.1.6 Inference budget

| Profile | Florence-2-base | Donut |
|---|---|---|
| p95 CPU (i7-1265U) | ≤ 500 ms | ≤ 600 ms |
| p95 iGPU (Iris Xe via OpenVINO GPU) | ≤ 200 ms | ≤ 240 ms |
| p95 dGPU (RTX A2000 via CUDA) | ≤ 50 ms | ≤ 60 ms |
| INT8/FP16 disk size | ~140 MB | ~120 MB |
| Working memory peak | ≤ 1.2 GB | ≤ 1.0 GB |
| Cold-load penalty | ≤ 4 s (warm at process start) | ≤ 4 s |

#### 6.1.7 Deployment & rollout

**Artifact path.** `<storage_root>/models/container-ocr/v{N}/model.onnx` (and `model.openvino.xml`+`.bin` for the OpenVINO runner) plus `model.metadata.json` carrying `{ sha256, training_set_hash, eval_metrics, base_model }`. Loaded via `IInferenceRunner` (§4) — call site doesn't know whether it got Florence-2 or Donut, only the logits tensor.

| Phase | Behavior | Gate |
|---|---|---|
| 0. Dev eval | Frozen test-set run | EM ≥ 95 %, CER ≤ 1.5 %, latency budgets met |
| 1. Shadow | New model alongside Tesseract; output logged with `decode_path = "shadow"`, unused | ≥ 14 days, observed EM ≥ 95 %, no per-prefix slice regression |
| 2. Primary + safety net | New model primary. **Safety net:** if `check_digit_passed == false` OR `confidence < 0.85`, fall back to ported Tesseract; if that also fails, queue for manual entry on analyst console (`InspectionWorkflow` enters `ContainerNumber.Pending` sub-state) | < 5 % safety-net-fired rate sustained 7 days |
| 3. Primary + sample audit | New model 100 %. Tesseract retained as safety-net only. Florence-2-large or Claude vision runs on 1 % sample for drift detection | SLO: 30-day EM drift ≤ 2 %, check-digit pass ≥ 98 % |

`Finding` records the prediction; `ScanArtifact` of `kind = "PlateRoi"` is the input; `InspectionCase.subject_identifier` is set only after check-digit pass + confidence gate, otherwise remains `null` and the workflow surfaces the manual-entry chip.

#### 6.1.8 Failure modes & guards

- **Oblique > 15° rotation.** Synthetic generator covers ±25°; if upstream ROI detector reports rotation > 20°, prepend `<ROTATED>` to task prompt; re-balance training mix if EM on slice drops below 90 %.
- **Partial occlusion.** `<unreadable>` class trained on systematically masked ROIs; when ROI detector flags > 20 % occlusion AND model emits a non-`<unreadable>` prediction, raise confidence-gate threshold from 0.85 to 0.92.
- **Weathered/painted-over plates.** Training augmentation covers; check-digit gate is the ultimate filter.
- **Mistaken-for-text on container surfaces.** Upstream ROI detector is plate-shape-aware; OCR only sees ROIs. If no ROI is produced, OCR is skipped — never fall back to "scan whole image."
- **Decoder loop / repetition.** Hard token budget `T = 16` + constrained beam search; if no valid sequence emerges, prediction is `<unreadable>`.
- **Owner prefix not in BIC registry.** Confidence clamped to `min(confidence, 0.7)`, forcing safety-net gate even when the model is "sure."

#### 6.1.9 Open questions

| ID | Question | Status |
|---|---|---|
| Q-D1 | Does the upstream plate-ROI detector ship in Phase 7.1, or does first deployment use the v1 contour detector ported as an internal helper? Latency budget assumes the new detector. | Open |
| Q-D2 | Florence-2-base vs Florence-2-large for the offline cloud-teacher pass on hard ROIs — is the 0.77 B variant's latency acceptable as a one-off labeling step? | Open |
| Q-D3 | LoRA-only vs full fine-tune for the decoder. Plan: LoRA in Phase 1, ablate full fine-tune if Phase 1 misses the 95 % bar. | Recommendation logged |
| Q-D4 | Confidence calibration. Mean per-token softmax is crude; consider temperature scaling or a per-prefix logistic calibrator. Affects safety-net threshold (currently 0.85, unvalidated). | Open |
| Q-D5 | OpenVINO runner ship cadence — same release as the model, or follow-up with OnnxRuntime+DirectML first? Gates the 200 ms iGPU bar. | Open |
| Q-D6 | Manual-entry surfacing. Which v2 UI surface exposes the `ContainerNumber.Pending` chip — analyst console only, or also the lane-PC operator? | Open |
| Q-D7 | Retrain cadence. Working assumption: when ≥ 5 k new analyst-corrected labels accumulate, or quarterly. | Deferred to Phase 7.4 |
| Q-D8 | Tenant-keyed model variants. Different ports see different owner-prefix distributions. Worth a per-location LoRA on a shared base, or one global model? Decide once ≥ 2 locations live. | Deferred to Phase 7.3 |

### 6.2 HS-conditioned anomaly detection

*Tier: image-analysis-direct.*

#### 6.2.1 Problem

Closed-set classifiers (cargo-tuned YOLO, RT-DETR — see §1.2) can only flag threat categories that appear in their training labels. Customs interdiction is dominated by **unknown unknowns**: novel concealment methods, never-before-seen contraband geometries, mis-declared cargo whose appearance does not match its declared HS code. The v1 pipeline has no answer to this category — heuristic detection fires only on hand-coded shapes, and the Claude vision call is scoped to container-strip splitting, not threat detection.

The v2 answer: score every scan against a **distribution of benign cargo conditioned on the BOE-declared HS code**. A laptop shipment that looks nothing like prior `HS 8471` shipments is anomalous *for its declared category* even if every individual feature is benign-looking. This is the standard one-class anomaly-detection framing from the manufacturing-defect literature, applied — for the first time in production cargo X-ray, as far as published work shows — to customs.

The output is **not a verdict**. It is a triage signal feeding `Finding` records that drive analyst priority and (later, after calibration) `Verdict.decision = HoldForInspection`. **The model never auto-seizes** (see §6.2.7).

**First-mover note (locked 2026-04-28).** Searches across cargo-X-ray and baggage-screening literature through Q1 2026 show DINOv2 + PatchCore / FastFlow / DiffusionAD applied to MVTec, Visa, MPDD, medical CT — but **no published cargo X-ray application**. The anomaly-detection community has not crossed into customs imaging; the customs-AI community has not adopted modern one-class methods. Publishable result + defensible commercial moat.

**Locked acceptance:** 2026-04-28
- **Image AUROC ≥ 0.85** on a curated seizure-vs-clear holdout from `post_hoc_outcome` customs feedback.
- **Pixel/spatial AUROC ≥ 0.75** against analyst-drawn ROI boxes.
- **F1@PRO ≥ 0.55** for region-overlap evaluation.
- **p95 inference latency ≤ 400 ms** on lane-PC iGPU (DirectML).
- Zero outbound network calls per scan in production.

#### 6.2.2 Inputs / outputs

**Input — preferred path.** `(3, H, W)` raw float32 with channels `(HE, LE, material/Z_eff)` from the adapter. Long-edge resize to 518 px (DINOv2-B/14 native — patch grid divides cleanly: 518 / 14 = 37 patches/edge). Per-channel percentile stretch 0.5–99.5 % → [0, 1]. Final tensor: `(3, 518, 518)` letterboxed.

**Input — fallback path.** When `IScannerAdapter` does not surface raw HE/LE channels, accept `(1, H, W)` LUT-pseudocolor and broadcast to 3 channels. Adapter signals which path applies via a new `ScannerCapabilities.RawChannelsAvailable` flag (Q-E10). Documented penalty: ~5 pp lower image AUROC ([arXiv 2108.12505](https://arxiv.org/abs/2108.12505)) — fallback path **does not satisfy the 0.85 bar**; degraded scans get half-weight contribution to analyst priority.

**HS conditioning.** From `AuthorityDocument` of `DocumentType = CustomsDeclaration` linked to the `InspectionCase`, extract the BOE's HS code and use the **HS-2 prefix** (chapter level). HS-4 ablation deferred to Phase 2.

**Outputs.**

| Output | Shape | Use |
|---|---|---|
| `image_score` | scalar [0,1] | Sigmoid-calibrated anomaly score; primary signal |
| `heatmap` | `(37, 37)` upsampled bilinear | Per-patch nearest-neighbor distance for analyst overlay |
| `hs_bank_used` | string | HS-2 code whose memory bank scored this scan |
| `confidence` | scalar [0,1] | Bank-coverage confidence |

| Score band | `Finding.severity` | Workflow effect |
|---|---|---|
| `< 0.30` | none | Case advances on normal queue |
| `0.30–0.60` | `Informational` | Heatmap overlay shown; queue priority unchanged |
| `0.60–0.85` | `Warning` | Priority bump; heatmap overlay default-on |
| `≥ 0.85` | `Critical` | Auto-route to senior analyst queue; flagged in `CaseCompleteness.violations` (information-only — does not block) |

`Finding.location_in_image jsonb` populated from top-K (default K=3) heatmap peaks; `finding_type = "anomaly_score"` distinct from closed-set `"object_detection"`.

#### 6.2.3 Architecture

| Choice | Decision | Locked |
|---|---|---|
| Backbone | DINOv2 ViT-B/14 ([Meta, GitHub](https://github.com/facebookresearch/dinov2)), pretrained then fine-tuned on benign cargo | 2026-04-28 |
| Fine-tuning losses | DINO + iBOT (self-supervised, no labels needed beyond "this is benign") | 2026-04-28 |
| Anomaly head — primary | **PatchCore** with per-HS-2 memory banks ([FR-PatchCore Sensors 2024](https://www.mdpi.com/1424-8220/24/5/1368)) | 2026-04-28 |
| Anomaly head — Phase 2 ablation | **FastFlow** ([ResearchGate](https://www.researchgate.net/publication/356249645_FastFlow_Unsupervised_Anomaly_Detection_and_Localization_via_2D_Normalizing_Flows)) for tighter pixel-level localization | Open |
| Anomaly head — research-tier | **DiffusionAD** ([survey arXiv 2501.11430](https://arxiv.org/html/2501.11430v3)) | **Rejected for v1** |
| Conditioning granularity (v1) | **HS-2 (chapter)** — ~100 banks, high coverage per bank | 2026-04-28 |
| Memory-bank sampling | k-greedy coreset (PatchCore default), 1 % subsample target | 2026-04-28 |
| Patch features | DINOv2 layer 9 + 11 concatenated, L2-normalized | 2026-04-28 |

**Why PatchCore primary.** Most operationally boring of the three options — and that is the recommendation. Memory-bank-with-NN (1) requires no per-class hyperparameter tuning beyond coreset rate, (2) deploys as static memory bank + DINOv2 feature extractor, (3) has well-understood production deploys at industrial-vision firms, (4) friendly to low-data HS chapters (works at 100 examples per class).

**Why FastFlow Phase-2.** Tighter pixel-level heatmaps (continuous likelihood vs PatchCore's discrete-NN distance). Trigger to start the ablation: Phase 7.1 has been live ≥ 60 days and we have ≥ 50 seizure cases with ROI boxes for at least 5 HS chapters.

**Why DiffusionAD rejected for v1.** 5–50 forward passes per scan + per-class diffusion training (~10× normalizing flows) is incompatible with lane-PC budget. Re-evaluate post-cutover if hardware includes a real GPU and the field has a credible cargo-X-ray DiffusionAD baseline.

**Why HS-2 not HS-4.** HS-4 fragments the data: most HS-4 codes will have < 100 production benign scans even after a year. HS-2 chapters reach 100+ within weeks. HS-4 ablation only on chapters where count exceeds bootstrap threshold.

**Integration.** Two `InferenceModel` rows: `dinov2-cargo-features` (single ONNX-exported DINOv2 ViT-B/14, produces `(N, 768, 37, 37)` patch tokens) and `patchcore-bank-hs02-{XX}` (one artifact per HS-2 chapter, ~50 MB each). Two-stage call: feature extraction once, then bank lookup keyed by case's HS-2. Banks are tiny (~5 GB total for ~100 chapters); load all at process start.

#### 6.2.4 Training data

**One-class regime.** Fine-tune and per-HS memory banks see **only benign cargo**. "Benign" = `Verdict.decision = Clear` with `post_hoc_outcome.seized_count = 0` after a 90-day quiet period.

| Source | Description | Estimated volume (12 mo at Tema) |
|---|---|---|
| Primary | `Scan` rows where `Verdict.decision = Clear`, `seized_count = 0`, 90-day quiet elapsed | ~150–300 k scans |
| Secondary | Same predicate, 30-day quiet — for sparse HS chapters | adds ~30 k |
| Holdout (test) | ~500 confirmed seizures + ~5 k matched clears stratified by HS-2 | locked at training time |

**HS distribution check (mandatory).** Histogram per HS-2; flag chapters with < 100 examples for rare-HS bootstrap; flag chapters whose declared HS-2 distribution diverges > 2× from global trade-flow.

**Augmentation (DINOv2 fine-tune only).** Horizontal flip; brightness/gamma jitter (±10 %); per-channel intensity jitter (±5 %). **No rotation** (gravity axis is informative). **No CutOut** (handled implicitly by iBOT). Banks see un-augmented features.

**Splits.** 95/5 fine-tune train/val. Per-HS bank fit uses all primary-pool scans for that HS-2. Holdout test never seen during fine-tune or bank fit.

#### 6.2.5 Loss & evaluation

**Loss (DINOv2 fine-tune).** Standard DINO + iBOT self-supervised; warm-start from public DINOv2-B/14; 50 k steps at lr 1e-4 with cosine decay.

**Loss (PatchCore head).** No gradient — k-greedy coreset over patch features, stored as L2-normalized vectors in FAISS IVF-Flat (~10 k vectors per chapter, sub-millisecond NN).

**Inference scoring.** Per-patch nearest-neighbor cosine distance; image score = max-of-patch, sigmoid-calibrated to [0,1] using validation distribution per HS-2 (Platt-scaling **per chapter** — absolute distance scale varies).

| Metric | Target | Use |
|---|---|---|
| Image AUROC | ≥ 0.85 | Primary acceptance bar |
| Pixel AUROC | ≥ 0.75 | Spatial localization |
| F1@PRO (MVTec convention) | ≥ 0.55 | Region overlap |
| AUPRC | report only | Robustness on imbalanced holdout |
| Per-HS coverage | ≥ 0.85 image AUROC on chapters with ≥ 500 train scans | Honest reporting |
| Calibration ECE | ≤ 0.10 | Score thresholds in §6.2.2 require calibration |
| Latency p95 | ≤ 400 ms iGPU, ≤ 1.2 s CPU | Operational |

**A/B harness.** Shadow run on a fraction of traffic uses a single global memory bank (skipping conditioning); if conditioned model's lift over global drops below 5 pp AUROC for 7 days, raise alert.

#### 6.2.6 Inference budget

| Profile | Target | Notes |
|---|---|---|
| DINOv2-B/14 params | ~86 M | Last 4 transformer blocks unfrozen for fine-tune |
| FP16 backbone disk | ~170 MB | Cold-loaded once at process start |
| Per-HS-2 bank | ~30–60 MB | ~10 k coreset patches × 768 dims |
| Total bank footprint | ~5 GB | All-active at process start |
| p95 backbone (i7-1265U CPU) | ≤ 1.2 s | Degraded, not target |
| **p95 backbone (Iris Xe iGPU, DirectML)** | **≤ 400 ms** | **Target deployment profile** |
| p95 backbone (RTX A2000 dGPU, CUDA) | ≤ 60 ms | When available |
| p95 bank lookup (FAISS) | ≤ 3 ms | Negligible |
| Working memory peak | ≤ 1.5 GB | |
| Cold-load penalty | ≤ 8 s | Backgrounded at process start |

**Hardware reality check.** 400 ms p95 on Iris Xe is **the pacing constraint**. Escape valves: (1) DINOv2-S/14 (~22 M params, ~100 ms p95, ~3 pp AUROC penalty), (2) per-Location inference appliance (single dGPU per Location), (3) defer to Phase 7.3+. **Q-E1.**

#### 6.2.7 Deployment & rollout

| Phase | Behavior | Gate to next |
|---|---|---|
| 0. Dev eval | Frozen holdout from §6.2.4 | Image AUROC ≥ 0.85, pixel AUROC ≥ 0.75 on chapters with ≥ 500 train scans |
| 1. Shadow | Production scans get the model run; output written to `Finding` rows with `severity = Informational`; analyst UI hides heatmap; queue priority unaffected. Operators see the score in a debug dashboard | ≥ 30 days; AUROC on production-truth ≥ 0.80; no calibration drift > 0.05 ECE per chapter |
| 2. Visible advisory | Heatmap overlay shown (default-on for `Critical`, default-off for `Warning` and below); queue priority bumped per §6.2.2; **no automated `Verdict` flips** | ≥ 60 days; analyst-feedback shows heatmap helpful (`roi_dwell_in_heatmap_peak_ms / roi_dwell_total_ms ≥ 0.4` on `Critical`); seizure recall on `Critical` ≥ 70 % |
| 3. Triage assist | `Critical` scores cap at `Verdict.decision = HoldForInspection` only — **never `Seize`**. Senior analyst still has final say. | Sustained Phase-2 metrics for 90 days |

**Safety net (locked 2026-04-28).** Model **never auto-seizes**. Hardest cap is `HoldForInspection` (analyst-mandatory review), unlocked only at Phase 3. Until then, anomaly score only changes priority + visibility.

**Analyst feedback loop.** New fields on `AnalystReview`: `anomaly_score_at_open`, `roi_dwell_in_heatmap_peak_ms`, `analyst_agreement` ∈ `{ confirmed, dismissed, partial, not_visible }`. Feeds quarterly retraining via §6.4.

#### 6.2.8 Failure modes & guards

- **HS-misdeclaration evasion.** Cocaine declared as `HS 0901` (coffee) gets scored against the coffee bank. **Guard:** run two scores in parallel — per-HS (primary) + global benign (fallback bank fit on all chapters). Surface `score_hs / score_global` ratio; large gap → flag for **manifest-vs-X-ray scrutiny** (§6.3 stub). Cross-check with declared value/weight/origin (`AuthorityDocument.payload`). Audit trail records the bank used and the gap.
- **Rare-HS bootstrapping.** Chapters with < 100 benign examples cannot fit a meaningful coreset. **Tiers:** `count < 30` → no per-HS bank, use global with `confidence = 0`; `30 ≤ count < 100` → per-HS bank with `confidence = 0.5`, thresholds × 1.2 to avoid over-firing; `count ≥ 100` → standard fit, `confidence = 1.0`. Q-E2 covers synthetic augmentation from §6.6.
- **Mixed-HS shipments.** v1 uses highest-declared-value line's HS-2; `Finding.basis` records the choice. Phase-2: ensemble across declared HS-2 banks. Q-E3.
- **Empty containers.** Separate stratum at fit time, keyed on `subject_type = Container, declared_empty = true`.
- **Scanner drift / calibration shift.** Stratify holdout by `scanner_device_instance_id`; per-scanner KL-divergence drift detector; pin training data to scanners with ≥ 90 days stable post-calibration.
- **Distribution shift over time.** Quarterly retrain via §6.4; drift alert on A/B harness.
- **Adversarial perturbation.** Less concerning in cargo than in face-recognition — the adversary cannot meaningfully add gradient-shaped noise to a physical container. Skip.

#### 6.2.9 Open questions

| ID | Question | Status |
|---|---|---|
| Q-E1 | Lane-PC iGPU latency budget. 400 ms p95 on Iris Xe is the load-bearing assumption. Validate on real hardware in Phase 7.0; if short, switch to DINOv2-S/14 or per-Location inference appliance. | Open — needs hardware validation |
| Q-E2 | Synthetic data for rare-HS bootstrap. Does mixing §6.6 TIP synthesis into a real-data memory bank help or poison the distribution? | Deferred to post-§6.6 |
| Q-E3 | Mixed-HS shipment scoring. Better strategy: ensemble across declared HS-2 lines weighted by declared-value share. Ablation only after ≥ 1 k mixed-HS holdout cases. | Open |
| Q-E4 | HS-4 conditioning ablation. Per-chapter answer (HS 8471 has breadth that helps; HS 0901 is narrow enough that HS-2 is fine). | Open |
| Q-E5 | INT8 / FP16 quantization of cargo-fine-tuned DINOv2-B/14. Vision transformers are quantization-sensitive; needs ablation. | Open |
| Q-E6 | FastFlow Phase-2 trigger. Confirm "60 days live + 50 seizures with ROI boxes across 5 chapters" is the right minimum bar. | Open |
| Q-E7 | DiffusionAD revisit cadence. Trigger: peer-reviewed cargo-X-ray DiffusionAD result with image AUROC ≥ 0.90 on a public benchmark, plus production hardware that includes a real GPU. | Open |
| Q-E8 | Pixel AUROC ground truth source. v2 captures `roi_interactions jsonb` but not seizure-bounding-box ROIs. Need a UI affordance for the seizing officer to draw the seized-item ROI. | Open — UI dependency |
| Q-E9 | First-mover publication. Decide venue and data-release posture. | Open — separate decision |
| Q-E10 | `ScannerCapabilities.RawChannelsAvailable` flag — must be added to `IScannerAdapter.Capabilities` and threaded through §3 `ParsedArtifact` contract before fallback path is implementable. | Open — contract change |

### 6.3 Manifest ↔ X-ray consistency scorer

*Tier: image-analysis-direct.*

#### 6.3.1 Problem

Customs scanning has one job VLM-vendors don't ship well: confirm that what the importer **declared** matches what the scanner **sees**. Today every cargo X-ray system either (a) leaves consistency-checking entirely to the analyst's eyeball or (b) bolts on a closed-source "manifest verification" SKU that operates only on the rendered preview and only on the vendor's own document parser. Neither generalises across authorities, neither survives a country switch, and neither uses the dual-energy Z-channel that physically distinguishes "1 t of cotton garments" from "1 t of cigarettes" once both are in identical 20-foot containers.

We already hold the document side via `AuthorityDocument` rows the `IExternalSystemAdapter` pulls per case — Ghana ICUMS gives them to us free. We already hold the X-ray side as `ScanArtifact` rows preserving the raw `(HE, LE, material)` channels per §1.3. The remaining problem is wiring a **per-line-item scorer** that compares the two, emits per-line `Finding`s plus a composite consistency score on the `InspectionCase`, without dragging vendor or country names into core domain.

This is the single highest-value v2 ML feature that has no off-the-shelf substitute.

**Locked acceptance:** 2026-04-28
- Separate clear vs hold cases at AUROC ≥ 0.75 on the held-out customs feedback set.
- Per-line-item discrepancy precision ≥ 0.70 on analyst-confirmed mismatches.
- Zero auto-seizures. Score outputs are advisory `Finding`s; `Verdict` decision stays human.
- p95 end-to-end latency ≤ 15 s per case (this is **not** a real-time gate — runs alongside analyst pickup).

#### 6.3.2 Inputs / outputs

**Inputs.**

1. `AuthorityDocument` rows where `document_type ∈ {Boe, Cmr, ImportDeclaration, CommercialInvoice}`. Concrete payload typed under `NickERP.Inspection.Authorities.CustomsGh` but the scorer reads only vendor-neutral fields: `line_items[].{hs_code, declared_qty, qty_unit, declared_weight_kg, packaging, description_text}`. Asks `IAuthorityRulesProvider.ValidateAsync(case)` first; only validated docs flow to stage 1.
2. `ScanArtifact` rows where `artifact_kind ∈ {Primary, Material}`. **Raw float32 channels preferred** per §1.3. If a scanner adapter cannot expose raw channels, the scorer logs `ChannelDegraded` and falls back to single-channel grayscale with reduced density-discrimination headroom.
3. Per-tenant `commodity_density` reference table — Postgres `jsonb` lookup `nickerp_inspection.hs_commodity_reference` keyed by HS-6, holding `{ z_eff_min, z_eff_median, z_eff_max, expected_density_kg_per_m3, typical_packaging[], confidence ∈ {Authoritative, Curated, Inferred} }`.
4. `InspectionWorkflow.current_state` ∈ {`Validated`, `Assigned`} (after authority docs arrive, before/during analyst review).

**Outputs.**

1. Per-line `Finding` rows — `finding_type = "ManifestXrayMismatch"` (new enum value), `severity ∈ {Info, Low, Medium, High}`, `location_in_image jsonb` carrying `{primary_artifact_id, bbox, grounded_label}`, `note` machine-generated rationale.
2. One composite `consistency_score ∈ [0, 1]` per case, persisted on a sidecar `ml_findings_scratch` row keyed by `(case_id, model_version)` — **not** on `AnalystReview.confidence_score`, which would pollute analyst-calibration signal (Q-F2).
3. `MlTelemetry` rows per stage for active learning (§6.4).
4. **Never a `Verdict`.** Score contributes advisory text to `Verdict.basis` but `Verdict.decision` is set by `decided_by_user_id` exclusively.

#### 6.3.3 Architecture (locked 2026-04-28)

Three stages, each behind `IInferenceRunner`. End-to-end VLM (single model takes raw X-ray + document image and emits a verdict) is **rejected for v1** — too data-hungry, too opaque for analyst trust, and cannot expose per-line attribution which is half the point.

```
[AuthorityDocument]                          [ScanArtifact (raw HE/LE/Z)]
        │                                                │
        ▼                                                │
┌──────────────────┐                                     │
│ Stage 1          │                                     │
│ Doc-parse        │                                     │
│ Donut primary    │                                     │
│ Qwen2.5-VL-7B    │                                     │
│ on disagreement  │                                     │
└──────┬───────────┘                                     │
       │ line_items[]                                    │
       ▼                                                 │
┌──────────────────┐                                     │
│ HS lookup        │                                     │
│ (rules provider) │                                     │
└──────┬───────────┘                                     │
       │ (HS6, qty, weight, packaging, prompt_text)      │
       ▼                                                 ▼
                ┌─────────────────────────────────────┐
                │ Stage 2                             │
                │ Grounded detection                  │
                │ Florence-2-large, text-prompted     │
                └────────────────┬────────────────────┘
                                 │ bboxes per prompt
                                 ▼
                ┌─────────────────────────────────────┐
                │ Stage 3                             │
                │ Per-line scoring                    │
                │  count_match × spatial_occupancy ×  │
                │  density_vs_declared                │
                └────────────────┬────────────────────┘
                                 ▼
                       Findings + composite score
```

| Stage | Model | Runtime | Why |
|---|---|---|---|
| 1 — Doc parse | **Donut** (`donut-base-finetuned-cord-v2`, FP16) primary; **Qwen2.5-VL-7B** ([arXiv 2502.13923](https://arxiv.org/abs/2502.13923)) INT8/AWQ fallback | OnnxRuntime DirectML for Donut; OnnxRuntime CUDA for Qwen | Donut is OCR-free, deterministic, ~200 MB INT8, p95 ≤ 1.5 s on iGPU; handles 90 %+ of well-formed BOE/CMR. Qwen2.5-VL fires only when (a) Donut confidence < 0.6 on any line, (b) doc rotated > 5° / partial / handwritten, (c) authority schema unrecognised. Fallback target: ≤ 10 % rate. |
| 2 — Grounded detection | **Florence-2-large** ([HuggingFace](https://huggingface.co/microsoft/Florence-2-large)) with `<CAPTION_TO_PHRASE_GROUNDING>` task | OnnxRuntime DirectML | MIT-licensed, runs on iGPU, accepts free-text grounding. Open-vocab YOLO variants (YOLO-World, Grounding DINO) **rejected** for v1 — published cross-vendor X-ray transfer scores ([CLIP X-ray adaptation, arXiv 2406.10961](https://arxiv.org/abs/2406.10961)) consistently show Florence-class VLMs absorbing the pseudocolor-LUT shift better with smaller fine-tune budgets. |
| 3 — Scorer | Pure C# (no ML) — three sub-scores | In-process | Transparent, debuggable, analyst-explainable. Hiding it inside another VLM trades AUROC for opacity. |

**Per-line scoring math:**

```
count_match_i        = clip(detected_i / declared_qty_i, 0, 1.5)
spatial_occupancy_i  = (Σ bbox_area_i) / expected_area_for_packaging_i
density_vs_declared_i = 1 − |z_eff_observed_i − z_eff_reference_i| / z_eff_window_i

sub_score_i = w_count    * f(count_match_i)
            + w_spatial  * g(spatial_occupancy_i)
            + w_density  * h(density_vs_declared_i)

case_score = aggregate(sub_score_i, declared_weight_i)
```

`f`, `g`, `h` are clipped quadratic kernels centred at "declared matches reality" (1.0 for count and density, packaging-dependent for spatial). Initial weights `(w_count, w_spatial, w_density) = (0.3, 0.3, 0.4)` — density carries heaviest weight because it's the channel customs vendors don't expose. Calibrated against held-out feedback, **not hardcoded** in core domain (Q-F3).

**Plumbing.** `IAuthorityRulesProvider.ValidateAsync` gates whether the scorer runs. Three `IInferenceRunner.LoadAsync` model entries: `manifest-doc-parse`, `manifest-doc-parse-fallback`, `manifest-grounded-detect`. HS → free-text prompt resolution lives in `IAuthorityRulesProvider.ResolveCommodityPromptAsync(hs6)` — country-specific synonyms in `Authorities.CustomsGh`, never core. The pipeline is one `IConsistencyScorer` use-case in `NickERP.Inspection.Application.ManifestConsistency`, invoked by an `InspectionWorkflow` transition `Validated → Scoring → Assigned`.

#### 6.3.4 Training data

| Source | Description | Estimated volume |
|---|---|---|
| BOE / CMR document corpus | v1 `AuthorityDocument` rows with `payload jsonb` already structured by ICUMS ingestor; export-only (Q-F4) | ~120 k documents |
| X-ray + matched declarations | `Scan` ↔ `AuthorityDocument` joined on `case_id` with `Verdict` set + `post_hoc_outcome` populated | ~25–40 k case-pairs after 60-day post-hoc settle |
| Commodity density reference seed | WCO HS classification + USDA/FAO + analyst-curated top-200 by case volume | Target: ~5 k HS-6 codes, 95 %+ HS-2 coverage |
| Grounded-detection fine-tune set | Florence-2 needs box-level supervision per HS prompt; from analyst-confirmed `Finding.location_in_image` | ~8 k bboxed prompts; supplement with §6.6 TIP synthetic |
| Holdout test (locked) | Frozen at training time, stratified by HS-2, location, `Verdict.decision`; includes "feedback gold" with downstream customs confirmation | 5 % of supervised pairs |

Augmentation: pseudocolor-LUT canonicalisation per [Sci. Data 2026](https://www.nature.com/articles/s41597-026-07149-8); horizontal flip; mild perspective warp; simulated metal-plate occlusion.

#### 6.3.5 Loss & evaluation

| Stage | Primary metric | Target |
|---|---|---|
| 1 — Doc parse | Field-level F1 against analyst-confirmed line items | F1 ≥ 0.92 on `(hs_code, declared_qty, packaging)` jointly; HS-6 EM ≥ 0.88 |
| 2 — Grounded detection | mAP@0.5 against analyst-bboxed ROIs, per HS-2 chapter | mAP ≥ 0.45 on top-20 chapters; ≥ 0.30 long-tail; recall ≥ 0.70 |
| 3 — Composite scorer | AUROC of `case_score` vs `Verdict.decision ∈ {HoldForInspection, Seize}` | **AUROC ≥ 0.75 — locked**; per-line precision ≥ 0.70 — locked; ECE ≤ 0.10 |

**Loss.**
- Stage 1: Donut native cross-entropy on JSON token sequence + structural-validity penalty.
- Stage 2: Florence-2 grounding head loss (DETR-style Hungarian + L1 + GIoU); LoRA per tenant if cross-tenant transfer drops > 5 pts.
- Stage 3: no learned loss — calibrated against held-out feedback by grid-search over weights and kernel widths. Recalibration cadence: monthly (Q-F3).

A/B harness: score is computed for 100 % of eligible cases from Phase 1; only *display* is gated. Customs-feedback loop: daily job updates 90-day rolling AUROC; freeze on > 0.05 drift.

#### 6.3.6 Inference budget

| Stage | Profile | p50 | p95 | Notes |
|---|---|---|---|---|
| 1 — Donut primary | DirectML iGPU | 0.8 s | 1.5 s | Per AuthorityDocument page |
| 1 — Qwen2.5-VL fallback | DirectML iGPU | 6 s | 10 s | Fires for ≤ 10 % of cases |
| 1 — Qwen2.5-VL fallback | CUDA dGPU | 1.5 s | 2.5 s | If CUDA available |
| HS lookup | In-process | < 5 ms | < 20 ms | Postgres + per-tenant cache |
| 2 — Florence-2-large | DirectML iGPU | 1.5 s | 3 s | Per ScanArtifact |
| 2 — Florence-2-large | CUDA dGPU | 0.4 s | 0.8 s | |
| 3 — Scorer (C#) | In-process | < 50 ms | < 200 ms | |
| **End-to-end** | **DirectML iGPU, no Qwen fallback** | **2.5 s** | **5 s** | Hot path |
| **End-to-end** | **DirectML iGPU, with Qwen fallback** | **8 s** | **14 s** | The 5–10 % cohort |

Combined model footprint (FP16): Donut 200 MB + Florence-2-large 1.7 GB + Qwen2.5-VL-7B INT8/AWQ ~5 GB. Donut + Florence-2 warm at process start (≤ 6 s); Qwen lazy-load on first need (≤ 12 s, acceptable).

#### 6.3.7 Deployment & rollout

Mirrors §3.7's four-phase pattern with one structural change: the safety net is "do nothing visible until score is calibrated," not "fall back to a teacher" — there is no teacher.

| Phase | Behavior | Gate |
|---|---|---|
| 0. Dev eval | Three stages run offline against frozen holdout; per-stage gates from §6.3.5 met | Stage gates green; manual review of 200 random scored cases shows < 10 % obviously-wrong findings |
| 1. Shadow | 100 % of eligible cases scored; findings written to `pending_findings`. **Not displayed to analysts.** | 30 days; AUROC ≥ 0.75 sustained; per-line precision ≥ 0.70 on rolling 30-day customs-feedback subset |
| 2. Primary + safety net | Findings shown as **advisory**, clearly labelled "ML suggestion, confirm before flagging". Low-confidence (composite < 0.4 or NaN sub-score) **suppressed entirely**. Analyst `confidence_score` recorded but not influenced by ML. | 60 days; analyst-confirmed precision ≥ 0.70; analyst override rate < 40 %; zero auto-seizures verified |
| 3. Primary + per-tenant tuning | Per-tenant LoRA adapters for stage 2 where data justifies. Per-location score thresholds. 5 % audit sample. | SLO: AUROC drift < 0.05 over rolling 90 days |

**Safety net (locked 2026-04-28):** (1) score never auto-actioned — `Verdict.decision` only after `decided_by_user_id` set; (2) low-confidence routes to analyst with score suppressed; (3) per-tenant kill switch (`tenant_settings.manifest_xray_scorer_enabled`); (4) every `Finding` carries `source_model_id` + `source_model_version` for replay.

#### 6.3.8 Failure modes & guards

| Failure | Symptom | Guard |
|---|---|---|
| HS-code wrong but commodity correct | Misclassified textiles HS 62 vs HS 61, both knit. Density inconsistent vs declared HS even though cargo is honest | Rules provider runs `IAuthorityRulesProvider.AreHsCodesCommoditySimilar(hs_a, hs_b)` — same HS-2 chapter → downgrade severity, tag `LikelyMisclassification` not `LikelyMisdeclaration` |
| Multi-line container with mixed cargo | Declared 60% cartons electronics, 40% drums lubricant | Per-line scoring handles by attribution. **Guard:** NMS by IoU 0.5 within HS-2; cross-chapter overlaps tolerated |
| Hidden compartment with no declared line | Cargo declared correctly; high-Z anomaly behind it | Out of scope here — explicitly §6.2's job. **Guard:** when no declared line explains a high-`z_eff` ROI > 5 % frame area, emit `UnexplainedDensity` not `ManifestXrayMismatch` |
| Declaration-of-similar-density misdirection | Ceramic tiles declared to mask high-density contraband | Adversarial — math passes. **Guard:** flag as `Inconclusive` when ALL of `(declared_density > 75th-percentile, packaging = Bulk, single-line)`. Owned by `Authorities.CustomsGh`, not the scorer |
| Beam-hardening / metal-streak collapse | Material channel saturates behind engine block | Stage 2 emits `region_quality` mask; stage 3 down-weights/excludes low-quality ROIs; `DegradedRegion` telemetry. Long-term: §6.8 |
| Document image too poor for Donut | Skewed scan, handwritten amendments | Donut conf < 0.6 → Qwen2.5-VL fires. Both < 0.6 → `parse_failed`, scorer skips, telemetry `MlSkipped(reason='doc_parse_failed')` |
| Channel degradation (no raw HE/LE/Z) | Adapter exposes only LUT | `w_density = 0`, re-normalise; `ChannelDegraded` flag; severity ceiling drops to `Medium` |
| Tenant kill-switch flipped mid-case | Admin disables mid-stage | Setting checked at start of every stage; transitions to `MlSkipped(reason='tenant_disabled')`; in-flight Findings retained for audit, never displayed |

#### 6.3.9 Open questions

| ID | Question | Status |
|---|---|---|
| Q-F1 | Raw HE/LE/Z exposure per scanner adapter — confirm which legacy adapters can expose raw float32 vs LUT-only. Sets the score quality bound. | Open |
| Q-F2 | Composite score persistence before `ReviewSession` opens. **Recommend** sidecar `ml_findings_scratch` keyed `(case_id, model_version)`. Reject `AnalystReview.confidence_score` (pollutes calibration) and `case.metadata jsonb` (invisible to ML telemetry). | Recommendation logged |
| Q-F3 | Scorer weight calibration — monthly grid-search? Per-tenant or global? Persisted in `InferenceModel.metadata` or sibling `scoring_config` table? | Open |
| Q-F4 | v1 training-data export script design — point-in-time read-only export; sibling to Q-A2. | Open |
| Q-F5 | **Sourcing the HS commodity density reference table.** Candidates: WCO (free, no density), USDA/FAO (free, partial), commercial cargo-density datasets (paid, redistribution unclear), in-house seed by analyst review of top-200 HS-6 codes. **Recommendation:** in-house seed for top-200 (~85 % of case volume), supplement public, mark `confidence` per row, never block scoring on missing rows. Licence audit before any commercial ingest. | Open — recommendation logged |
| Q-F6 | HS code → free-text prompt resolution language. English for Ghana; multilingual for francophone West Africa is open SOTA. | Deferred to Phase 7.4 |
| Q-F7 | Per-line `Finding` UI surface — side-panel checklist or inline overlay? Affects override-rate measurement. | Open — UX decision |
| Q-F8 | Qwen2.5-VL-7B licence posture for production deployment. Tongyi Qianwen License has fine-tuned-weights restrictions. | Open |
| Q-F9 | Cross-vendor X-ray adaptation. Pseudocolor canonicalisation per [Sci. Data 2026](https://www.nature.com/articles/s41597-026-07149-8) is the front-runner. Empirical check before second-vendor location goes live. | Deferred to Phase 7.2 |
| Q-F10 | Late-arriving `post_hoc_outcome jsonb` — retroactively log to original case for AUROC, or treat as new training row? **Recommend** both — original immutable in `MlTelemetry`, post-hoc as separate `outcome_recorded_at` row, join at eval time. | Recommendation logged |

### 6.4 Active learning loop

*Tier: image-analysis-adjacent.*

> **Closes [`ARCHITECTURE.md`](ARCHITECTURE.md) §12 Q4** ("Post-hoc outcome capture — how does customs seizure/clearance feedback flow back into v2 for ML labels?"). Locked structure 2026-04-28; weights and cadences marked **Open** are tunable in Phase 7.4.

#### 6.4.1 Problem

v1 retrains nothing. Models drift; analyst behaviour drifts (new staff, new commodity mix, seasonal contraband patterns); customs feedback (seizure / clearance verdicts that arrive weeks after the scan) is never folded back into supervision. The container-split student (§3) and any future model behind `IInferenceRunner` (§4) need a closed loop: every analyst decision and every customs outcome should change the next training run, in priority order.

The existing v1 posture — "snapshot a teacher, train once, ship, hope" — is what got us into today's gap analysis (§1.1). v2's review schema (`AnalystReview` per [`ARCHITECTURE.md`](ARCHITECTURE.md) §7.5) already carries the signals; this section is the spec for *using* them.

**Locked acceptance: 2026-04-28**
- Retrain cadence ≤ 30 days while traffic is active (≥ 100 reviewed cases/week per location).
- Per-cycle MAE drift detected within 7 days of degradation (rolling 7-day vs prior 30-day baseline; alert at +2 px MAE for §3, +0.05 AUROC for §6.2).
- Zero outbound network calls per scan in production; cloud teachers may be invoked only on the labeling cycle, not on hot-path inference.
- No raw operator PII or tenant cargo manifests leave the central cluster during a labeling cycle.

#### 6.4.2 Inputs / outputs

**Review-time signals consumed.** All read from `AnalystReview`, `ReviewSession`, `Finding`, `Verdict` (per [`ARCHITECTURE.md`](ARCHITECTURE.md) §5.1), plus `audit.events` for chronology.

| Signal | Source field | Semantics |
|---|---|---|
| `time_to_decision_ms` | `AnalystReview.time_to_decision_ms` | Triage priority — long decisions = hard cases |
| `roi_interactions` | `AnalystReview.roi_interactions jsonb` | Where the analyst looked; pseudo-attention map |
| `confidence_score` | `AnalystReview.confidence_score` (0.0–1.0, required) | Direct uncertainty; (1 − c) is a primary candidate-selection term |
| `verdict_changes` | `AnalystReview.verdict_changes jsonb` | Mid-review verdict flips; high-flip = ambiguous |
| `peer_disagreement_count` | `AnalystReview.peer_disagreement_count` | Ground-truth-ish — two analysts disagreeing is a strong "label me" signal |
| `post_hoc_outcome` | `AnalystReview.post_hoc_outcome jsonb` | True label from customs (seizure / clearance / re-export); arrives **weeks** after the scan |

**Subscriptions — which family consumes which signals.**

| Family | Consumes | Doesn't consume |
|---|---|---|
| §3 Container-split student | `confidence_score`, `verdict_changes`, customs outcome (low-weight prior) | `roi_interactions` (irrelevant — task is geometric) |
| §6.1 OCR | `confidence_score`, customs outcome (manifest cross-check), peer disagreement on plate text | `roi_interactions` |
| §6.2 Anomaly detection | All six; `post_hoc_outcome` is the gold label | — |
| §6.3 Manifest scorer | `post_hoc_outcome`, `roi_interactions` (where analyst found the discrepancy) | — |

**Outputs.**
1. Ranked candidate label set per cycle in `inspection.active_learning_candidates`.
2. Labeling project materialised in Label Studio (one project per `(model_family, cycle_id)`), pre-populated with SAM 2 mask proposals.
3. Training-set manifest with `training_set_hash` per §3.7.
4. Candidate `InferenceModel` row (status `registered`) per retrain trigger.

#### 6.4.3 Architecture (locked 2026-04-28)

**4-stage loop.** `score → sample → label → retrain`. Pure random-sampling is rejected — labeling budget is heavily skewed toward disagreement and seizure cases.

```
┌────────────┐  ┌────────────┐  ┌────────────┐  ┌─────────────────┐
│ 1. SCORE   │─▶│ 2. SAMPLE  │─▶│ 3. LABEL   │─▶│ 4. RETRAIN      │
│ priority Q │  │ top-K +    │  │ Label      │  │ candidate →     │
│            │  │ stratify   │  │ Studio +   │  │ shadow → A/B    │
│            │  │            │  │ SAM 2      │  │                 │
└────────────┘  └────────────┘  └────────────┘  └─────────────────┘
      ▲                                                  │
      └──── feedback: candidate metrics, drift signal ───┘
```

**Stage 1 — priority queue scoring.** Weighted sum, computed nightly over rolling 90-day review window per `(tenant_id, location_id, model_family)`:

```
priority(scan, family) =
    3.0 * peer_disagreement_count
  + 2.0 * (1 - confidence_score)
  + 5.0 * 1[post_hoc_outcome.label != verdict.decision]   # gold disagreement
  + 1.0 * embedding_distance_to_centroid(family)           # drift
```

Weight justification (all **Open** for tuning in Phase 7.4):
- **5× on customs disagreement** — this is the only true label in the loop.
- **3× on peer disagreement** — proxies a true label; cheaper to obtain than customs feedback but noisier.
- **2× on `(1 − confidence)`** — classical uncertainty sampling ([Settles 2009](https://burrsettles.com/pub/settles.activelearning.pdf)). Capped at 2× because pure uncertainty biases toward known-unknowns.
- **1× on embedding distance** — DINOv2 features for image families; sentence-transformer for OCR text. Catches distribution shift the others miss.

Stratify per `(HS_code_2digit, scanner_instance_id)` to avoid one busy lane or HS chapter swallowing the budget.

**Stage 2 — sample.** Top-K = **500** per cycle per family (Open). Within K: 70 % from priority ranking, 20 % stratified random across HS×scanner buckets that priority under-covers, 10 % pure random control set (lets us measure that priority-scoring is beating random — see §6.4.8 selection-bias guard).

**Stage 3 — label.** Label Studio project; SAM 2 pre-labels propagated; dual-review on any sample where weight (c) fired (customs disagreement). See §6.4.5.

**Stage 4 — retrain.** Triggered when N gold labels accumulate since last retrain (§6.4.6).

#### 6.4.4 Data pipeline

**Extraction.** Nightly job in `NickERP.Inspection.ActiveLearning.Extractor` (a hosted background service; not an adapter). Reads core entities directly. RLS still applies — extractor runs under a service principal with all-tenant location assignments; cross-tenant pooling explicitly disallowed without contractual sign-off (Q-G3).

**Landing zone.** `<storage_root>/active-learning/cycles/{cycle_id}/` containing `manifest.json`, `candidates.parquet`, `artifacts/` (symlinks/hardlinks into `<storage_root>/scans/...`), `presigned/` (Label Studio import bundle), `integrity.sha256` (Merkle root), `audit/cycle_open.json` (DomainEvent emitted to `audit.events`).

**Integrity.** Every file sha256'd at write; cycle manifest carries rolled-up Merkle root. Label Studio init webhook verifies before opening.

**Retention.** 18 months online + cold archive thereafter (compliance — same as `audit.events`).

#### 6.4.5 Labeling workflow

**Tool: [Label Studio](https://labelstud.io/)** (Apache-2.0, self-hostable, Postgres-backed). Picked over CVAT because (1) v2's workload is image-and-document — Label Studio's mixed-template projects handle that natively where CVAT specialises in image-only; (2) Label Studio has first-class **model-in-the-loop pre-labeling** via the ML Backend API, core to the SAM 2 acceleration. See [the official CVAT-vs-Label-Studio comparison](https://www.cvat.ai/resources/blog/cvat-or-label-studio-which-one-to-choose).

Deployment: Label Studio Community Edition on the same v2 Postgres cluster (own DB `label_studio`, own role, no RLS — internal tool). Single instance, behind reverse proxy + SSO.

**SAM 2 model-in-the-loop.** [SAM 2 (arXiv 2408.00714)](https://arxiv.org/abs/2408.00714) runs as a Label Studio ML Backend (FastAPI sidecar). Annotator clicks once; SAM 2 proposes the mask; annotator confirms or refines. Mask propagation cuts annotation time roughly 4–6× per the SAM 2 paper.

**Templates.**
- §3 container-split: 1D split-X picker per artifact + "no split" toggle.
- §6.1 OCR: plate text input with confidence radio.
- §6.2 anomaly: SAM 2 mask + threat-class + severity.
- §6.3 manifest: two-pane (BOE PDF, X-ray frame) with linked-region pairs.

**Dual-review for high-stakes labels.** Any candidate where weight (c) fired goes to two annotators; if labels disagree, senior analyst arbitrates.

**Throughput estimate.** 60 sec/label simple (split, OCR), 180 sec/label complex (anomaly mask, manifest pairing). With 2 analysts × 4 hr/day, K=500 clears in ~2.5 days simple, ~7 days complex. Sets the floor on cycle cadence; well under the 30-day acceptance.

#### 6.4.6 Retrain orchestration

| Family | Trigger N (gold labels) | Cadence floor | Cadence ceiling |
|---|---|---|---|
| §3 Container-split student | **500** | 7 days | 30 days |
| §6.1 OCR | 500 | 7 days | 30 days |
| §6.2 Anomaly detection | **1000** (false-positive cost is operational) | 14 days | 30 days |
| §6.3 Manifest scorer | 1000 | 14 days | 30 days |

Retrain **must** fire when either the gold-label count is hit or the cadence ceiling elapses with ≥ 100 gold labels available. If neither, log `retrain_skipped` with reason and continue.

**Experiment tracking: MLflow on-prem** (open-source, Apache-2.0, Postgres backend that fits the v2 cluster). Picked over W&B for on-prem requirement. MLflow's Model Registry maps cleanly onto the existing `inference_models` table — one row in `inference_models` ↔ a Model Version in MLflow with a one-to-one `metadata.mlflow_run_id`.

**Promotion gates.** §3.7's rollout phases reused verbatim. Specific gates:

| Gate | §3 family | §6.2 family |
|---|---|---|
| Dev eval | MAE ≤ 4 px AND F1@5px ≥ 0.97 | AUROC ≥ 0.85 AND PR-AUC ≥ 0.50 |
| Shadow window | 14 days, MAE ≤ active + 0.5 px | 14 days, AUROC ≥ active − 0.01 |
| Promote to primary | Improvement ≥ noise floor on the random control slice | Same |
| Demote / rollback | Live MAE drift > 2 px over rolling 7 days | AUROC drift > 0.05 |

**Rollback path.** Every cycle's `(active_model_id, candidate_model_id, candidate_promoted_at)` triple in `inspection.active_learning_cycles`; `POST /api/admin/models/rollback` restores prior `active` in one minute end-to-end. No DB rewrite.

#### 6.4.7 Deployment & rollout

| Phase | Behavior | Gate |
|---|---|---|
| G0. Dry-run scoring | Stage 1 nightly; queue persists; **no labeling, no retrain**. Verify priority correlates with seizure outcomes (Spearman ≥ 0.4) | 14 days clean; weight tuning landed |
| G1. Single-family labeling | §3 only. K=200 (half default) for ramp-up. One cycle, one retrain, one shadow window | Candidate matches/beats active on shadow; rollback drill done |
| G2. Multi-family labeling | §6.1 + §6.2 join. K=500 per family. Independent cycles; staggered start dates | All three families have shipped one promoted retrain; dual-review path validated |
| G3. Automated retrain | Cron-driven triggers fire without human kickoff; admin sees candidate model in registry awaiting shadow approval | Sustained ≤ 30-day cadence for 90 days; per-family drift detected within 7 days of synthetic regression test |

#### 6.4.8 Failure modes & guards

- **Label leakage between train/eval.** Active-learning cycles will keep selecting *related* scans (same container, same vessel call, same operator within a shift). **Guard:** training-set splitter groups by `(vessel_call_id, operator_user_id, calendar_day)`; never split a group across train/val/test.
- **Analyst-bias amplification.** Cap any single `analyst_user_id`'s contribution at 25 % per cycle; resample if exceeded.
- **`post_hoc_outcome` lag.** Customs typically lands 2–8 weeks; some never lands. **Guard:** scoring runs over rolling 90-day window; lag histogram per cycle; if median lag exceeds 60 days for two consecutive cycles, **5× weight de-rates to 3×** automatically until it recovers.
- **Selection-bias drift (priority loop eats its own tail).** **Guard:** 10 % random control slice is mandatory and is the metric we evaluate on. If `eval_on_random / eval_on_priority` ratio drifts below 0.6, raise alarm and force the next cycle to 30/70 random/priority for one cycle.
- **Customs-feedback poisoning.** Wrong outcomes amplify at 5×. **Guard:** outcomes come through the Q-G1 closure adapter (formal), not analyst free-text; cross-check `audit.events`; high-impact disagreements (Clear→Seize after promotion) require dual-source confirmation.
- **Tenant isolation regressions.** Active-learning cross-tenant pooling **off by default**. **Guard:** startup probe verifies extractor role has only `SELECT` on extraction tables, zero write paths, rotated quarterly.
- **Cycle integrity tampering.** Sha256 manifest at landing-zone write; Label Studio init webhook verifies before opening. Mismatch → `cycle_integrity_failure`, abort.
- **MLflow run drift.** Retrain whose `mlflow_run_id` cannot be resolved at promote time fails the gate.

#### 6.4.9 Open questions

| ID | Question | Status |
|---|---|---|
| **Q-G1** | **Closes ARCHITECTURE Q4: post-hoc outcome capture.** Customs seizure / clearance feedback flows back via a new `IExternalSystemAdapter` of `ExternalSystemType.Customs` running in **inbound mode** — adapter polls (or receives webhooks from) the authority's seizure register, materialises an `AuthorityDocument` of `DocumentType.PostHocOutcome`, and a domain event handler writes the outcome to `AnalystReview.post_hoc_outcome jsonb` of the originating case. Manual-entry tool exists as fallback for authorities without an outcome API. | **Closed (posture); adapter spec Open** |
| Q-G2 | Weight tuning. Initial weights `{c:5, peer:3, unc:2, drift:1}` are educated guesses. Phase G0's 14-day Spearman analysis gives the first re-tune. Need an offline simulator. | Open |
| Q-G3 | Cross-tenant pooling for low-frequency families. Permitted only with contractual sign-off and de-identified embeddings. Default: **off**. | Open — needs commercial decision |
| Q-G4 | SAM 2 hosting. CPU-only (slow but deployable everywhere) or NVIDIA labeling host? **Recommend** NVIDIA — SAM 2 latency on CPU is annotator-painful. | Recommendation logged |
| Q-G5 | Active-learning UI for analysts during live review. **Recommend** hide entirely — could bias the live verdict. Surface only in admin retrospectives. | Recommendation logged |
| Q-G6 | MLflow vs platform telemetry overlap. Defined seam: MLflow owns experiment lineage, OTel (§4.6) owns production serving, link is `metadata.mlflow_run_id`. | Closed |
| Q-G7 | Retire-the-teacher question. After §3 student beats Claude on the random control slice for two G3 cycles, do we stop calling Claude entirely? **Recommend** keep 1 % indefinitely as drift sentinel. | Recommendation logged |
| Q-G8 | Catastrophic-cycle stop. Two consecutive rollbacks within 14 days pauses all G3 automation across families until operator clears it. | Open |

### 6.5 Per-scanner threshold calibration

*Tier: image-analysis-direct.*

> **Replaces in v1:** Canny 50/150 (`ContainerObjectDetectionService.cs:119`), percentile-stretch 0.5/99.5 (`services/image-splitter/inspector/decoders/fs6000.py:30`), 50-px disagreement guard (`services/image-splitter/inspector/pipeline/orchestrator.py:136–150`), 72 h pending-without-images timeout (`ImageAnalysisOrchestratorService.cs:40`), 16384 max image dimension (`FS6000FormatDecoder.cs:70`).
> **Acceptance bar (locked 2026-04-28):** detection precision improves ≥ 5 % on edge-case scanners (those whose default-threshold detection precision was ≥ 10 pp below fleet median); zero regressions on median scanners.

#### 6.5.1 Problem

v1 treats every scanner identically. Five separate compile-time constants apply to every device on every lane regardless of detector wear, beam-current drift, lane-PC throughput, or local cargo mix. The result: edge-case scanners sit measurably below fleet-median detection precision, and the only fix is a code edit + redeploy.

v2 treats thresholds as **per-`ScannerDeviceInstance` runtime configuration** versioned alongside the model artifacts they parameterize. Auto-tune proposes new values from rolling per-scanner statistics; an admin reviews and approves before activation; a 24 h shadow run validates that the new values do not regress live traffic before promotion.

This is a **calibration-of-existing-pipeline** concern, not a model concern. It runs orthogonal to §3 and §4: both consume threshold values via the same lookup contract.

#### 6.5.2 Inputs / outputs

**Per-scanner rolling stats.** Materialized weekly per `scanner_device_instance_id` from existing telemetry channels. Stored in `scanner_threshold_stats` keyed by `(scanner_device_instance_id, stat_kind, window_end)`, retained 12 months.

| Stat | Source | Window |
|---|---|---|
| Edge-density histogram (post-Canny px-count distribution) | `inference.run` postprocess hook on detection | 30 d rolling |
| Pre-LUT pixel-value distribution (HE, LE, material channels) | adapter `ParseAsync` — 1 % sample | 30 d rolling |
| Cross-strategy split-position disagreement, in px | container-split orchestrator | 30 d rolling |
| Pending-without-images dwell time | `InspectionWorkflow` state log | 30 d rolling |
| Max-observed image dimension per device | `ScanArtifact.width`, `ScanArtifact.height` | 90 d rolling |
| Detection precision vs analyst verdict | `Finding` ↔ `Verdict.basis` join | 30 d rolling |

**Threshold profile shape:**

```jsonc
{
  "edge_detection": { "canny_low": 42, "canny_high": 138 },
  "normalization":  { "percentile_low": 0.6, "percentile_high": 99.4 },
  "split_consensus": { "disagreement_guard_px": 38 },
  "watchdogs":      { "pending_without_images_hours": 96 },
  "decoder_limits": { "max_image_dim_px": 16384 }
}
```

The exact set of keys is **not global** — see 6.5.3.

#### 6.5.3 Architecture (locked 2026-04-28)

**Entity.** New core entity `ScannerThresholdProfile` with fields: `id` (uuid PK), `tenant_id`, `scanner_device_instance_id` (FK), `version` (monotonic per scanner), `values` (jsonb, validated against `ScannerDeviceType.threshold_schema`), `status` ∈ `{proposed, shadow, active, superseded, rejected}`, `effective_from`/`effective_to`, `proposed_by` ∈ `{bootstrap, auto_tune, manual}`, `proposal_rationale jsonb`, `approved_by_user_id` (FK → `User`), `approved_at`, `shadow_started_at`/`shadow_completed_at`, `shadow_outcome jsonb`.

Unique partial index `(scanner_device_instance_id) WHERE status = 'active'` enforces exactly one active profile per scanner. `tenant_isolation_*` policy and `FORCE ROW LEVEL SECURITY` per the platform pattern.

**Schema-per-scanner-type (locked).** Each `IScannerAdapter` declares its threshold schema via `ScannerDeviceType.threshold_schema` (JSON Schema). One-size-fits-all global schema is **rejected** — it forces every adapter to grow when any one adds a knob. Validator on insert and adapter-version bump; non-conforming `values` mark the profile `rejected` and emit alert.

**Propose → approve → activate flow.**

```
[stats job (cron, weekly)]
        │ proposal rows status='proposed', proposed_by='auto_tune'
        ▼
[admin UI: side-by-side current vs proposed + rationale]
        │ admin clicks Approve
        ▼
[shadow runner (24 h)] runs new path on 5 % of traffic
        │ status='shadow', logs outcome deltas
        ▼
[shadow gate]
   ├── pass → status='active', effective_from=now()
   └── fail → status='rejected', alert
        │
        ▼
[old active row → status='superseded', effective_to=now()]
```

The 24 h shadow window is a hard requirement before any auto-tuned profile activates. Manual profiles follow the same gate; admin role `inspection.threshold_admin` may bypass shadow with explicit `bypass_reason`.

**Apply path & cache.** Per-scan threshold lookup is hot-path (every Canny call, every percentile-stretch). Resolved through:

```csharp
public interface IScannerThresholdResolver
{
    ValueTask<ScannerThresholdSnapshot> GetActiveAsync(
        Guid scannerDeviceInstanceId, CancellationToken ct);
}
```

- **Cache scope:** per `scanner_device_instance_id`, in-process, indefinite TTL.
- **Invalidation:** PostgreSQL `LISTEN/NOTIFY` channel `threshold_profile_updated` carries `(scanner_device_instance_id, version)`. Trigger on `UPDATE` of `status`. Resolver subscribes once at host startup.
- **Latency budget:** see §6.5.6.

Decision: **LISTEN/NOTIFY rather than time-bounded TTL.** When an admin clicks Approve, they want the new threshold to take effect on the next scan; TTL is unacceptable for that UX.

#### 6.5.4 Initial calibration data

**Bootstrap.** On first deploy, every `ScannerDeviceInstance` gets a `version=0` profile stamped from v1 hardcoded values, with `proposed_by='bootstrap'`, `status='active'`, `proposal_rationale.source='v1_hardcoded_values_2026_04_28'`. Guarantees behavioural parity with v1 on day-one cutover.

#### 6.5.5 Auto-tune algorithm

Different threshold types have different statistical character. **One algorithm does not fit all.**

| Threshold class | Strategy | Notes |
|---|---|---|
| Normalization (percentile-stretch low/high) | Percentile-based windowing on 30 d rolling pre-LUT histogram per scanner | Locked. Trivially correct: percentile thresholds estimated from percentiles of actual distribution. |
| Split-disagreement guard | Set guard = µ + 3σ of observed cross-strategy disagreement when verdict was correct, capped to v1 default × 2 | Locked. Adapts to noisier scanners without unbounded relaxation. |
| Pending-without-images watchdog | Set hours = p99 of observed pending dwell time on healthy scans, floored at v1 default | Locked. Fewer false-alarm timeouts on slow lanes. |
| Max image dimension | Set = max observed × 1.1, capped at 32768 | Locked. Defensive only. |
| Edge-detection thresholds (Canny low/high) | **Out of scope for v0 auto-tune.** v0 uses offline grid search with replay; v1 spec extends to MAB. | See below. |

**Edge-detection auto-tune (deferred).** Detection thresholds trade precision for recall; the right operating point depends on scanner-specific Finding-conversion behaviour. The classical fit is a multi-armed bandit over a discretized threshold grid, with reward = analyst-confirmed-finding rate per scan. **Recommend Thompson sampling** (Bayesian, per-arm uncertainty for free, robust to small N) — see Russo et al., [*A Tutorial on Thompson Sampling*](https://arxiv.org/abs/1707.02038). Vowpal Wabbit ([github](https://github.com/VowpalWabbit/vowpal_wabbit)) is the reference implementation; we'd wrap them in a managed service rather than P/Invoke.

**v0 substitute: offline replay grid search.** Score each scanner's archived 30 d of scans against a 5×5 grid of `(canny_low, canny_high)` candidates using stored finding ground truth from `Verdict.basis`. Pick highest-precision cell at recall ≥ baseline. Cheap, deterministic.

**Replay harness.** A `ScannerThresholdReplay` job reads archived `RawScanArtifact` blobs (cold storage), re-runs the relevant pipeline stage with candidate thresholds, joins against `Finding` + `Verdict` for ground truth, emits a precision/recall row per `(scanner, threshold_candidate, date_window)`. Runs offline; does not touch live traffic.

#### 6.5.6 Apply latency budget

| Path | Budget |
|---|---|
| Per-scan threshold lookup (hot path) | ≤ **1 ms** p99 — in-process dict, no SQL |
| Cold lookup (first scan after host start) | ≤ **20 ms** p99 — single SQL fetch + JSON parse + schema validate |
| Profile-update propagation (single host) | ≤ **5 s** p99 — LISTEN/NOTIFY round-trip |
| Profile-update propagation (multi-host fleet) | ≤ **5 s** p99 per host — NOTIFY broadcasts to every connected backend |
| Shadow run start-up after approve | ≤ **30 s** |

**Open**: when API is sharded across regions, NOTIFY is per-cluster. Today (Phase 7.0–7.3) all hosts share one Postgres primary; revisit at multi-region (likely Phase 7.7+).

#### 6.5.7 Deployment & rollout

| Phase | Behavior | Gate |
|---|---|---|
| 0. Schema + bootstrap | Migration creates tables. Bootstrap stamps `version=0` per scanner from v1 constants. Resolver wired in, every scanner returns bootstrap profile | All scanners produce identical detection output to pre-deploy on a 7 d back-test |
| 1. Manual-tune UI | Admin authors `proposed_by='manual'` profile via UI; offline replay shows expected delta; 24 h shadow; promote to active. **No auto-tune yet.** | One full propose→shadow→activate cycle on at least one edge-case scanner with measured precision lift |
| 2. Auto-tune proposal (no auto-activate) | Weekly cron emits `auto_tune` proposals for normalization/disagreement/watchdog/max-dim. Detection thresholds emit grid-search proposals. **Admin must approve.** | 4 weeks of weekly proposals reviewed; ≥ 80 % approved-as-is or with-minor-edit |
| 3. Auto-tune shadow-then-activate | For non-detection thresholds only, auto-approve → 24 h shadow → activate if shadow gate passes; alert on fail. Detection thresholds remain admin-approved (eligible for auto-activation only after MAB rollout) | Acceptance bar: ≥ 5 % precision lift on edge-case scanners; zero regression on median |

Bootstrap is non-reversible by design. Rollback is "set every scanner's active profile to its `version=0` row" — one SQL update.

#### 6.5.8 Failure modes & guards

- **Auto-tune chasing transient distribution shift.** A week of unusual cargo (festival imports, weather anomaly) can shift histograms enough to propose unwarranted values. **Guard:** require ≥ 4 of last 6 weekly windows to agree within 5 % before proposal; outlier windows flagged in `proposal_rationale.window_dispersion`.
- **Admin approving without inspection (rubber-stamp risk).** **Guard:** Approve action requires admin to acknowledge rationale string (checkbox per proposal); audit log records acknowledgment hash. The 24 h shadow run is the real safety net.
- **Schema drift between adapter version and profile rows.** **Guard:** validator on adapter-version bump emits missing-key list; resolver fills missing keys with adapter-declared default; backfill job stamps a new `auto_tune` proposal carrying merged values awaiting admin approval. Adapter downgrades reject if any extant profile carries keys older schema doesn't declare.
- **Shadow gate insufficient evidence.** Low-traffic scanner may not accumulate enough events. **Guard:** shadow stays open until N ≥ 200 reviewed events accumulate or 7 d elapses; 7 d-no-evidence marks `shadow_outcome.verdict='inconclusive'`; proposal stays `shadow` pending admin override.
- **Concurrent admin approvals on the same scanner.** **Guard:** the `WHERE status='active'` partial unique index makes the race a constraint violation; second commit fails with "another admin just approved profile vN, refresh".
- **NOTIFY message lost (rare but possible).** **Guard:** every host re-subscribes on connection establishment AND issues a "fetch active version" probe per cached scanner on subscription; version skew triggers cache eviction. 1 h ceiling TTL as belt-and-braces.

#### 6.5.9 Open questions

| ID | Question | Status |
|---|---|---|
| Q-H1 | Per-scanner per-time-of-day calibration. Belt-speed and operator-throughput vary by shift. Phase-0 says scanner-only; revisit if shift-stratified stats show > 10 pp precision delta. | Open |
| Q-H2 | MAB for detection thresholds — Thompson sampling vs LinUCB. **Recommend** Thompson first; upgrade to contextual once we have ≥ 6 months of per-scanner reward signal. | Recommendation logged |
| Q-H3 | Cross-scanner pooling. Two FS6000s of same firmware revision in the same Location may share a prior. Out of scope until ≥ 5 same-type scanners per Location. | Deferred |
| Q-H4 | Threshold change as a `DomainEvent`. **Recommend yes** — activation is a state change and §7.4 says every state change emits an event. | Recommendation logged |
| Q-H5 | Edge-node behaviour (Phase 7.6+). NOTIFY does not cross WAN. **Recommend** edge polls active-profile-version on reconnect; intermediate updates while offline are eventual consistency. | Deferred to Phase 7.6 |
| Q-H6 | Replay-harness storage cost. 30 d of `RawScanArtifact` blobs per scanner is multi-TB at fleet scale. If cold storage cost dominates, downsample to 1 % stratified. | Open |
| Q-H7 | Threshold-schema versioning. **Recommend** validate-at-write-time (schema_version stamped on profile row); migration only when key is renamed. | Recommendation logged |
| Q-H8 | Confidence floor for `auto_tune` proposals. Suggest minimum N=500 reviewed scans per stat type before any auto-proposal is emitted. | Open |

### 6.6 Threat Image Projection (TIP) synthetic data generator

*Tier: image-analysis-direct.*

> **Status:** Locked architecture 2026-04-28. Offline batch tool — no `IInferenceRunner` integration; runs as a scheduled job, not on the inspection critical path.
> **Foundational reference:** Rogers et al., ["Threat Image Projection,"](http://imageanalysis.cs.ucl.ac.uk/documents/TIP_Carnahan_web.pdf) Carnahan 2016.
> **Modern descendants surveyed:** Meta-TIP (style-adaptive projection with foreground-background contrastive loss), RWSC-Fusion (region-wise style-controlled fusion), and ["Taming Generative Synthetic Data for X-ray Prohibited Item Detection"](https://www.researchgate.net/publication/397780565_Taming_Generative_Synthetic_Data_for_X-ray_Prohibited_Item_Detection) (2024).

#### 6.6.1 Problem

There is no large public cargo X-ray dataset. Every published benchmark at scale (SIXray, OPIXray, HiXray, PIDray) is **baggage**, not cargo — the domain gap (object scale, beam energy, container attenuation, scanner geometry) is documented to break direct transfer in the literature. Real cargo seizures are rare events; waiting for organic positive labels means model evaluation is sample-starved indefinitely.

TIP solves this by exploiting the **multiplicative nature of transmission X-ray** (Beer's Law: I/I₀ = exp(-∫μ(x)·dx)). Two attenuating bodies along the same ray combine by **summing line integrals of linear attenuation**, equivalently **multiplying transmitted intensities**. So a clean scan of an isolated threat object can be composited *physically correctly* over a clean scan of benign cargo.

Downstream consumers:
- **§6.1 OCR corpus expansion** — synthesized container-plate views with controlled wear, paint-run, oblique angle, and partial occlusion. Direct training input.
- **§6.2 anomaly detection eval** — held-out evaluation set with known-anomaly ground truth. **Eval only, never train.** The synthetic-to-real generalization gap is well-documented.
- **§6.3 manifest consistency** — labeled grounded-detection examples for HS-conditioned prompt experiments.

#### 6.6.2 Inputs / outputs

**Threat library (read-only input).** One row per isolated threat instance with fields: `threat_id` (uuid), `threat_class` ∈ `{Firearm, Currency, Narcotic, Contraband_Other, BenignBaseline}`, `he_path` / `le_path` (float32 .npy in raw HE/LE), `alpha_mask_path` (soft segmentation), `material_zeff` (per-pixel Z map), `source_scanner_type` (required for noise model matching), `source_seizure_id` (provenance), `pose_canonical jsonb`, `tags jsonb`.

**Benign corpus query.** `Scan` rows where `Verdict.decision = 'Clear'`, `decided_at < now() - 90 days`, and `AuthorityDocument.payload ->> 'post_hoc_outcome' = 'cleared_finalized'`. The 90-day finalization lookback guards against late-arriving customs reversals (a "Clear" that flips to "Seize" 30 days post-clearance has happened in v1 telemetry).

**Output dataset manifest.** Written to `<storage_root>/synthetic/tip/<run_id>/manifest.jsonl`, one line per synthesized scan with `synthetic_scan_id`, `synthetic: true`, `run_id`, `tenant_id`, `benign_source_scan_id`, `threat_instances[]` (with pose, translation, occlusion, bbox), `renderer_version`, `scanner_noise_profile`, `outputs` (paths to HE/LE/material/preview), `ground_truth_hash`.

**Persistence.** Each row writes a `Scan` with a `synthetic=true` tag (new column, nullable, default false) and `ScanArtifact` rows pointing at float32 .npy outputs. Same schema as real rows — no parallel synthetic-only storage. Downstream consumers query `WHERE synthetic = true` to opt-in.

#### 6.6.3 Architecture (Locked 2026-04-28)

**Composition space: linear attenuation, *not* pseudocolor.** Threats and benign cargo are composited in **raw HE/LE float32 space** before vendor LUT. This is the same principle as §1.3. Compositing in pseudocolor is a **rejected approach**: it works for visually plausible TSA-style baggage TIP but breaks the dual-energy invariant.

The math (per pixel, per energy band E ∈ {HE, LE}):

```
T_combined(x, y, E) = T_benign(x, y, E) · T_threat(x, y, E)^α(x, y)
```

equivalently in log-attenuation:

```
A_combined = A_benign + α · A_threat
```

where `A = -log(I/I₀)` and `α ∈ [0, 1]` is the soft alpha mask. The Z_eff material channel is **not** computed by compositing pre-rendered Z maps (which is wrong — Z is a *ratio* of HE/LE attenuations, not additive). Instead, Z is recomputed *after* HE+LE composition via the same per-scanner Z calibration the live pipeline uses.

**Rejected: pure CycleGAN baggage→cargo domain transfer.** Documented failure modes in the literature: mode collapse on dense overburden, hallucinated container walls, and *erasure* of the threat itself when the discriminator over-corrects.

**Acceptable hybrid (Phase 2, deferred):** Meta-TIP-style style-adaptive refinement applied as a *post-process* to close residual seam artifacts. Composition stays multiplicative; GAN is only ever a polish layer with contrastive loss penalizing foreground drift.

#### 6.6.4 Threat library curation

| Source | Status | Notes |
|---|---|---|
| Segmented seized-item scans (in-house) | Q-I1 — sourcing **Open** | Highest-fidelity but volume-limited by seizure rate |
| Commercial threat databases (TSA-licensed, NEMA TIP, Smiths/Rapiscan vendor decks) | Q-I1 — sourcing **Open** | Procurement + licensing question |
| Synthetic threat generation (CT phantoms → simulated 2D X-ray) | Tier 3 | Last-resort; introduces synth-on-synth risk |

**Segmentation pipeline** (in-house):
1. Operator captures item *in isolation* on a calibration belt — clean alpha mask via thresholding on linear-attenuation map.
2. SAM 2 ([arXiv 2408.00714](https://arxiv.org/abs/2408.00714)) refines mask with one click prompt — handles soft edges (cloth-wrapped contraband, fissured powder).
3. Material attribution: per-pixel Z_eff from HE/LE ratio. Per-threat *expected* Z_eff range recorded in `tags.zeff_range`.
4. Provenance row in `threat_library_provenance` with `seizure_id`, `case_id`, `extracted_by_user_id`, `extracted_at`, redaction flags.

**Bias guard.** Track per-class scanner-source distribution. If 95 % of `Firearm` examples come from FS6000, the renderer **must** weight by source-scanner mix when sampling — otherwise the eval set becomes a scanner-fingerprint detector dressed as a threat detector.

#### 6.6.5 Renderer

**Implementation: PyTorch.** Justification: 3D rotation of attenuation volumes via `torch.nn.functional.grid_sample` is GPU-accelerated and batched; per-scanner noise samplers via `torch.distributions`; ONNX rejected (TIP is offline, no inference-runtime portability requirement); pure NumPy rejected (no GPU, ~83 GPU-equivalent-hours wasted on 100 k examples).

**Pose model.** 3D rotation jitter: θ_x ∈ [-15°, 15°], θ_y ∈ [-15°, 15°], θ_z ∈ [-180°, 180°]. Narrow x/y matches field distribution. The threat is treated as a **2.5D projection**: source 2D scan back-projected to a thin attenuation slab, rotated, re-projected. Bounded projection error within ±15°. In-plane translation: uniform over benign cargo's interior bounding box, rejected if < 5 % inside cargo extent.

**Noise injection.** Per-source-scanner profile, captured once during calibration:
- Poisson photon noise on raw counts (parameter: I₀ from scanner spec).
- Gaussian read noise (σ_read fitted from clear-belt frames).
- Pixel non-uniformity gain map (per-scanner, multiplicative).
Samples drawn during composition (not pre-baked) — same threat over same benign with different jitter seeds gives different noise realizations. Matters for §6.2 eval.

**Occlusion masks.** Drawn from a learned distribution of cargo-overburden geometries (generated from §6.2's benign-corpus segmentation). Sampled per-instance, target occluded fraction uniform in [0 %, 60 %]. The 60 % cap is empirical — beyond that, even human analysts in v1 telemetry mark `Inconclusive`.

#### 6.6.6 Generation budget

Per-instance render cost (single threat over single benign, batch=1, RTX A2000 6 GB class):

| Stage | Cost |
|---|---|
| Benign load (mmap of float32 .npy) | 8 ms |
| Threat load + 3D rotation + reprojection | 35 ms |
| Multiplicative composition (HE + LE) | 6 ms |
| Z recomputation (per-scanner calibration) | 12 ms |
| Noise injection | 14 ms |
| Occlusion mask blend | 5 ms |
| Disk write (HE + LE + Z + preview JPEG) | 90 ms |
| **Total per instance** | **~170 ms** (steady state, batch=8 amortizes I/O to ~110 ms) |

**100 k synthetic dataset cost:**
- Single GPU: 100 k × 0.11 s ≈ 3.1 GPU-hours.
- With ~3× headroom for failed-jitter rejections: **~9 GPU-hours total**.
- Storage: 100 k × (HE 8 MB + LE 8 MB + Z 4 MB + preview 200 KB) ≈ **2 TB**. Routes through §7.7's blob cache for serving.

Overnight job on a single A2000.

#### 6.6.7 Validation harness

The acceptance bar is **synth-to-real generalization gap**, not "synthetic looks plausible." Plausibility is necessary but insufficient.

**Locked acceptance bar (2026-04-28):** the §6.2 anomaly detection model evaluated on the synthetic eval set produces **AUROC within ±0.05** of the same model's AUROC on a real-seizure-vs-clear holdout.

**Harness procedure.**
1. Hold out 20 % of real seizures as the **real-eval set** (never seen by §6.6 — strict provenance check on `source_seizure_id`).
2. Generate the **synth-eval set** from the *other* 80 % of seizures over a benign corpus stratified to match real-eval's HS-code distribution.
3. Run §6.2's anomaly model on both. Compute AUROC, AUPRC, per-class F1.
4. Report the gap. If `|AUROC_synth − AUROC_real| > 0.05`, the run is rejected — investigate threat-library bias, illumination distribution mismatch, or noise-model drift.

**Continuous validation.** Every quarterly threat-library refresh re-runs the harness; results logged to a `tip_validation_runs` table with the gap, the renderer version, and the threat-library checksum.

#### 6.6.8 Failure modes & guards

| Failure mode | Symptom | Guard |
|---|---|---|
| Synth-only overfitting | Anomaly AUROC=0.99 on synth, ~0.7 on real | Locked policy: synthetic data **only ever evaluated**, never trained on for §6.2. §6.1 OCR is allowed to train on synthetic but holds out a 20 % real-plate test set with hard pass-fail |
| Threat-library bias | One scanner type / region dominates a class | Sampling weighted to match deployment-fleet scanner mix; Q-I3 |
| Illumination distribution mismatch | Per-energy intensity histogram diverges from real | Pre-flight check: KL divergence between synth-batch HE histogram and benign-corpus, threshold 0.2; abort if exceeded |
| Seam artifacts at composition boundary | Visible halo around threat in pseudocolor preview | Soft alpha mask + Phase-2 Meta-TIP polish layer when rolled out |
| Z-channel pathologies | Recomputed Z falls outside physical range (Z<5 or Z>40) | Reject and resample; log incidents. > 1 % reject flags a Z-calibration bug |
| Late-arriving customs reversal | A "Clear" benign source becomes "Seize" post-hoc; dataset contaminated | 90-day finalization filter + `tip_corpus_invalidation` event listener flags affected runs for regeneration |
| Tenant data leak via threat library | Seized-item scan from Tenant A appears in Tenant B's synthetic | Threat library is **per-tenant** by default; cross-tenant sharing requires explicit admin consent + audit event |
| Provenance gap | Synthetic example used as evidence in downstream review without operator awareness | `synthetic=true` tag is **load-bearing** — UI must badge synthetic distinctly; analyst review on synthetic scans is barred from `OutboundSubmission` |

#### 6.6.9 Open questions

| ID | Question | Status |
|---|---|---|
| Q-I1 | **Threat library sourcing — the headline open question.** In-house seizure capture vs. commercial threat-database licensing (TSA-licensed cargo TIP libraries, NEMA reference sets, vendor decks from Smiths/Rapiscan) vs. CT-phantom synthesis. Procurement, licensing terms, redaction posture, per-class volume targets all unresolved. **Headline blocker for §6.6.** | **Open** |
| Q-I2 | Per-scanner noise model fitting cadence. One-time at commission, or periodic re-fit as detectors age? Detector aging is real (1–3 % gain drift/year). | Open |
| Q-I3 | Sampling weights to match fleet-scanner mix. If threat library is 80 % FS6000-sourced and the fleet adds Nuctech in Q3, weights need a refresh path. | Open |
| Q-I4 | Phase-2 Meta-TIP polish layer — when (which seam-artifact severity threshold) and trained on what data? Defer until v0 ships and we have measured residual artifact rate. | Deferred |
| Q-I5 | 2.5D projection error for out-of-plane rotations near ±15°. Quantify against actual stereo-captured threats before locking the pose distribution. | Open — needs measurement |
| Q-I6 | Cross-tenant threat-library sharing policy. Default per-tenant; review when 2nd tenant onboards. | Deferred to 2nd tenant |
| Q-I7 | OCR training-on-synthetic exception. Validate empirically: does §6.1 hold real-plate EM when trained on synthetic-heavy mix, or overfit to synthetic plate fonts? | Open — empirical |
| Q-I8 | `synthetic=true` schema migration. Coordinate with whichever phase introduces TIP into scaffolding so the migration lands once. | Open — sequencing |

### 6.7 Dual-view registration

*Tier: image-analysis-direct.*

> **Status (2026-04-28):** Tier-3, no active dual-view scanner in the fleet. Spec is **design-ready, deploy-deferred** — same posture as DICOS (§5.5). Activated when the first dual-view adapter ships.

Modern cargo and baggage X-ray inspection systems increasingly capture two simultaneous views — top and side — to recover the depth ambiguity that a single transmission projection cannot resolve. A few new-generation systems ([Astrophysics Multi-View CT](https://astrophysicsinc.com/multi-view-ct/)) approach 3D reconstruction; the broader market is still 2-view 2D. Public ML benchmarks have caught up: the [DvXray dataset (IEEE 2024)](https://ieeexplore.ieee.org/document/10458082/) is the first 16 k-pair public dual-view baggage benchmark, and [arXiv 2511.18385](https://arxiv.org/html/2511.18385) treats the second view as a language signal for cross-attended detection.

#### 6.7.1 Problem

Given two scan strips of the same container — top-view and side-view — emitted by a dual-view scanner, produce a single registered pair where pixel column `x_top` and `x_side` correspond to the **same belt-travel position**. Without registration, downstream view-fusion (§6.2 anomaly, §6.3 manifest) is incoherent: the model would attend across views talking about different parts of the container.

The naive read of "register two images" reaches for 2D image registration — phase correlation, feature-based homography, optical flow. **That framing is wrong here.** Top and side share *exactly one* degree of freedom: the belt-travel coordinate. The container does not deform between views, the detectors are fixed in the gantry, and the two views look at orthogonal cross-sections of the same sweep. The misalignment is a 1D shift along the belt axis, set by the fixed physical spacing between top-detector and side-detector arrays divided by belt speed at scan time. **It is a 1D problem, not a 2D problem.** (Locked 2026-04-28.)

#### 6.7.2 Inputs / outputs

**Input.** Two `ScanArtifact` rows from the same `Scan`:
- `artifact_kind = Primary` — top view, shape `(H_top, W)` after decode
- `artifact_kind = SideView` — side view, shape `(H_side, W)` after decode

Both have the same `W` (belt-travel axis) by adapter contract — adapters that emit ragged widths must pad/crop to a common belt extent before yielding.

**Output.** Both artifacts persist with a new `MetadataJson` field:

```json
{
  "registration_offset_px": -47,
  "registration_confidence": 0.91,
  "registration_method": "ncc_vertical_edge_v1",
  "registration_belt_origin_px": 0
}
```

`registration_offset_px` is the **signed shift to apply to the side view** so its column 0 aligns with the top view's column 0. Both artifacts share `registration_belt_origin_px = 0` — column index = belt-travel coordinate. Downstream consumers read `registration_offset_px` from the SideView only; the Primary is canonical.

When confidence < threshold, the adapter additionally emits a `Finding` with `finding_type = 'registration_low_confidence'` for analyst attention.

#### 6.7.3 Architecture (Locked 2026-04-28)

The algorithm lives in the **scanner adapter**, not core. Vendor-specific timing characteristics (belt speed, detector array spacing, top↔side detector offset) are calibration data the adapter owns; core sees only the registered pair plus metadata.

Pipeline per scan, inside the adapter's `ParseAsync`:

1. **Per-view edge profile.** For each view: compute per-column vertical-edge gradient magnitude (Sobel-y, abs, sum-over-rows). Result: 1D signal of length `W` per view. Vertical edges (container side walls, axle vertical lines, dunnage) project sharply at the same belt position in both views; horizontal structure does not.
2. **Pre-filter.** High-pass each profile (subtract running mean, window ≈ 64 px) to suppress slow exposure trends. Optional: clip to `[μ − 2σ, μ + 4σ]` to suppress hot pixels.
3. **1D normalized cross-correlation** between the two profiles over search window `[−W_search, +W_search]`, default ±256 px. NCC peak location → `registration_offset_px`. Peak magnitude → `registration_confidence`.
4. **Sub-pixel refinement.** Parabolic fit through the peak ±1 NCC samples → fractional offset; round to int for `registration_offset_px`, keep float as `registration_offset_px_frac`.
5. **Persist.** Write metadata to SideView artifact's `MetadataJson`. Emit `Finding` if confidence < threshold.

**Why not 2D phase correlation / feature-based / optical flow.** No benefit: true degree of freedom is 1D, and 1D NCC on edge profiles concentrates SNR on the structures most stable across views. 2D phase correlation costs an FFT-pair (~30–80 ms) and gives a (Δx, Δy) where Δy is meaningless and noisy. Feature-based methods (ORB/SIFT) require shared features across orthogonal projections, which dual-view *doesn't* have. (Rejected, locked 2026-04-28.)

**Adapter capability flag.** Add to `IScannerAdapter.Capabilities`:

```csharp
public sealed record ScannerCapabilities(
    // … existing flags …
    bool SupportsDualView,                            // NEW
    DualViewGeometry? DualViewGeometry);              // NEW, non-null iff SupportsDualView
```

Same pattern as `SupportsDicosExport` in §5 — design-ready slot, no concrete adapter on day 1.

#### 6.7.4 Calibration

Per-scanner calibration parameters live alongside other per-instance thresholds in the `ScannerThresholdProfile` row from §6.5.

| Key | Type | Default | Source |
|---|---|---|---|
| `dualview.search_window_px` | int | 256 | belt-speed × max expected timing skew |
| `dualview.expected_offset_px` | int | 0 | nominal top↔side detector spacing ÷ belt speed × pixel pitch |
| `dualview.confidence_threshold` | float | 0.55 | tuned per scanner from holdout NCC distribution |
| `dualview.edge_window_px` | int | 64 | high-pass detrend window |
| `dualview.subpixel_refine` | bool | true | — |

**Calibration procedure** (one-off per scanner instance at commissioning, then periodic): score 200 known-good pairs, fit `expected_offset_px` = median NCC offset, fit `confidence_threshold` = 5th-percentile peak NCC. Re-run on belt-mechanics maintenance.

#### 6.7.5 Apply budget

Per pair, on lane-PC CPU (i7-1265U class, single thread):

| Stage | Budget |
|---|---|
| Sobel-y + sum (both views) | ≤ 15 ms |
| High-pass detrend | ≤ 3 ms |
| 1D NCC over ±256-px search | ≤ 25 ms |
| Sub-pixel refine + metadata write | ≤ 5 ms |
| **Total** | **≤ 50 ms p95 per pair** (locked) |

Even at 30 scans/min sustained, registration is not the bottleneck. If a future high-throughput scanner exceeds budget, the adapter MAY downsample edge profiles 2× before NCC — accuracy loss within the ≤ 8 px MAE bar.

**Acceptance bar (locked 2026-04-28):** registration MAE ≤ 8 px on a labeled holdout pair set (200+ pairs, hand-labeled or vendor-provided ground-truth offsets); < 2 % of pairs flagged `registration_low_confidence` on a healthy scanner.

#### 6.7.6 Deployment & rollout

Activated when the first dual-view scanner adapter enters the fleet — **Phase TBD, not active by default.** Until then the code path does not exist; `SupportsDualView` returns `false` on every shipped adapter.

| Phase | Behavior | Gate |
|---|---|---|
| 0. Spec | This section. No code. | First dual-view adapter committed |
| 1. Adapter + offline eval | Adapter implements registration; evaluated offline against labeled holdout | MAE ≤ 8 px, low-confidence rate < 2 % |
| 2. Shadow | Adapter emits registered pairs; downstream consumers ignore offset and run per-view. Metrics logged | 14 days clean, no model regression |
| 3. Fused-view consumers enabled | §6.2 runs cross-attention across views when `registration_confidence ≥ threshold`; §6.3 uses both. Below threshold → fall back to per-view | — |

Container-split (§3) runs **per-view independently** in all phases — splits should be the same belt-coordinate within registration tolerance, useful as a sanity probe for the metric.

#### 6.7.7 Failure modes & guards

- **Asymmetric belt acceleration on long containers.** Belt speed not perfectly uniform on 40-foot+ containers, especially at start/stop. **Guard:** segment the strip into 3 windows (head/middle/tail), compute per-window NCC; if windowed offsets disagree by > 12 px, flag `registration_nonuniform_belt` and emit `Finding`. Phase 2 could fit a piecewise-linear warp; out of scope for v0.
- **View-occlusion by bulk metal.** Heavy payload saturates one view's vertical-edge profile. NCC still resolves on surrounding clean regions, but peak NCC depressed. **Guard:** per-pair confidence captures it; below-threshold routes to analyst flag.
- **Missing one of the two views.** Adapter ingest dropped the side artifact. **Guard:** registration only runs when both `Primary` and `SideView` artifacts exist on the same `Scan`. Single-view scans pass through with no metadata; downstream consumers must read `registration_offset_px == null` as "no fusion available."
- **Vibration / pendulum on suspended cargo.** Container swing introduces phase between top and side that is not pure 1D shift. Symptom: low NCC peak with broad correlation surface. **Guard:** confidence threshold catches it.
- **Detector saturation on high-density containers.** Both views hit noise floor; profiles uninformative. **Guard:** if `sum(edge_profile) < min_edge_energy`, skip registration and emit `Finding finding_type = 'registration_skipped_low_signal'`.
- **Belt direction reversed.** Scanner mounted reverse vs calibration assumption. **Guard:** if `|registration_offset_px - expected_offset_px| > 3 × search_window`, log `dualview.offset_outside_calibration` — likely commissioning error.

#### 6.7.8 Open questions

| ID | Question | Status |
|---|---|---|
| Q-J1 | Do current FS6000 / ASE units in fleet have side-view output at all? Vendor docs are ambiguous. **Action:** vendor contact during the same conversation as Q-C1 (FS6000 DICOS). | Open |
| Q-J2 | If FS6000 / ASE *do* offer side-view as an optional channel, are existing v1 decoders dropping it silently? Verify against `services/image-splitter/inspector/decoders/{fs6000,ase}.py` headers. **No v1 edits** — read-only inspection. | Open |
| Q-J3 | Concrete shape of `DualViewGeometry` record on `ScannerCapabilities` — minimum: detector-spacing-mm, pixel-pitch-mm-per-px. **Recommend** also nominal belt speed for calibration default. | Recommendation logged |
| Q-J4 | Should `registration_offset_px` live on `SideView` only, or also on `Primary` (with sign flipped) for symmetry? **Recommend** SideView-only — Primary is canonical, writers should not duplicate state. | Recommendation logged |
| Q-J5 | Piecewise-linear warp (vs single shift) for non-uniform belt acceleration — defer until field data on how often the windowed-disagreement guard fires. | Deferred |
| Q-J6 | Does the §6.2 anomaly model want `registration_offset_px` as a feature (so it can soft-attend on borderline-confidence pairs), or only as a hard gate? | Deferred to §6.2 spec |
| Q-J7 | Cross-attention vs late-fusion in §6.2 / §6.3 once registration is available — design choice for those sections. | Cross-reference, Open |
| Q-J8 | DvXray and arXiv 2511.18385 are baggage benchmarks, not cargo. Cargo dual-view labeled data is even scarcer. Tie-in with §6.6 — TIP can synthesize matched-pair top/side projections cheaply if the geometry model is right. | Open opportunity |

### 6.8 Beam-hardening and metal-streak correction

*Tier: image-analysis-direct.*

#### 6.8.1 Problem

Cargo X-ray is a 2D dual-energy transmission modality: HE and LE channels are line-integral attenuation maps, not reconstructed CT volumes. Behind dense overburden — engine blocks, transformer cores, stacked rebar, motorcycle frames packed against the container wall — two failure modes show up together:

1. **Beam hardening.** The polychromatic source's low-energy photons are absorbed disproportionately. The HE/LE ratio that §6.2's anomaly detector uses to estimate Z_eff stops being linear in path length. Steel reads "denser steel + a bit of lead"; lead behind steel reads as "uranium-ish." Z-discrimination collapses exactly where contraband most often hides.
2. **Streak / scatter halos.** Forward-scattered photons and detector cross-talk spread mass from dense occluders into adjacent low-density regions, smearing a phantom shadow across declared cargo. The shadow has no physical material — it's a measurement artifact — but downstream models treat it as signal.

CT MAR literature is mature ([RadioGraphics 2018](https://pubs.rsna.org/doi/full/10.1148/rg.2018170102), [ScienceDirect 2024](https://www.sciencedirect.com/science/article/abs/pii/S0720048X23005909)) but most of its toolkit assumes a 3D reconstruction stage we do not have. What does transfer: the **closed-form polynomial linearization** of the beam-hardening curve, and **image-domain deep restoration** trained on synthetic-paired data. Recent transformer restoration work ([Restormer, CVPR 2022](https://arxiv.org/abs/2111.09881)) has been applied directly to single-shot X-ray imagery with strong results ([Springer 2025 — Restormer on walnut X-rays](https://link.springer.com/article/10.1007/s11760-025-04274-6)).

This section specifies a **two-stage corrector** that runs as a pre-processing pass before §6.2 anomaly detection, gated per-region so we never pay transformer cost on clean cargo.

#### 6.8.2 Inputs / outputs

**Input.** Raw `ScanArtifact` channels for one `Scan`, preferably before any vendor LUT:
- `(HE, LE)` — `(2, H, W)` float32, normalized 0.5%–99.5% percentile-stretched (matches §3 convention).
- Optional `material` channel if the adapter emits one; not required.
- `ScannerDeviceInstance.scanner_id` (carried via §6.5 calibration profile) — selects the per-device beam-hardening polynomial coefficients.

**Output.** Corrected `ScanArtifact` channels of identical shape:
- `(HE_corr, LE_corr)` — `(2, H, W)` float32, written as new `ScanArtifact` rows with `artifact_kind = CorrectedPrimary` and a `derivation` link back to source artifacts (don't overwrite raw — keep the audit trail).
- Per-region `correction_quality_score ∈ [0, 1]` — confidence that corrected pixels are reliable. Persisted as `jsonb`: `{ regions: [{ bbox, score, stage_used }] }` where `stage_used ∈ {closed_form, restormer, none}`.
- `correction_metrics` span attached to §6.2 telemetry.

#### 6.8.3 Architecture (locked 2026-04-28)

**Two-stage cascade. Reject blanket-Restormer.**

| Stage | Component | Runs on | Cost |
|---|---|---|---|
| 1. Closed-form beam-hardening correction | Per-device polynomial linearization of HE/LE ratio + image-domain scatter-kernel superposition | **Every scan, full image** | Cheap (≤ 30 ms p95) |
| 2. Region-gated deep restoration | Restormer-class transformer applied only to crops flagged by the gating classifier | **Crops from ≤ 15 % of scans** | Heavy (≤ 200 ms p95 when triggered) |

**Stage 1 — closed-form.** Fits a 3rd-order polynomial `μ_corrected = a₁·μ_raw + a₂·μ_raw² + a₃·μ_raw³` on the HE channel using a per-scanner calibration set of known step-wedges. Coefficients live on `ScannerCalibrationProfile` (§6.5). Scatter halo correction is a separable Gaussian deconvolution with a kernel measured per scanner. Pure CPU; no model load; deterministic.

**Stage 2 — gated Restormer.** A small **gating classifier** (~1 M params, MobileNetV3-small backbone, INT8) sweeps the closed-form output at 1/4 resolution and emits a coarse heatmap of "overburden likelihood." Connected components above threshold define correction regions. Each region cropped (with 32 px context padding), passed through Restormer at full resolution, composited back via feathered alpha blending. Restormer params: 4 encoder/decoder stages, 36-channel base, MDTA + GDFN blocks; ~26 M params at full size, distilled to ~12 M for the lane-PC profile.

**Both models run through `IInferenceRunner`** (§4). Gating classifier registers as `InferenceModel.family = 'overburden-gate'`, restoration as `family = 'metal-streak-restormer'`. Hot-swap, sha256, telemetry — all inherited from §4.5–§4.6.

**Why not blanket Restormer.** A 26 M-param transformer across every full-resolution scan would cost 800–1500 ms p95 even on dGPU and would correct regions that didn't need correction (introducing hallucination risk on clean cargo).

**Sensor fusion (backscatter + transmission) is out of scope.** Backscatter would solve dense-overburden more directly, but the current fleet is transmission-only. Tracked as Q-K4.

#### 6.8.4 Training data for Restormer

Restormer requires **paired (degraded, clean) examples**. Real cargo doesn't come with a clean ground truth, so we synthesize.

| Source | Description | Estimated volume |
|---|---|---|
| **Clean base** | Production scans, no analyst-flagged overburden, `Verdict = Clear`, ≥ 14 days post-decision | 80 k scans |
| **Synthetic injection** | Composite dense-metal occluders over clean base via TIP-style multiplicative compositing (§6.6) | 5–10× per clean = 400 k–800 k pairs |
| **Real overburden holdout** | Scans where analysts flagged overburden + `post_hoc_outcome` confirmed contraband | ~200–500 cases |
| **Calibration wedges** | Per-scanner step-wedge captures from Stage-1 fitting; reused for Restormer fine-tune | 50 wedges × N scanners |

**Synthesis recipe.** Pull a clean cargo scan; pick a 256×256 crop with cargo present; sample a metal-occluder mask from a library of analyst-labeled real occluders (Q-K2); apply Beer-Lambert: `I_degraded = I_clean · exp(-μ_metal · t_metal)` where `μ_metal` is sampled from {steel, lead, copper, aluminum} attenuation tables; add scanner-measured scatter halo kernel; add Poisson photon noise scaled to post-attenuation flux. The clean crop is the target; degraded is the input.

**Augmentation.** Horizontal flip; ±10° rotation; brightness/gamma jitter ±10 %; mild Gaussian blur σ 0–0.5 px; random metal-mask scaling 0.5×–2×; random metal-material reassignment.

**Splits.** 80/10/10 train/val/test, **stratified by source scanner serial** so the test set contains scanners the model never trained on.

#### 6.8.5 Loss and evaluation

**Loss (Restormer training).**
- Primary: L1 reconstruction loss in HE channel.
- Auxiliary: L1 on LE (weight 0.5) — preserves dual-energy ratio.
- Auxiliary: SSIM loss (weight 0.3) — preserves local structure, prevents regression toward a smooth mean.
- **No adversarial loss.** Hallucinated detail is a real failure mode; we explicitly prefer slightly blurry corrections over confident-but-fabricated structure.

**Eval metrics.**

| Metric | Source | Target |
|---|---|---|
| PSNR on synthetic test | Reconstruction quality | ≥ 32 dB on degraded, ≥ 40 dB on lightly-degraded |
| SSIM on synthetic test | Structure preservation | ≥ 0.92 |
| **§6.2 anomaly AUROC lift on flagged regions** | Downstream — the bar that matters | **≥ 0.05 absolute on dense-overburden stratum** |
| Closed-form-only AUROC lift | Ablation — confirms Stage 2 pulls weight | reported, not gated |
| Hallucination rate (analyst-judged on 200-scan sample) | Manual review | ≤ 2 % of corrected regions |
| Cross-scanner generalization | PSNR on held-out scanners | within 2 dB of in-distribution |

**A/B harness.** Once shadow phase is clean, route 5–10 % of flagged-overburden scans through correction-on, the rest through correction-off (Stage 1 only). Compare §6.2 AUROC and analyst time-to-decision.

#### 6.8.6 Inference budget (locked 2026-04-28)

| Stage | Profile | Target p95 |
|---|---|---|
| Closed-form (Stage 1) | CPU, full image 2295×1378 | ≤ 30 ms |
| Gating classifier (~1 M params, INT8) | CPU / iGPU via DirectML | ≤ 10 ms |
| Restormer-on-region (~12 M distilled, FP16) | iGPU via DirectML, per crop | ≤ 200 ms |
| Restormer-on-region | dGPU via CUDA, per crop | ≤ 35 ms |
| Aggregate added latency, no-trigger path | Stage 1 + gate only (≥ 85 % of scans) | ≤ 40 ms |
| Aggregate added latency, trigger path | Stage 1 + gate + Restormer (≤ 15 % of scans) | ≤ 240 ms |
| Cold-load penalty | Both warmed at process start | ≤ 3 s |

Trigger budget reserved for worst-case scan with up to 4 distinct overburden regions. If a scan triggers > 4, only top-4 by gate confidence are restored; rest stay closed-form-only with `stage_used = closed_form_overflow`.

#### 6.8.7 Deployment and rollout

| Phase | Behavior | Gate |
|---|---|---|
| 0. Dev eval | Both models offline against frozen synthetic + real-overburden test sets | Synthetic PSNR ≥ 32 dB; real-overburden AUROC lift ≥ 0.05 |
| 1. Shadow | Pipeline runs on every production scan; corrected channels logged but **§6.2 still consumes raw**. Quality scores logged, latency budget verified | ≥ 14 days, p95 budgets met, hallucination rate ≤ 2 % on sampled review |
| 2. Opt-in per scanner | Operators enable correction per-scanner via §6.5 `ScannerThresholdProfile` flag `beam_hardening_correction_enabled`. §6.2 consumes corrected channels for opted-in scanners. Stage 1 only — Restormer still shadow | 30 days clean, AUROC lift confirmed on opted-in scanners' real traffic |
| 3. Stage 2 enabled per scanner | Same per-scanner opt-in escalates to enable region-gated Restormer. Stage-1-only remains a fallback if the runner faults | SLO: 30-day rolling AUROC lift ≥ 0.05, hallucination complaints ≤ 1 / 1000 scans |

**Per-scanner-instance gating** (not tenant-wide, not global): `ScannerThresholdProfile` (§6.5) carries `beam_hardening_stage1_enabled` and `beam_hardening_stage2_enabled` as independent flags. A scanner with a known pathological scatter profile can stay on Stage 1 only.

**Artifact paths.** `<storage_root>/models/overburden-gate/v{N}/model.onnx` and `<storage_root>/models/metal-streak-restormer/v{N}/model.onnx`. Per-scanner Stage-1 polynomial coefficients live on `ScannerCalibrationProfile`, not as model artifacts.

#### 6.8.8 Failure modes and guards

- **Over-correction creating false negatives.** The model "cleans up" a real anomaly because it looks like a metal artifact. **Guard:** any region where corrected `correction_quality_score < 0.6` is annotated and §6.2 is told to treat its anomaly score with reduced weight, not increased confidence. Both raw and corrected channels persisted; analyst UI offers a toggle.
- **Gating classifier misses subtle overburden.** Closed-form correction is silently inadequate, §6.2 is fed mildly-corrupted channels and over-confidently scores them clean. **Guard:** gating threshold deliberately calibrated to over-trigger (target 15 % rate even though ~8 % truly need Stage 2). False-positive gates cost latency, not accuracy.
- **Restormer hallucinating content where signal is truly missing.** Behind a transformer core photon count is genuinely zero — there is no hidden information to recover. **Guards:** (a) no GAN loss; (b) crops where input has > 40 % zero-flux pixels are flagged `stage_used = restormer_low_confidence` and corrected output is alpha-blended at 0.5 weight against closed-form; (c) analyst UI surfaces a "synthesized region" overlay so reviewers see what the model invented.
- **Cross-scanner drift.** Stage-1 polynomial coefficients are per-scanner; a drifted spectrum produces wrong corrections. **Guard:** `ScannerCalibrationProfile.calibrated_at` watched by a monthly re-calibration check; expired calibrations disable Stage 1 and emit a warning.
- **Calibration spoofing.** A bad actor with admin access could submit a deliberately wrong polynomial. **Guard:** calibration coefficient updates emit a `DomainEvent`; admin-UI changes require dual-approval per §7.4-style audit trail.
- **Pre-render pipeline interaction.** §7.7 pre-renders thumbnails/previews from raw artifacts. Correction must run *before* pre-render or the human reviewer sees uncorrected previews while §6.2 sees corrected channels. **Resolution:** correction runs as a pre-render dependency; queue carries `correction_required` and pre-render workers wait on the corrected artifact.

#### 6.8.9 Open questions

| ID | Question | Status |
|---|---|---|
| Q-K1 | How are per-scanner Stage-1 polynomial coefficients fit and refreshed? Lab step-wedge captures during commissioning + field re-cal on a cadence — but who owns the cadence and the wedge fixtures? Likely rolls into §6.5. | Open |
| Q-K2 | Real-overburden mask labeling pipeline. Need a small labeled set of metal-occluder masks for synthetic compositing. Use Label Studio per §6.4, or stand up a one-off labeling drive? | Open |
| Q-K3 | Per-scanner-instance vs per-`ScannerDeviceType` Restormer weights. Default to fleet-wide; revisit if cross-scanner generalization fails the §6.8.5 bar. | Recommendation logged |
| Q-K4 | **Backscatter sensor fusion.** Backscatter would solve dense-overburden directly. Out of scope for transmission-only fleet; flagged as separate future track. | Deferred — separate track |
| Q-K5 | Should §6.8 emit DICOS-shaped correction provenance (which polynomial, which model version, which regions) as part of TDR algorithm metadata in §5.4? Aligns with "store as received + derive" but adds payload weight. | Deferred |
| Q-K6 | Does correction run on central pre-render only, or also on edge nodes (§7.6)? Edge nodes have weaker GPUs; Stage 2 may be central-only with Stage 1 at the edge. | Deferred to Phase 7.6 |
| Q-K7 | Hallucination-rate measurement methodology. Need a structured rubric (definitely-hallucinated / plausibly-hallucinated / clearly-recovered) and an inter-rater reliability bar. | Open |
| Q-K8 | Interaction with §6.5 thresholds when Stage 2 is on. Correction shifts §6.2's AUROC curve; plan: re-calibrate thresholds per-scanner after Stage 2 enablement. | Open |

### 6.9 In-house threat library capture pipeline

*Tier: image-analysis-adjacent.*

> **Status:** Locked architecture 2026-04-28. Closes §6.6's Q-I1 in favour of **option (a) — in-house seizure capture as the primary threat-library source**, with commercial libraries (Q-I1 option b) and CT-phantom synthesis (Q-I1 option c) **deferred** as augmentation tracks. This section specifies the operational pipeline that turns one real customs seizure into one usable row in the §6.6 threat library.
> **Foundational reference (segmentation):** Ravi et al., ["SAM 2: Segment Anything in Images and Videos,"](https://arxiv.org/abs/2408.00714) Meta AI 2024.
> **Foundational reference (dual-energy material attribution):** Eilbert & Krug, "Effective Atomic Number from Dual-Energy Transmission X-Ray," in [Wang & Wu (eds.), *Computer Vision in X-ray Testing*, Springer 2015](https://link.springer.com/book/10.1007/978-3-319-20747-6).

#### 6.9.1 Problem

§6.6's TIP renderer is *physically correct* only if the threat instances it composites are *physically correct* — captured on a real customs scanner, in raw HE/LE float32 space, with a clean alpha mask, with a known-good Z_eff per pixel, and with provenance that withstands chain-of-custody scrutiny. Three sourcing strategies were debated in Q-I1:

| Option | Verdict |
|---|---|
| (a) In-house seizure capture | **Selected — primary track, locked 2026-04-28** |
| (b) Commercial threat libraries (TSA-licensed cargo TIP, NEMA, vendor decks from Smiths/Rapiscan/Nuctech) | Deferred — pursue as supplementary once procurement closes; addresses class long-tails (HEU, exotic narcotics) we can't seize at volume |
| (c) CT-phantom synthesis (3D phantom → simulated 2D X-ray) | Deferred — last-resort for classes neither (a) nor (b) cover; introduces synth-on-synth risk |

Why in-house first: scanner-fidelity match (a threat captured on the same scanner family that will see the synthesized scan in production gives §6.6's per-scanner noise model a free anchor); auditable provenance (every threat instance traces to a real `InspectionCase` with `Verdict.decision = Seize`); marginal cost is operator time at seize, no licensing fees; the four threat classes that dominate this fleet's seizure profile all hit per-port at a rate (§6.9.5) that supports a viable ramp.

What in-house *does not* cover: classes with near-zero local seizure rate (radiological/nuclear; precision-manufactured chemical-weapon precursors) and pose distributions a single seizure can't span. Those wait for vendor decks.

#### 6.9.2 Capture protocol — operator workflow when a seizure happens

The capture event is a sub-workflow appended to the seizure handling already documented in §6.4: when `Verdict.decision = Seize` lands, a `CaptureIsolatedThreat` task materialises on the seizing analyst's queue (§6.9.8).

**Equipment.**
- Dedicated **calibration belt** at the seizure room — low-deck conveyor feeding the same scanner head used for live cargo, sized for hand-placed objects, equipped with a printed alignment grid. One belt per port.
- The same `ScannerDeviceInstance` operating in **calibration mode** (a scanner mode flag carried by `IScannerAdapter`; FS6000 supports it natively, other adapters declare via `ScannerCapabilities.SupportsCalibrationMode`).
- Clean, lint-free, low-attenuation tray (3 mm acrylic) for items too small to sit on the belt directly.
- Evidentiary chain bag, sealed before and after.

**Room.** Locked seizure-handling room. **No cargo present** during capture (otherwise §6.6.3's clean-benign-times-clean-threat invariant breaks). Single-tenant access; `Location` of capture logged on the provenance row. Camera-monitored for chain-of-custody.

**Operator training.** Two-hour module covering: cleaning the item (remove packaging/residue/evidence tags — but **do not disassemble**, see §6.9.9), placement on the alignment grid in a documented canonical pose (declared per threat-class taxonomy in §6.9.4), running the scanner in calibration mode, confirming the raw blob landed, returning the item to evidence within 15 minutes. Training certification stamped on `User.capture_certified_at`; un-certified users can't trigger a `CaptureIsolatedThreat` task close.

**Operator workflow.**
1. Task fires in the analyst's v2 portal queue. Carries the originating `case_id`, proposed `threat_class` (pre-filled from the seizure's `Finding.finding_type`), and a checklist.
2. Operator unbags, photographs as-found state (uploaded as `CaseAttachment`).
3. Operator cleans per the threat-class-specific decontamination card (e.g. Currency: untaped; Firearm: removed from carry case but **not** field-stripped).
4. Operator places item on calibration belt at canonical pose, flags top-down vs side-view by belt direction.
5. Scanner runs calibration mode. Adapter writes raw blob to `<storage_root>/captures/inbox/<capture_id>/`, emits `RawCaptureLanded` domain event.
6. Operator confirms on-screen preview. If item ran off belt edge, double-exposure, etc., marks `failed_recapture` and re-runs from step 4.
7. Item returns to evidence; bag reseal photographed; task closes with `capture_quality ∈ {good, marginal, failed}`.

A `marginal` flag still ingests but is excluded from §6.6.5's primary sampling — kept as a secondary-pose source.

#### 6.9.3 Ingest pipeline

Pipeline runs in a hosted background service, `NickERP.Inspection.ThreatLibrary.Ingest` (sibling to §6.4's `ActiveLearning.Extractor`), driven by the `RawCaptureLanded` event.

**Stage 1 — Decode and persist raw.** Adapter that wrote the blob also wrote a sidecar JSON with scanner serial, mode, timestamp, operator id. Ingest reads through the same `IScannerAdapter.ParseAsync` path live cargo uses, materialising HE/LE/material as float32 .npy files content-addressed by sha256 (§6.9.7).

**Stage 2 — Segmentation via SAM 2.** Same FastAPI ML Backend the §6.4.5 Label Studio integration uses (deduplicates infra). One synthetic click prompt at the centroid of the connected component above an HE-attenuation threshold; SAM 2 returns an alpha mask. Operator reviews in §6.9.6 capture app and refines (additional click prompts, brush corrections) for soft edges (cloth-wrapped narcotics, fissured powder). SAM 2 model version stamped on the provenance row.

**Stage 3 — Z_eff material attribution per pixel.** `Z_eff(x,y) = f(HE(x,y), LE(x,y))` computed using the per-scanner Z calibration the live pipeline maintains via `ScannerCalibrationProfile` (§6.5). Per-threat *expected* Z_eff range recorded in `tags.zeff_range`; pixels falling outside are flagged. > 5 % out-of-range routes to operator re-review (likely contamination).

**Stage 4 — Alpha mask refinement.** SAM 2's mask is binarized at the foreground edge but soft inside. 3-pixel Gaussian feather at the binary edge; soft-interior alpha blended to match linear-attenuation profile so §6.6.3's `α · A_threat` composition produces no seam.

**Stage 5 — Redaction.** Three passes in raw-pixel space:
- **Scan-corner stamp** — `ScannerDeviceType` declares stamp regions; raw pixels masked to local-mean attenuation of surrounding belt.
- **License-plate** — small detection model sweeps the preview-render. Any positive triggers a hold (operator re-capture).
- **PII** — Currency-class threats carry `tags.pii_class = 'banknote_serials'`; ingest applies a deterministic blur over serial-number regions (template-driven by note denomination). Other PII surfaces (passports as Contraband_Other) get a manual redaction step before promotion from `pending_redaction` to `active`.

**Stage 6 — Provenance row write.** `threat_library_provenance` row materialises with `status = active`. `DomainEvent.ThreatLibraryEntryAdded` appended to `audit.events` carrying source `case_id`, `seizure_id`, capturing operator, sha256s of all artifact files, SAM 2 model version, redaction summary.

**Stage 7 — Bias-guard re-counting.** Ingest recomputes the per-(class, scanner-source) tally that §6.6.4's bias guard reads. If any class crosses the 95 % single-scanner threshold, `ThreatLibraryBiasWarning` event fires; new captures of the over-represented combination get sampled at lower weight by §6.6.5 until balance recovers.

#### 6.9.4 Schema

**Table: `inspection.threat_library_provenance`** (per-tenant, RLS enforced).

| Column | Type | Notes |
|---|---|---|
| `threat_id` | `uuid PK` | Cited from §6.6.2's threat library |
| `tenant_id` | `uuid NOT NULL` | RLS key |
| `location_id` | `uuid NOT NULL` | Capturing port |
| `threat_class` | `text NOT NULL CHECK` | Enum: `Firearm`, `Currency`, `Narcotic`, `Contraband_Other`, `BenignBaseline` |
| `threat_subclass` | `text` | Open vocabulary; see taxonomy below |
| `source_seizure_id` | `uuid NOT NULL` | FK → `cases.case_id` |
| `source_verdict_id` | `uuid NOT NULL` | FK → `verdicts` (the `decision = Seize` that triggered capture) |
| `capture_case_id` | `uuid` | Internal capture event as a degenerate `InspectionCase` of `subject_type = ThreatCapture` |
| `captured_at` | `timestamptz NOT NULL` | When the calibration-belt scan ran |
| `captured_by_user_id` | `uuid NOT NULL` | Certified operator |
| `source_scanner_instance_id` | `uuid NOT NULL` | FK → `ScannerDeviceInstance` |
| `source_scanner_type_code` | `text NOT NULL` | Denormalised for §6.6.4 bias query speed |
| `he_path`, `le_path`, `material_zeff_path`, `alpha_mask_path` | `text NOT NULL` | Content-addressed; `<storage_root>/threat-library/<sha>/{he,le,material_zeff,alpha_mask}.npy` |
| `pose_canonical` | `jsonb NOT NULL` | `{ orientation, scanner_view, anchors[] }` per the threat-class taxonomy |
| `tags` | `jsonb NOT NULL` | `zeff_range`, `mass_estimate_g`, `with_packaging`, `pii_class`, `subclass_confidence`, `operator_notes`, `linked_threats` |
| `sam2_model_version` | `text NOT NULL` | Stamped per ingest |
| `segmentation_quality_score` | `numeric(4,3)` | Operator-confirmed; 0.0–1.0 |
| `redaction_flags` | `jsonb NOT NULL` | `{ stamp, license_plate, pii, manual_review }` |
| `legal_hold_status` | `text NOT NULL DEFAULT 'none'` | Enum: `none`, `held`, `released` |
| `status` | `text NOT NULL` | Enum: `pending_redaction`, `active`, `quarantined`, `retired` |
| `created_at`, `updated_at` | `timestamptz NOT NULL` | |

Indexes: `(tenant_id, threat_class, source_scanner_type_code)` for §6.6.4 bias guard; partial `(tenant_id, status) WHERE status = 'active'` for §6.6.5 sampling; `(legal_hold_status) WHERE legal_hold_status = 'held'` for retention sweeper.

**Threat-class taxonomy (Locked 2026-04-28; subclass openness flagged Q-L1).**

| Class | Subclass examples | Canonical pose |
|---|---|---|
| `Firearm` | `Pistol`, `Rifle`, `Shotgun`, `Component_Receiver`, `Ammunition_Bulk` | Top-down on belt, slide/bolt forward, magazine inserted, action closed |
| `Currency` | `Banknote_Bundle`, `Banknote_Loose`, `Coin_Bulk` | Top-down, bundles oriented long-edge to belt direction |
| `Narcotic` | `Powder_Compressed`, `Powder_Loose`, `Pill_Mass`, `Plant_Material`, `Liquid_Concealed` | Top-down, packaging removed *only if* legally permitted (otherwise `with_packaging=true`) |
| `Contraband_Other` | `Counterfeit_Goods`, `Wildlife_Product`, `Tobacco_Untaxed`, `Chemical_Precursor` | Class-specific |
| `BenignBaseline` | `Decoy_Lookalike` | A deliberately captured benign object resembling a threat (starter pistol, paper stack cut to banknote dimensions). Trains §6.2 not to fire on look-alikes |

#### 6.9.5 Volume targets

§6.6's TIP renderer needs per-class minimums to drive §6.2 anomaly evaluation without bias. **Per-tenant**, single-port (Tema) deployment.

| Class | Phase-1 floor | Phase-2 target | Per-scanner-source minimum |
|---|---|---|---|
| `Firearm` | 200 instances | 800 | ≥ 50 from each of top-2 scanner types |
| `Currency` | 300 instances | 1,200 | ≥ 80 from each of top-2 scanner types |
| `Narcotic` | 400 instances | 1,500 | ≥ 100 from each of top-2 scanner types |
| `Contraband_Other` | 300 instances | 1,000 | ≥ 60 from each of top-2 scanner types |
| `BenignBaseline` | 100 instances | 400 | n/a (operator-curated) |

**Ramp realism.** Tema's seizure rate from v1 verdict logs (3-year window): ~3.2 firearm / ~8 currency / ~6 narcotic / ~4 other-contraband seizures per month. Assuming 80 % capture-success rate plus the §6.9.8 late-capture path, Phase-1 floor is reachable in **~14 months for Currency, ~18 months for Narcotic, ~26 months for Firearm**. Phase-2 targets are 4–5 years out.

This is why §6.6 v0 cannot wait for §6.9 to fill in alone — Q-I1's deferred (b) commercial track must run in parallel to bridge the front-loaded years. The §6.9 pipeline is what makes §6.6 *eventually* self-sufficient.

#### 6.9.6 Tooling

**Capture app.** New page in the v2 Blazor portal at `/inspection/threat-capture/{taskId}`. Server-rendered Blazor Server (consistent with `apps/portal`). Wall-mounted touch terminal in the seizure room shares the operator's workstation login. Mobile capture flagged Q-L4 for outdoor seizures.

State machine: Brief → As-found photo upload → Scanner ready (one-button "I'm ready, start calibration scan") → Preview review (auto-render + SAM 2 mask overlay; operator confirms or refines via click prompts) → Subclass + tags → Redaction review → Submit.

The capture app reuses the same authentication, RLS, and `ICaseRepository` reads as the rest of the portal.

**Integration with §6.4 review console.** When `Verdict.decision = Seize` lands, a domain-event handler creates a `CaptureIsolatedThreat` work item on the seizing analyst's queue. The work item is **not** an `AnalystReview` — it's a new `CaptureTask` entity in the same queue infrastructure (`inspection.work_queue`). 4-hour SLA escalates to analyst-supervisor's queue (configurable per `Location`, Q-L5).

#### 6.9.7 Storage & retention

**Path scheme.** Content-addressed by sha256, sharded by 2-byte prefix:

```
<storage_root>/threat-library/<tenant_id>/<sha256[:2]>/<sha256>/
    he.npy
    le.npy
    material_zeff.npy
    alpha_mask.npy
    preview.jpg
    metadata.json
```

`<tenant_id>` segments at path level so backup-restore of one tenant doesn't risk leakage into another's tree (defence-in-depth on top of §6.6.8's per-tenant default).

**Cross-tenant access.** Default **off**. Re-flagged as Q-L7. Cross-tenant share would be implemented by an `OutboundSubmission`-shaped explicit grant entity, **never** by symlink or path-share.

**Retention policy.**

| State | Online retention | Cold archive |
|---|---|---|
| `active` | indefinite while `legal_hold_status != released` | n/a |
| `quarantined` (verdict overturned post-seizure) | 12 months | 7 years |
| `retired` (superseded by re-capture) | 6 months online | 7 years |
| Failed captures | 30 days raw blob; no provenance row | none |

**Legal-hold posture.** While the parent `InspectionCase` carries `legal_hold = true` (set by the seizing authority via the §6.11 inbound adapter), `legal_hold_status = held` prevents deletion, cross-tenant export, and modification of any field except `legal_hold_status` itself and `tags.operator_notes`. A `legal_hold_released` event clears the flag.

**Encryption.** At-rest via the v2 cluster default (LUKS on storage volume). Per-row envelope encryption considered and **rejected** — disproportionate to the threat model when the storage volume already requires admin access; revisit if §6.9 ever serves multi-cluster or cloud-tier.

#### 6.9.8 Workflow integration

**Domain-event handler `OnVerdictSeized`** subscribes to `audit.events` filtered by `event_type = VerdictDecided AND payload->>'decision' = 'Seize'`. Logic:

1. Idempotency: skip if a `CaptureTask` already exists for `(verdict_id, threat_class)`.
2. Determine `threat_class` from the seizure `Finding.finding_type` mapping (table-driven; missing mapping warns and falls back to `Contraband_Other`).
3. Create one `CaptureTask` per distinct threat type (a single seizure can yield multiple captures if firearms + currency are seized together).
4. Place on seizing analyst's queue with 4-hour SLA.

**Idempotency on re-seizure verdicts.** v1 telemetry shows 0.3 % of `Clear` flips to `Seize` post-hoc (per the §6.11 supersession path). Original case may be evidentially closed but a `CaptureTask` can still open against it — the late-capture path.

**Late-capture.** `captured_at` from time of *capture*, not seizure. `seizure_to_capture_lag_hours` logged for retrospective analysis. Lag > 30 days ingests with `tags.late_capture = true`; §6.6.5 sampling does not down-weight, but bias monitoring tracks whether late-captured threats systematically differ.

**Capture-of-capture forbidden.** A captured threat is itself stored as a degenerate `InspectionCase` of `subject_type = ThreatCapture`. The seize-handler explicitly excludes capture cases — capturing a capture would loop. Guard: `subject_type != 'ThreatCapture'` precondition.

**Audit chain.** Full lineage end-to-end:

```
audit.events: VerdictDecided(decision=Seize, case_id=A) →
audit.events: CaptureTaskCreated(verdict_id=V, task_id=T) →
audit.events: RawCaptureLanded(task_id=T, scanner_id=S, sha256=H) →
audit.events: ThreatLibraryEntryAdded(threat_id=X, source_verdict_id=V) →
... eventually ...
synthetic/tip/<run_id>/manifest.jsonl mentions threat_id=X
```

Every link carries a `correlation_id`; investigators can pull the full chain from one synthetic scan back to the originating customs seizure.

#### 6.9.9 Failure modes & guards

| Failure | Symptom | Guard |
|---|---|---|
| **Incomplete capture.** Operator forgot to clean residue (tape, ink stamps, tracer dust) | Synthetic scans carry residual non-threat attenuation; §6.6.7 generalization gap blows out | Mandatory pre-capture photo (§6.9.2 step 2) reviewed by operator's supervisor on a 10 % audit; SAM 2 mask review surfaces unexpected high-attenuation specks (Z out of plausible range for class) |
| **Contamination — packaging still attached.** Powdered narcotic captured inside the original tape-and-cling-wrap bundle | Threat's attenuation is the threat *plus packaging* | `tags.with_packaging` MUST be set; `with_packaging=true` rows route to a separate §6.6 sampling bucket. Cannot mix with `with_packaging=false` rows in the same TIP run unless explicitly opted in |
| **Operator capture errors** (item ran off belt; double-exposure; scanner in diagnostic mode not calibration mode) | Visible artifacts in preview | Step 6 of §6.9.2 catches; `failed_recapture` flag prevents ingest |
| **Mis-classified threat-class at capture.** Operator picks `Narcotic` when subclass is actually a chemical precursor (`Contraband_Other`) | Wrong class bucket; §6.6 oversamples wrong family | Quarterly threat-library re-review by senior analyst; reclassification updates row in place but original class preserved in `audit.events` |
| **Cross-tenant leakage** | Tenant A's seizure surfaces in Tenant B's synthetic | Per-tenant default; RLS on `threat_library_provenance`; storage path includes `tenant_id`; cross-tenant share requires explicit grant entity (Q-L7) |
| **Adapter-specific raw format drift** (FS6000 firmware update changes calibration-mode raw layout) | New captures parse with garbled HE/LE; ingest passes silently if parser doesn't fail-fast | `IScannerAdapter.ParseAsync` carries `expected_format_version` that mismatches loud; ingest stage 1 fails closed; `ScannerFirmwareDriftSuspected` event |
| **Z calibration drift between capture day and synthesis day** | §6.6.7 anomaly AUROC shifts mysteriously after a calibration refresh | `material_zeff_path` re-computed lazily on read using *current* `ScannerCalibrationProfile`; raw HE/LE is the source of truth, Z is a derived view |
| **Operator un-certified at capture time** (cert expired, system glitch) | Quality flags missing | Capture app verifies `User.capture_certified_at + 12 months > now()` at task open; "I'm ready" button disabled with explicit message if not |
| **Deliberate threat-library poisoning** (insider with capture privileges submits a doctored capture) | Synthetic eval drifts to mask a specific real-threat class | Dual-approval on rows tagged `Firearm` or `Contraband_Other`; sha256 of raw blob committed to a tamper-evident chain (`prev_event_hash` per ARCHITECTURE.md); periodic offline diff of threat-library composition vs historical baseline |

#### 6.9.10 Open questions

| ID | Question | Status |
|---|---|---|
| Q-L1 | Subclass vocabulary control — open free-text vs locked-enum vs hybrid. **Recommend** hybrid: per-class enum + free-text overflow flagged for quarterly promotion to enum. | Recommendation logged |
| Q-L2 | Calibration-belt fixture standardization across ports. Standardize alignment grid + belt-deck height as a kit. | Open |
| Q-L3 | SAM 2 hot-swap policy. Re-segmenting historic captures on SAM 2 upgrade could shift §6.6 evaluation; need a freeze-then-rebaseline protocol. | Open |
| Q-L4 | Mobile capture flow for outdoor seizures (truck park, vessel side). Calibration belt is room-bound. | Open — defer to second port |
| Q-L5 | SLA escalation cadence. 4-hour analyst → supervisor; supervisor → location admin? Configurable per `Location`. | Open |
| Q-L6 | Multi-threat-per-seizure splitting. Cash hidden inside a firearm grip — capture must split or composite. | Open — needs operator-input |
| Q-L7 | Cross-tenant threat-library sharing. Default off per §6.6.8; explicit-grant entity unspecified. **Bundle with Q-G3.** | Open — bundle with Q-G3 |
| Q-L8 | Commercial-library augmentation track (Q-I1 option b). Procurement, redaction, schema mapping. Defer until Phase-2 floors approach saturation. | Deferred |
| Q-L9 | CT-phantom synthesis (Q-I1 option c). Activate only if a class fails its Phase-1 floor at the 24-month mark. | Deferred — trigger-driven |
| Q-L10 | Operator certification revocation flow. If captures show systematic bias, what's the procedure to invalidate historic captures vs revoke forward? | Open |
| Q-L11 | Z_eff calibration drift policy. Lazy re-derive on every read (locked default) vs snapshot-at-capture (auditable but stale). Snapshot fallback flagged for high-stakes evidentiary export. | Locked posture, fallback Open |
| Q-L12 | Decoy/`BenignBaseline` curation policy. Who proposes, who approves, ratio per real-threat instance. §6.2 false-positive rate is the eventual metric. | Open |

### 6.10 HS commodity density reference table seed and curation

*Tier: image-analysis-adjacent.*

> **Status:** Locked sourcing strategy 2026-04-28 (closes Q-F5). Curation pipeline locked; per-tier sample thresholds tunable in Phase 7.4. Resolves how `nickerp_inspection.hs_commodity_reference` (introduced in §6.3.2) gets populated; does **not** redefine the §6.3 scorer math or `IAuthorityRulesProvider.ResolveCommodityPromptAsync` contract — those stay where they are.
> **Sibling specs:** §6.3 (the consumer), §6.5 (per-scanner Z calibration that bounds "what does this row mean").

#### 6.10.1 Problem

§6.3.3's stage-3 scorer asks one direct question per declared line: "is the dual-energy `z_eff_observed` inside the `(z_eff_min, z_eff_max)` window for this HS-6 code, and is the back-of-envelope density compatible with the declared weight × packaging?" That question is only answerable when `nickerp_inspection.hs_commodity_reference` actually has a row for the HS-6 in front of us, and that row's window is **calibrated against this fleet's scanners**. If the table is empty, missing the long tail, or seeded from physically wrong values, every density sub-score is either skipped (suppressing real signal) or scored against the wrong reference (poisoning the case score).

Public sources alone are insufficient for three reasons:

1. **No public source publishes Z_eff windows for cargo X-ray.** WCO classifies; USDA/FAO publish bulk density; engineering tables publish material density. None publish dual-energy effective atomic number ranges as observed through a 20-foot container at customs-scanner beam energies. Z_eff is what physically distinguishes "1 t cotton garments" from "1 t cigarettes" (§6.3.1) — and it is the channel only **we** measure.
2. **Public density tables drift from container reality.** A USDA value for "rice, milled" assumes a calibrated lab cylinder. The same rice declared on a real BOE arrives in plastic-lined jute sacks on a slip sheet inside a steel intermodal box; the apparent through-container density seen by a scanner differs by 10–25 % from the lab value, and the variance per HS-6 is what the scorer's window must capture.
3. **Per-customer regional cargo mix.** Ghana ICUMS' HS-6 frequency distribution is **not** transferable to the next tenant's port. The "top-200 ≈ 85 % of case volume" pareto holds, but **which** 200 changes per tenant.

The strategy locked at Q-F5 — in-house seed for the top-200 HS-6 codes (~85 % of case volume) plus public sources filling the long tail — depends on a curation pipeline that produces auditable, scanner-calibration-versioned rows, downgrades confidence honestly when in-house samples are thin, and never silently overwrites an analyst-validated row with a public-source value.

#### 6.10.2 Schema

Concrete shape of `nickerp_inspection.hs_commodity_reference`:

| Column | Type | Notes |
|---|---|---|
| `hs6` | `char(6)` | Composite PK with `tenant_id` |
| `tenant_id` | `uuid NOT NULL` | RLS key; `tenant_isolation_*` policy + `FORCE ROW LEVEL SECURITY` per platform pattern |
| `z_eff_min` | `numeric(4,2) NOT NULL` | Robust lower bound; consumed by §6.3 `density_vs_declared` |
| `z_eff_median` | `numeric(4,2) NOT NULL` | Robust central tendency |
| `z_eff_max` | `numeric(4,2) NOT NULL` | Robust upper bound |
| `z_eff_window_method` | `text NOT NULL` | One of `iqr_1.5x`, `p5_p95`, `public_source`, `inferred_sibling` |
| `expected_density_kg_per_m3` | `numeric(8,2) NULL` | Through-container apparent density, NOT lab density. Null permitted for items where density adds no signal |
| `density_window_kg_per_m3` | `numrange NULL` | Half-open range; `density_vs_declared` drops out of scoring when null |
| `typical_packaging` | `text[] NOT NULL DEFAULT '{}'` | Free-text tags from `Authorities.CustomsGh` packaging vocabulary |
| `confidence` | `enum('Authoritative','Curated','Inferred') NOT NULL` | See §6.10.6 |
| `sources` | `jsonb NOT NULL DEFAULT '[]'::jsonb` | Per-source provenance; see §6.10.5 |
| `sample_count` | `integer NOT NULL DEFAULT 0` | In-house sample count |
| `scanner_calibration_version_at_fit` | `jsonb NULL` | Map of `scanner_device_instance_id → ScannerThresholdProfile.version` at fit time; ties this row's window to the §6.5 calibration that produced it |
| `last_validated_at` | `timestamptz NOT NULL` | Last analyst review or automated re-fit |
| `validated_by_user_id` | `uuid NULL` | FK → `User`. Null only for fully-automated tier-3 long-tail rows |
| `next_review_due_at` | `timestamptz NOT NULL` | Drives §6.10.8 cadence |
| `notes` | `text NULL` | Free-form analyst rationale |

Indexes: PK on `(tenant_id, hs6)`, partial `(tenant_id, confidence) WHERE confidence = 'Inferred'` for tier-3 review queues, GIN on `sources`. Per-tenant by design — see §6.10.10 cross-tenant guard.

#### 6.10.3 Top-200 identification

The top-200 list is the per-tenant ranking of HS-6 codes by case volume over a rolling 12-month window. Sourced from v1's existing `AuthorityDocument` corpus (read-only export per the Q-A2 pattern), and from v2's `AuthorityDocument` rows once Phase 7.2 traffic is live.

**Ranking query (sketch):**

```sql
SELECT
    SUBSTRING(payload -> 'line_items' -> idx ->> 'hs_code' FOR 6) AS hs6,
    COUNT(DISTINCT ad.case_id) AS case_count,
    COUNT(*) AS line_count,
    SUM((payload -> 'line_items' -> idx ->> 'declared_weight_kg')::numeric) AS total_declared_kg
FROM authority_document ad
CROSS JOIN LATERAL generate_series(0,
    jsonb_array_length(payload -> 'line_items') - 1) AS idx
WHERE ad.tenant_id = :tenant
  AND ad.received_at >= now() - interval '12 months'
  AND ad.document_type IN ('Boe', 'ImportDeclaration')
  AND payload -> 'line_items' -> idx ->> 'hs_code' IS NOT NULL
GROUP BY 1
ORDER BY case_count DESC
LIMIT 200;
```

Lives at `tools/v1-label-export/top200_hs6.sql` — sibling tool to Q-A2's container-split export. **Strictly read-only** against v1.

A separate `cumulative_case_count_pct` query reports at what rank the 80/85/90 % thresholds actually fall — the "top-200 ≈ 85 %" estimate is a planning heuristic, not a contract; the cutoff is whichever rank actually crosses 85 %.

Re-runs quarterly (matches §6.10.8 cadence) and on tenant onboarding.

#### 6.10.4 In-house seeding

Per HS-6 in the top-200 ranking, an analyst-led pipeline produces one row.

1. **Sample selection.** Pull `Verdict.decision = 'Clear'` cases ≥ 90 days post-decision with post-hoc finalization (`AuthorityDocument.payload ->> 'post_hoc_outcome' = 'cleared_finalized'`) AND any line item resolves to this HS-6. Same 90-day filter as §6.6.2.
2. **Cargo-region extraction.** From each `ScanArtifact` of `artifact_kind = 'Material'`, mask out container walls and floor (re-uses `ContainerObjectDetectionService` segmentation, ported from v1 in Phase 7.2).
3. **Per-pixel Z_eff distribution.** Aggregate cargo-region Z_eff across all sampled scans for this HS-6, weighted by cargo pixel area. Trim top/bottom 1 % to suppress saturation pixels and beam-hardening streaks (§6.8 territory; until §6.8 lands, the trim is the cheapest defence).
4. **Robust window fit.** `z_eff_median = median(samples)`. `z_eff_min = Q1 − 1.5·IQR`, `z_eff_max = Q3 + 1.5·IQR`, clipped to the physical Z range `[5, 40]`. Stamp `z_eff_window_method = 'iqr_1.5x'`.
5. **Density fit.** From declared `weight_kg / declared volume estimate per packaging tag`, fit robust median + p5/p95. Density is **null** when packaging is heterogeneous (e.g. consolidated personal effects) — scorer drops `density_vs_declared` for that row and re-normalises, which is correct.
6. **Calibration stamp.** Record `scanner_calibration_version_at_fit` as `ScannerThresholdProfile.version` for every `scanner_device_instance_id` represented. Required so when §6.5 ships a new threshold profile, this row knows it owes a re-fit (§6.10.9).
7. **Analyst sign-off.** For top-200 codes (`confidence` candidate `Authoritative` or `Curated`): analyst loads proposed row in v2 portal, reviews 16-scan thumbnail strip with overlaid Z heatmaps, edits packaging tags / notes, clicks Approve. `validated_by_user_id`, `last_validated_at`, `next_review_due_at = now() + interval '90 days'` set on commit.
8. **Long-tail automation.** For HS-6 outside top-200 with `sample_count ≥ 30`, same fit pipeline runs **without** mandatory analyst review and writes `confidence = 'Curated'`, `validated_by_user_id = NULL`. Weekly review queue surfaces these for spot-check.

#### 6.10.5 Public-source ingestion

Public sources fill rows the in-house pipeline cannot. They never overwrite a row with `confidence ∈ {Authoritative, Curated}` and `last_validated_at` more recent than the public-source ingestion timestamp.

| Source | URL | Coverage | Licence | Notes |
|---|---|---|---|---|
| WCO HS classification | [wcoomd.org](https://www.wcoomd.org/en/topics/nomenclature/instrument-and-tools/hs-nomenclature-2022-edition.aspx) | All HS-6 (taxonomy only) | Free, attribution | Names/synonyms feed `ResolveCommodityPromptAsync`; no density, no Z_eff |
| USDA Agricultural Handbook 66 | [ars.usda.gov](https://www.ars.usda.gov/) | Agricultural commodities (chapters 7, 8, 9, 10, 12 mostly) | Public-domain US gov | Bulk density only; no Z_eff |
| FAO commodity reference tables | [fao.org](https://www.fao.org/faostat/en/) | Agricultural + fisheries | Free, terms-of-use review | Density and moisture; no Z_eff |
| Engineering Toolbox | [engineeringtoolbox.com](https://www.engineeringtoolbox.com/density-materials-d_1652.html) | Engineering bulk materials (chapters 25, 26, 28, 72–83) | Permissive but non-redistributable in bulk — **per-row citation only** | Lab density, not through-container; needs in-house empirical correction factor |
| Academic Z_eff measurements | [arXiv 2108.12505](https://arxiv.org/abs/2108.12505) and per-paper bibliography | Sparse; usually a single material class per paper | Per-paper (most CC-BY) | Only public Z_eff anchor; lab beam energies may not match scanner beam — sanity check, not primary source |
| Commercial cargo-density libraries | Vendor-specific | Broad cargo coverage | **Paid; redistribution check required** — defaults to "do not ingest" until a tenant procures and licences sharing | Q-M5 |

**Ingestion job.** Nightly cron under `tools/hs-reference-ingest/` reads each enabled source, normalises HS-6 to 2022 nomenclature, writes proposed rows (`confidence = 'Inferred'`) for HS-6 codes the in-house pipeline has not seeded. Each contributing source is one entry in `sources jsonb` with `source_id`, `fetched_at`, `row_hash`, `fields_contributed`, `raw_url`, `licence`, `agreement_with_in_house`. Disagreement > 25 % flags `agreement_with_in_house = disagree_25pct` and surfaces to the analyst queue.

#### 6.10.6 Confidence tier rules

| Tier | Rule | Behaviour at scoring |
|---|---|---|
| `Authoritative` | `sample_count ≥ 100` AND `validated_by_user_id IS NOT NULL` AND **at least one public source agrees within 15 %** on density | §6.3 weights `w_density` at default `0.4` |
| `Curated` | (`sample_count ≥ 30` AND `validated_by_user_id IS NOT NULL`) OR (single high-quality public source covering all numeric fields) | `w_density` at `0.4` but caps `Finding.severity` at `Medium` |
| `Inferred` | `sample_count < 30` AND only weak/no public sources, OR HS-6-by-similarity inference from sibling codes in the same HS-4 | Caps `Finding.severity` at `Low`; window widened ×1.5 |

Tier transitions are one-way upgrades only — a row drops only when an analyst explicitly marks it during review (e.g. evidence of poisoning); otherwise stays at the latest tier until next scheduled review.

#### 6.10.7 Coverage targets

| Phase | HS-2 chapter coverage | Top-200 coverage |
|---|---|---|
| 7.0 | Schema only, no rows | n/a |
| 7.2 | ≥ 60 % of in-tenant declared HS-2 chapters carry at least one `Curated` row | ≥ 50/200 |
| 7.3 | ≥ 85 % HS-2; full top-200 seeded | 200/200, ≥ 80 % `Authoritative` or `Curated` |
| 7.4 | ≥ 95 % HS-2; long-tail `Inferred` rows for any HS-6 the scorer encounters | n/a — acceptance gate per §6.3.7 phase 2 |
| 7.5+ | 99 %+ HS-2; `Inferred` rate < 20 % of distinct HS-6 ever scored | n/a — Q-M3 |

**HS-6 codes never seen in production.** Rules provider returns a synthesized `Inferred` row built by HS-4 sibling inference: median of all rows in the same HS-4 with `confidence ≥ Curated`, widened Z_eff window ×1.5, `expected_density_kg_per_m3 = NULL` if siblings disagree by > 30 %. Synthesized row is **not persisted** — recomputed on demand and emits `MlTelemetry(reason='hs6_inferred_from_siblings')` so the curation queue can promote it when sample volume materialises.

#### 6.10.8 Update cadence

| Trigger | Cadence | Action |
|---|---|---|
| Tier-1 (`Authoritative`) review | Quarterly (90 d via `next_review_due_at`) | Analyst confirms or re-fits; updates `last_validated_at` |
| Tier-2 (`Curated`) review | Semi-annual (180 d) | Same, lower priority queue |
| Tier-3 (`Inferred`) review | Annual or event-driven | Promoted whenever `sample_count` crosses tier threshold |
| New HS-6 emergence | On-demand when an HS-6 appears `N ≥ 50` times in a rolling 90-day window AND has no row | Auto-enqueue an in-house seeding job; analyst reviews on completion. `N` configurable per tenant in `tenant_settings.hs_reference_seed_threshold` |
| Per-scanner-calibration update | On every `ScannerThresholdProfile` activation that affects normalization or Z calibration (§6.5) | Re-fit candidates: any row whose `scanner_calibration_version_at_fit` references the affected scanner. Re-fit runs in background; row stays at current values until new fit lands |
| Public-source refresh | Nightly cron | Per §6.10.5 |

Daily job scans for `next_review_due_at < now()` and emits review-queue events; admin UI surfaces backlog count and per-tenant SLO.

#### 6.10.9 Validation harness

A live validation accumulator runs per `(tenant_id, scanner_device_instance_id, hs6)` triple and tracks whether observed Z_eff distributions stay inside the stored window. **Calibration drift detection**, not scorer evaluation.

**Per-case contribution.** Every time §6.3 stage-3 scorer runs against a row, one `hs_reference_observation` row writes: `(case_id, hs6, z_eff_observed_p50, z_eff_observed_p95, in_window, scanner_device_instance_id, calibration_version_at_observation)`.

**Drift signal.** Rolling 30-day per-`(scanner, hs6)`: `out_of_window_rate = mean(NOT in_window)`. Triggers:
- `out_of_window_rate > 0.20` for ≥ 7 consecutive days AND `sample_count ≥ 50` over the window → `RecalibrationProposed` event; row enters analyst queue with proposed new window.
- `calibration_version_at_observation != scanner_calibration_version_at_fit` for ≥ 50 % of observations → `StaleCalibration` event; re-fit **automatic** for `Inferred`, **proposed** for `Curated`/`Authoritative`.
- `sample_count_in_window` drops to zero with `sample_count > 50` upstream → `WindowTooNarrow`; row degraded to `Inferred` and analyst-reviewed.

The harness never silently widens windows on `Authoritative` or `Curated` rows — it proposes, the analyst disposes.

#### 6.10.10 Failure modes & guards

| Failure | Symptom | Guard |
|---|---|---|
| Over-narrow Z_eff windows poisoning §6.3 score | Honest cargo systematically scores `LikelyMisdeclaration`; analyst override rate spikes for one HS-6 | Validation harness `WindowTooNarrow` auto-degrades to `Inferred`. Per-tenant override-rate dashboard flags HS-6 codes whose override rate > 60 % over 30 days |
| Cross-tenant data leakage | Tenant A's seizure-derived seed influences Tenant B's reference window | Table is per-tenant by PK `(tenant_id, hs6)` and `FORCE ROW LEVEL SECURITY`. Cross-tenant pooling **explicitly off by default**; opt-in via signed tenant-admin consent + audit event (Q-M2) |
| Seasonal commodity variance | Mango-export season shifts apparent density for HS 080450 by 8–12 % vs off-season | Quarterly cadence catches it. Optional per-HS-6 `seasonal_window jsonb` deferred (Q-M4) |
| Scanner calibration drift between fit time and use time | Old window now too tight or too wide | `scanner_calibration_version_at_fit` + per-calibration-update revalidation. `StaleCalibration` event is the live tripwire |
| Mis-attribution from operator-error HS labels in source data | Importer declared wrong HS-6; that case fed the seed under wrong code | Sample selection requires `Verdict = Clear` AND post-hoc finalization (necessary but not sufficient — a mislabelled clear is still a clear). Hard guard: reject samples where `z_eff_observed` falls > 3·IQR outside rolling robust median **before** the fit recurses. Logs rejected sample count to `notes` |
| Public source contradicts in-house seed silently | USDA disagrees by 30 % and cron overwrites | Ingestion **never** overwrites rows with `confidence ∈ {Authoritative, Curated}`. On disagreement > 25 %, raises `agreement_with_in_house = disagree_25pct` |
| Empty `hs_commodity_reference` on a new tenant | First-day deployment has no rows; scorer skips every density check | Bootstrap from a global `hs_commodity_reference_public_seed` table (built from public-source ingestion only, all `confidence = 'Inferred'`) when row count is zero. Bootstrap rows tagged `sources[].source_id = 'global_public_bootstrap'`, replaced as in-house seeding fills in |

#### 6.10.11 Open questions

| ID | Question | Status |
|---|---|---|
| Q-M1 | Per-scanner-type partitioning of Z_eff windows. Different scanner generations may produce systematically different Z_eff for same physical commodity. **Recommend** capturing scanner-type in `scanner_calibration_version_at_fit` so the data exists when needed. | Open — recommendation logged |
| Q-M2 | Cross-tenant pooling consent flow. Locations within one Tenant pool by default; cross-Tenant pooling stays off absent a separate signed agreement. UI flow for consent + audit chain unspecified. | Open |
| Q-M3 | `Inferred` rate target for steady state. < 20 % is the stretch target; realistic floor depends on tail-thickness. **Action:** measure post-Phase-7.4 before locking. | Deferred to Phase 7.5 |
| Q-M4 | Seasonal stratification (`seasonal_window jsonb`). Add only after the validation harness confirms ≥ 5 pp out-of-window-rate seasonal pattern on at least one tier-1 HS-6. Avoid premature schema growth. | Deferred — needs measurement |
| Q-M5 | Commercial cargo-density library procurement. Default posture is "do not ingest." Revisit if a tenant procures one. | Open — procurement |
| Q-M6 | HS-6 nomenclature version skew. WCO updates HS every 5 years (HS 2022 → HS 2027 due 2027-01-01). **Recommend** manual port for `Authoritative`/`Curated`, automatic for `Inferred`. | Open — recommendation logged |
| Q-M7 | Long-tail automation tier. Are there HS-2 chapters where unreviewed automation is too risky regardless of sample size (chapter 93 — arms; chapter 71 — precious stones)? **Recommend** explicit `chapter_requires_analyst_review` allowlist; default-on for chapters 71, 72, 93. | Open — recommendation logged |
| Q-M8 | Per-fit sample-rejection threshold (`> 3·IQR`). Conservative; may reject legitimate but high-variance commodities (mixed-vehicle-parts). **Recommend** logging rejection rate per HS-6 and tightening / loosening per chapter once we have signal. | Open |
| Q-M9 | `next_review_due_at` SLO enforcement. **Recommend** alert + auto-degrade after 2× cadence; never block (a stale row is still better than no row). | Open — recommendation logged |
| Q-M10 | Density-window units for non-volumetric packaging. Density may be inherently meaningless. Verify no §6.3 telemetry path divides by null. | Open — implementation check |

### 6.11 Inbound post-hoc outcome adapter

*Tier: supporting-platform.*

> **Closes the "adapter spec Open" half of §6.4.9 Q-G1.** Posture (inbound `IExternalSystemAdapter` of `ExternalSystemType.Customs` materialising `AuthorityDocument` rows of `DocumentType.PostHocOutcome`, with manual-entry fallback) was **locked 2026-04-28** in §6.4.9. This section specifies the contract surface, operation modes, mapping rules, idempotency, reconciliation, and rollout. Concrete plugin: `modules/inspection/plugins/NickERP.Inspection.ExternalSystems.IcumsGh/` for ICUMS Ghana — first tenant, authority-neutral at the contract level.

#### 6.11.1 Problem

§6.4 hangs the **5×-weighted "gold disagreement" term** of its priority score on `1[post_hoc_outcome.label != verdict.decision]`. That signal is the only true label in the active-learning loop — peer disagreement and confidence are proxies; customs seizure / clearance / re-export verdicts are the supervised ground truth. Without a reliable inbound channel for them, the priority queue degenerates into uncertainty-sampling-only and the §6.4.8 "customs-feedback poisoning" guard has no clean path to defend.

v1 captures none of this. Customs feedback flows back to operators by phone, by email, and via paper bordereaux; it never lands as structured data in NSCIM. v2 must close the loop.

**Locked acceptance: 2026-04-28**
- Outcomes from any onboarded authority materialise as `AuthorityDocument` rows within ≤ 24 h of authority decision (push or pull).
- `AnalystReview.post_hoc_outcome jsonb` of the originating case is updated within ≤ 1 h of the `AuthorityDocument` landing.
- Authority-side outcome corrections (Seize→Clear reversals) are applied without rewriting prior `AuthorityDocument` rows — supersession is appended.
- Zero outbound network calls leave the cluster on the inbound path; pull-mode adapters are the only egress and are scoped to authority APIs.
- Manual-entry path exists for any authority without an outcome API; same `AuthorityDocument` shape, distinguishable provenance.

#### 6.11.2 Adapter contract fit

The inbound adapter implements the existing `IExternalSystemAdapter` contract in `ARCHITECTURE.md §6.2`:

```csharp
public interface IExternalSystemAdapter
{
    string TypeCode { get; }                      // "ICUMS-GH", "GRA-GH"
    string DisplayName { get; }
    ExternalSystemCapabilities Capabilities { get; }
    JsonSchema ConfigSchema { get; }

    Task<ConnectionTestResult> TestAsync(
        ExternalSystemConfig cfg, CancellationToken ct);

    // Pull side — get authority docs for a case
    Task<IReadOnlyList<AuthorityDocument>> FetchDocumentsAsync(
        ExternalSystemConfig cfg, CaseLookupCriteria lookup, CancellationToken ct);

    // Push side — submit a verdict back
    Task<SubmissionResult> SubmitAsync(
        ExternalSystemConfig cfg, OutboundSubmissionRequest req, CancellationToken ct);
}
```

`FetchDocumentsAsync` already covers per-case pull. Post-hoc outcomes need a **second pull shape** — bulk fetch over a time window, not per case — plus a webhook receive surface. Two contract additions, both opt-in via capability flags so existing outbound-only adapters compile unchanged:

```csharp
public sealed record ExternalSystemCapabilities(
    bool SupportsDocumentPull,
    bool SupportsVerdictSubmit,
    bool SupportsOutcomePull,        // new — bulk window-fetch
    bool SupportsOutcomePush,        // new — webhook receiver
    IReadOnlyList<string> SupportedDocumentTypes);

public interface IInboundOutcomeAdapter : IExternalSystemAdapter
{
    Task<IReadOnlyList<AuthorityDocument>> FetchOutcomesAsync(
        ExternalSystemConfig cfg, OutcomeWindow window, CancellationToken ct);

    Task<IReadOnlyList<AuthorityDocument>> ReceiveOutcomeWebhookAsync(
        ExternalSystemConfig cfg, InboundWebhookEnvelope envelope, CancellationToken ct);
}
```

`IInboundOutcomeAdapter` is a **derived** contract; an adapter declares it by implementing the interface and setting capability flags. Adapters that only outbound-submit leave both flags `false`.

The orchestrator that drives this surface is `NickERP.Inspection.Application.PostHocOutcomes.OutcomeIngestionOrchestrator` — a hosted background service, not an adapter.

#### 6.11.3 Operation modes

Per-`ExternalSystemInstance` mode selection in `ExternalSystemConfig.payload jsonb`:

| Mode | When | Mechanics | Default cadence |
|---|---|---|---|
| **Pull (default)** | Authority offers query API, no webhook | Orchestrator invokes `FetchOutcomesAsync(window)` on a schedule | **Every 30 min** with offset minutes per instance to avoid thundering-herd. Clones v1's production cadence from `IcumPipelineOrchestratorService.BatchIntervalMinutes = 30` (see `NickScanCentralImagingPortal.API/appsettings.json:282`). |
| **Push** | Authority emits webhooks at decision time | Adapter exposes a route via `IInboundWebhookHandler`; receiver verifies signature and dispatches to `ReceiveOutcomeWebhookAsync` | Authority-driven |
| **Hybrid (push + reconciliation pull)** | Push offered but flaky; pull fills gaps | Push primary, pull at lower cadence (1× daily) for prior 7-day window | Push real-time + pull 1×/day |
| **Manual** | Authority offers neither | See §6.11.9 | Operator-driven |

Mode + cadence + jitter window + retry policy are properties on `ExternalSystemConfig.payload jsonb`. Mode change emits `DomainEvent.OutcomeIngestionModeChanged`. **Locked 2026-04-28**: hybrid is the recommended default for any authority supporting both surfaces.

**ICUMS Ghana — concrete mode lock (2026-04-29).** Q-N1 vendor call confirmed ICUMS supports both **batch-by-date-range** AND per-declaration lookups, but webhooks were not offered. The first ICUMS Ghana `ExternalSystemInstance` runs in **pull mode at v1's production cadence — every 30 minutes** (cloned from `IcumPipelineOrchestratorService.BatchIntervalMinutes = 30` in v1's `NickScanCentralImagingPortal.API/appsettings.json:282`, not invented). Per-declaration lookup is retained as the reconciliation-fallback path for unmatched cases. Hybrid mode stays available in the contract for future authorities that expose webhooks; not used by ICUMS today.

#### 6.11.4 Authentication shapes

| Shape | Where | Storage |
|---|---|---|
| **mTLS + client cert** | ICUMS Ghana convention; UNCTAD ASYCUDA-derived stacks | Cert thumbprint in `ExternalSystemConfig.payload.mtls.thumbprint`; cert in `LocalMachine\My`. Reuses the leaf-thumbprint pinning chain from `reference_nickscan_api_cert_thumbprint` — **leaf, not CA root** |
| **OAuth2 client-credentials** | Modern authority gateways (EU Customs Trader Portal) | `client_id`, `client_secret_ref` (vault reference), `token_endpoint`, `scope` in `ExternalSystemConfig.payload.oauth2` |
| **HMAC-signed webhooks** | Push receivers | `signing_key_ref` + `algorithm` (`HMAC-SHA256` default) in `ExternalSystemConfig.payload.webhook` |

Push-mode receivers additionally require **mTLS at the edge** (terminate at reverse proxy, forward `X-Client-Cert-Thumbprint`), **IP allow-list** narrowing to authority-published egress ranges (mismatch = 403, audit `WebhookRejectedIpDenied`), and **replay window ≤ 5 min** on the HMAC nonce (older = 401, audit `WebhookRejectedReplay`).

Per `feedback_confirm_before_weakening_security`: any future PR proposing to loosen these requires explicit user confirmation and at least one non-weakening alternative.

#### 6.11.5 Source-data shape (concrete: ICUMS Ghana)

Authority-neutral at contract level — `AuthorityDocument.payload jsonb` is opaque to core. Concrete payload shape lives in `NickERP.Inspection.Authorities.CustomsGh`.

```json
{
  "$schema": "authorities.customs-gh.posthoc-outcome.v1",
  "declaration_number": "C-2026-04-28-073491",
  "container_id": "MSCU1234567",
  "outcome": "Seized",                           // Cleared | Seized | ReleasedWithDuty | ReExported | Pending
  "seized_count": 3,
  "seized_items": [
    { "hs_code": "2402.20", "description": "Cigarettes — undeclared",  "quantity": 1200, "unit": "carton" },
    { "hs_code": "8542.31", "description": "Integrated circuits — misdeclared", "quantity": 47, "unit": "kg" }
  ],
  "decided_at": "2026-04-26T13:11:00+00:00",
  "decided_by_officer_id": "GRA-CO-0431",
  "decision_reference": "GRA-SEIZURE-2026-0241",
  "evidence_attached": [
    { "kind": "photo", "uri_ref": "icums://evidence/2026-0241/01.jpg", "sha256": "…" }
  ],
  "supersedes_decision_reference": null,
  "entry_method": "api"
}
```

Typed in `NickERP.Inspection.Authorities.CustomsGh.PostHocOutcomePayloadV1`. Adapter validates against the JSON Schema sidecar shipped inside the plugin.

#### 6.11.6 Mapping → v2 entities

1. **Case lookup.** Try in order: (1) `(authority_code, declaration_number)` → `InspectionCase` via `AuthorityDocument` joins of any earlier `DocumentType.Declaration` row; (2) Fallback: `(scanner_serial, captured_at_window ± 4 h, container_number)` matched against `Scan` and parent case. The 4-hour window covers belt-clock skew. (3) If neither matches: case-match-failure branch (§6.11.14).
2. **Materialise `AuthorityDocument`** with `tenant_id`, `location_id` from matched case (RLS enforces); `case_id`; `external_system_instance_id`; `document_type = PostHocOutcome`; `reference_number = decision_reference`; `received_at` = orchestrator clock; `decided_at` lives inside `payload.decided_at`; `payload jsonb` = validated typed payload; `idempotency_key` per §6.11.7.
3. **Emit domain event** `AuthorityDocumentReceived(case_id, document_type=PostHocOutcome, document_id, supersedes_id?)` to `audit.events`.
4. **Event handler `PostHocOutcomeUpdater`** reads the document, locates latest `AnalystReview` for the case, updates `AnalystReview.post_hoc_outcome jsonb` to a normalised `{ outcome, decided_at, decision_reference, document_id, supersedes_chain[] }`, emits `AnalystReviewPostHocOutcomeUpdated`.
5. **§6.4 active-learning extractor consumes** the updated review on its next nightly job. No special-case wiring.

The handler **never modifies historical `AuthorityDocument` rows** — corrections are appended (§6.11.7).

#### 6.11.7 Idempotency and late arrivals

```
IdempotencyKey = sha256(
    authority_code         // "ICUMS-GH"
  || ":" || declaration_number
  || ":" || outcome.decided_at_iso8601
  || ":" || decision_reference
)
```

Same outcome reported twice (push retry, hybrid-mode reconciliation pull picking up an already-pushed event) materialises with the same key; orchestrator uses `INSERT … ON CONFLICT (external_system_instance_id, idempotency_key) DO NOTHING` and emits no domain event on the no-op path.

**Outcome corrections.** A Seize reversed to Clear two months later arrives with `supersedes_decision_reference` populated. Orchestrator: (1) computes a **fresh** `idempotency_key` from new `decided_at`; (2) resolves `supersedes_decision_reference` to prior `AuthorityDocument.id`; sets `supersedes_document_id` on the new row; (3) emits `AuthorityDocumentSuperseded(superseded_id, superseder_id)`; (4) handler updates `post_hoc_outcome jsonb` to latest non-superseded value; prior document remains queryable for audit and §6.4 lineage.

A pathological "ABA" sequence extends the supersession chain monotonically; the chain is part of the normalised `post_hoc_outcome jsonb`.

#### 6.11.8 Reconciliation

Per-instance cursor:

```sql
inspection.outcome_pull_cursors (
    external_system_instance_id  uuid PRIMARY KEY,
    last_successful_pull_at      timestamptz NOT NULL,
    last_pull_window_until       timestamptz NOT NULL,
    consecutive_failures         int NOT NULL DEFAULT 0,
    tenant_id                    uuid NOT NULL  -- RLS
);
```

Pull window: `since = last_pull_window_until - 24 h` (overlap absorbs authority-side late commits); `until = now() - 5 min` (skew buffer; never pull rows the authority hasn't had a moment to settle). Cursor advances **only on full success** of the page-iteration loop. Idempotency makes replay safe.

**Gap detection.** Nightly `OutcomeGapDetector`: for every closed case ≥ 90 days old with no `PostHocOutcome` AuthorityDocument, emit `OutcomeGapDetected(case_id, age_days)`. These cases enter §6.4 as `outcome_unresolved` — the disagreement term collapses to `0`; they contribute to a gap-rate metric that gates the §6.4.8 "median lag > 60 days for two cycles → 5× de-rate to 3×" rule.

#### 6.11.9 Manual-entry fallback

Admin tool at `modules/inspection/apps/portal/Areas/Admin/PostHocOutcomes/`. Operator path: select `InspectionCase` by declaration/container number → form mirrors §6.11.5 payload shape → submit invokes `PostHocOutcomeManualEntryService.RecordAsync(caseId, payload, operator_user_id)` which materialises an `AuthorityDocument` identical in shape to the API path but with `external_system_instance_id` = the **manual-entry pseudo-instance** (provisioned at tenant onboarding, `payload.mode = "manual"`, no credentials), `payload.entry_method = "manual"`, `payload.entered_by_user_id` = operator.

Same domain-event flow; same handler. Downstream consumers can filter on `payload.entry_method = "manual"`. The §6.4.8 "customs-feedback poisoning" guard treats manual entries as **lower trust** than API entries by default (Q-N4).

#### 6.11.10 Security & access control

- **RLS on `AuthorityDocument`** — `PostHocOutcome` rows inherit case's `tenant_id` and `location_id`. `FORCE ROW LEVEL SECURITY` and `'0'` fail-closed default per `reference_rls_now_enforces`. Orchestrator runs under a service principal with explicit per-tenant location assignments, never `BYPASSRLS`.
- **Admin role `inspection.outcome_admin`** — gates manual-entry tool and pseudo-instance reads. Distinct from `inspection.case_review` (analyst) and `inspection.tenant_admin`.
- **Webhook endpoints** behind mTLS + IP allow-list + HMAC (§6.11.4).
- **Audit every read.** `PostHocOutcome` document reads emit `AuthorityDocumentRead(document_id, reader_user_id, accessed_at)` to `audit.events` — only read-emits-event table family in v2.
- **Officer PII** (`decided_by_officer_id`) — masked in §6.4 extractor by default; visible only to `inspection.outcome_admin` (Q-N5).
- **No anonymous access ever.** Per `reference_nscim_production_authoritative` and `reference_week1_security_deployed`, v2 API auth is strict; outcome endpoints are no exception.

#### 6.11.11 Latency expectations

Customs feedback typically lands 2–8 weeks after the scan (§6.4.8). The 90-day reconciliation window (§6.11.8) spans the typical 2–8-week distribution plus a 4-week tail before the gap-detector fires. §6.4.8 median-lag rule unchanged: median lag > 60 days for two consecutive cycles → 5× weight de-rates to 3×. This adapter feeds the lag histogram by emitting `posthoc.outcome_lag_days` (computed as `decided_at − InspectionCase.opened_at`) per ingest.

Outcome-late-arrival corrections > 6 months are handled by the supersession chain; §6.4's `post_hoc_outcome jsonb` updates and the next nightly cycle picks it up. **No retroactive rewrite of prior cycles' training sets** — would violate §6.4.8's label-leakage guard.

#### 6.11.12 Telemetry

OpenTelemetry spans (parent: `OutcomeIngestionOrchestrator.RunCycleAsync`): `posthoc.fetch`, `posthoc.materialize`, `posthoc.match_case`, `posthoc.handler` with attributes covering instance, mode, window, outcomes returned/inserted/deduped/superseded, match strategy and outcome.

Prometheus metrics:
- `posthoc_outcomes_received_total{authority, outcome, mode}` (counter)
- `posthoc_match_failures_total{reason}` — `reason` ∈ {`unknown_declaration`, `ambiguous_window`, `case_pre_v2_cutover`, `case_archived`}
- `posthoc_lag_days_histogram{authority}` — buckets 1, 3, 7, 14, 30, 60, 90, 180; feeds §6.4.8
- `posthoc_supersession_total{authority}`
- `posthoc_pull_cursor_lag_seconds{instance}` — alarm at > 6 h
- `posthoc_webhook_rejections_total{reason}` — `reason` ∈ {`signature_invalid`, `replay`, `ip_denied`, `unknown_authority`}
- `posthoc_manual_entries_total{tenant, operator}`

Logs carry `correlation_id` from the outcome's `idempotency_key` so a webhook reject + later pull-mode pickup are stitched.

#### 6.11.13 Deployment & rollout

| Phase | Behavior | Gate |
|---|---|---|
| 0. Dev eval (manual only) | Manual-entry tool live; no API or webhook. Operators backfill ≥ 30 cases as a sanity set | Handler latency < 1 h end-to-end on the 30-case set; RLS verified by cross-tenant probe |
| 1. Shadow | Pull or push wired up; outcomes write to a parallel `pending_outcomes` table, **not yet linked**. Domain events emit `PostHocOutcomePending` instead of `AuthorityDocumentReceived`. Operators spot-check correctness against authority source-of-record | ≥ 14 days; mismatch rate < 1 % on a 100-row sample |
| 2. Primary + 5 % audit | Link enabled. `PostHocOutcomeUpdater` writes to `AnalystReview.post_hoc_outcome`; §6.4 consumes. **5 % sample** also written to `pending_outcomes` for ongoing audit | 30 days clean; §6.4 priority-Spearman against gold disagreement remains ≥ 0.4 (per §6.4.7 G0 acceptance) |
| 3. Primary | Audit slice retired or trimmed to 1 % drift sentinel; gap-detector live; supersession path exercised | SLO: gap-detect false-positive rate < 5 %, supersession handling clean on synthetic correction-replay |

Per-authority phase tracking in `inspection.posthoc_rollout_phase` (one row per `(tenant_id, external_system_instance_id)`).

#### 6.11.14 Failure modes & guards

- **Case-match failure: declaration not in v2.** Cutover window — case existed in v1 only. Unmatched outcomes within v1→v2 cutover window land in `inspection.unmatched_outcomes` with raw payload preserved. A reconciliation tool `PostHocOutcomeReconciler.ReplayAsync` replays them once the v1 historical-case backfill (per `MIGRATION-FROM-V1.md` reference data step 6) lands.
- **Webhook replay attacks.** HMAC nonce + ≤ 5 min replay window; duplicates also fail at the idempotency layer.
- **Authority-side data corruption** (`outcome=Seized` with a `decision_reference` from a Cleared decision). §6.4.8 dual-source confirmation rule for high-impact outcomes (Clear→Seize after promotion) blocks single-source flips at promotion time; the document still lands but §6.4 priority weighting is held until a second source confirms.
- **Outcome supersession race.** Two corrections arrive within seconds. Materialisation uses an advisory lock keyed on `(external_system_instance_id, supersedes_decision_reference)`; second writer detects existing supersession and refuses (no-op + `OutcomeSupersedeRaceDetected` event).
- **Missing officer identity leaking PII.** §6.4 extractor masks officer-id by default; admin UI surfaces unmasked only with `inspection.outcome_admin` claim; every unmask emits `AuthorityDocumentRead`.
- **Pull cursor advance on incomplete window.** Cursor advance wrapped in same transaction as `AuthorityDocument` inserts; on rollback the cursor stays put. Idempotency makes replay safe even on partial advance.
- **Adapter authentication rotation outage.** Cert / OAuth secret rotates without operational notice; pulls 401 forever. `posthoc_pull_cursor_lag_seconds{instance}` alarms at > 6 h; runbook escalation at > 24 h.
- **Manual-entry double-submit.** Manual-entry service computes idempotency key the same way → same key → no-op.
- **Outcome arrives for a case in `Pending` review state.** Authority decided faster than the analyst. Handler still writes `post_hoc_outcome jsonb`; §6.4 priority computation tolerates null verdict (disagreement term collapses to `0`); review UI surfaces pre-arrived outcome with a "authority decision already on file" banner.

#### 6.11.15 Open questions

| ID | Question | Status |
|---|---|---|
| **Q-N1** | First concrete authority API: ICUMS Ghana endpoint shape. Confirm whether ICUMS exposes query-by-decided-at-window or only per-declaration lookup; latter forces hybrid mode for reasonable cadence. | Open — vendor contact required |
| Q-N2 | Webhook receive surface: separate route (`/api/posthoc/webhooks/{instanceId}`) vs reusing §4.5 inbound webhook router pattern. **Recommend** dedicated route — auth shape differs and we want isolated rate-limiting. | Recommendation logged |
| Q-N3 | `OutcomeWindow` semantics — `decided_at` window vs `received_at` window vs `last_modified_at` window. Authorities vary; contract should accept all three with a sub-capability flag. | Open |
| Q-N4 | Manual-entry trust weighting in §6.4. Default: same weight as API; some operators argue API entries deserve higher trust. **Action:** measure manual-vs-API outcome reversal rate over first 60 days of mixed traffic. | Open — empirical |
| Q-N5 | Officer-PII handling. **Recommend** mask in extractor; preserve in DB under RLS + read-audit per §6.11.10. | Recommendation logged |
| Q-N6 | Cross-tenant pooling of outcomes for low-frequency authorities. Same posture as §6.4.9 Q-G3 — off by default, contractual sign-off, de-identified embeddings only. | Closed-by-reference to Q-G3 |
| Q-N7 | Edge-node behaviour. When edge nodes come online, do they receive outcome webhooks? **Recommend** no — outcomes always land centrally; edges are scan-side only. | Recommendation logged |
| Q-N8 | Outcome corrections older than 1 year. Do we accept and supersede, or freeze the chain? **Recommend** accept and supersede; emit `OutcomeCorrectionStale` for human review. | Open |
| Q-N9 | Schema versioning. `authorities.customs-gh.posthoc-outcome.v1` is first; how does v2 of the typed payload coexist with in-flight v1 documents during rollover? Standard payload-version table approach. | Open — standard pattern, tooling pending |
| Q-N10 | Bulk manual backfill tool. v1 has historical outcomes captured ad-hoc in operator notes. **Recommend** offer a CSV importer for the manual-entry path during cutover; only under `inspection.outcome_admin` + dual-approval; every imported row carries `payload.entry_method = "manual_bulk"`. | Open |

---

## 7. Cross-cutting open questions

The full open-question list lives in each section's `*.9` table (Q-A through Q-K). This table curates the **load-bearing, cross-section-blocking, or user-decision** items only — see the per-section table for the rest.

### 7.0 Tier reminder

The cross-cutting open questions below are sorted by purpose, not by tier. When prioritising, weight image-analysis-direct items above adjacent and platform items unless an external dependency forces otherwise.

### 7.1 Headline blockers (need a user decision before sections can ship)

| ID | Question | Section | Status |
|---|---|---|---|
| Q-A2 | v1 splitter label export | §3.9 | **Closed 2026-04-28** — v1 splitter labels persist in Postgres `image_split_jobs` + `image_split_results`. Read-only export script landed at `tools/v1-label-export/export_splits.py`. |
| Q-G1 | Post-hoc outcome capture (closes ARCHITECTURE Q4) | §6.4 / §6.11 | **Closed 2026-04-28 — full spec** — inbound `IInboundOutcomeAdapter : IExternalSystemAdapter` with pull/push/hybrid/manual modes, supersession-chain idempotency, 4-phase rollout. Concrete plugin path: `plugins/NickERP.Inspection.ExternalSystems.IcumsGh/`. |
| Q-I1 | Threat library sourcing | §6.6 / §6.9 | **Closed 2026-04-28 — option (a) selected** — in-house seizure capture as primary track. Operational pipeline specced in §6.9 (capture protocol, ingest, schema, volume targets, tooling, retention, workflow integration). Phase-1 floors reachable in ~14–26 months per class; commercial track (Q-L8) and CT-phantom (Q-L9) deferred as augmentation. |
| Q-F5 | HS commodity density reference sourcing | §6.3 / §6.10 | **Closed 2026-04-28 — recommendation accepted** — in-house seed for top-200 HS-6 codes (~85 % case volume), public sources fill long tail, three-tier confidence enum. Curation pipeline specced in §6.10 (top-200 query, in-house seeding, public-source ingestion, validation harness). Licence audit before any commercial dataset ingest (Q-M5). |
| Q-C1 / Q-J1 / Q-N1 | FS6000 firmware (DICOS + side-view) + ICUMS Ghana API shape | §5.6 + §6.7.8 + §6.11.15 | **All three closed 2026-04-29 by vendor calls.** Q-C1 NO (no DICOS export — §5 stays deploy-deferred, no plan change). Q-J1 NO (no side-view — §6.7 stays deploy-deferred, no plan change). Q-N1 YES with both modes (batch-by-date + per-declaration; no webhooks). §6.11.3 updated to lock ICUMS pull-mode-only; hybrid mode unused for first authority. See [`runbooks/vendor-call-2026-04-results.md`](runbooks/vendor-call-2026-04-results.md). |

### 7.2 Contract changes blocking Phase 7.0 freeze

| ID | Question | Section | Status |
|---|---|---|---|
| Q-E10 | `ScannerCapabilities.RawChannelsAvailable` flag — needed by §6.2 fallback path; threads through §3 `ParsedArtifact` | §6.2.9 | Open — contract change |
| Q-J3 | `DualViewGeometry` record on `ScannerCapabilities` — fields: detector-spacing-mm, pixel-pitch-mm-per-px, nominal belt speed | §6.7.8 | Recommendation logged |
| Q-K8 (implied) | New `artifact_kind = CorrectedPrimary` enum value on `ScanArtifact` | §6.8 | Implied — schema migration |
| Q-I8 | `synthetic=true` schema migration on `Scan` | §6.6.9 | Open — sequencing |

### 7.3 Hardware / latency assumptions

| ID | Question | Section | Status |
|---|---|---|---|
| Q-E1 | Lane-PC iGPU latency budget — DINOv2-B/14 at 400 ms p95 on Iris Xe is the load-bearing assumption. **Validate on real hardware in Phase 7.0.** | §6.2.9 | Open — needs hardware validation |
| Q-D5 | OpenVINO runner ship cadence (gates 200 ms iGPU bar for §6.1) | §6.1.9 | Open |
| Q-B1 | Concurrency model on shared DirectML adapter | §4.7 | Open |

### 7.4 Tenancy / multi-location

| ID | Question | Section | Status |
|---|---|---|---|
| Q-B3 | Tenant-keyed model variant convention + admin UI | §4.7 | Open |
| Q-D8 | Per-tenant LoRA adapters for OCR — different ports see different owner-prefix distributions | §6.1.9 | Deferred to Phase 7.3 |
| Q-G3 | Cross-tenant pooling for low-frequency families (anomaly) | §6.4.9 | Open — needs commercial decision |
| Q-F6 | HS prompt resolution language — multilingual for 2nd tenant | §6.3.9 | Deferred to Phase 7.4 |

### 7.5 Foundational research / measurement gaps

| ID | Question | Section | Status |
|---|---|---|---|
| Q-A3 | Teacher version freeze + label model-version stamping | §3.9 | Locked posture, action TBD |
| Q-A4 / Q-D7 / Q-G2 | Retrain cadence + active-learning weight tuning | §3.9 / §6.1.9 / §6.4.9 | Deferred to Phase 7.4 |
| Q-E5 | INT8 / FP16 quantization of DINOv2-B/14 — vision transformers are quantization-sensitive | §6.2.9 | Open |
| Q-E8 | Pixel AUROC ground truth — needs UI affordance for seizing officer to draw seized-item ROI | §6.2.9 | Open — UI dependency |
| Q-K2 | Real-overburden mask labeling pipeline for §6.8 Restormer training | §6.8.9 | Open |
| Q-J2 | Verify v1 decoders aren't silently dropping side-view if scanners do emit it | §6.7.8 | Open — read-only inspection |

### 7.6 Verification / memory-correction flags

| ID | Question | Section | Status |
|---|---|---|---|
| Q-X1 | ICUMS Outbox `OrderByDescending(.Priority)` location | external | **Re-verified 2026-04-28 → demoted from "verify before fix" to "convention check."** Grep across v1 turned up **10 occurrences** of `OrderByDescending(...Priority)` spread across `ContainerScanQueueRepository.cs:156`, `ICUMSDownloadQueueRepository.cs:89,121`, `ICUMSSubmissionService.cs:73`, `ReadyGroupsCacheService.cs:85,186`, `ImageAnalysisController.cs:797,1532`, `ICUMSSubmissionQueueController.cs:35`, `ImageAnalysisDecisionController.cs:259,584`. This is a **system-wide convention** — higher numeric Priority = more urgent. The `audit_2026_04_28` memory's "CRIT order-flip" claim looks like a misread of the convention. **Not a bug. Not blocking v2.** Memory note should be amended on next memory consolidation pass. |

### 7.7 New question prefixes added in §6.9–§6.11

The full per-section tables remain in §6.9.10 (Q-L1–Q-L12), §6.10.11 (Q-M1–Q-M10), and §6.11.15 (Q-N1–Q-N10). Cross-cutting items pulled into the buckets above:

| ID | Headline | Section |
|---|---|---|
| Q-L7 | Cross-tenant threat-library sharing — bundles with Q-G3 | §6.9.10 |
| Q-L8 / Q-L9 | Commercial threat library + CT-phantom augmentation tracks (Q-I1 options b/c) | §6.9.10 |
| Q-M2 | Cross-tenant pooling consent flow for HS reference rows | §6.10.11 |
| Q-M5 | Commercial cargo-density library procurement | §6.10.11 |
| Q-N1 | First concrete authority API (ICUMS Ghana) endpoint shape — **closed 2026-04-29: YES, batch-by-date + per-declaration both supported, no webhooks** | §6.11.15 |
| Q-N4 | Manual-entry trust weighting in §6.4 — empirical signal needed | §6.11.15 |

### 7.8 Closed questions (for the record)

| ID | Question | Resolution |
|---|---|---|
| Q-A2 | v1 splitter label export | Closed 2026-04-28 — see §7.1. |
| Q-G1 | Post-hoc outcome capture (ARCHITECTURE Q4) | **Closed 2026-04-28 — full spec** in §6.11. |
| Q-I1 | Threat library sourcing | **Closed 2026-04-28** — in-house option (a) selected; operational pipeline in §6.9. |
| Q-F5 | HS commodity density reference sourcing | **Closed 2026-04-28** — recommendation accepted; curation pipeline in §6.10. |
| Q-C1 | FS6000 DICOS export availability | **Closed 2026-04-29 — NO.** §5 stays deploy-deferred. No plan change. |
| Q-J1 | FS6000 side-view output availability | **Closed 2026-04-29 — NO.** §6.7 stays deploy-deferred. No plan change. |
| Q-J2 | v1 decoders silently dropping side-view | **Closed 2026-04-29 — moot** (no side-view to drop). |
| Q-N1 | ICUMS Ghana outcome API mode | **Closed 2026-04-29 — YES (date-range + per-declaration; no webhooks).** §6.11.3 ICUMS instance locked to pull-mode-only. |
| Q-G6 | MLflow vs platform telemetry overlap | Closed 2026-04-28 — MLflow owns experiment lineage, OTel (§4.6) owns serving, link is `metadata.mlflow_run_id`. |
| Q-N6 | Cross-tenant pooling of outcomes | Closed-by-reference to Q-G3. |
| Q-B2 | Cold-load latency budget | Likely closed — warmup is in §4 spec. |
| Q-C2 | Store DICOS-as-received vs decode-and-discard | Locked: store-as-received. |

---

## 8. Iteration log

### 2026-04-28 — initial draft
- Authored §1 (gap analysis) from the v1 explorer + SOTA research swept this date.
- Authored §2 (priority table).
- Authored §3 (container-split student spec) — locked acceptance metrics, architecture, training data sources, rollout phases.
- Authored §4 (`IInferenceRunner` plugin contract) — interfaces, records, tensor abstraction, lifecycle, telemetry.
- Authored §5 (DICOS readiness assessment) — locked posture: design-ready, deploy-deferred.
- Stubbed §6.1–6.8 placeholders for Tier 2/3 items.
- Logged 13 open questions in §7 (cross-cutting) including a verification flag (Q-X1) for an apparent ICUMS Outbox priority-ordering bug seen during the v1 sweep.

### 2026-04-28 — full §6 specs landed + v1 label-export tooling
- Authored **§6.1 OCR replacement** (Florence-2-base primary; Donut tested fallback; Tesseract retained as safety-net). Q-D1–Q-D8.
- Authored **§6.2 HS-conditioned anomaly detection** (DINOv2 + PatchCore primary; FastFlow Phase-2 ablation; DiffusionAD rejected for v1). First-mover in published cargo X-ray work. Q-E1–Q-E10.
- Authored **§6.3 manifest ↔ X-ray consistency scorer** (Donut → HS lookup → Florence-2-large grounding → C# scorer). Q-F1–Q-F10. Headline open: HS commodity density reference sourcing (Q-F5).
- Authored **§6.4 active learning loop** (Label Studio + SAM 2 + MLflow on-prem). **Closes ARCHITECTURE Q4** via Q-G1 — post-hoc outcome capture posture locked.
- Authored **§6.5 per-scanner threshold calibration** (`ScannerThresholdProfile` entity + propose/approve/24h-shadow/activate flow + LISTEN/NOTIFY cache). Q-H1–Q-H8.
- Authored **§6.6 TIP synthetic data generator** (multiplicative composition in linear-attenuation space; Z recomputed post-composite). Q-I1–Q-I8. Headline open: threat library sourcing (Q-I1).
- Authored **§6.7 dual-view registration** (1D NCC on vertical-edge profiles, lives in scanner adapter; not core). Q-J1–Q-J8. Active when first dual-view scanner enters fleet.
- Authored **§6.8 beam-hardening / metal-streak correction** (closed-form Stage 1 + region-gated Restormer Stage 2; no GAN loss). Q-K1–Q-K8.
- **Resolved Q-A2** — v1 splitter labels live in Postgres `image_split_jobs` + `image_split_results` (~88 jobs / 328 strategy rows / 21 with operator ground truth as of 2026-04-28). Read-only export script written at `tools/v1-label-export/export_splits.py` (~551 lines, slightly over the 400-line target; verified read-only via `psycopg2.set_session(readonly=True)` + an `assert_out_not_in_v1()` guard that rejects `--out` paths under `C:\Shared\NSCIM_PRODUCTION\`).
- **Re-verified Q-X1** — `OrderByDescending(.Priority)` is a system-wide convention across 10 occurrences in v1 (queue repos, ICUMSSubmissionService, ReadyGroupsCacheService, ImageAnalysisController, ImageAnalysisDecisionController). Higher numeric `Priority` = more urgent. The `audit_2026_04_28` memory's "CRIT order-flip" claim looks like a misread; demoted from "verify before fix" to "memory note should be amended."
- Updated §2 priority table — all 12 items now point to concrete spec sections.
- Restructured §7 cross-cutting open questions into headline-blockers / contract-changes / hardware / tenancy / research / verification / closed buckets. The full per-section Q lists remain in each `*.9` table.
- Cross-reference added from `ARCHITECTURE.md` header.

### 2026-04-28 — round 2: §6.9–§6.11 specs landed + v2 inference module C# scaffold
- Authored **§6.9 in-house threat library capture pipeline** (closes Q-I1 in favour of option a). Operational pipeline: capture protocol, calibration-belt fixture, SAM 2 segmentation, Z_eff attribution, three-pass redaction (scan-corner stamp + license-plate + PII), `threat_library_provenance` schema, threat-class taxonomy, per-class volume targets with realistic ramp estimates (~14–26 months per class to Phase-1 floor at Tema seizure rate), capture app at `/inspection/threat-capture/{taskId}`, content-addressed storage with legal-hold posture, `OnVerdictSeized` domain-event handler with idempotency on re-seizure, capture-of-capture forbidden. Q-L1–Q-L12.
- Authored **§6.10 HS commodity density reference table** (closes Q-F5 with recommended in-house-top-200 + public-tail strategy). Concrete schema for `nickerp_inspection.hs_commodity_reference`, top-200 ranking SQL (sibling tool to Q-A2), in-house seeding pipeline with robust IQR fits + scanner-calibration stamping, public-source matrix (WCO / USDA / FAO / Engineering Toolbox / academic Z_eff), three-tier confidence rules, validation harness with drift-signal triggers, daily new-HS-6-emergence detection. Q-M1–Q-M10.
- Authored **§6.11 inbound post-hoc outcome adapter** (closes Q-G1 fully — full adapter spec, not just posture). New `IInboundOutcomeAdapter : IExternalSystemAdapter` with pull / push / hybrid / manual modes, four authentication shapes (mTLS / OAuth2 / HMAC + per `feedback_confirm_before_weakening_security`), supersession-chain idempotency, 90-day reconciliation window, `OutcomeGapDetector` nightly job, manual-entry pseudo-instance for authorities without an API, per-tenant per-instance 4-phase rollout. Q-N1–Q-N10.
- **Scaffolded v2 inference module in C#** per §4 contract. Three new projects: `NickERP.Inspection.Inference.Abstractions` (15 files, contract-only, no NuGet deps), `NickERP.Inspection.Inference.OnnxRuntime` (6 files, CPU + DirectML EPs, NuGet `Microsoft.ML.OnnxRuntime 1.20.1`), `NickERP.Inspection.Inference.Mock` (6 files, deterministic test double with optional fixture playback). Plugin attribute pattern `[Plugin("kebab-code")]` + `plugin.json` sidecars matching v2 conventions; contract-version-attribute stamp `1.0`; Inference contract registered in `NickERP.Tests.slnx`. **All three projects build clean (zero warnings).**
- **Closed four headline blockers from round 1's §7.1:** Q-A2 (label export — script written), Q-G1 (post-hoc outcome — adapter specced in §6.11), Q-I1 (threat library — in-house option locked + §6.9 spec), Q-F5 (HS density — recommendation accepted + §6.10 spec).
- Updated §2 priority table with items 13/14/15 (the three new specs) plus a callout to the inference module scaffold.
- Reorganized §7 — added §7.7 (new question prefixes Q-L/Q-M/Q-N) and §7.8 (closed-questions register).
- Out-of-scope items the scaffold agent flagged but did not fix (preserved here as backlog candidates): (a) `ROADMAP.md` doesn't yet mention the Inference plugin family; (b) existing `PluginsServiceCollectionExtensions.AddNickErpPlugins` is a no-op for plugin instances — pre-existing v2 TODO; (c) `OnnxRuntimeRunner` doesn't pipe `IntraOpThreads` / `InterOpThreads` from `InferenceRunnerConfig` into per-load `SessionOptions` — needs an additive contract bump or host-wiring pattern.

### 2026-04-29 — first execution slice: vendor script + migrations + contract additions + end-to-end wiring

The user paid down five concrete next-action items in one round. Four parallel agents + a Claude-side cleanup pass.

- **#1 Vendor call script** (`docs/runbooks/vendor-call-2026-04.md`, 161 lines) — one-page operational runbook for whoever takes the FS6000 vendor + ICUMS Ghana technical-contact calls. Three plain-language questions the caller reads aloud (Q-C1 DICOS export availability, Q-J1 side-view output, Q-N1 ICUMS endpoint shape). Each with four-branch responses (yes / yes-with-conditions / no / "let me get back to you"), what-to-write-down, and the immediate next action keyed to the relevant roadmap section. Two follow-up email templates included. Constraint compliance verified: information-gathering only, no commitments.
- **#2 Schema migrations** (single migration `Add_PhaseR3_TablesInferenceModernization` timestamped `20260429062458`) for the five new tables: `inspection.scanner_threshold_profiles`, `inspection.threat_library_provenance`, `inspection.hs_commodity_reference`, `inspection.outcome_pull_cursors`, `inspection.posthoc_rollout_phase`. Five new entity classes in `NickERP.Inspection.Core/Entities/`; `InspectionDbContext.cs` extended with the five `DbSet`s + `OnModelCreating` blocks including RLS policies + `FORCE ROW LEVEL SECURITY`. Both `NickERP.Inspection.Core` and `NickERP.Inspection.Database` build clean. Three small spec deviations adopted v2 platform conventions over the doc text: (a) `TenantId` is `bigint long`, not `uuid` (matches v2 `ITenantOwned` contract; doc text out of date); (b) `density_window_kg_per_m3` stored as canonical-range `text` not `numrange` to keep `Core` free of Npgsql; (c) status enums are `int` columns with `HasConversion<int>()`, not `text CHECK`, matching v2 convention. `CaseSubjectType.ThreatCapture` (referenced by §6.9 / §6.6.2) noted as not yet added to the enum — out of scope for the migration; tracked for follow-up.
- **#3 Phase 7.0 contract additions** — additive only, `ContractVersion` minor bump 1.1 → 1.2 on both `Scanners.Abstractions` and `ExternalSystems.Abstractions`. New `ScannerCapabilities` fields: `RawChannelsAvailable`, `SupportsDualView`, `DualViewGeometry?`, `SupportsDicosExport`, `IReadOnlyList<DicosFlavor>? DicosFlavors`, `SupportsCalibrationMode`. New `ParsedArtifact.FormatVersion` (the §6.9.9 fail-closed guard mechanism — surfaced on the artifact, not as a `ParseAsync` parameter). New `ExternalSystemCapabilities` fields: `SupportsOutcomePull`, `SupportsOutcomePush`. New types: `DualViewGeometry` record, `DicosFlavor` enum, `OutcomeWindow` record, `OutcomeWindowKind` enum, `InboundWebhookEnvelope` record. New interface `IInboundOutcomeAdapter : IExternalSystemAdapter` with `FetchOutcomesAsync` + `ReceiveOutcomeWebhookAsync`. **All four concrete adapter projects (FS6000, Mock-Scanner, IcumsGh, Mock-ExternalSystem) still build clean — zero source changes needed** (positional record + appended-with-defaults trick).
- **#4 ROADMAP.md update + IntraOp/InterOp plumbing fix** — `ROADMAP.md` §4.9 added (sub-track table for the eleven specs + scaffold status), §5 "Post-hoc outcome capture" marked Resolved, §6 "AI-driven analysis assistance" moved from out-of-scope to in-scope under B.1.5. `OnnxRuntimeRunner.cs` + `ServiceCollectionExtensions.cs` updated: third constructor `OnnxRuntimeRunner(ILoggerFactory, InferenceRunnerConfig?)` introduced, new helper `ApplyThreadBudget` applies `IntraOpNumThreads`/`InterOpNumThreads` per session, `AddInferenceOnnxRuntime(Action<InferenceRunnerConfig>?)` accepts an optional configurator. Backwards compatible — existing call sites keep working without changes.
- **#5 First wiring slice — end-to-end smoke test PASSED.**
  - Python: stub U-Net at `tools/inference-bringup/train_stub_split.py` (ResNet-18 encoder + 4-down/4-up decoder + 1×1 head + Y-axis mean-pool; 14.3M params; random init, no training — fastest path per spec).
  - Exported to ONNX opset 18 with dynamic batch dim at `storage/models/container-split/v1/model.onnx` + `model.metadata.json` carrying sha256 `4e9a8f26d291…`.
  - C# smoke-test app at `modules/inspection/tools/InferenceSmokeTest/`. Built, run, **exited 0**.
  - **Output tensor shape `(1, 1568)` — exactly matches §3.2.** Zero-input → uniform 0.497 sigmoid output as expected for random-init.
  - p95 inference timing on this hardware: preprocess 36 µs / inference 307 ms / postprocess 18 µs on CPU (unoptimized stub; warmup not enabled).
  - **Validates the §3 + §4 contract chain end-to-end on real hardware** — sha256 fail-fast worked, model load succeeded, `IInferenceRunner.RunAsync` returned correctly-shaped output.
  - Pre-existing v2 issues observed (out of scope, flagged for follow-up): (a) DirectML EP not wired through to consumer's bin via `CopyLocalLockFileAssemblies` — the runner reports `[AzureExecutionProvider, CPU]` only at runtime; (b) torch 2.11's exporter externalizes weights to a sidecar `model.onnx.data` (57 MB) — `model.metadata.json`'s sha256 only covers the graph file; recommend either disabling external-data format for sub-2GB models or extending the manifest to verify secondary file hashes.
- **Closed five questions:** Q-A2 (label export), Q-G1 (post-hoc outcome — full spec in §6.11), Q-I1 (threat library — in-house option a), Q-F5 (HS density — in-house top-200 + public tail), Q-N6 (cross-tenant pooling — closed-by-reference to Q-G3).
- **Doc surface:** §6.9, §6.10, §6.11 fully specced and assembled into the doc; §2 priority table updated (items 13–15 added); §7 cross-cutting reorganized into 7 buckets with all closures recorded; §8 (this entry).

### 2026-04-29 — vendor calls completed + first migration applied to `nickerp_inspection`

Two short rounds, no fresh dev work — closing out the open external dependencies.

- **Vendor calls completed.** Results captured in [`docs/runbooks/vendor-call-2026-04-results.md`](runbooks/vendor-call-2026-04-results.md). All three questions resolved:
  - **Q-C1 NO.** FS6000 firmware does not export DICOS / NEMA IIC 1. §5 stays at "design-ready, deploy-deferred" — no plan change. Capability flag `SupportsDicosExport` retained in contract for future hardware.
  - **Q-J1 NO.** FS6000 produces single top-down view only; no side-view channel. §6.7 stays at "design-ready, deploy-deferred" — no plan change. `SupportsDualView` + `DualViewGeometry` retained in contract. Q-J2 closed as moot (no side-view to drop).
  - **Q-N1 YES (best-case).** ICUMS Ghana API supports both batch-by-date-range fetch AND per-declaration lookups. Webhooks not offered. §6.11.3 updated to lock the first ICUMS Ghana `ExternalSystemInstance` to **pull-mode-only** (once-daily batch-by-date), with per-declaration retained as the reconciliation-fallback path for unmatched cases. Hybrid mode stays in the contract for future authorities; not used by ICUMS today.
  - **Outstanding follow-ups:** vendor email confirmations (firmware version + ICUMS engineer contact details + ICUMS API documentation). Tracked in the results file with a 5-day calendar reminder.
- **First migration applied to `nickerp_inspection`.** Migration `20260429062458_Add_PhaseR3_TablesInferenceModernization` applied as `postgres` superuser via `NICKSCAN_DB_PASSWORD` (which doubles as the postgres password in this setup, per `tools/migrations/phase-h3/relocate-migrations-history.sh` convention). All five new tables verified present in the `inspection` schema with correct column counts: `scanner_threshold_profiles` (17), `threat_library_provenance` (25), `hs_commodity_reference` (17), `outcome_pull_cursors` (5), `posthoc_rollout_phase` (9). `nscim_app` can `SELECT` from all five — default privileges from `Add_NscimAppRole_Grants` applied automatically as expected.
- **Privilege gap noticed for follow-up:** `nscim_app` does not currently have `REFERENCES` privilege on parent tables in `inspection` schema (e.g. `external_system_instances`), so it cannot apply migrations that add foreign keys. Today the workaround is "run migrations as `postgres`," consistent with the existing tooling pattern. A small future grant migration could let `nscim_app` apply additive migrations on its own. Not blocking; tracked.
- **Closed four more questions:** Q-C1, Q-J1, Q-J2 (moot), Q-N1.
- **Doc surface:** §6.11.3 ICUMS mode lock added; §7.1 + §7.7 + §7.8 closure entries; this entry.

### 2026-04-29 — round 5: master + four parallel teams ship A/B/C/D

User-driven scope correction round. Four parallel team-agents under a master coordinator. Three tiers' worth of work landed in one pass.

- **Team A — §6.5 per-scanner threshold calibration wiring (image-analysis-direct, foundational).** New `NickERP.Inspection.Application` project introduced (first Application project in v2; sibling to Database/Web). Types: `IScannerThresholdResolver`, `ScannerThresholdSnapshot` record (typed projection of the JSON values shape from §6.5.2 — `Version, CannyLow, CannyHigh, PercentileLow, PercentileHigh, SplitDisagreementGuardPx, PendingWithoutImagesHours, MaxImageDimPx`), `ScannerThresholdResolver` (in-process dictionary cache + Postgres `LISTEN/NOTIFY` on channel `threshold_profile_updated` + 1h belt-and-braces TTL + `IHostedService` for the listen-loop + health check), `ServiceCollectionExtensions.AddScannerThresholdCalibration`. Admin Razor page at `/admin/thresholds` (list + detail with side-by-side current-vs-proposed JSON + Approve/Reject actions emitting `DomainEvent.ScannerThresholdProposalApproved` / `ScannerThresholdProposalRejected`). Bootstrap migration `20260429140000_BootstrapScannerThresholdProfilesV0` generated stamping a `version=0 active` row per `ScannerDeviceInstance` carrying v1's hardcoded constants (canny 50/150, percentile 0.5/99.5, 50-px disagreement guard, 72 h pending, 16384 max dim) — **NOT applied** (operator action). `ScannerThresholdProfile` was missing from `InspectionDbContext` despite being in Core + the R3 migration; Team A added the `DbSet` + `OnModelCreating` block + `InspectionDbContextModelSnapshot` entries to close the drift. `Application` and `Database` build clean.
- **Team B — §6.1 container-OCR (Florence-2) scaffold (image-analysis-direct, operator-visible).** New plugin `NickERP.Inspection.Inference.OCR.ContainerNumber` exposing `IContainerNumberRecognizer.RecognizeAsync(plateRoiBytes, correlationId, tenantId, ct) → ContainerNumberRecognition(predicted, confidence, checkDigitPassed, decodePath ∈ {Primary, ManualQueueRequired}, modelId, modelVersion)`. Internals: ImageSharp 384×384 ImageNet-normalised CHW float32 preprocessor, log-domain constrained beam-search decoder over `[A-Z]{4}[0-9]{7}` ∪ `<unreadable>`, ISO 6346 mod-11 check-digit gate (correctly rejects `mod=10` reserved). Stub model lands at `storage/models/container-ocr/v1/model.onnx` (sha256 `fbba3a37…`); smoke test app at `modules/inspection/tools/OcrSmokeTest/` runs end-to-end through `IInferenceRunner` → returns `decodePath = ManualQueueRequired` correctly on random-init weights. Python tooling at `tools/inference-training/container-ocr/`: `harvest_plates.py` (read-only over v1 — refuses writes under `NSCIM_PRODUCTION/`), `synth_plates.py` (Pillow-based ISO-6346-valid generator), `train.py` (Florence-2 + LoRA via `peft`; pre-flight only without `--actually-train`), `export_onnx.py` (LoRA-merged → ONNX opset 18 dynamic batch). v1 has ≈3,811 plate ROIs ready to harvest (`fs6000images` joined to `containerannotations` for analyst overrides). Schema discovered to differ from §6.1.4 text — actual columns are `fs6000scans.containernumber` (not `fs6000images.containernumber`) and `containerannotations.text WHERE type='ocr_correction'` (not `containernumbercorrections.corrected_value`); `harvest_plates.py` uses the real columns; doc text in §6.1.4 left to next-iteration cleanup. Tesseract fallback in §6.1.7 collapsed to `ManualQueueRequired` since v2 has no Tesseract.
- **Team C — §3 split-student real training pipeline (image-analysis-direct, headline track).** Python pipeline at `tools/inference-training/container-split/`: `prepare_data.py` (fetches v1 image bytes via splitter HTTP `GET /api/split/{id}/original` on port 5320 — port 5400 was 404 — applies the §3.2 chain: top-25 % crop → 1568 long-edge → pad to 472×1568 → 0.5/99.5 percentile-stretch → `.npy` + `manifest.parquet`), `train.py` (ResNet-18/U-Net with ImageNet-pretrained encoder and 1-channel stem from 3-channel mean per §3.3, Gaussian-targeted MSE σ=8 px per §3.5, plain PyTorch, 80/10/10 stratified by `container_count` × `scanner_type`, training_set_hash captured), `evaluate.py` (full §3.5 metric set with stratified slices), `export_onnx.py` (ONNX opset 18 dynamic batch + complete `model.metadata.json`). Smoke run executed: 88 v1 scans / 70 train / 9 val / 9 test, **1 epoch** training (loss 0.241 → 0.196 train; val_mae 651 px). New v2 model at `storage/models/container-split/v2/model.onnx` (sha256 `da57e289…`, 14.3M params, external-weights `model.onnx.data` 57 MB). InferenceSmokeTest re-run with `--model-dir v2 --model-version v2` → **PASS**, output shape `(?, 1568)` per §3.2, p95=211 ms CPU (target ≤100 ms — gap is FP32 + agent host hardware; quantization is out-of-scope for this round). **Honest data-volume note: 88 examples / 21 ground-truth labels is statistical noise; this artifact validates the pipeline end-to-end but is not production-ready.** All four §3.5 acceptance gates fail at this corpus size, as expected. Do NOT promote past Phase 0 dev-eval. v1 stub at `storage/models/container-split/v1/` left untouched. Found that the agent runtime's external-weights ONNX file is not covered by `model.metadata.json.sha256` — flagged as a §3.7 spec gap for next iteration.
- **Team D — Section-tier classification (structural anchor).** See preceding §8 entry below — covered by that team's own log line.
- **Master cross-cutting fix (post-team).** Team A flagged `IInboundOutcomeAdapter.cs` (added round-3) referencing `AuthorityDocument` instead of `AuthorityDocumentDto` (the parent contract uses the Dto post-FU-7 rename). Two-line fix applied; `ExternalSystems.Abstractions` rebuilds clean (0 warnings, 0 errors). The `Inspection.Web` build still file-locks against the running dev process — environmental, not source-correctness; verified by Abstractions-isolated build.
- **Carry-forwards for the user (operator decisions, not dev work):**
  1. Apply the Team A bootstrap migration (`20260429140000_BootstrapScannerThresholdProfilesV0`) when ready — it stamps every `ScannerDeviceInstance` with v1's hardcoded constants as a `version=0 active` profile so the resolver has data on first read.
  2. Restart the Inspection.Web dev process to release the DLL locks before the next end-to-end build.
  3. Real fine-tune of Florence-2 (Team B) and a real multi-epoch train of the split-student (Team C) need GPU time — out of agent-runtime scope.

### 2026-04-29 — tier classification + structural anchor

Restructured the doc to keep future iterations anchored to image-analysis work. Added a new "Section tiers" block at the top (after Principles, before §1) classifying every numbered section into one of three tiers — **image-analysis-direct** (the actual ML/CV: §3 split student, §4 inference runner, §5 DICOS, §6.1 OCR, §6.2 anomaly, §6.3 manifest↔X-ray, §6.5 thresholds, §6.6 TIP, §6.7 dual-view, §6.8 metal-artifact); **image-analysis-adjacent** (training-data infra: §6.4 active learning, §6.9 threat library, §6.10 HS density); and **supporting-platform** (label plumbing: §6.11 inbound outcome adapter) — with a "How to use this doc" line directing readers to lead with the direct tier. Renamed the §2 priority table's "Tier" column to "Phase" and inserted a new "Tier (D/A/P)" column between Phase and Item, with a single-letter tier marker per row (no row reordering, no other column changes). Added a one-line italics tier callout immediately under each `### 6.x` heading, before any existing blockquote `>` callouts in that section. Added a §7.0 "Tier reminder" paragraph at the top of §7 noting that the cross-cutting open-questions buckets below are purpose-sorted, not tier-sorted, and that prioritisation should weight image-analysis-direct items above adjacent and platform items unless an external dependency forces otherwise. No spec content was modified — only classification metadata was added.

### Next iteration (planned)
- **Phase 7.1 hardware validation** for Q-E1 (lane-PC iGPU latency for DINOv2-B/14, ≤ 400 ms p95 on Iris Xe — load-bearing for §6.2 acceptance). Validate on actual lane PCs before §6.2 gets implementation effort.
- **Wire DI for `IInboundOutcomeAdapter`** — host-side concern; small slice adding `OutcomeIngestionOrchestrator` (per §6.11.2) and the `inspection.outcome_admin` role. With ICUMS mode now locked to pull-only, this is simpler than originally scoped.
- **Fix the DirectML wiring gap** flagged in 2026-04-29 round-3 smoke test — the OnnxRuntime plugin's NuGet refs need to flow through to consumers' bin directories. Trivial-but-architectural.
- **Apply the two follow-up migrations on disk** when reviewed: `20260429063951_Cleanup_StaleScanRenderArtifactHistoryRow` (pure data cleanup — deletes one orphan row from migration-history table) and `20260429064022_Drop_PublicEFMigrationsHistory` (drops the legacy `public.__EFMigrationsHistory` table now that production is stable on per-schema history). Both are benign housekeeping with intentional no-op `Down()` methods.
- **Begin Phase G0 of §6.4** (dry-run scoring) as soon as v2 has ≥ 14 days of `AnalystReview` rows accumulated.
- **Add `CaseSubjectType.ThreatCapture` enum value** (§6.9 follow-up).
- **ICUMS API documentation arrival** (post-vendor-call email follow-up). Once received, finalize the ICUMS payload schema in `Authorities.CustomsGh.PostHocOutcomePayloadV1`.
