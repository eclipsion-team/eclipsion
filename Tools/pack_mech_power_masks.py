"""Place generated LED swatches over existing mech visor/service-light pixels.

Usage: python Tools/pack_mech_power_masks.py <six-swatch-sheet> <preview.png>
Source rows: cyan and amber. Columns: powered, low, off.
This packages raster artwork; the chassis PNGs are never rewritten.
"""
import argparse
import json
from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
DEST = ROOT / 'Resources/Textures/_Crescent/Objects/Specific/Mechs'
# Pixel-aligned sockets: south, north, east, west, open, broken.
# Each tuple is x, y, width, height within the existing 48px chassis frame.
SOCKETS = {
    'suzume': [[(23,22,3,1),(28,16,1,1)], [(22,23,3,1),(28,16,1,1)],
               [(28,21,2,1),(23,16,1,1)], [(17,21,2,1),(23,16,1,1)],
               [(22,21,3,1),(27,16,1,1)], [(22,21,3,1),(27,16,1,1)]],
    'jaipei': [[(22,16,3,1)], [(22,22,3,1)], [(28,15,2,1)], [(16,16,2,1)],
               [(22,18,3,1)], [(22,16,3,1)]],
    'yamori': [[(23,22,2,1)], [(23,23,2,1)], [(26,22,2,1)], [(19,22,2,1)],
               [(23,23,2,1)], [(22,22,2,1)]],
    'oni': [[(22,14,3,1)], [(22,21,3,1)], [(29,15,2,1)], [(17,15,2,1)],
            [(22,14,3,1)], [(22,15,2,1)]],
    'ravager': [[(23,21,1,1)], [(22,24,3,1)], [(30,20,1,1)], [(16,20,1,1)],
                [(22,22,3,1)], [(23,21,1,1)]],
    'bastion': [[(22,20,3,1)], [(22,23,3,1)], [(28,19,2,1)], [(18,19,2,1)],
                [(22,20,3,1)], [(22,20,3,1)]],
    'svarog': [[(22,19,3,1)], [(22,23,3,1)], [(29,18,2,2)],
               [(16,18,2,1),(25,18,1,1)], [(22,22,3,1)], [(21,19,3,1)]],
}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source', type=Path)
    parser.add_argument('preview', type=Path)
    args = parser.parse_args()
    source = Image.open(args.source).convert('RGBA')
    swatches = []
    for row in range(2):
        for col in range(3):
            cell = source.crop((round(col*source.width/3), round(row*source.height/2),
                                round((col+1)*source.width/3), round((row+1)*source.height/2)))
            cell.putalpha(cell.getchannel('A').point(lambda a: 255 if a >= 128 else 0))
            cell = cell.crop(cell.getbbox())
            # Use the opaque interior, not export fringes around the swatch.
            swatches.append(cell.crop((cell.width//3, cell.height//3,
                                      cell.width*2//3, cell.height*2//3)))

    preview = Image.new('RGB', (112+7*192, 32+7*208), '#292c33')
    draw = ImageDraw.Draw(preview)
    labels = ['ON / FRONT', 'LOW / FRONT', 'OFF / FRONT', 'ON / BACK',
              'ON / SIDE', 'ON / OPEN', 'OFF / BROKEN']
    for i,label in enumerate(labels):
        draw.text((112+i*192+35,8),label,fill='white')
    for row,(name,sockets) in enumerate(SOCKETS.items()):
        directory = DEST / (name+'.rsi')
        source_sheet = Image.open(directory/(name+'.png')).convert('RGBA')
        chassis = [source_sheet.crop(((i%2)*48,(i//2)*48,(i%2+1)*48,(i//2+1)*48)) for i in range(4)]
        chassis += [Image.open(directory/(name+suffix+'.png')).convert('RGBA') for suffix in ['-open','-broken']]
        metadata = json.loads((directory/'meta.json').read_text())
        metadata['states'] = [s for s in metadata['states'] if '-power-' not in s['name']]
        masks = {}
        for status_index,status in enumerate(['powered','low','off']):
            swatch = swatches[(0 if name in ['suzume','jaipei','bastion'] else 3)+status_index]
            frames = []
            for index,rectangles in enumerate(sockets):
                frame = Image.new('RGBA',(48,48))
                for x,y,w,h in rectangles:
                    patch = swatch.resize((w,h),Image.Resampling.NEAREST)
                    assert all(patch.getpixel((px,py))[3]==255 for px in range(w) for py in range(h))
                    assert all(chassis[index].getpixel((x+px,y+py))[3]==255 for px in range(w) for py in range(h)), (name,index,(x,y,w,h))
                    frame.paste(patch,(x,y))
                frames.append(frame)
            masks[status] = frames
            sheet = Image.new('RGBA',(96,96))
            for i,frame in enumerate(frames[:4]):
                sheet.paste(frame,((i%2)*48,(i//2)*48))
            state = name+'-power-'+status
            sheet.save(directory/(state+'.png'))
            metadata['states'].append({'name':state,'directions':4})
            state = name+'-open-power-'+status
            frames[4].save(directory/(state+'.png'))
            metadata['states'].append({'name':state})
        state = name+'-broken-power-off'
        masks['off'][5].save(directory/(state+'.png'))
        metadata['states'].append({'name':state})
        (directory/'meta.json').write_text(json.dumps(metadata,indent=2)+'\n')
        draw.text((8,32+row*208+85),name.upper(),fill='white')
        for col,(status,index) in enumerate([('powered',0),('low',0),('off',0),('powered',1),('powered',2),('powered',4),('off',5)]):
            combined = chassis[index].copy()
            mask = masks[status][index].copy()
            # Match the client's dim, shaded unpowered lens tint.
            if status=='off':
                mask = Image.merge('RGBA', tuple(c.point(lambda v: round(v*96/255)) for c in mask.split()[:3])+(mask.getchannel('A'),))
            combined.alpha_composite(mask)
            large = combined.resize((192,192),Image.Resampling.NEAREST)
            preview.paste(large,(112+col*192,32+row*208),large)
        print(name, '7 power states packed; body pixels preserved')
    args.preview.parent.mkdir(parents=True,exist_ok=True)
    preview.save(args.preview)


if __name__ == '__main__':
    main()
