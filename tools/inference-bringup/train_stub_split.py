#!/usr/bin/env python3
"""
v2 inference scaffold bring-up — stub container-split exporter
==============================================================

Purpose
-------
Produce a *valid* ONNX artifact for the v2 ``container-split`` family that
matches the I/O contract in §3.2 of ``docs/IMAGE-ANALYSIS-MODERNIZATION.md``,
so the just-landed ``NickERP.Inspection.Inference.OnnxRuntime`` runner can be
exercised end-to-end on real hardware. The model itself is a random-init
ResNet-18 / U-Net stub — the point of this script is the **contract**, not
accuracy.

TODO(post-bring-up): Replace with the real trainer once §3.4 training data
has been exported via ``tools/v1-label-export/export_splits.py`` and the
loss/eval harness in §3.5 is wired up. For now: random init, no training, no
weight files. **Do not promote this artifact past Phase 0 (dev-eval).**

Architecture (§3.3)
-------------------
- Encoder: ``torchvision.models.resnet18(weights=None)`` — 4 down-blocks.
  ``weights=None`` keeps the network random-init so we don't pull ImageNet
  during a bring-up run.
- Decoder: 4 up-blocks (Upsample 2x + Conv-BN-ReLU) with skip connections
  from the matching encoder stage.
- Head: 1x1 conv to a single channel, then *mean-pool over the Y axis*
  collapsing ``(N, 1, H, W)`` to ``(N, W)`` per §3.2's "1D heatmap of length
  W". Sigmoid is applied so the output stays in [0, 1] like the real model
  will (§3.2 calls the output "probabilities along the X axis").

I/O (§3.2)
----------
- Input:  ``(N, 1, 472, 1568)`` ``float32`` — ``input``  (dynamic batch).
- Output: ``(N, 1568)`` ``float32`` — ``heatmap`` (dynamic batch).

Artifact paths (§3.7)
---------------------
- ``<storage_root>/models/container-split/v1/model.onnx``
- ``<storage_root>/models/container-split/v1/model.metadata.json``

Usage
-----
::

    "C:/Shared/ERP V2/tools/inference-bringup/.venv/Scripts/python.exe" \\
        "C:/Shared/ERP V2/tools/inference-bringup/train_stub_split.py"

Or, with a custom storage root::

    python train_stub_split.py --storage-root "C:/Shared/ERP V2/storage"

Exits 0 on success and prints the SHA-256 of the exported ONNX bytes.
"""

from __future__ import annotations

import argparse
import datetime as _dt
import hashlib
import json
import os
import sys
from pathlib import Path

import torch
import torch.nn as nn
import torch.nn.functional as F
from torchvision.models import resnet18

# --- §3.2 constants ---------------------------------------------------------

INPUT_HEIGHT = 472
INPUT_WIDTH = 1568
INPUT_CHANNELS = 1
ONNX_OPSET = 18

INPUT_NAME = "input"
OUTPUT_NAME = "heatmap"

MODEL_ID = "container-split"
MODEL_VERSION = "v1.0.0-stub"


# --- model ------------------------------------------------------------------


class _UpBlock(nn.Module):
    """Decoder up-block: bilinear x2 then 3x3 Conv-BN-ReLU x2 with skip concat."""

    def __init__(self, in_ch: int, skip_ch: int, out_ch: int) -> None:
        super().__init__()
        self.conv1 = nn.Conv2d(in_ch + skip_ch, out_ch, kernel_size=3, padding=1, bias=False)
        self.bn1 = nn.BatchNorm2d(out_ch)
        self.conv2 = nn.Conv2d(out_ch, out_ch, kernel_size=3, padding=1, bias=False)
        self.bn2 = nn.BatchNorm2d(out_ch)

    def forward(self, x: torch.Tensor, skip: torch.Tensor) -> torch.Tensor:
        x = F.interpolate(x, size=skip.shape[-2:], mode="bilinear", align_corners=False)
        x = torch.cat([x, skip], dim=1)
        x = F.relu(self.bn1(self.conv1(x)), inplace=True)
        x = F.relu(self.bn2(self.conv2(x)), inplace=True)
        return x


class ContainerSplitStubModel(nn.Module):
    """ResNet-18 / U-Net stub for the §3.3 architecture.

    The encoder is a torchvision ResNet-18 stripped of its avgpool/fc tail
    and re-headed with a 1-channel input conv (we feed a single-channel
    rendered top-strip per §3.2, not the 3-channel ImageNet shape).

    The decoder is four bilinear-upsample U-Net blocks fed by the matching
    encoder taps. The head produces ``(N, 1, H, W)`` then mean-pools the Y
    axis to ``(N, W)``. A sigmoid keeps the output in [0, 1].
    """

    def __init__(self) -> None:
        super().__init__()
        encoder = resnet18(weights=None)

        # Single-channel input adaptation. ResNet-18's stem expects 3 input
        # channels; we replace it with a 1-channel-in version. Bias stays
        # off (BN follows immediately).
        self.stem = nn.Sequential(
            nn.Conv2d(INPUT_CHANNELS, 64, kernel_size=7, stride=2, padding=3, bias=False),
            encoder.bn1,
            encoder.relu,
        )
        # Tap before maxpool so the first skip retains 472/2 = 236 px height.
        self.maxpool = encoder.maxpool

        # Encoder stages produce 64, 64, 128, 256, 512 channel feature maps.
        self.layer1 = encoder.layer1  # 64 ch
        self.layer2 = encoder.layer2  # 128 ch, /2
        self.layer3 = encoder.layer3  # 256 ch, /2
        self.layer4 = encoder.layer4  # 512 ch, /2

        # Decoder up-blocks. Channels are conventional U-Net.
        self.up1 = _UpBlock(in_ch=512, skip_ch=256, out_ch=256)
        self.up2 = _UpBlock(in_ch=256, skip_ch=128, out_ch=128)
        self.up3 = _UpBlock(in_ch=128, skip_ch=64, out_ch=64)
        self.up4 = _UpBlock(in_ch=64, skip_ch=64, out_ch=32)

        # Final 1x1 to a single heatmap channel.
        self.head = nn.Conv2d(32, 1, kernel_size=1)

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        # Encoder taps -- record skips before each downsample.
        s0 = self.stem(x)         # (N, 64, H/2,  W/2)
        x1 = self.maxpool(s0)     # (N, 64, H/4,  W/4)
        e1 = self.layer1(x1)      # (N, 64, H/4,  W/4)
        e2 = self.layer2(e1)      # (N, 128, H/8, W/8)
        e3 = self.layer3(e2)      # (N, 256, H/16, W/16)
        e4 = self.layer4(e3)      # (N, 512, H/32, W/32)

        # Decoder lifts back up, fusing skips.
        d1 = self.up1(e4, e3)     # → (N, 256, H/16, W/16)
        d2 = self.up2(d1, e2)     # → (N, 128, H/8,  W/8)
        d3 = self.up3(d2, e1)     # → (N, 64,  H/4,  W/4)
        d4 = self.up4(d3, s0)     # → (N, 32,  H/2,  W/2)

        # Up-sample once more so spatial output matches input (H, W).
        d4 = F.interpolate(d4, size=(INPUT_HEIGHT, INPUT_WIDTH), mode="bilinear", align_corners=False)

        logits = self.head(d4)    # (N, 1, H, W)

        # §3.3 head: 1×1 conv → mean-pool over the Y axis → 1D heatmap of length W.
        heatmap = logits.mean(dim=2)        # (N, 1, W)
        heatmap = heatmap.squeeze(1)        # (N, W)
        heatmap = torch.sigmoid(heatmap)    # constrain to [0, 1]
        return heatmap


# --- export -----------------------------------------------------------------


def _sha256_of(path: Path) -> str:
    sha = hashlib.sha256()
    with path.open("rb") as fp:
        for chunk in iter(lambda: fp.read(1 << 20), b""):
            sha.update(chunk)
    return sha.hexdigest()


def _export(model: nn.Module, out_path: Path) -> None:
    out_path.parent.mkdir(parents=True, exist_ok=True)
    dummy = torch.zeros((1, INPUT_CHANNELS, INPUT_HEIGHT, INPUT_WIDTH), dtype=torch.float32)

    # Sanity check forward pass before paying the export cost.
    model.eval()
    with torch.no_grad():
        out = model(dummy)
    expected_shape = (1, INPUT_WIDTH)
    if tuple(out.shape) != expected_shape:
        raise RuntimeError(
            f"Forward pass output shape {tuple(out.shape)} != expected {expected_shape}; "
            "check the architecture before exporting."
        )

    torch.onnx.export(
        model,
        dummy,
        str(out_path),
        input_names=[INPUT_NAME],
        output_names=[OUTPUT_NAME],
        dynamic_axes={
            INPUT_NAME: {0: "batch"},
            OUTPUT_NAME: {0: "batch"},
        },
        opset_version=ONNX_OPSET,
        do_constant_folding=True,
    )


def _write_metadata(metadata_path: Path, sha256: str) -> None:
    payload = {
        "model_id": MODEL_ID,
        "version": MODEL_VERSION,
        "sha256": sha256,
        "onnx_opset": str(ONNX_OPSET),
        "exported_at": _dt.datetime.now(_dt.timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z"),
        "exported_by": "stub-trainer-bringup",
        "git_commit": None,
        "training_set_hash": None,
        "eval_metrics": None,
        "base_model": "resnet18-unet-stub",
        "notes": (
            "Random-init stub for v2 inference scaffold bring-up. Not for production. "
            "See §3 of docs/IMAGE-ANALYSIS-MODERNIZATION.md."
        ),
    }
    metadata_path.parent.mkdir(parents=True, exist_ok=True)
    with metadata_path.open("w", encoding="utf-8") as fp:
        json.dump(payload, fp, indent=2)
        fp.write("\n")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument(
        "--storage-root",
        default=os.environ.get("ERP_V2_STORAGE_ROOT", r"C:\Shared\ERP V2\storage"),
        help="storage root; the artifact is written under <root>/models/container-split/v1/.",
    )
    args = parser.parse_args(argv)

    storage_root = Path(args.storage_root)
    model_dir = storage_root / "models" / "container-split" / "v1"
    onnx_path = model_dir / "model.onnx"
    metadata_path = model_dir / "model.metadata.json"

    print(f"[stub-trainer] storage root: {storage_root}")
    print(f"[stub-trainer] target onnx:  {onnx_path}")

    # Deterministic random init so re-runs land on byte-identical weights ->
    # SHA-256 stays stable across bring-up reruns until the architecture
    # actually changes.
    torch.manual_seed(0)

    model = ContainerSplitStubModel()
    n_params = sum(p.numel() for p in model.parameters())
    print(f"[stub-trainer] model parameters: {n_params:,} (target ~12 M per §3.3)")

    print(f"[stub-trainer] exporting ONNX (opset {ONNX_OPSET}, dynamic batch) ...")
    _export(model, onnx_path)
    sha = _sha256_of(onnx_path)
    print(f"[stub-trainer] onnx sha256: {sha}")

    _write_metadata(metadata_path, sha)
    print(f"[stub-trainer] metadata written: {metadata_path}")

    # Verify the artifact actually parses as ONNX before we declare success
    # — the C# runner will fail-fast on a corrupt file.
    import onnx

    onnx_model = onnx.load(str(onnx_path))
    onnx.checker.check_model(onnx_model)
    print(
        "[stub-trainer] onnx.checker.check_model OK — "
        f"ir_version={onnx_model.ir_version}, opset={onnx_model.opset_import[0].version}"
    )
    print(f"[stub-trainer] SUCCESS sha256={sha}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
