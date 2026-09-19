"""Pack approved six-view raster sheets into the faction mech RSIs.

This only crops, resizes, indexes the palette and packages existing artwork.
Input layout: south/north/east on row one, west/open/broken on row two.
Requires Pillow. Source sheets and preview paths must be supplied explicitly.
"""

import argparse
import json
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
DESTINATION = ROOT / "Resources/Textures/_Crescent/Objects/Specific/Mechs"
# Match the native footprint of Lancer (33 high), Bogatyr (40) and Gilgamesh
# (34). Bastion has five extra pixels above its chassis for the crown.
HEIGHTS = {"suzume": 33, "jaipei": 40, "yamori": 33, "oni": 40,
           "ravager": 34, "bastion": 39, "svarog": 35}
CELL = 48


def frames_from_sheet(path, target_height):
    source = Image.open(path).convert("RGBA")
    crops = []
    for index in range(6):
        col, row = index % 3, index // 3
        cell = source.crop((round(col * source.width / 3),
                            round(row * source.height / 2),
                            round((col + 1) * source.width / 3),
                            round((row + 1) * source.height / 2)))
        # RSI pixels are opaque or empty. Discard the export's faint alpha
        # fringe before computing bounds, so it cannot shrink the chassis.
        cell.putalpha(cell.getchannel("A").point(lambda a: 255 if a >= 128 else 0))
        bounds = cell.getbbox()
        if bounds is None:
            raise ValueError(f"{path}: empty frame {index}")
        crops.append(cell.crop(bounds))

    scale = min(target_height / max(c.height for c in crops),
                36 / max(c.width for c in crops))
    frames = []
    for crop in crops:
        small = crop.resize((max(1, round(crop.width * scale)),
                             max(1, round(crop.height * scale))),
                            Image.Resampling.NEAREST)
        frame = Image.new("RGBA", (CELL, CELL))
        # Existing mech art is grounded on the bottom edge of its 48px cell.
        frame.paste(small, ((CELL - small.width) // 2, CELL - small.height))
        frames.append(frame)

    # One shared, undithered palette for every direction/state. Quantizing
    # opaque samples avoids wasting palette slots on the invisible backdrop.
    samples = [pixel[:3] for frame in frames for pixel in frame.getdata() if pixel[3]]
    sample_image = Image.new("RGB", (len(samples), 1))
    sample_image.putdata(samples)
    # Maximum coverage retains rare cyan/amber sensor pixels that an area-
    # weighted palette can otherwise collapse into the surrounding armor.
    palette = sample_image.quantize(colors=24, method=Image.Quantize.MAXCOVERAGE,
                                    dither=Image.Dither.NONE)
    indexed = []
    for frame in frames:
        opaque = frame.convert("RGB").quantize(palette=palette,
                                                dither=Image.Dither.NONE).convert("RGBA")
        opaque.putalpha(frame.getchannel("A"))
        clean = Image.new("RGBA", frame.size)
        clean.paste(opaque, mask=opaque.getchannel("A"))
        indexed.append(clean)
    return indexed


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path, help="directory containing <mech>.png sheets")
    parser.add_argument("--output", type=Path, default=DESTINATION)
    parser.add_argument("--preview", type=Path, required=True)
    args = parser.parse_args()

    # Prepare the whole batch before replacing any game asset.
    batch = {name: frames_from_sheet(args.source / f"{name}.png", height)
             for name, height in HEIGHTS.items()}
    preview = Image.new("RGB", (128 + 6 * 192, 32 + len(batch) * 200), "#292c33")
    draw = ImageDraw.Draw(preview)
    for index, label in enumerate(("SOUTH", "NORTH", "EAST", "WEST", "OPEN", "BROKEN")):
        draw.text((128 + index * 192 + 60, 8), label, fill="white")
    for row, (name, frames) in enumerate(batch.items()):
        dest = args.output / f"{name}.rsi"
        dest.mkdir(parents=True, exist_ok=True)
        closed = Image.new("RGBA", (CELL * 2, CELL * 2))
        for index, frame in enumerate(frames[:4]):
            closed.paste(frame, ((index % 2) * CELL, (index // 2) * CELL))
        closed.save(dest / f"{name}.png")
        frames[4].save(dest / f"{name}-open.png")
        frames[5].save(dest / f"{name}-broken.png")
        metadata = {
            "version": 1,
            "license": "CC-BY-SA-3.0",
            "copyright": "AI-generated with OpenAI imagegen for Encore. HULLROT mecha art used as style reference. Cropped, palette-normalized and packed into 48x48 RSI states.",
            "size": {"x": CELL, "y": CELL},
            "states": [{"name": name, "directions": 4},
                       {"name": f"{name}-open"}, {"name": f"{name}-broken"}],
        }
        (dest / "meta.json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
        draw.text((8, 32 + row * 200 + 90), name.upper(), fill="white")
        for index, frame in enumerate(frames):
            enlarged = frame.resize((192, 192), Image.Resampling.NEAREST)
            preview.paste(enlarged, (128 + index * 192, 32 + row * 200), enlarged)
        print(name, "6 states; bounds:", [f.getbbox() for f in frames])
    args.preview.parent.mkdir(parents=True, exist_ok=True)
    preview.save(args.preview)


if __name__ == "__main__":
    main()
