#!/usr/bin/env python3
"""
v2 container-OCR — LoRA-merged Florence-2 → ONNX exporter
==========================================================

Purpose
-------
Take the fine-tuned LoRA checkpoint produced by ``train.py``, merge the
adapter into the base Florence-2 weights, and export to ONNX opset 18 with
a dynamic batch dim — the artifact format the v2
``NickERP.Inspection.Inference.OCR.ContainerNumber`` plugin loads. Also
writes ``model.metadata.json`` carrying the artifact sha256, training-set
hash placeholder, eval metrics, and ISO 6346 alphabet so the runtime can
sanity-check the contract before opening a session.

Sibling pattern of ``tools/inference-bringup/train_stub_split.py``: same
metadata schema, same exit-code numbering, same alphabet pinning.

Usage
-----
::

    python export_onnx.py \\
        --checkpoint "C:/Shared/ERP V2/storage/training/container-ocr/v1/lora-checkpoint" \\
        --base-model microsoft/Florence-2-base \\
        --out-dir "C:/Shared/ERP V2/storage/models/container-ocr/v1" \\
        --token-budget 16

    # The script emits:
    #   <out-dir>/model.onnx
    #   <out-dir>/model.metadata.json

Exit codes
----------
    0   success
    2   bad CLI args
    3   ONNX export failed
    4   onnx.checker rejected the exported graph
    5   torch / transformers / peft missing
    6   training_state.json absent or malformed (no metrics to embed)

Metadata schema
---------------
::

    {
      "model_id": "container-ocr-v1",
      "version": "v1.0.0",
      "sha256": "<hex>",
      "onnx_opset": "18",
      "exported_at": "2026-04-29T...Z",
      "exported_by": "export_onnx.py",
      "git_commit": "<sha or null>",
      "training_set_hash": "<sha256 of harvest manifest, or null>",
      "eval_metrics": {
        "exact_match": 0.95,
        "character_error_rate": 0.014,
        "check_digit_pass_rate": 0.985,
        "unreadable_precision": 0.87,
        "unreadable_recall": 0.62,
        "p50_latency_cpu_ms": 312,
        "p95_latency_cpu_ms": 480
      },
      "base_model": {
        "id": "microsoft/Florence-2-base",
        "license": "MIT",
        "attribution": "Florence-2 (Microsoft, MIT). https://aka.ms/florence-2"
      },
      "alphabet": "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789",
      "token_budget": 16,
      "input_shape": [3, 384, 384],
      "notes": "..."
    }

Implementation notes
--------------------
- Florence-2's ONNX export is non-trivial because the base model is a
  causal seq-to-seq with custom decoding. The exporter wraps the model
  in a thin ``OnnxRecognitionWrapper`` that exposes a single
  ``forward(pixel_values) -> logits[T, V]`` graph: the encoder produces
  visual features, T autoregressive decoder steps run unrolled, and the
  output logits are sliced down to the 36-symbol ISO 6346 alphabet
  before return. This keeps the C# decoder simple (no separate
  encoder/decoder graphs to wire).
- The exporter REFUSES to run if ``training_state.json`` is missing —
  the metadata block must carry real eval metrics or the artifact is
  not promotable past Phase 0 per §6.1.7.
- Training-set hash is the sha256 of the harvest manifest CSV from
  ``harvest_plates.py``. ``train.py`` writes this into
  ``training_state.json``; the exporter only forwards it.
"""
from __future__ import annotations

import argparse
import datetime as _dt
import hashlib
import json
import logging
import sys
from pathlib import Path
from typing import Any, Dict, Optional

EXIT_OK = 0
EXIT_BAD_ARGS = 2
EXIT_EXPORT_FAILED = 3
EXIT_CHECKER_FAILED = 4
EXIT_DEPS_MISSING = 5
EXIT_NO_TRAINING_STATE = 6

ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"

logger = logging.getLogger("v2-container-ocr-export")


def _try_imports():
    try:
        import torch  # type: ignore  # noqa: F401
        import transformers  # type: ignore  # noqa: F401
        import peft  # type: ignore  # noqa: F401
        import onnx  # type: ignore  # noqa: F401
        return True
    except ImportError as e:
        sys.stderr.write(
            f"FATAL: required ML stack missing ({e}). Install with:\n"
            "    pip install 'torch>=2.4' 'transformers>=4.43' 'peft>=0.12' 'onnx>=1.16'\n"
        )
        return False


def _sha256_of(path: Path) -> str:
    sha = hashlib.sha256()
    with path.open("rb") as fp:
        for chunk in iter(lambda: fp.read(1 << 20), b""):
            sha.update(chunk)
    return sha.hexdigest()


def _load_training_state(checkpoint_dir: Path) -> Optional[Dict[str, Any]]:
    p = checkpoint_dir / "training_state.json"
    if not p.is_file():
        return None
    try:
        with p.open("r", encoding="utf-8") as fh:
            return json.load(fh)
    except json.JSONDecodeError as e:
        sys.stderr.write(f"FATAL: malformed training_state.json: {e}\n")
        return None


def _build_onnx_wrapper(base_model: str, checkpoint_dir: Path, token_budget: int):
    """Compose the inference graph: visual encoder + unrolled decoder +
    final 36-column slice. Returns a torch.nn.Module ready for
    ``torch.onnx.export``.
    """
    import torch
    import torch.nn as nn
    from peft import PeftModel  # type: ignore
    from transformers import AutoModelForCausalLM, AutoProcessor  # type: ignore

    logger.info("loading base + LoRA: %s ← %s", base_model, checkpoint_dir)
    base = AutoModelForCausalLM.from_pretrained(base_model, trust_remote_code=True)
    merged = PeftModel.from_pretrained(base, str(checkpoint_dir)).merge_and_unload()
    processor = AutoProcessor.from_pretrained(base_model, trust_remote_code=True)

    # Map alphabet → token ids in Florence-2's tokenizer so the slice can
    # be expressed with a fixed indexer at export time.
    tok = processor.tokenizer
    alphabet_ids = []
    for ch in ALPHABET:
        ids = tok.encode(ch, add_special_tokens=False)
        if len(ids) != 1:
            raise RuntimeError(
                f"alphabet char {ch!r} did not encode to a single token "
                f"(got {ids}). Adjust the tokenizer or the alphabet pin."
            )
        alphabet_ids.append(ids[0])
    alphabet_idx_t = torch.tensor(alphabet_ids, dtype=torch.long)

    class OnnxRecognitionWrapper(nn.Module):
        def __init__(self, inner: nn.Module, alphabet_idx: torch.Tensor, T: int):
            super().__init__()
            self.inner = inner
            self.register_buffer("alphabet_idx", alphabet_idx, persistent=False)
            self.token_budget = T

        def forward(self, pixel_values: torch.Tensor) -> torch.Tensor:
            # Florence-2 prefix '<CONTAINER_OCR>' added to the tokenizer
            # during fine-tune. We push a fixed BOS/decoder_start_token_id.
            B = pixel_values.shape[0]
            decoder_input_ids = torch.full(
                (B, 1),
                fill_value=self.inner.config.decoder_start_token_id or 0,
                dtype=torch.long,
                device=pixel_values.device,
            )
            # T autoregressive steps with greedy expansion. Beam search
            # happens on the C# side at runtime, so we just need the
            # marginal logits per step.
            full_logits = []
            for _ in range(self.token_budget):
                out = self.inner(
                    pixel_values=pixel_values,
                    decoder_input_ids=decoder_input_ids,
                    return_dict=True,
                )
                step_logits = out.logits[:, -1, :]  # (B, V_full)
                full_logits.append(step_logits)
                next_token = step_logits.argmax(dim=-1, keepdim=True)
                decoder_input_ids = torch.cat([decoder_input_ids, next_token], dim=1)

            stacked = torch.stack(full_logits, dim=1)  # (B, T, V_full)
            # Slice down to the 36-symbol alphabet so the C# decoder can
            # run the constrained beam search without seeing the full
            # 51 200-row BART vocabulary.
            sliced = stacked.index_select(dim=-1, index=self.alphabet_idx)
            return sliced  # (B, T, 36)

    return OnnxRecognitionWrapper(merged, alphabet_idx_t, token_budget), alphabet_ids


def export(
    checkpoint_dir: Path,
    base_model: str,
    out_dir: Path,
    token_budget: int,
    image_size: int,
) -> int:
    if not _try_imports():
        return EXIT_DEPS_MISSING

    state = _load_training_state(checkpoint_dir)
    if state is None:
        sys.stderr.write(
            "FATAL: training_state.json is missing or malformed under "
            f"{checkpoint_dir}. The exporter refuses to write a metadata "
            "block without real eval_metrics — finish a training run "
            "with train.py --actually-train first.\n"
        )
        return EXIT_NO_TRAINING_STATE

    import torch  # type: ignore
    import onnx  # type: ignore

    out_dir.mkdir(parents=True, exist_ok=True)
    onnx_path = out_dir / "model.onnx"
    metadata_path = out_dir / "model.metadata.json"

    wrapper, alphabet_ids = _build_onnx_wrapper(base_model, checkpoint_dir, token_budget)
    wrapper.eval()
    dummy = torch.zeros((1, 3, image_size, image_size), dtype=torch.float32)

    logger.info("torch.onnx.export → %s (opset 18, dynamic batch)", onnx_path)
    try:
        torch.onnx.export(
            wrapper,
            dummy,
            str(onnx_path),
            input_names=["pixel_values"],
            output_names=["logits"],
            dynamic_axes={
                "pixel_values": {0: "batch"},
                "logits": {0: "batch"},
            },
            opset_version=18,
            do_constant_folding=True,
        )
    except Exception as e:
        sys.stderr.write(f"FATAL: torch.onnx.export failed: {e}\n")
        return EXIT_EXPORT_FAILED

    try:
        onnx_model = onnx.load(str(onnx_path))
        onnx.checker.check_model(onnx_model)
    except Exception as e:
        sys.stderr.write(f"FATAL: onnx.checker rejected the exported graph: {e}\n")
        return EXIT_CHECKER_FAILED

    sha = _sha256_of(onnx_path)
    payload = {
        "model_id": "container-ocr-v1",
        "version": state.get("model_version", "v1.0.0"),
        "sha256": sha,
        "onnx_opset": "18",
        "exported_at": _dt.datetime.now(_dt.timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z"),
        "exported_by": "export_onnx.py",
        "git_commit": state.get("git_commit"),
        "training_set_hash": state.get("training_set_hash"),
        "eval_metrics": state.get("eval_metrics"),
        "base_model": {
            "id": base_model,
            "license": "MIT",
            "attribution": "Florence-2 (Microsoft, MIT). https://aka.ms/florence-2",
        },
        "alphabet": ALPHABET,
        "alphabet_token_ids": alphabet_ids,
        "token_budget": token_budget,
        "input_shape": [3, image_size, image_size],
        "notes": (
            "Florence-2-base + LoRA, fine-tuned for ISO 6346 container-number OCR per "
            "§6.1 of docs/IMAGE-ANALYSIS-MODERNIZATION.md. The C# constrained beam "
            "decoder operates on the [B, T, 36] logits slice; vocabulary order matches "
            "Iso6346.AllowedAlphabet exactly."
        ),
    }
    with metadata_path.open("w", encoding="utf-8") as fh:
        json.dump(payload, fh, indent=2)
        fh.write("\n")

    logger.info("artifact: %s (sha256 %s)", onnx_path, sha)
    logger.info("metadata: %s", metadata_path)
    return EXIT_OK


def main(argv: Optional[list[str]] = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--checkpoint", required=True, help="Directory holding the merged LoRA weights + training_state.json")
    ap.add_argument("--base-model", default="microsoft/Florence-2-base")
    ap.add_argument("--out-dir", required=True, help="Where to write model.onnx + model.metadata.json")
    ap.add_argument("--token-budget", type=int, default=16, help="T (max output tokens). Must match plugin ContainerOcrConfig.MaxTokenBudget.")
    ap.add_argument("--image-size", type=int, default=384, help="Square input edge in pixels.")
    ap.add_argument("--verbose", action="store_true")
    args = ap.parse_args(argv)

    logging.basicConfig(
        level=logging.DEBUG if args.verbose else logging.INFO,
        format="%(asctime)s [%(levelname)s] %(message)s",
    )

    return export(
        checkpoint_dir=Path(args.checkpoint),
        base_model=args.base_model,
        out_dir=Path(args.out_dir),
        token_budget=args.token_budget,
        image_size=args.image_size,
    )


if __name__ == "__main__":
    sys.exit(main())
