// scan-viewer.js — analyst W/L viewer interop module (Sprint V4 + Sprint 20 B1.2/B1.3).
//
// Loaded from ScanViewer.razor via `JS.InvokeAsync<IJSObjectReference>(
// "import", "./js/scan-viewer.js")`. Exposes a per-canvas state machine:
// fetch the 1024px PNG preview the pre-render service produces (8-bit
// grayscale, percentile-clipped — see PreRenderWorker.cs +
// FS6000PreviewRenderer.RenderHighEnergyPng), build an offscreen
// ImageData, and re-render the visible canvas with window/level
// transforms + ROI overlay + annotation overlays applied on demand.
//
// Sprint 20 B1.2 additions:
//   - tool mode ("inspect" | "annotate") — when the user enters annotate
//     mode, mouseup creates an annotation rectangle (Razor side
//     intercepts the result to persist via AnalystAnnotationService).
//   - persisted-annotation overlays — Razor reads /api/inspection/
//     annotations on init and hands the array to setAnnotations(); the
//     module overlays them on every redraw.
//
// Sprint 20 B1.3 additions:
//   - prefetchPreview(url) — kicks off a background fetch with low
//     priority so the next-scan preview lands in the HTTP cache before
//     the operator clicks the next/prev pager.
//
// math reference (window/level): output = (input - (level - window/2)) * 255 / window
// clamped to [0, 255]. Standard medical-imaging convention: `level` is
// the midpoint of the visible band, `window` is the total range.

// Per-canvas state. Map keyed by the canvas element so multiple viewers
// can coexist on the same page (we don't today, but the contract is
// cheap and protects against accidental cross-talk).
const _state = new Map();

const TOOL_INSPECT = 'inspect';
const TOOL_ANNOTATE = 'annotate';

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
        annotations: [],         // [{x,y,w,h,severity,id?}] — drawn every redraw
        pendingRect: null,       // mouse drag in progress: viewport coords
        tool: TOOL_INSPECT,
        windowSize: 255,
        level: 128,
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
 * + persisted annotation overlays afterwards so they stay put across
 * slider drags.
 */
export function applyWindowLevel(canvasEl, windowSize, level) {
    const s = _state.get(canvasEl);
    if (!s) return;

    s.windowSize = windowSize;
    s.level = level;

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
    // Re-overlay persisted annotations (B1.2).
    _drawAnnotations(ctx, s.annotations);
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

    const imgRect = _toImageRect(canvasEl, s, x1, y1, x2, y2);
    if (imgRect === null) {
        clearRoi(canvasEl);
        return null;
    }
    const { xMin, yMin, xMax, yMax, w, h } = imgRect;

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
    _drawAnnotations(ctx, s.annotations);

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

    // Re-paint without the overlay. Re-applies the current W/L so the
    // canvas stays consistent with the slider state.
    applyWindowLevel(canvasEl, s.windowSize, s.level);
}

// =====================================================================
// Sprint 20 / B1.2 — annotation tool.
// =====================================================================

/**
 * Set the active tool. "inspect" (default) keeps mouseup → setRoi
 * behavior. "annotate" routes mouseup through finalizeAnnotation,
 * which the Razor side persists via AnalystAnnotationService.
 */
export function setTool(canvasEl, tool) {
    const s = _state.get(canvasEl);
    if (!s) return;
    s.tool = tool === TOOL_ANNOTATE ? TOOL_ANNOTATE : TOOL_INSPECT;
}

/**
 * Replace the persisted-annotation set on the canvas. Razor calls this
 * on initial load (passing the result of GET /api/inspection/
 * annotations?artifactId=...) and after any add/delete round-trip.
 */
export function setAnnotations(canvasEl, annotations) {
    const s = _state.get(canvasEl);
    if (!s) return;
    s.annotations = Array.isArray(annotations) ? annotations.map(a => ({
        id: a.id ?? a.Id ?? null,
        x: a.x ?? a.X ?? 0,
        y: a.y ?? a.Y ?? 0,
        w: a.w ?? a.W ?? 0,
        h: a.h ?? a.H ?? 0,
        severity: (a.severity ?? a.Severity ?? 'info').toLowerCase(),
    })) : [];
    applyWindowLevel(canvasEl, s.windowSize, s.level);
}

/**
 * Finalize the in-progress annotation drag. Returns the rectangle in
 * image-pixel space (or null for sub-2px clicks) so Razor can POST it
 * to /api/inspection/annotations.
 */
export function finalizeAnnotation(canvasEl, x1, y1, x2, y2) {
    const s = _state.get(canvasEl);
    if (!s) return null;
    const imgRect = _toImageRect(canvasEl, s, x1, y1, x2, y2);
    if (imgRect === null) return null;
    return { x: imgRect.xMin, y: imgRect.yMin, w: imgRect.w, h: imgRect.h };
}

// =====================================================================
// Sprint 20 / B1.3 — predictive prefetch.
// =====================================================================

/**
 * Kick off a background fetch of the given URL with low priority so
 * the next/prev scan switch is instant (the browser cache satisfies
 * the subsequent <img>/fetch). Best-effort — failures are swallowed.
 */
export function prefetchPreview(url) {
    if (!url || typeof url !== 'string') return;
    try {
        // `priority: 'low'` is a soft hint accepted by recent Chromium /
        // Firefox builds. Browsers that don't recognise it ignore the key
        // and the fetch still warms the HTTP cache.
        fetch(url, {
            credentials: 'same-origin',
            cache: 'default',
            priority: 'low',
            // keepalive lets the request finish even if the user
            // navigates away mid-flight.
            keepalive: false,
        }).catch(() => { /* swallow — best-effort prefetch */ });
    } catch (_e) {
        // Older browsers throw on unknown init keys; fall back to a
        // plain fetch without options.
        try { fetch(url); } catch (_e2) { /* really nothing we can do */ }
    }
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

function _toImageRect(canvasEl, s, x1, y1, x2, y2) {
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
    if (w < 2 || h < 2) return null;     // sub-2px drag = click
    return { xMin, yMin, xMax, yMax, w, h };
}

function _drawRoiOverlay(ctx, roi) {
    ctx.save();
    ctx.fillStyle = 'rgba(124, 58, 237, 0.18)';   // matches --nickerp-color-* purple band
    ctx.strokeStyle = 'rgba(124, 58, 237, 0.85)';
    ctx.lineWidth = 1;
    ctx.fillRect(roi.x, roi.y, roi.w, roi.h);
    ctx.strokeRect(roi.x + 0.5, roi.y + 0.5, roi.w - 1, roi.h - 1);
    ctx.restore();
}

function _drawAnnotations(ctx, annotations) {
    if (!annotations || annotations.length === 0) return;
    ctx.save();
    for (const a of annotations) {
        const colour = _annotationColour(a.severity);
        ctx.strokeStyle = colour.stroke;
        ctx.fillStyle = colour.fill;
        ctx.lineWidth = 2;
        ctx.fillRect(a.x, a.y, a.w, a.h);
        ctx.strokeRect(a.x + 1, a.y + 1, a.w - 2, a.h - 2);
    }
    ctx.restore();
}

function _annotationColour(severity) {
    switch ((severity || '').toLowerCase()) {
        case 'critical':
            return { stroke: 'rgba(220, 38, 38, 0.95)', fill: 'rgba(220, 38, 38, 0.18)' };
        case 'warning':
            return { stroke: 'rgba(202, 138, 4, 0.95)',  fill: 'rgba(202, 138, 4, 0.18)' };
        default:
            return { stroke: 'rgba(2, 132, 199, 0.95)',  fill: 'rgba(2, 132, 199, 0.18)' };
    }
}
