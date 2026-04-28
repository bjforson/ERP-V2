// scan-viewer.js — analyst W/L viewer interop module (Sprint V4).
//
// Loaded from ScanViewer.razor via `JS.InvokeAsync<IJSObjectReference>(
// "import", "./js/scan-viewer.js")`. Exposes a small per-canvas state
// machine: fetch the 1024px PNG preview the pre-render service produces
// (8-bit grayscale, percentile-clipped — see PreRenderWorker.cs +
// FS6000PreviewRenderer.RenderHighEnergyPng), build an offscreen
// ImageData, and re-render the visible canvas with window/level
// transforms + ROI overlay applied on demand.
//
// Scope: V4 ports v1's W/L viewer to v2 against the 8-bit preview only.
// 16-bit raw channels are out of scope here (separate work item in the
// IMAGE-ANALYSIS-MODERNIZATION track) — the offscreen ImageData is the
// source of truth and is intentionally 8-bit per channel.
//
// math reference (window/level): output = (input - (level - window/2)) * 255 / window
// clamped to [0, 255]. Standard medical-imaging convention: `level` is
// the midpoint of the visible band, `window` is the total range.

// Per-canvas state. Map keyed by the canvas element so multiple viewers
// can coexist on the same page (we don't today, but the contract is
// cheap and protects against accidental cross-talk).
const _state = new Map();

/**
 * Fetch the preview PNG, build the offscreen ImageData, and seed the
 * visible canvas with an identity (window=255, level=128) draw.
 * Returns { width, height } so the Razor side can size whatever it
 * wants around the canvas.
 */
export async function init(canvasEl, imageUrl) {
    if (!canvasEl) throw new Error('scan-viewer.init: canvas element is null');

    // Fetch with credentials so the auth cookie travels (the endpoint
    // is [Authorize]).
    const resp = await fetch(imageUrl, { credentials: 'same-origin', cache: 'default' });
    if (!resp.ok) {
        throw new Error(`scan-viewer.init: fetch failed (${resp.status})`);
    }
    const blob = await resp.blob();
    const bitmap = await createImageBitmap(blob);

    const w = bitmap.width;
    const h = bitmap.height;

    // Offscreen canvas holds the raw decoded pixels — never window/leveled.
    // ImageData reads come from this so pixel probe + ROI stats always
    // operate on the original 8-bit values.
    const offscreen = document.createElement('canvas');
    offscreen.width = w;
    offscreen.height = h;
    const offCtx = offscreen.getContext('2d', { willReadFrequently: true });
    offCtx.drawImage(bitmap, 0, 0);
    const original = offCtx.getImageData(0, 0, w, h);

    // Visible canvas size — set the pixel buffer to the image dimensions
    // and let CSS handle the on-screen scaling. Avoids resampling math
    // here; the browser does it during paint.
    canvasEl.width = w;
    canvasEl.height = h;

    _state.set(canvasEl, {
        offscreen,
        original,
        currentRoi: null,
        width: w,
        height: h,
    });

    // Identity draw — full 8-bit range, midpoint 128.
    applyWindowLevel(canvasEl, 255, 128);

    return { width: w, height: h };
}

/**
 * Apply a window/level transform to the original ImageData and paint
 * the result on the visible canvas. Re-applies the current ROI overlay
 * (if any) afterwards so the rectangle stays put across slider drags.
 */
export function applyWindowLevel(canvasEl, windowSize, level) {
    const s = _state.get(canvasEl);
    if (!s) return;

    const ctx = canvasEl.getContext('2d');
    const src = s.original.data;
    const out = ctx.createImageData(s.width, s.height);
    const dst = out.data;

    // Pre-compute the transform: pixel -> (pixel - lo) * 255 / window
    // clamped to [0, 255].
    const w = Math.max(1, windowSize);     // never divide by zero
    const lo = level - w / 2;
    const scale = 255 / w;
    const lut = new Uint8ClampedArray(256);
    for (let i = 0; i < 256; i++) {
        lut[i] = Math.max(0, Math.min(255, Math.round((i - lo) * scale)));
    }

    // RGBA stride; the preview is grayscale so R=G=B and A=255 already.
    // We sample the R channel as the source intensity (the renderer
    // packs the same value into R/G/B).
    for (let i = 0; i < src.length; i += 4) {
        const v = lut[src[i]];
        dst[i] = v;
        dst[i + 1] = v;
        dst[i + 2] = v;
        dst[i + 3] = 255;
    }

    ctx.putImageData(out, 0, 0);

    // Re-overlay the current ROI rectangle, if any.
    if (s.currentRoi) {
        _drawRoiOverlay(ctx, s.currentRoi);
    }
}

/**
 * Translate a viewport (clientX/clientY) coordinate to the underlying
 * image pixel and return its original (non-windowed) intensity.
 * Returns null when the cursor is outside the canvas.
 */
export function getPixelAt(canvasEl, clientX, clientY) {
    const s = _state.get(canvasEl);
    if (!s) return null;

    const rect = canvasEl.getBoundingClientRect();
    if (rect.width === 0 || rect.height === 0) return null;

    const x = Math.floor(((clientX - rect.left) / rect.width) * s.width);
    const y = Math.floor(((clientY - rect.top) / rect.height) * s.height);
    if (x < 0 || y < 0 || x >= s.width || y >= s.height) return null;

    const idx = (y * s.width + x) * 4;
    const value = s.original.data[idx];   // R channel = grayscale value
    return { x, y, value };
}

/**
 * Compute ROI stats over the original (non-windowed) ImageData and
 * paint a translucent rectangle on the visible canvas. Returns the
 * computed stats so the Razor side can render them in the controls
 * panel. Coordinates are in viewport space; we translate to image
 * pixel space inside.
 */
export function setRoi(canvasEl, x1, y1, x2, y2) {
    const s = _state.get(canvasEl);
    if (!s) return null;

    const rect = canvasEl.getBoundingClientRect();
    if (rect.width === 0 || rect.height === 0) return null;

    const toImage = (cx, cy) => ({
        x: Math.floor(((cx - rect.left) / rect.width) * s.width),
        y: Math.floor(((cy - rect.top) / rect.height) * s.height),
    });
    const a = toImage(x1, y1);
    const b = toImage(x2, y2);

    const xMin = Math.max(0, Math.min(a.x, b.x));
    const yMin = Math.max(0, Math.min(a.y, b.y));
    const xMax = Math.min(s.width - 1, Math.max(a.x, b.x));
    const yMax = Math.min(s.height - 1, Math.max(a.y, b.y));

    const w = xMax - xMin + 1;
    const h = yMax - yMin + 1;
    if (w < 2 || h < 2) {
        // Treat sub-2px drags as a "click" and clear instead.
        clearRoi(canvasEl);
        return null;
    }

    // Stats — single pass over original (non-windowed) intensities.
    const data = s.original.data;
    let sum = 0;
    let sumSq = 0;
    let mn = 255;
    let mx = 0;
    let n = 0;
    for (let yy = yMin; yy <= yMax; yy++) {
        for (let xx = xMin; xx <= xMax; xx++) {
            const idx = (yy * s.width + xx) * 4;
            const v = data[idx];
            sum += v;
            sumSq += v * v;
            if (v < mn) mn = v;
            if (v > mx) mx = v;
            n++;
        }
    }
    const mean = sum / n;
    const variance = sumSq / n - mean * mean;
    const stddev = variance > 0 ? Math.sqrt(variance) : 0;

    s.currentRoi = { x: xMin, y: yMin, w, h };
    const ctx = canvasEl.getContext('2d');
    _drawRoiOverlay(ctx, s.currentRoi);

    return {
        x: xMin,
        y: yMin,
        w,
        h,
        mean: Math.round(mean * 10) / 10,
        min: mn,
        max: mx,
        stddev: Math.round(stddev * 10) / 10,
        n,
    };
}

export function clearRoi(canvasEl) {
    const s = _state.get(canvasEl);
    if (!s) return;
    if (!s.currentRoi) return;
    s.currentRoi = null;

    // Re-paint without the overlay. The W/L state carried by the LUT
    // would re-apply on the next slider tick anyway, but clearing here
    // keeps the visible canvas honest if the user just wanted the
    // overlay gone.
    const ctx = canvasEl.getContext('2d');
    ctx.drawImage(s.offscreen, 0, 0);
}

export function dispose(canvasEl) {
    const s = _state.get(canvasEl);
    if (!s) return;
    // Drop references; the GC will reclaim the offscreen canvas + ImageData.
    s.offscreen.width = 0;
    s.offscreen.height = 0;
    _state.delete(canvasEl);
}

// ----- internal helpers ------------------------------------------------

function _drawRoiOverlay(ctx, roi) {
    ctx.save();
    ctx.fillStyle = 'rgba(124, 58, 237, 0.18)';   // matches --nickerp-color-* purple band
    ctx.strokeStyle = 'rgba(124, 58, 237, 0.85)';
    ctx.lineWidth = 1;
    ctx.fillRect(roi.x, roi.y, roi.w, roi.h);
    ctx.strokeRect(roi.x + 0.5, roi.y + 0.5, roi.w - 1, roi.h - 1);
    ctx.restore();
}
