# Clearspace | Draws the app logo and writes every file made from it:
#   Clearspace/Assets/Clearspace.ico   the app icon, 16 to 256 px
#   installer/WizardSmall.png          the installer's corner picture
#   installer/WizardLarge.png          the installer's side picture
# Run from the repository root:  python installer/make-icons.py      (needs Pillow: pip install pillow)
#
# CHANGED (logo, round 2): no dark box behind it any more. The logo is just a folder, on a transparent
# background, with a C on its front: a dark ring that is open on the right, and a teal dot in the opening.
# The shapes are on a 120 x 120 grid, the same numbers as docs/images/clearspace-logo.svg.
import io, math, struct, sys
from pathlib import Path
from PIL import Image, ImageDraw

FRONT, BACK = (0xD3, 0xA1, 0x5F, 255), (0xA8, 0x7A, 0x41, 255)  # the Dark theme's Accent, and a shade darker
INK, TEAL = (0x1A, 0x19, 0x17, 255), (0x5B, 0xC4, 0xB0, 255)    # the Dark theme's Base, and the dot
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]


def draw(size: int) -> Image.Image:
    """The logo at `size` pixels: drawn large, then scaled down so the edges come out smooth."""
    small = size <= 24            # tiny sizes: a heavier C and a bigger dot, so they survive as a few pixels
    scale = max(8, 1024 // size)
    big = size * scale
    unit = big / 120
    image = Image.new('RGBA', (big, big), (0, 0, 0, 0))
    pen = ImageDraw.Draw(image)

    def box(x1, y1, x2, y2):
        return [x1 * unit, y1 * unit, x2 * unit, y2 * unit]

    def disc(x, y, r, fill):
        pen.ellipse(box(x - r, y - r, x + r, y + r), fill=fill)

    # The back of the folder with its tab (rounded at the top left, slanted on the right) ...
    pen.rounded_rectangle(box(8, 16, 46, 60), radius=9 * unit, fill=BACK, corners=(True, False, False, False))
    pen.polygon([(45.5 * unit, 16 * unit), (59 * unit, 29.5 * unit), (45.5 * unit, 29.5 * unit)], fill=BACK)
    pen.rounded_rectangle(box(8, 29, 112, 100), radius=9 * unit, fill=BACK, corners=(False, True, True, True))
    # ... and the front, a little lower, so a strip of the back shows along the top.
    pen.rounded_rectangle(box(8, 38, 112, 104), radius=9 * unit, fill=FRONT)

    # The C: a ring open on the right, with round ends, and the dot in the opening.
    cx, cy = (55, 71) if small else (58, 71)
    radius, stroke, gap = (22, 10, 42) if small else (19, 9.5, 38)
    outer = radius + stroke / 2
    pen.arc(box(cx - outer, cy - outer, cx + outer, cy + outer), start=gap, end=360 - gap, fill=INK, width=round(stroke * unit))
    for angle in (gap, -gap):
        disc(cx + radius * math.cos(math.radians(angle)), cy + radius * math.sin(math.radians(angle)), stroke / 2, INK)
    disc(cx + radius + 3.5, cy, 8.5 if small else 6.2, TEAL)

    return image.resize((size, size), Image.LANCZOS)


def write_ico(path: Path) -> None:
    """A .ico holding each size as a PNG (what Windows Vista and later expect for the large sizes)."""
    frames = []
    for size in SIZES:
        data = io.BytesIO()
        draw(size).save(data, 'PNG', optimize=True)
        frames.append((size, data.getvalue()))
    header = struct.pack('<HHH', 0, 1, len(frames))
    offset = 6 + 16 * len(frames)
    entries = b''
    for size, data in frames:
        entries += struct.pack('<BBBBHHII', size % 256, size % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    path.write_bytes(header + entries + b''.join(data for _, data in frames))


def write_wizard_images(folder: Path) -> None:
    logo = draw(256)
    small = Image.new('RGBA', (256, 256), (0, 0, 0, 0))
    small.alpha_composite(logo.resize((208, 208), Image.LANCZOS), (24, 24))
    small.save(folder / 'WizardSmall.png', optimize=True)
    large = Image.new('RGBA', (416, 797), (0, 0, 0, 0))
    large.alpha_composite(logo.resize((224, 224), Image.LANCZOS), ((416 - 224) // 2, 150))
    large.save(folder / 'WizardLarge.png', optimize=True)


if __name__ == '__main__':
    root = Path(sys.argv[1]) if len(sys.argv) > 1 else Path('.')
    if not (root / 'Clearspace' / 'Assets').is_dir():
        sys.exit('Run this from the repository root (the folder that holds Clearspace and installer).')
    write_ico(root / 'Clearspace' / 'Assets' / 'Clearspace.ico')
    write_wizard_images(root / 'installer')
    print('Wrote Clearspace/Assets/Clearspace.ico, installer/WizardSmall.png and installer/WizardLarge.png')
