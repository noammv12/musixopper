#!/usr/bin/env python3
"""Generates src/Assets/Saley.ico — the app icon (exe, Explorer, alt-tab).

Rounded green square with a bold round-capped white "S" built from two
tangent-circle arcs, drawn at 1024px and downsampled. The tray icon is
rendered at runtime instead (it must adapt to the taskbar theme) with the
same arc geometry — keep the two in sync.

Usage: python make_icon.py [output.ico]
Requires: Pillow
"""
import math
import sys
from PIL import Image, ImageDraw

S = 1024


def draw_base():
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))

    # Vertical gradient, masked to a rounded square.
    top, bottom = (52, 199, 89), (31, 138, 59)  # #34C759 -> #1F8A3B
    grad = Image.new("RGBA", (S, S))
    gd = ImageDraw.Draw(grad)
    for y in range(S):
        f = y / (S - 1)
        color = tuple(round(top[i] + (bottom[i] - top[i]) * f) for i in range(3)) + (255,)
        gd.line([(0, y), (S, y)], fill=color)
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, S - 1, S - 1], radius=int(S * 0.22), fill=255)
    img.paste(grad, (0, 0), mask)

    # "S" mark on a 16-unit design grid: two tangent circles r=2.35 centered
    # (8, 5.65) and (8, 10.35); stroke 2.3u, round caps. Angles are GDI+
    # convention (0deg = +x, positive = clockwise with y-down), so the C#
    # tray renderer uses the exact same numbers.
    d = ImageDraw.Draw(img)
    u = S * 0.055
    ox = S * 0.5 - 8 * u
    oy = S * 0.5 - 8 * u
    white = (255, 255, 255, 245)
    stamp_r = 1.15 * u  # half the 2.3u stroke

    def arc(cx, cy, r, start_deg, sweep_deg):
        steps = 96
        for i in range(steps + 1):
            theta = math.radians(start_deg + sweep_deg * i / steps)
            x = ox + (cx + r * math.cos(theta)) * u
            y = oy + (cy + r * math.sin(theta)) * u
            d.ellipse([x - stamp_r, y - stamp_r, x + stamp_r, y + stamp_r], fill=white)

    arc(8, 5.65, 2.35, -45, -225)   # top bowl: upper-right terminal -> center
    arc(8, 10.35, 2.35, 270, 225)   # bottom bowl: center -> lower-left terminal
    return img


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else "src/Assets/Saley.ico"
    base = draw_base()
    sizes = [16, 24, 32, 48, 64, 128, 256]
    frames = [base.resize((s, s), Image.LANCZOS) for s in sizes]
    frames[-1].save(out, format="ICO", append_images=frames[:-1],
                    sizes=[(s, s) for s in sizes])
    print(f"wrote {out}")


if __name__ == "__main__":
    main()
