# -*- coding: utf-8 -*-
"""Encore faction mech RSI generator.

Legacy procedural drafts, superseded by the approved imagegen artwork.
Use pack_faction_mech_sprites.py to package approved raster source sheets.
See faction_mech_sprite_prompts.md for the art specification.

Follows the conventions of the hand-drawn vanilla mecha.rsi set: tight 4-stop
material ramps, a 1px near-black outline, top-left light source, near-black
seams between limbs, and genuinely different south / north / side silhouettes.
Chassis proportions encode the tier (scout / medium / siege) so a player can
read the threat from the silhouette alone.
"""
import os, random
from PIL import Image

S = 48
OUT = (26, 27, 33, 255)


def hx(s):
    s = s.lstrip('#')
    return (int(s[0:2], 16), int(s[2:4], 16), int(s[4:6], 16), 255)


def R(hi, lt, mid, dk):
    return {'hi': hx(hi), 'lt': hx(lt), 'mid': hx(mid), 'dk': hx(dk)}


def dim(r, f):
    def d(c):
        return (int(c[0] * f), int(c[1] * f), int(c[2] * f), 255)
    return {k: d(v) for k, v in r.items()}


CERAMIC = R('f4f7f9', 'd6dce2', 'aeb7c0', '757e88')
STEEL = R('b7bec6', '8d959e', '676e76', '424850')
GUN = R('6e767f', '4e555d', '363c43', '23272c')
CRIMSON = R('e8574a', 'c2332b', '8f211c', '5a1310')
RUST = R('d6714a', 'ab4f2e', '7b341c', '4b1f10')
PURPLE = R('b98ed6', '8e62ad', '674184', '412653')
GOLD = R('ffd87c', 'e5b444', 'b78924', '7b5915')
ORANGE = R('ffb45e', 'e08c38', 'a96322', '6d3f13')
GLASS = R('d8f6ff', '6fd0ee', '2e8fb5', '17536e')
GLASSA = R('ffe6a8', 'f0b348', 'b87d1e', '6d470f')
CAVITY = R('6d5340', '4a3728', '2e2219', '191210')

CYAN = hx('8ff0ff')
AMBER = hx('ffc24d')
GOLDL = hx('ffe9a8')


class Cv:
    def __init__(self):
        self.p = {}

    def put(self, x, y, c):
        x, y = int(x), int(y)
        if 0 <= x < S and 0 <= y < S:
            self.p[(x, y)] = c

    def get(self, x, y):
        return self.p.get((int(x), int(y)))

    def box(self, x0, y0, x1, y1, c):
        for y in range(int(y0), int(y1) + 1):
            for x in range(int(x0), int(x1) + 1):
                self.put(x, y, c)

    def erase(self, x0, y0, x1, y1):
        for y in range(int(y0), int(y1) + 1):
            for x in range(int(x0), int(x1) + 1):
                self.p.pop((x, y), None)


def plate(cv, x0, y0, x1, y1, r, rnd=1):
    """Bevelled armour plate: light top/left, dark bottom/right, cut corners."""
    x0, y0, x1, y1 = int(x0), int(y0), int(x1), int(y1)
    w, h = x1 - x0 + 1, y1 - y0 + 1
    for y in range(y0, y1 + 1):
        for x in range(x0, x1 + 1):
            if rnd and w >= 3 and h >= 3:
                if (x == x0 or x == x1) and (y == y0 or y == y1):
                    continue
            c = r['mid']
            if y == y0 or x == x0:
                c = r['lt']
            if y == y1 or x == x1:
                c = r['dk']
            if y == y0 and x == x0:
                c = r['hi']
            cv.put(x, y, c)


def vent(cv, x0, y0, x1, y1, r, step=2):
    plate(cv, x0, y0, x1, y1, r)
    for y in range(int(y0) + 1, int(y1), step):
        for x in range(int(x0) + 1, int(x1)):
            cv.put(x, y, r['dk'])


def seam(cv, x0, y0, x1, y1):
    """Near-black panel seam, only over pixels that already exist."""
    for y in range(int(y0), int(y1) + 1):
        for x in range(int(x0), int(x1) + 1):
            if cv.get(x, y) is not None:
                cv.put(x, y, OUT)


def glasspane(cv, x0, y0, x1, y1, g):
    """Lit canopy: bright body plus a corner specular, so a closed cockpit can
    never be mistaken for the dark cavity of the open state."""
    plate(cv, x0, y0, x1, y1, g)
    cv.box(x0 + 1, y0 + 1, x1 - 1, y1 - 1, g['lt'])
    for i in range(2):                               # corner specular
        cv.put(x0 + 1 + i, y0 + 1 + i, g['hi'])
    cv.box(x0 + 1, y1 - 1, x1 - 1, y1 - 1, g['mid'])


def outline(cv):
    """Grow a 1px dark border around the silhouette (8-connected)."""
    solid = set(cv.p.keys())
    for (x, y) in list(solid):
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                if dx == 0 and dy == 0:
                    continue
                if (x + dx, y + dy) not in solid:
                    cv.put(x + dx, y + dy, OUT)
    return cv


def to_img(cv):
    im = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    px = im.load()
    for (x, y), c in cv.p.items():
        px[x, y] = c
    return im


# ---------------------------------------------------------------- mech specs

def geo(cls):
    if cls == 'scout':
        return dict(hy=9, hh=4, ty=15, tor=7, sh=12, by=29, fy=43, gap=2, dep=8, legw=5)
    if cls == 'medium':
        return dict(hy=7, hh=5, ty=14, tor=9, sh=15, by=30, fy=45, gap=3, dep=9, legw=6)
    return dict(hy=6, hh=5, ty=13, tor=11, sh=19, by=31, fy=46, gap=3, dep=11, legw=7)


MECHS = {
    'suzume': dict(cls='scout', prim=CERAMIC, sec=STEEL, glass=GLASS, acc=CYAN, feat='mast'),
    'jaipei': dict(cls='medium', prim=CERAMIC, sec=STEEL, glass=GLASS, acc=CYAN, feat='hardpoint'),
    'yamori': dict(cls='scout', prim=CRIMSON, sec=GUN, glass=GLASSA, acc=AMBER, feat='visor'),
    'oni': dict(cls='medium', prim=CRIMSON, sec=GUN, glass=GLASSA, acc=AMBER, feat='antennae'),
    'ravager': dict(cls='siege', prim=RUST, sec=GUN, glass=GLASSA, acc=AMBER, feat='plow'),
    'bastion': dict(cls='siege', prim=PURPLE, sec=GOLD, glass=GLASSA, acc=GOLDL, feat='pauldrons'),
    'svarog': dict(cls='siege', prim=ORANGE, sec=STEEL, glass=GLASSA, acc=AMBER, feat='stacks'),
}


def L(d):
    return 24 - d


def Rr(d):
    return 23 + d


def head(cv, m, g, back_view=False):
    """Dark gunmetal head drawn last so it reads against every faction palette."""
    P, A = m['prim'], m['acc']
    hy, hh, ty = g['hy'], g['hh'], g['ty']
    plate(cv, L(hh), hy, Rr(hh), ty + 1, GUN)
    plate(cv, L(hh + 1), hy + 1, Rr(hh + 1), hy + 2, P)     # brow crest
    if back_view:
        cv.box(L(hh - 1), hy + 4, Rr(hh - 1), hy + 4, GUN['dk'])
        cv.box(L(hh - 1), hy + 6, Rr(hh - 1), hy + 6, GUN['dk'])
        cv.put(L(1), hy + 5, A)
    else:
        plate(cv, L(hh - 1), hy + 4, Rr(hh - 1), hy + 6, m['glass'])
        cv.box(L(hh - 2), hy + 5, Rr(hh - 2), hy + 5, A)
    seam(cv, L(hh), ty + 1, Rr(hh), ty + 1)


# ---------------------------------------------------------------- front view

def front(m, open_hatch=False):
    cv = Cv()
    g = geo(m['cls'])
    P, Sc, G, A = m['prim'], m['sec'], m['glass'], m['acc']
    hy, hh, ty, tor, sh, by, fy, gap = (g['hy'], g['hh'], g['ty'], g['tor'],
                                        g['sh'], g['by'], g['fy'], g['gap'])

    # --- legs
    for s in (-1, 1):
        ox, ix = (L(tor), L(gap)) if s < 0 else (Rr(gap), Rr(tor))
        plate(cv, ox, by, ix, by + 6, Sc)                  # thigh
        plate(cv, ox + 1, by + 6, ix - 1, fy - 3, Sc)      # shin
        plate(cv, ox - 1, fy - 2, ix, fy, P)               # foot
        plate(cv, ox, by + 5, ix, by + 7, P)               # knee guard
        seam(cv, ox, by + 8, ix, by + 8)
    plate(cv, L(tor - 1), by - 2, Rr(tor - 1), by + 1, Sc)  # pelvis
    seam(cv, L(tor - 1), by + 1, Rr(tor - 1), by + 1)

    # --- arms
    for s in (-1, 1):
        ao, ai = (L(sh), L(sh - 4)) if s < 0 else (Rr(sh - 4), Rr(sh))
        plate(cv, ao, ty + 4, ai, ty + 10, Sc)             # upper arm
        plate(cv, ao, ty + 10, ai, ty + 16, P)             # forearm
        plate(cv, ao, ty + 16, ai, ty + 19, Sc)            # fist / mount
        seam(cv, ao, ty + 10, ai, ty + 10)
        seam(cv, ao, ty + 16, ai, ty + 16)

    # --- torso
    plate(cv, L(tor), ty, Rr(tor), by, P)
    seam(cv, L(tor - 4), ty + 1, L(tor - 4), by - 4)       # panel lines
    seam(cv, Rr(tor - 4), ty + 1, Rr(tor - 4), by - 4)
    plate(cv, L(tor - 2), by - 5, Rr(tor - 2), by - 2, Sc)  # waist band
    for x in range(L(tor - 3), Rr(tor - 3), 3):
        cv.put(x, by - 4, Sc['dk'])
    seam(cv, L(sh - 4) - 1, ty + 4, L(sh - 4) - 1, ty + 19)  # arm / torso seams
    seam(cv, Rr(sh - 4) + 1, ty + 4, Rr(sh - 4) + 1, ty + 19)

    # --- cockpit canopy
    cw = 3 if m['cls'] == 'scout' else 4
    cx0, cy0, cx1, cy1 = L(cw), ty + 2, Rr(cw), ty + 8
    if open_hatch:
        plate(cv, cx0 - 1, cy0 - 1, cx1 + 1, cy1 + 1, CAVITY, rnd=0)
        cv.box(cx0, cy0, cx1, cy1, CAVITY['dk'])
        cv.box(cx0 + 1, cy1 - 2, cx1 - 1, cy1, CAVITY['mid'])       # seat back
        cv.put(cx0 + 1, cy0 + 1, A)
        plate(cv, cx0 - 2, cy1 + 2, cx1 + 2, cy1 + 4, P)            # hatch, swung down
        seam(cv, cx0 - 2, cy1 + 2, cx1 + 2, cy1 + 2)
    else:
        glasspane(cv, cx0, cy0, cx1, cy1, G)
        plate(cv, cx0 - 1, cy0 - 1, cx0 - 1, cy1 + 1, Sc)           # canopy frame
        plate(cv, cx1 + 1, cy0 - 1, cx1 + 1, cy1 + 1, Sc)
        seam(cv, cx0 - 1, cy1 + 1, cx1 + 1, cy1 + 1)

    # --- pauldrons, over the arms
    for s in (-1, 1):
        po, pi = (L(sh + 1), L(tor - 1)) if s < 0 else (Rr(tor - 1), Rr(sh + 1))
        plate(cv, po, ty - 2, pi, ty + 5, P)
        cv.box(po + 1, ty + 3, pi - 1, ty + 3, P['dk'])
        seam(cv, po, ty + 5, pi, ty + 5)

    head(cv, m, g)
    _feature(cv, m, g, 'S')
    return cv


# ---------------------------------------------------------------- back view

def back(m):
    cv = Cv()
    g = geo(m['cls'])
    P, Sc, A = m['prim'], m['sec'], m['acc']
    hy, hh, ty, tor, sh, by, fy, gap = (g['hy'], g['hh'], g['ty'], g['tor'],
                                        g['sh'], g['by'], g['fy'], g['gap'])

    for s in (-1, 1):
        ox, ix = (L(tor), L(gap)) if s < 0 else (Rr(gap), Rr(tor))
        plate(cv, ox, by, ix, by + 6, Sc)
        plate(cv, ox + 1, by + 6, ix - 1, fy - 3, Sc)
        plate(cv, ox + 1, fy - 2, ix - 1, fy, Sc)          # heel block, no toe cap
        seam(cv, ox, by + 8, ix, by + 8)
    plate(cv, L(tor - 1), by - 2, Rr(tor - 1), by + 1, Sc)

    for s in (-1, 1):
        ao, ai = (L(sh), L(sh - 4)) if s < 0 else (Rr(sh - 4), Rr(sh))
        plate(cv, ao, ty + 4, ai, ty + 10, Sc)
        plate(cv, ao, ty + 10, ai, ty + 16, dim(P, 0.85))
        plate(cv, ao, ty + 16, ai, ty + 19, Sc)
        seam(cv, ao, ty + 10, ai, ty + 10)

    plate(cv, L(tor), ty, Rr(tor), by, P)
    seam(cv, L(sh - 4) - 1, ty + 4, L(sh - 4) - 1, ty + 19)
    seam(cv, Rr(sh - 4) + 1, ty + 4, Rr(sh - 4) + 1, ty + 19)

    # dorsal radiator stack where the canopy would be
    vent(cv, L(tor - 3), ty + 2, Rr(tor - 3), by - 7, Sc)
    plate(cv, L(tor - 2), by - 6, Rr(tor - 2), by - 2, dim(P, 0.9))
    for s in (-1, 1):                                       # thruster nozzles
        nx = L(tor - 2) if s < 0 else Rr(tor - 4)
        plate(cv, nx, by - 5, nx + 2, by - 3, GUN)
        cv.put(nx + 1, by - 4, A)

    for s in (-1, 1):
        po, pi = (L(sh + 1), L(tor - 1)) if s < 0 else (Rr(tor - 1), Rr(sh + 1))
        plate(cv, po, ty - 2, pi, ty + 5, P)
        cv.box(po + 1, ty, pi - 1, ty, P['dk'])
        seam(cv, po, ty + 5, pi, ty + 5)

    head(cv, m, g, back_view=True)
    _feature(cv, m, g, 'N')
    return cv


# ---------------------------------------------------------------- side view

def side(m):
    """Profile view, facing +x: shallow depth, staggered legs, head drawn last."""
    cv = Cv()
    g = geo(m['cls'])
    P, Sc, G, A = m['prim'], m['sec'], m['glass'], m['acc']
    hy, hh, ty, tor, by, fy, d, lw = (g['hy'], g['hh'], g['ty'], g['tor'],
                                      g['by'], g['fy'], g['dep'], g['legw'])
    bx, fx = 24 - d + 1, 23 + d - 1
    far = dim(Sc, 0.6)

    # --- far leg, trailing behind
    fl0, fl1 = bx + 1, bx + lw
    plate(cv, fl0, by, fl1, by + 6, far)
    plate(cv, fl0 + 1, by + 6, fl1, fy - 3, far)
    plate(cv, fl0, fy - 2, fl1 + 1, fy, far)
    # --- near leg, striding forward
    nl0, nl1 = fx - lw, fx - 1
    plate(cv, nl0, by, nl1, by + 6, Sc)
    plate(cv, nl0 + 1, by + 6, nl1, fy - 3, Sc)
    plate(cv, nl0, by + 5, nl1, by + 7, P)                  # knee guard
    plate(cv, nl0, fy - 2, fx, fy, P)                       # foot with toe cap
    seam(cv, nl0, by + 8, nl1, by + 8)

    plate(cv, bx + 2, by - 2, fx - 2, by + 1, Sc)           # hips
    seam(cv, bx + 2, by + 1, fx - 2, by + 1)

    # --- torso, banded so the profile does not read as one flat slab
    plate(cv, bx + 2, ty, fx - 2, by - 7, P)                # chest
    plate(cv, bx + 3, by - 6, fx - 3, by - 3, Sc)           # waist band
    for x in range(bx + 4, fx - 3, 3):
        cv.box(x, by - 5, x, by - 4, Sc['dk'])
    seam(cv, bx + 2, by - 7, fx - 2, by - 7)
    cv.erase(bx + 2, by - 4, bx + 2, by - 3)                # tapered back
    vent(cv, bx - 1, ty + 3, bx + 2, by - 7, Sc)            # dorsal pack overhang
    plate(cv, bx - 1, by - 6, bx + 2, by - 4, GUN)          # thruster nozzle
    cv.put(bx, by - 5, A)
    glasspane(cv, fx - 3, ty + 1, fx - 2, ty + 5, G)        # canopy seen edge-on
    seam(cv, fx - 4, ty + 1, fx - 4, ty + 6)

    # --- near pauldron and arm, separated from the torso by seams
    plate(cv, bx + 3, ty - 2, fx - 4, ty + 4, P)
    seam(cv, bx + 3, ty + 4, fx - 4, ty + 4)
    plate(cv, fx - 3, ty + 6, fx + 1, ty + 11, Sc)          # arm overhangs the front
    plate(cv, fx - 3, ty + 11, fx + 1, ty + 17, P)
    plate(cv, fx - 3, ty + 17, fx + 1, ty + 20, Sc)
    seam(cv, fx - 4, ty + 6, fx - 4, ty + 20)
    seam(cv, fx - 3, ty + 11, fx + 1, ty + 11)
    seam(cv, fx - 3, ty + 17, fx + 1, ty + 17)

    _feature(cv, m, g, 'E')

    # --- head last so nothing buries it
    hx0, hx1 = L(hh) + 2, Rr(hh) + 2
    plate(cv, hx0, hy, hx1, ty + 1, GUN)
    plate(cv, hx0, hy + 1, hx1 - 3, hy + 2, P)              # brow crest
    plate(cv, hx1 - 2, hy + 3, hx1, hy + 5, G)              # visor, forward
    cv.put(hx1 - 1, hy + 4, A)
    seam(cv, hx0, ty + 1, hx1, ty + 1)
    return cv


# ---------------------------------------------------------------- per-mech id

def _feature(cv, m, g, dir):
    """The one silhouette cue that tells this chassis apart from its siblings."""
    P, Sc, A, F = m['prim'], m['sec'], m['acc'], m['feat']
    hy, hh, ty, tor, sh, by, fy, d = (g['hy'], g['hh'], g['ty'], g['tor'],
                                      g['sh'], g['by'], g['fy'], g['dep'])
    bx, fx = 24 - d + 1, 23 + d - 1
    hx0, hx1 = L(hh) + 2, Rr(hh) + 2          # head box in the profile view

    if F == 'mast':                            # Suzume: folding sensor mast
        if dir == 'E':
            plate(cv, hx0 - 3, hy - 6, hx0 - 2, hy + 1, Sc)
            plate(cv, hx0 - 4, hy - 9, hx0 - 1, hy - 6, Sc)
            cv.put(hx0 - 3, hy - 8, A)
        else:
            plate(cv, L(hh + 1), hy - 6, L(hh), hy, Sc)
            plate(cv, L(hh + 2), hy - 9, L(hh - 1), hy - 6, Sc)
            cv.put(L(hh + 1), hy - 8, A)
    elif F == 'antennae':                      # Oni: paired antennae
        xs = (hx0, hx0 + 3) if dir == 'E' else (L(hh + 1), Rr(hh + 1))
        for xx in xs:
            cv.box(xx, hy - 5, xx, hy - 1, Sc['lt'])
            cv.put(xx, hy - 6, A)
    elif F == 'visor':                         # Yamori: wide scout visor bar
        if dir == 'S':
            cv.box(L(hh + 1), hy + 5, Rr(hh + 1), hy + 5, A)
            plate(cv, L(hh + 2), hy + 4, L(hh + 1), hy + 6, Sc)
            plate(cv, Rr(hh + 1), hy + 4, Rr(hh + 2), hy + 6, Sc)
        elif dir == 'E':
            cv.box(hx1 - 3, hy + 4, hx1, hy + 4, A)
    elif F == 'hardpoint':                     # Jaipei: shoulder weapon hardpoints
        if dir == 'E':
            plate(cv, bx + 1, ty - 6, bx + 7, ty - 2, Sc)
            cv.put(bx + 2, ty - 5, A)
            seam(cv, bx + 1, ty - 2, bx + 7, ty - 2)
        else:
            for s in (-1, 1):
                po, pi = (L(sh), L(sh - 4)) if s < 0 else (Rr(sh - 4), Rr(sh))
                plate(cv, po, ty - 6, pi, ty - 2, Sc)
                cv.put(po + 1, ty - 5, A)
                seam(cv, po, ty - 2, pi, ty - 2)
    elif F == 'pauldrons':                     # Bastion: ceremonial pauldrons
        if dir == 'E':
            plate(cv, bx + 1, ty - 7, hx1 - 2, ty + 6, P)
            plate(cv, bx + 3, ty - 5, hx1 - 4, ty - 2, Sc)
            cv.put(bx + 4, ty - 4, A)
            seam(cv, bx + 1, ty + 6, hx1 - 2, ty + 6)
            seam(cv, bx + 1, ty - 1, hx1 - 2, ty - 1)
        else:
            for s in (-1, 1):
                po, pi = (L(sh + 3), L(tor)) if s < 0 else (Rr(tor), Rr(sh + 3))
                plate(cv, po, ty - 7, pi, ty + 6, P)
                plate(cv, po + 2, ty - 5, pi - 2, ty - 2, Sc)
                cv.put(po + 3, ty - 4, A)
                seam(cv, po, ty + 6, pi, ty + 6)
                seam(cv, po, ty - 1, pi, ty - 1)
    elif F == 'stacks':                        # Svarog: industrial exhaust stacks
        if dir == 'E':
            plate(cv, bx, ty - 7, bx + 3, ty - 1, Sc)
            cv.put(bx + 1, ty - 6, A)
        else:
            for s in (-1, 1):
                xx = L(sh - 1) if s < 0 else Rr(sh - 3)
                plate(cv, xx, ty - 7, xx + 2, ty - 1, Sc)
                cv.put(xx + 1, ty - 6, A)
    elif F == 'plow':                          # Ravager: breaching plow
        if dir == 'E':
            plate(cv, fx - 3, by - 4, fx + 1, by + 4, Sc)
            for y in range(by - 3, by + 4, 2):
                cv.put(fx + 1, y, Sc['hi'])
            seam(cv, fx - 4, by - 4, fx - 4, by + 4)
        elif dir == 'S':
            plate(cv, L(tor + 2), by - 2, Rr(tor + 2), by + 3, Sc)
            for x in range(L(tor + 1), Rr(tor + 1), 3):
                cv.box(x, by + 2, x, by + 3, Sc['hi'])
            seam(cv, L(tor + 2), by - 2, Rr(tor + 2), by - 2)
        else:
            plate(cv, L(tor + 2), by - 2, Rr(tor + 2), by + 1, dim(Sc, 0.75))


# ---------------------------------------------------------------- broken

def broken(m, name):
    cv = front(m)
    g = geo(m['cls'])
    hy, hh, ty, tor, sh, by, fy = (g['hy'], g['hh'], g['ty'], g['tor'],
                                   g['sh'], g['by'], g['fy'])
    rnd = random.Random(sum(ord(c) for c in name))

    # left pauldron and forearm blown off, bare frame left behind
    cv.erase(L(sh + 3), ty - 7, L(tor), ty + 3)
    cv.erase(L(sh), ty + 10, L(sh - 4), ty + 19)
    plate(cv, L(tor + 3), ty + 1, L(tor), ty + 5, GUN)
    plate(cv, L(sh - 1), ty + 10, L(sh - 3), ty + 13, GUN)

    # hull torn open along the right flank
    for i in range(4):
        y = ty + 7 + i * 3
        cv.erase(Rr(tor - 1 - i % 2), y, Rr(tor), y + 1)

    # buckled knee, mech has dropped onto it
    cv.erase(L(tor), fy - 2, L(g['gap']), fy)

    # shattered canopy
    cw = 3 if m['cls'] == 'scout' else 4
    cx0, cy0, cx1, cy1 = L(cw), ty + 2, Rr(cw), ty + 8
    cv.box(cx0, cy0, cx1, cy1, GUN['dk'])
    plate(cv, cx0, cy0, cx1, cy1, dim(m['glass'], 0.5))
    for i in range(cx1 - cx0):
        cv.put(cx0 + i, cy0 + 1 + (i % 3), OUT)

    # soot over everything, then dents
    for (x, y), c in list(cv.p.items()):
        if c == OUT:
            continue
        cv.p[(x, y)] = (int(c[0] * 0.7), int(c[1] * 0.7), int(c[2] * 0.73), 255)
    for _ in range(30):
        x = rnd.randrange(L(sh), Rr(sh) + 1)
        y = rnd.randrange(ty - 2, fy + 1)
        if cv.get(x, y) and cv.get(x, y) != OUT:
            cv.put(x, y, OUT if rnd.random() < 0.55 else GUN['dk'])

    # sparks kept to one warm ramp, never rainbow confetti
    spark = [hx('fff3c4'), hx('ffc24d'), hx('ff8a3d')]
    for _ in range(10):
        x = rnd.randrange(L(sh), Rr(sh) + 1)
        y = rnd.randrange(ty, by + 6)
        if cv.get(x, y):
            cv.put(x, y, spark[rnd.randrange(3)])
    return cv


# ---------------------------------------------------------------- assembly

def build(name, m):
    s = outline(front(m))
    n = outline(back(m))
    e = outline(side(m))
    sheet = Image.new('RGBA', (96, 96), (0, 0, 0, 0))
    ei = to_img(e)
    sheet.alpha_composite(to_img(s), (0, 0))
    sheet.alpha_composite(to_img(n), (48, 0))
    sheet.alpha_composite(ei, (0, 48))
    sheet.alpha_composite(ei.transpose(Image.FLIP_LEFT_RIGHT), (48, 48))
    op = to_img(outline(front(m, open_hatch=True)))
    br = to_img(outline(broken(m, name)))
    return sheet, op, br


if __name__ == '__main__':
    dest = os.environ.get('DEST')
    prev = Image.new('RGBA', (48 * 6 * 4 + 20, 48 * 4 * len(MECHS)), (35, 35, 42, 255))
    for i, (name, m) in enumerate(MECHS.items()):
        sheet, op, br = build(name, m)
        if dest:
            d = os.path.join(dest, name + '.rsi')
            sheet.save(os.path.join(d, name + '.png'))
            op.save(os.path.join(d, name + '-open.png'))
            br.save(os.path.join(d, name + '-broken.png'))
        Z = 4
        for f in range(4):
            fr = sheet.crop(((f % 2) * 48, (f // 2) * 48, (f % 2) * 48 + 48, (f // 2) * 48 + 48))
            prev.alpha_composite(fr.resize((48 * Z, 48 * Z), Image.NEAREST), (f * 48 * Z, i * 48 * Z))
        for j, o in enumerate((op, br)):
            prev.alpha_composite(o.resize((48 * Z, 48 * Z), Image.NEAREST), (20 + (4 + j) * 48 * Z, i * 48 * Z))
    prev.save(os.environ['PREVIEW'])
    print('ok')
