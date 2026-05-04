NickHR PWA Icons
================

This directory contains the PWA app icons referenced in manifest.json.

Required files:
  - icon-192.png  (192x192 pixels, PNG)
  - icon-512.png  (512x512 pixels, PNG)

These PNG icons need to be created with a proper design tool (e.g., Figma, Adobe Illustrator).
The icon should feature the NickHR brand mark on a #1565C0 (blue) background.

Alternatively, use a tool like realfavicongenerator.net or pwa-asset-generator to generate
all required sizes from a single SVG source.

SVG source template (save as icon.svg, then export to PNG):
---
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512">
  <rect width="512" height="512" rx="80" fill="#1565C0"/>
  <text x="256" y="330" font-family="Inter,sans-serif" font-size="280"
        font-weight="700" fill="white" text-anchor="middle">N</text>
</svg>
---
