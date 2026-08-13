#!/usr/bin/env python3
"""Generates src/Assets/Bridget.ico — the app icon (exe, Explorer, alt-tab).

Rounded graphite square (the app's black→silver brand) with a bold
round-capped silver-white "B" built from a stem and two equal right-side
bowls, drawn at 1024px and downsampled. The tray icon is rendered at
runtime instead (it must adapt to the taskbar theme) with the same
geometry — keep the two in sync.

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
    top, bottom = (44, 44, 48), (18, 18, 21)  # #2C2C30 -> #121215
    grad = Image.new("RGBA", (S, S))
    gd = ImageDraw.Draw(grad)
    for y in range(S):
        f = y / (S - 1)
        color = tuple(round(top[i] + (bottom[i] - top[i]) * f) for i in range(3)) + (255,)
        gd.line([(0, y), (S, y)], fill=color)
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, S - 1, S - 1], radius=int(S * 0.22), fill=255)
    img.paste(grad, (0, 0), mask)

    # "B" mark on a 16-unit design grid: stem at x=6.6 from y=3.3 to 12.7,
    # two equal bowls r=2.35 centered (6.6, 5.65) and (6.6, 10.35); stroke
    # 2.0u, round caps. Angles are GDI+ convention (0deg = +x, positive =
    # clockwise with y-down) — the C# tray renderer uses the same numbers.
    d = ImageDraw.Draw(img)
    u = S * 0.055
    ox = S * 0.5 - 8 * u
    oy = S * 0.5 - 8 * u
    silver = (227, 227, 234, 248)  # the theme's AccentBrush #E3E3EA
    stamp_r = 1.0 * u  # half the 2.0u stroke

    def stamp(x16, y16):
        x = ox + x16 * u
        y = oy + y16 * u
        d.ellipse([x - stamp_r, y - stamp_r, x + stamp_r, y + stamp_r], fill=silver)

    def arc(cx, cy, r, start_deg, sweep_deg):
        steps = 96
        for i in range(steps + 1):
            theta = math.radians(start_deg + sweep_deg * i / steps)
            stamp(cx + r * math.cos(theta), cy + r * math.sin(theta))

    def line(x0, y0, x1, y1):
        steps = 96
        for i in range(steps + 1):
            f = i / steps
            stamp(x0 + (x1 - x0) * f, y0 + (y1 - y0) * f)

    line(6.6, 3.3, 6.6, 12.7)          # stem
    arc(6.6, 5.65, 2.35, -90, 180)     # top bowl (right half)
    arc(6.6, 10.35, 2.35, -90, 180)    # bottom bowl (right half)
    return img


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else "src/Assets/Bridget.ico"
    base = draw_base()
    sizes = [16, 24, 32, 48, 64, 128, 256]
    frames = [base.resize((s, s), Image.LANCZOS) for s in sizes]
    frames[-1].save(out, format="ICO", append_images=frames[:-1],
                    sizes=[(s, s) for s in sizes])
    print(f"wrote {out}")


if __name__ == "__main__":
    main()
