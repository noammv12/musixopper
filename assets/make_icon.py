#!/usr/bin/env python3
"""Generates src/Assets/Musixopper.ico — the app icon (exe, Explorer, alt-tab).

Rounded green square with a white eighth-note, drawn at 1024px and
downsampled. The tray icon is rendered at runtime instead (it must adapt
to the taskbar theme), so this file only affects the executable's identity.

Usage: python make_icon.py [output.ico]
Requires: Pillow
"""
import sys
from PIL import Image, ImageDraw

S = 1024


def bezier(p0, c1, c2, p1, t):
    mt = 1 - t
    x = mt**3 * p0[0] + 3 * mt**2 * t * c1[0] + 3 * mt * t**2 * c2[0] + t**3 * p1[0]
    y = mt**3 * p0[1] + 3 * mt**2 * t * c1[1] + 3 * mt * t**2 * c2[1] + t**3 * p1[1]
    return x, y


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

    # Eighth-note on a 16-unit design grid, optically centered.
    d = ImageDraw.Draw(img)
    u = S * 0.052  # glyph scale
    ox = S * 0.5 - 7.6 * u
    oy = S * 0.5 - 8.4 * u
    white = (255, 255, 255, 245)

    def pt(x, y):
        return ox + x * u, oy + y * u

    # Head
    d.ellipse([pt(2.8, 10.4), pt(8.8, 14.4)], fill=white)
    # Stem
    d.rectangle([pt(7.2, 3.0), pt(8.8, 12.6)], fill=white)
    # Flag: stroked cubic bezier, simulated with stamped circles.
    p0, c1, c2, p1 = pt(8.0, 3.6), pt(11.2, 4.0), pt(12.2, 5.8), pt(11.7, 8.4)
    r = 0.85 * u
    for i in range(61):
        x, y = bezier(p0, c1, c2, p1, i / 60)
        d.ellipse([x - r, y - r, x + r, y + r], fill=white)
    return img


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else "src/Assets/Musixopper.ico"
    base = draw_base()
    sizes = [16, 24, 32, 48, 64, 128, 256]
    frames = [base.resize((s, s), Image.LANCZOS) for s in sizes]
    frames[-1].save(out, format="ICO", append_images=frames[:-1],
                    sizes=[(s, s) for s in sizes])
    print(f"wrote {out}")


if __name__ == "__main__":
    main()
