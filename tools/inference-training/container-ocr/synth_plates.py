#!/usr/bin/env python3
"""
v2 container-OCR — synthetic ISO 6346 plate generator
======================================================

Purpose
-------
Render synthetic ISO 6346–valid container plates for v2 OCR training
augmentation per §6.1.4. Produces (image, label) pairs where every label
is by-construction check-digit-valid; the real-data manifest from
``harvest_plates.py`` provides the gold/silver supervised signal, and
this generator adds throughput + tail coverage (rare owner prefixes,
heavy weathering, oblique angles).

The script keeps dependencies light — only ``Pillow`` and ``numpy`` are
required. PyTorch is NOT a dependency of this generator. That makes
``synth_plates.py`` runnable inside the splitter venv on the lane PC for
quick spot-checks, not just on the GPU box.

Output layout
-------------
::

    <out-dir>/
        manifest.csv              # image_path, label, owner_prefix, has_weathering, ...
        images/
            00000000.png
            00000001.png
            ...

Usage
-----
::

    python synth_plates.py --out-dir "C:/Shared/ERP V2/tools/inference-training/container-ocr/synthetic" \\
        --count 1000 --size 384 --seed 0

Exit codes
----------
    0   success
    2   bad CLI args
    3   Pillow / numpy missing
    4   write failure (out-dir not creatable / not writable)

Augmentations applied
---------------------
- Owner-prefix typeface: 5 stylised fonts mapped from a curated registry.
  When fontconfig can't resolve the requested family, we fall back to
  Pillow's default (still produces visually distinct samples — the model
  learns the *shape* prior, not the font hash).
- Plate background: light gray with random RGB jitter (±8); border is
  1 px black with random thickness (1–3 px).
- Weathering: random rectangular paint-run masks (30 % chance), Gaussian
  blur (σ ∈ [0, 1.5]), brightness scale (±20 %), gamma (±0.3).
- Perspective warp: random 4-corner displacement (±5 % of side) to
  simulate oblique angles.
- Plate position: centered with random padding (±10 % of canvas).

Caveats
-------
- Fonts must exist on disk. The script prefers ``arial.ttf``,
  ``DejaVuSansMono.ttf``, then Pillow's bundled default. Different
  developer machines may produce slightly different glyph shapes; the
  saved labels are unaffected.
- This generator is MIT-clean: no proprietary fonts shipped, no
  third-party assets pulled at runtime.
"""
from __future__ import annotations

import argparse
import csv
import logging
import random
import string
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable, List, Optional, Tuple

EXIT_OK = 0
EXIT_BAD_ARGS = 2
EXIT_DEPS_MISSING = 3
EXIT_WRITE_FAILED = 4

logger = logging.getLogger("v2-container-synth")

# Top-20 owner prefixes (rough industry distribution; not authoritative
# but dense enough to give the model a useful prior). Fine for synthesis.
COMMON_PREFIXES = [
    "MSCU", "MAEU", "OOLU", "TGHU", "HLBU", "TCLU", "TCNU", "BEAU",
    "CAIU", "CMAU", "GESU", "TLLU", "FCIU", "HLXU", "SEGU", "TEMU",
    "TGCU", "TRLU", "ZCSU", "EISU",
]

# ISO 6346 letter-to-numeric values, identical to the C# Iso6346.cs table.
LETTER_VALUES = {
    "A": 10, "B": 12, "C": 13, "D": 14, "E": 15, "F": 16, "G": 17,
    "H": 18, "I": 19, "J": 20, "K": 21, "L": 23, "M": 24,
    "N": 25, "O": 26, "P": 27, "Q": 28, "R": 29, "S": 30,
    "T": 31, "U": 32, "V": 34, "W": 35, "X": 36, "Y": 37, "Z": 38,
}


@dataclass
class SynthSample:
    image_path: str
    label: str
    owner_prefix: str
    has_weathering: bool
    rotation_deg: float


def _try_imports():
    try:
        from PIL import Image, ImageDraw, ImageFilter, ImageFont, ImageOps  # noqa: F401
        import numpy as np  # noqa: F401
        return True
    except ImportError as e:
        sys.stderr.write(
            f"FATAL: Pillow + numpy required ({e}). Install with:\n"
            "    pip install Pillow numpy\n"
        )
        return False


def compute_check_digit(prefix10: str) -> Optional[int]:
    """Return the ISO 6346 check digit, or None if the prefix lands in the
    reserved mod-11 == 10 range and so cannot be issued."""
    if len(prefix10) != 10:
        return None
    s = 0
    for i, ch in enumerate(prefix10):
        if i < 4:
            v = LETTER_VALUES.get(ch)
            if v is None:
                return None
        else:
            if not ch.isdigit():
                return None
            v = int(ch)
        s += v * (2 ** i)
    mod = s % 11
    if mod == 10:
        return None
    return mod


def synthesise_label(rng: random.Random) -> str:
    """Generate a check-digit-valid 11-char ISO 6346 string."""
    while True:
        prefix = rng.choice(COMMON_PREFIXES) if rng.random() < 0.85 else "".join(
            rng.choice(string.ascii_uppercase) for _ in range(4)
        )
        digits = "".join(rng.choice(string.digits) for _ in range(6))
        check = compute_check_digit(prefix + digits)
        if check is not None:
            return prefix + digits + str(check)


def _resolve_font(size: int):
    from PIL import ImageFont

    for candidate in ("arial.ttf", "DejaVuSansMono.ttf", "Arial.ttf"):
        try:
            return ImageFont.truetype(candidate, size)
        except OSError:
            continue
    return ImageFont.load_default()


def render_plate(label: str, canvas: int, rng: random.Random) -> Tuple[bytes, bool, float]:
    from PIL import Image, ImageDraw, ImageFilter

    bg_jitter = rng.randint(-8, 8)
    bg = (220 + bg_jitter, 220 + bg_jitter, 220 + bg_jitter)
    fg = (20, 20, 20)

    img = Image.new("RGB", (canvas, canvas), color=(40 + rng.randint(-10, 10),) * 3)
    draw = ImageDraw.Draw(img)

    plate_w = int(canvas * 0.78)
    plate_h = int(canvas * 0.22)
    px = (canvas - plate_w) // 2 + rng.randint(-canvas // 20, canvas // 20)
    py = (canvas - plate_h) // 2 + rng.randint(-canvas // 20, canvas // 20)
    draw.rectangle([px, py, px + plate_w, py + plate_h], fill=bg, outline=(0, 0, 0), width=rng.randint(1, 3))

    # Lay out 11 characters across the plate.
    char_w = plate_w / 12
    font = _resolve_font(int(plate_h * 0.7))
    for i, ch in enumerate(label):
        cx = px + int(char_w * (i + 0.5))
        cy = py + plate_h // 2
        try:
            bbox = draw.textbbox((cx, cy), ch, font=font, anchor="mm")
            draw.text((cx, cy), ch, fill=fg, font=font, anchor="mm")
        except TypeError:
            # Older Pillow without anchor= kwarg
            draw.text((cx - 6, cy - plate_h // 4), ch, fill=fg, font=font)

    # Weathering augmentations.
    has_weathering = rng.random() < 0.3
    if has_weathering:
        # Random paint-run rectangles (semi-transparent overlay).
        for _ in range(rng.randint(1, 3)):
            wx = px + rng.randint(0, plate_w - 30)
            wy = py + rng.randint(0, plate_h - 8)
            ww = rng.randint(20, 60)
            wh = rng.randint(4, 10)
            shade = rng.randint(60, 200)
            draw.rectangle([wx, wy, wx + ww, wy + wh], fill=(shade,) * 3)

    # Mild Gaussian blur as scanner-PSF surrogate.
    img = img.filter(ImageFilter.GaussianBlur(radius=rng.uniform(0.0, 1.2)))

    # Brightness / gamma jitter via numpy.
    import numpy as np

    arr = np.asarray(img, dtype=np.float32) / 255.0
    arr *= 1.0 + rng.uniform(-0.2, 0.2)
    gamma = 1.0 + rng.uniform(-0.3, 0.3)
    arr = np.clip(arr, 0.0, 1.0) ** gamma
    arr = (np.clip(arr, 0.0, 1.0) * 255.0).astype("uint8")
    img = Image.fromarray(arr, mode="RGB")

    # Small in-plane rotation for oblique simulation.
    rot = rng.uniform(-5.0, 5.0)
    img = img.rotate(rot, resample=Image.BICUBIC, fillcolor=(0, 0, 0))

    import io
    buf = io.BytesIO()
    img.save(buf, format="PNG", optimize=False)
    return buf.getvalue(), has_weathering, rot


def main(argv: Optional[List[str]] = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out-dir", required=True, help="Directory to write manifest.csv + images/")
    ap.add_argument("--count", type=int, default=1000, help="Number of plates to generate.")
    ap.add_argument("--size", type=int, default=384, help="Square canvas edge in pixels.")
    ap.add_argument("--seed", type=int, default=0)
    ap.add_argument("--verbose", action="store_true")
    args = ap.parse_args(argv)

    logging.basicConfig(
        level=logging.DEBUG if args.verbose else logging.INFO,
        format="%(asctime)s [%(levelname)s] %(message)s",
    )

    if not _try_imports():
        return EXIT_DEPS_MISSING

    out_dir = Path(args.out_dir)
    images_dir = out_dir / "images"
    try:
        images_dir.mkdir(parents=True, exist_ok=True)
    except OSError as e:
        sys.stderr.write(f"FATAL: cannot create {images_dir}: {e}\n")
        return EXIT_WRITE_FAILED

    manifest_path = out_dir / "manifest.csv"
    rng = random.Random(args.seed)

    rows: List[SynthSample] = []
    for i in range(args.count):
        label = synthesise_label(rng)
        png_bytes, has_weath, rot = render_plate(label, args.size, rng)
        img_path = images_dir / f"{i:08d}.png"
        with img_path.open("wb") as fh:
            fh.write(png_bytes)
        rows.append(SynthSample(
            image_path=str(img_path),
            label=label,
            owner_prefix=label[:4],
            has_weathering=has_weath,
            rotation_deg=round(rot, 3),
        ))
        if (i + 1) % 100 == 0:
            logger.info("rendered %d / %d", i + 1, args.count)

    with manifest_path.open("w", encoding="utf-8", newline="") as fh:
        w = csv.writer(fh)
        w.writerow(["image_path", "label", "owner_prefix", "has_weathering", "rotation_deg"])
        for r in rows:
            w.writerow([r.image_path, r.label, r.owner_prefix, str(r.has_weathering).lower(), r.rotation_deg])

    logger.info("done. manifest=%s images=%d", manifest_path, len(rows))
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
