# SPDX-License-Identifier: MIT
# Copyright (c) 2026 Roberto Dolfini

"""Draws the Revit ribbon icons of Bilocus.

Each icon is drawn on a large canvas and scaled down, which gives clean
anti-aliasing at 32x32 (large button) and 16x16 (small button / tooltip).
Output: src/Bilocus.Revit/Resources/<name>32.png and <name>16.png.

Requires Pillow (development only, not shipped): pip install pillow
"""

import math
import os

from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "src", "Bilocus.Revit", "Resources")

S = 512  # drawing canvas; every coordinate below is in 0..512

# Mid-tone colors that stay readable on both the light and the dark ribbon.
BLUE = (47, 128, 237, 255)
ORANGE = (242, 140, 40, 255)
RED = (229, 72, 77, 255)
WHITE = (255, 255, 255, 255)


def canvas():
    return Image.new("RGBA", (S, S), (0, 0, 0, 0))


def line(draw, points, color, width):
    draw.line(points, fill=color, width=width, joint="curve")
    radius = width // 2
    for x, y in (points[0], points[-1]):
        draw.ellipse((x - radius, y - radius, x + radius, y + radius), fill=color)


def disc(draw, cx, cy, r, color):
    draw.ellipse((cx - r, cy - r, cx + r, cy + r), fill=color)


def arrow_head(draw, tip, angle, size, color):
    # Filled triangle pointing along angle (radians), tip at the given point.
    left = (tip[0] - size * math.cos(angle - 0.5), tip[1] - size * math.sin(angle - 0.5))
    right = (tip[0] - size * math.cos(angle + 0.5), tip[1] - size * math.sin(angle + 0.5))
    draw.polygon([tip, left, right], fill=color)


def icon_send_selection():
    # A Revit element (isometric box) leaving towards Blender (orange arrow).
    img = canvas()
    d = ImageDraw.Draw(img)
    w = 38
    cx, cy, a = 200, 330, 150
    top = (cx, cy - a)
    right = (cx + a, cy - a // 2)
    bottom = (cx, cy)
    left = (cx - a, cy - a // 2)
    down = a
    d.polygon([top, right, bottom, left], fill=(47, 128, 237, 70))
    line(d, [top, right, bottom, left, top], BLUE, w)
    line(d, [left, (left[0], left[1] + down), (bottom[0], bottom[1] + down),
             (right[0], right[1] + down), right], BLUE, w)
    line(d, [bottom, (bottom[0], bottom[1] + down)], BLUE, w)
    # Arrow from the box to the top right corner.
    start = (330, 175)
    tip = (480, 40)
    line(d, [start, (420, 95)], ORANGE, 46)
    arrow_head(d, tip, math.atan2(tip[1] - start[1], tip[0] - start[0]), 160, ORANGE)
    return img


def icon_remove_proxy():
    # Two proxy lines (segments with their end points) and a red delete badge.
    img = canvas()
    d = ImageDraw.Draw(img)
    for (x0, y0, x1, y1) in ((50, 330, 290, 90), (50, 470, 290, 230)):
        line(d, [(x0, y0), (x1, y1)], BLUE, 40)
        disc(d, x0, y0, 38, BLUE)
        disc(d, x1, y1, 38, BLUE)
    disc(d, 385, 385, 118, RED)
    line(d, [(338, 338), (432, 432)], WHITE, 44)
    line(d, [(432, 338), (338, 432)], WHITE, 44)
    return img


def icon_clear_preview():
    # A preview mesh (translucent isometric box, orange like Blender) and a red delete badge.
    img = canvas()
    d = ImageDraw.Draw(img)
    w = 34
    cx, cy, a = 200, 250, 150
    top = (cx, cy - a)
    right = (cx + a, cy - a // 2)
    bottom = (cx, cy)
    left = (cx - a, cy - a // 2)
    down = a
    d.polygon([top, right, bottom, left], fill=(242, 140, 40, 70))
    line(d, [top, right, bottom, left, top], ORANGE, w)
    line(d, [left, (left[0], left[1] + down), (bottom[0], bottom[1] + down),
             (right[0], right[1] + down), right], ORANGE, w)
    line(d, [bottom, (bottom[0], bottom[1] + down)], ORANGE, w)
    disc(d, 385, 385, 118, RED)
    line(d, [(338, 338), (432, 432)], WHITE, 44)
    line(d, [(432, 338), (338, 432)], WHITE, 44)
    return img


# Status16/32/64.png are not drawn here: they come from the Bilocus logo.
ICONS = {
    "SendSelection": icon_send_selection,
    "RemoveProxy": icon_remove_proxy,
    "ClearPreview": icon_clear_preview,
}


def main():
    if not os.path.isdir(OUT):
        os.makedirs(OUT)
    for name, draw_icon in ICONS.items():
        big = draw_icon()
        for size in (32, 16):
            path = os.path.join(OUT, "{}{}.png".format(name, size))
            big.resize((size, size), Image.LANCZOS).save(path)
            print(path)


if __name__ == "__main__":
    main()
