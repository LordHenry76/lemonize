"""Renders the Lemonize icon: a glossy lemon with a leaf, at 2048 px, then downsampled.

Usage: python make-icon.py src/Lemonize/Assets/lemonize.png src/Lemonize/Assets/lemonize.ico preview.png
"""
import sys
import numpy as np
from PIL import Image, ImageDraw, ImageFilter

S = 2048
out_png, out_ico, out_preview = sys.argv[1], sys.argv[2], sys.argv[3]

yy, xx = np.mgrid[0:S, 0:S].astype(np.float32) / S  # 0..1


def lemon_polygon(cx, cy, a, b, tip, angle_deg, n=720):
    t = np.linspace(-np.pi, np.pi, n, endpoint=False)
    # Ellipse whose radius swells into a pointed nub at both ends.
    nub = tip * (np.exp(-(t / 0.16) ** 2) + np.exp(-((np.abs(t) - np.pi) / 0.16) ** 2))
    x = (a + nub) * np.cos(t)
    y = b * np.sin(t) * (1 - 0.06 * np.cos(t) ** 2)
    th = np.radians(angle_deg)
    xr = x * np.cos(th) - y * np.sin(th)
    yr = x * np.sin(th) + y * np.cos(th)
    return [((cx + px) * S, (cy + py) * S) for px, py in zip(xr, yr)]


def mask_from(poly):
    m = Image.new("L", (S, S), 0)
    ImageDraw.Draw(m).polygon(poly, fill=255)
    return m


def to_rgba(rgb, alpha):
    arr = np.dstack([np.clip(rgb, 0, 255), np.asarray(alpha, dtype=np.float32)]).astype(np.uint8)
    return Image.fromarray(arr, "RGBA")


canvas = Image.new("RGBA", (S, S), (0, 0, 0, 0))

# --- Lemon body -------------------------------------------------------------
cx, cy, angle = 0.45, 0.57, -30
body_poly = lemon_polygon(cx, cy, 0.31, 0.235, 0.06, angle)
body = mask_from(body_poly)

# Soft contact shadow.
shadow = mask_from(lemon_polygon(cx + 0.012, cy + 0.035, 0.30, 0.21, 0.05, angle))
shadow = shadow.filter(ImageFilter.GaussianBlur(S * 0.025))
canvas.alpha_composite(to_rgba(np.zeros((S, S, 3)), np.asarray(shadow, np.float32) * 0.35))

# Base color: light from top-left, deep yellow toward bottom-right.
lx, ly = cx - 0.14, cy - 0.14
d = np.sqrt((xx - lx) ** 2 + (yy - ly) ** 2) / 0.52
d = np.clip(d, 0, 1)[..., None]
light = np.array([255, 236, 120], np.float32)
mid = np.array([255, 206, 30], np.float32)
dark = np.array([236, 150, 0], np.float32)
rgb = np.where(d < 0.55, light + (mid - light) * (d / 0.55), mid + (dark - mid) * ((d - 0.55) / 0.45))

# Rim darkening: inside edge gets a warmer, deeper tone.
inner = np.asarray(body.filter(ImageFilter.GaussianBlur(S * 0.03)), np.float32) / 255
rim = np.clip(1 - inner, 0, 1)[..., None] * 1.8
rgb = rgb * (1 - 0.35 * np.clip(rim, 0, 1)) + np.array([210, 120, 0], np.float32) * 0.35 * np.clip(rim, 0, 1)

# Peel texture: tiny dimples.
rng = np.random.default_rng(7)
pores = Image.new("L", (S, S), 0)
pd = ImageDraw.Draw(pores)
for _ in range(2600):
    px, py = rng.uniform(0.1, 0.9) * S, rng.uniform(0.2, 0.9) * S
    r = rng.uniform(2.0, 5.0)
    pd.ellipse((px - r, py - r, px + r, py + r), fill=int(rng.uniform(60, 140)))
pores = np.asarray(pores.filter(ImageFilter.GaussianBlur(2.2)), np.float32)[..., None] / 255
rgb = rgb * (1 - 0.10 * pores)

canvas.alpha_composite(to_rgba(rgb, body))

# Glossy highlight (blurred, clipped to the body).
hl = Image.new("L", (S, S), 0)
hd = ImageDraw.Draw(hl)
hd.polygon(lemon_polygon(cx - 0.09, cy - 0.10, 0.14, 0.05, 0.0, angle), fill=255)
hl = hl.filter(ImageFilter.GaussianBlur(S * 0.018))
hl_a = np.minimum(np.asarray(hl, np.float32), np.asarray(body, np.float32)) * 0.75
canvas.alpha_composite(to_rgba(np.full((S, S, 3), 255.0), hl_a))
spot = Image.new("L", (S, S), 0)
ImageDraw.Draw(spot).ellipse((S * 0.265, S * 0.405, S * 0.305, S * 0.433), fill=255)
spot = spot.filter(ImageFilter.GaussianBlur(S * 0.006))
canvas.alpha_composite(to_rgba(np.full((S, S, 3), 255.0), np.asarray(spot, np.float32) * 0.9))

# --- Stem and leaf (at the upper-right tip) ---------------------------------
tip_t = np.radians(angle)
tip_x = cx + (0.31 + 0.06) * np.cos(tip_t) * 0.97
tip_y = cy + (0.31 + 0.06) * np.sin(tip_t) * 0.97

stem = Image.new("RGBA", (S, S), (0, 0, 0, 0))
ImageDraw.Draw(stem).line(
    [(tip_x * S, tip_y * S), ((tip_x + 0.022) * S, (tip_y - 0.035) * S)], fill=(110, 82, 40, 255), width=int(S * 0.016)
)
canvas.alpha_composite(stem)


def leaf_polygon(base_x, base_y, length, width, angle_deg, n=400):
    t = np.linspace(0, 1, n)
    upper = np.stack([t * length, np.sin(np.pi * t) ** 0.9 * width * (1 - 0.25 * t)], 1)
    lower = np.stack([t[::-1] * length, -np.sin(np.pi * t[::-1]) ** 1.1 * width * 0.75 * (1 - 0.25 * t[::-1])], 1)
    pts = np.concatenate([upper, lower])
    th = np.radians(angle_deg)
    rot = np.array([[np.cos(th), -np.sin(th)], [np.sin(th), np.cos(th)]])
    pts = pts @ rot.T
    return [((base_x + px) * S, (base_y + py) * S) for px, py in pts], rot


lbx, lby = tip_x + 0.02, tip_y - 0.033
leaf_poly, rot = leaf_polygon(lbx, lby, 0.22, 0.075, -40)
leaf = mask_from(leaf_poly)
lshadow = leaf.filter(ImageFilter.GaussianBlur(S * 0.012))
canvas.alpha_composite(to_rgba(np.zeros((S, S, 3)), np.asarray(lshadow, np.float32) * 0.25))

# Leaf gradient along its length.
along = ((xx - lbx) * rot[0, 0] + (yy - lby) * rot[1, 0]) / 0.22
along = np.clip(along, 0, 1)[..., None]
across = ((xx - lbx) * rot[0, 1] + (yy - lby) * rot[1, 1])[..., None]
leaf_light = np.array([130, 210, 90], np.float32)
leaf_dark = np.array([34, 130, 62], np.float32)
lrgb = leaf_dark + (leaf_light - leaf_dark) * (0.35 + 0.65 * np.clip(-across / 0.07 + 0.3, 0, 1))
lrgb = lrgb * (0.9 + 0.1 * along)
canvas.alpha_composite(to_rgba(lrgb, leaf))

# Central vein.
vein = Image.new("L", (S, S), 0)
v0 = (lbx * S, lby * S)
v1 = ((lbx + 0.19 * rot[0, 0]) * S, (lby + 0.19 * rot[1, 0]) * S)
ImageDraw.Draw(vein).line([v0, v1], fill=255, width=int(S * 0.006))
vein_a = np.minimum(np.asarray(vein.filter(ImageFilter.GaussianBlur(1.5)), np.float32), np.asarray(leaf, np.float32)) * 0.55
canvas.alpha_composite(to_rgba(np.full((S, S, 3), [200, 240, 170], np.float32), vein_a))

# --- Output ------------------------------------------------------------------
bbox = canvas.getbbox()
pad = int(S * 0.03)
crop = canvas.crop((bbox[0] - pad, bbox[1] - pad, bbox[2] + pad, bbox[3] + pad))
side = max(crop.size)
square = Image.new("RGBA", (side, side), (0, 0, 0, 0))
square.alpha_composite(crop, ((side - crop.width) // 2, (side - crop.height) // 2))

master = square.resize((1024, 1024), Image.LANCZOS)
master.save(out_png)

frames = []
for size in (256, 128, 64, 48, 40, 32, 24, 20, 16):
    f = square.resize((size, size), Image.LANCZOS)
    if size <= 48:
        f = f.filter(ImageFilter.UnsharpMask(radius=0.6, percent=60, threshold=0))
    frames.append(f)
frames[0].save(out_ico, sizes=[f.size for f in frames], append_images=frames[1:])

# Preview sheet: big, medium, small on light and dark backgrounds.
sheet = Image.new("RGBA", (1100, 560), (240, 241, 244, 255))
dark_bg = Image.new("RGBA", (1100, 280), (24, 24, 28, 255))
sheet.alpha_composite(dark_bg, (0, 280))
for row, y in ((0, 20), (1, 300)):
    x = 20
    for size in (240, 128, 64, 48, 32, 24, 16):
        img = master.resize((size, size), Image.LANCZOS) if size > 48 else next(f for f in frames if f.width == size)
        sheet.alpha_composite(img, (x, y + (240 - size) // 2))
        x += size + 40
sheet.save(out_preview)
print("ok", master.size)
