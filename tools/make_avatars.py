#!/usr/bin/env python3
"""Avatar Duck: la papera vestita in modo diverso per ogni lettera (A-Z + 'other').

    python make_avatars.py --out Assets/avatars [--size 128]
    python make_avatars.py --out preview --sheet          # anche un foglio di anteprima

Stesso stile delle illustrazioni (contorno nero spesso, colori piatti, luce in alto a destra).
Ogni lettera ha una combinazione fissa di cappello + accessorio del viso + accessorio del
collo + colore di fondo: lo stesso utente ha sempre la stessa papera. L'app sceglie il file
con la prima lettera del nome visualizzato (vedi references/components.md, "Avatar").
"""
from __future__ import annotations

import argparse
import math
import os
import sys

from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
DUCK = os.path.join(HERE, "..", "src", "DuckPrinterManager", "Assets", "duck.png")

K3 = 3                        # scala: coordinate della papera (512) -> canvas
X0, Y0, SPAN = 95, -105, 470  # finestra inquadrata, in coordinate della papera
N = SPAN * K3
L = 9 * K3                    # contorno (come la papera: ~9 px a 512)
K = (0, 0, 0, 255)
W = (255, 255, 255, 255)


def P(x, y):
    return ((x - X0) * K3, (y - Y0) * K3)


def box(x0, y0, x1, y1):
    a, b = P(x0, y0)
    c, d = P(x1, y1)
    return (a, b, c, d)


def shade(c, f):
    return tuple(max(0, min(255, int(v * f))) for v in c[:3]) + (255,)


class Av:
    def __init__(self):
        self.img = Image.new("RGBA", (N, N), (0, 0, 0, 0))
        self.d = ImageDraw.Draw(self.img)

    # forme con luce: base scura + zona chiara in alto a destra + contorno
    def rrect(self, x0, y0, x1, y1, r, col, outline=True):
        self.d.rounded_rectangle(box(x0, y0, x1, y1), r * K3, fill=shade(col, 0.86))
        self.d.rounded_rectangle(box(x0 + (x1 - x0) * 0.18, y0, x1, y1 - (y1 - y0) * 0.22), r * K3, fill=col)
        if outline:
            self.d.rounded_rectangle(box(x0, y0, x1, y1), r * K3, outline=K, width=L)

    def ellipse(self, x0, y0, x1, y1, col, outline=True, light=True):
        self.d.ellipse(box(x0, y0, x1, y1), fill=shade(col, 0.86) if light else col)
        if light:
            self.d.ellipse(box(x0 + (x1 - x0) * 0.2, y0, x1, y1 - (y1 - y0) * 0.2), fill=col)
        if outline:
            self.d.ellipse(box(x0, y0, x1, y1), outline=K, width=L)

    def poly(self, pts, col, outline=True):
        q = [P(x, y) for x, y in pts]
        self.d.polygon(q, fill=col)
        if outline:
            self.d.line(q + [q[0]], fill=K, width=L, joint="curve")

    def line(self, pts, w=1.0, col=K):
        q = [P(x, y) for x, y in pts]
        lw = int(L * w)
        self.d.line(q, fill=col, width=lw, joint="curve")
        for p in (q[0], q[-1]):
            self.d.ellipse((p[0] - lw / 2, p[1] - lw / 2, p[0] + lw / 2, p[1] + lw / 2), fill=col)

    def arc(self, x0, y0, x1, y1, a0, a1, w=1.0, col=K):
        self.d.arc(box(x0, y0, x1, y1), a0, a1, fill=col, width=int(L * w))


# ===================================================================== cappelli
# testa: centro ~ (335,145), raggio ~115, sommita' y ~ 30; occhio (372,148); becco fino a x 500
def hat_none(a, c): pass


def hat_top(a, c):
    a.rrect(268, -70, 412, 40, 10, c)
    a.d.rectangle(box(272, 8, 408, 28), fill=shade(c, 0.55))
    a.rrect(228, 26, 452, 52, 13, c)


def hat_cap(a, c):
    a.d.chord(box(228, -20, 442, 180), 180, 360, fill=shade(c, 0.86))
    a.d.chord(box(268, -20, 442, 150), 200, 360, fill=c)
    a.d.chord(box(228, -20, 442, 180), 180, 360, outline=K, width=L)
    a.rrect(395, 64, 505, 90, 13, c)
    a.ellipse(325, -30, 345, -10, shade(c, 0.7), light=False)


def hat_crown(a, c):
    gold = (255, 205, 60, 255)
    a.poly([(255, 50), (250, -30), (290, 10), (330, -50), (370, 10), (410, -30), (415, 50)], gold)
    for x, col in ((290, (230, 70, 90, 255)), (332, c), (375, (60, 150, 230, 255))):
        a.ellipse(x - 13, 22, x + 13, 46, col, light=False)


def hat_party(a, c):
    a.poly([(330, -95), (265, 52), (410, 40)], c)
    for t in (0.35, 0.65):
        y = -95 + t * 140
        a.line([(330 - t * 62, y), (330 + t * 77, y - t * 10)], 0.6, W)
    a.ellipse(312, -125, 348, -89, (255, 255, 255, 255))


def hat_beanie(a, c):
    a.d.chord(box(225, -55, 445, 200), 180, 360, fill=shade(c, 0.86))
    a.d.chord(box(265, -55, 445, 160), 200, 360, fill=c)
    a.d.chord(box(225, -55, 445, 200), 180, 360, outline=K, width=L)
    a.rrect(220, 52, 450, 92, 18, shade(c, 0.75))
    a.ellipse(310, -95, 360, -45, W)


def hat_chef(a, c):
    w = (250, 250, 250, 255)
    for (x, y, r) in ((280, -10, 48), (335, -45, 55), (392, -12, 48)):
        a.ellipse(x - r, y - r, x + r, y + r, w)
    a.rrect(262, 0, 412, 58, 10, w)


def hat_cowboy(a, c):
    a.rrect(275, -45, 400, 40, 30, c)
    a.d.rectangle(box(278, 12, 397, 30), fill=shade(c, 0.5))
    a.line([(205, 25), (240, 48), (440, 44), (468, 18)], 2.6, K)
    a.line([(210, 26), (242, 46), (438, 42), (462, 20)], 1.6, shade(c, 0.9))


def hat_grad(a, c):
    a.rrect(275, -10, 400, 48, 8, (40, 40, 48, 255))
    a.poly([(215, -12), (335, -55), (460, -12), (335, 30)], (55, 55, 64, 255))
    a.line([(335, -12), (445, 0), (445, 60)], 0.5, (255, 200, 40, 255))
    a.ellipse(433, 55, 457, 80, (255, 200, 40, 255), light=False)


def hat_headphones(a, c):
    a.arc(232, -18, 438, 190, 180, 360, 2.2, K)
    a.arc(236, -14, 434, 186, 185, 355, 1.0, c)
    a.rrect(262, 100, 318, 190, 22, c)
    a.rrect(282, 115, 300, 175, 8, shade(c, 0.6), outline=False)


def hat_flower(a, c):
    cx, cy = 268, 40
    for i in range(5):
        ang = i / 5 * 2 * math.pi - math.pi / 2
        x, y = cx + 30 * math.cos(ang), cy + 30 * math.sin(ang)
        a.ellipse(x - 24, y - 24, x + 24, y + 24, c)
    a.ellipse(cx - 18, cy - 18, cx + 18, cy + 18, (255, 205, 60, 255))


def hat_bow(a, c):
    a.poly([(330, 30), (270, -10), (275, 60)], c)
    a.poly([(340, 30), (400, -10), (395, 60)], c)
    a.ellipse(316, 12, 354, 50, shade(c, 0.85), light=False)


def hat_viking(a, c):
    horn = (245, 238, 220, 255)
    a.poly([(250, 40), (205, -10), (215, -60), (240, -5), (275, 20)], horn)
    a.poly([(420, 40), (465, -10), (455, -60), (430, -5), (395, 20)], horn)
    a.d.chord(box(240, -20, 430, 160), 180, 360, fill=(150, 156, 168, 255))
    a.d.chord(box(240, -20, 430, 160), 180, 360, outline=K, width=L)
    a.rrect(235, 50, 435, 78, 12, (120, 126, 138, 255))


HATS = [hat_top, hat_cap, hat_crown, hat_party, hat_beanie, hat_chef, hat_cowboy,
        hat_grad, hat_headphones, hat_flower, hat_bow, hat_viking, hat_none]


# ===================================================================== viso
def face_none(a, c): pass


def face_glasses(a, c):
    a.d.ellipse(box(338, 116, 404, 182), outline=K, width=int(L * 0.9))
    a.line([(338, 146), (290, 138)], 0.8)


def face_patch(a, c):  # benda da pirata (di profilo un occhiale da sole sembrerebbe questo)
    a.poly([(334, 128), (414, 124), (404, 170), (346, 172)], (30, 30, 36, 255))
    a.line([(392, 132), (402, 136)], 0.5, (200, 200, 210, 255))
    a.line([(336, 132), (288, 128)], 0.8)


def face_monocle(a, c):
    gold = (230, 170, 40, 255)
    a.d.ellipse(box(344, 120, 400, 176), outline=gold, width=int(L * 0.8))
    a.line([(372, 176), (360, 230), (330, 250)], 0.35, gold)


def face_blush(a, c):
    a.ellipse(380, 175, 420, 200, (255, 140, 150, 160), outline=False, light=False)


FACES = [face_none, face_glasses, face_patch, face_monocle, face_blush]


# ===================================================================== collo
def neck_none(a, c): pass


def neck_bowtie(a, c):
    a.poly([(410, 262), (372, 238), (372, 290)], c)
    a.poly([(410, 262), (448, 238), (448, 290)], c)
    a.ellipse(396, 248, 424, 276, shade(c, 0.8), light=False)


def neck_scarf(a, c):
    a.line([(300, 255), (360, 270), (430, 250)], 3.4, K)
    a.line([(302, 255), (360, 270), (428, 250)], 2.4, c)
    a.rrect(318, 262, 352, 345, 10, shade(c, 0.9))


def neck_tie(a, c):
    a.poly([(400, 250), (420, 250), (428, 300), (410, 330), (392, 300)], c)


def neck_medal(a, c):
    a.line([(330, 240), (395, 300), (430, 245)], 0.6, c)
    a.ellipse(375, 290, 415, 330, (255, 200, 40, 255))


NECKS = [neck_none, neck_bowtie, neck_scarf, neck_tie, neck_medal]

# colori di fondo (pastello, leggibili sia di giorno sia di notte) e degli accessori
BGS = [(186, 214, 255), (190, 236, 205), (224, 206, 255), (255, 214, 186), (255, 200, 214),
       (182, 230, 228), (255, 236, 170), (214, 222, 232)]
ACC = [(225, 60, 70, 255), (45, 110, 225, 255), (40, 160, 90, 255), (140, 80, 210, 255),
       (240, 120, 30, 255), (30, 150, 160, 255), (220, 70, 150, 255), (70, 70, 85, 255)]


def combo(i: int):
    """Combinazione fissa per l'indice (lettera): passi coprimi -> 26 combinazioni diverse."""
    return (HATS[i % len(HATS)], FACES[(i * 3 + 1) % len(FACES)], NECKS[(i * 2 + i // 5) % len(NECKS)],
            BGS[(i * 3) % len(BGS)], ACC[(i * 5 + 2) % len(ACC)])


def render(i: int, size: int) -> Image.Image:
    hat, face, neck, bg, acc = combo(i)
    a = Av()
    duck = Image.open(DUCK).convert("RGBA").resize((512 * K3, 512 * K3), Image.LANCZOS)
    a.img.alpha_composite(duck, (-X0 * K3, -Y0 * K3))
    a.d = ImageDraw.Draw(a.img)
    neck(a, acc)  # davanti al petto
    face(a, acc)
    hat(a, acc)
    # cerchio di fondo + ritaglio tondo
    out = Image.new("RGBA", (N, N), (0, 0, 0, 0))
    ImageDraw.Draw(out).ellipse((0, 0, N - 1, N - 1), fill=bg + (255,))
    out.alpha_composite(a.img)
    mask = Image.new("L", (N, N), 0)
    ImageDraw.Draw(mask).ellipse((0, 0, N - 1, N - 1), fill=255)
    out.putalpha(Image.composite(out.getchannel("A"), Image.new("L", (N, N), 0), mask))
    return out.resize((size, size), Image.LANCZOS)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", required=True)
    ap.add_argument("--size", type=int, default=128)
    ap.add_argument("--sheet", action="store_true", help="salva anche sheet.png con tutte le varianti")
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    names = [chr(ord("a") + i) for i in range(26)] + ["other"]
    imgs = []
    for i, n in enumerate(names):
        im = render(i, a.size)
        im.save(os.path.join(a.out, f"{n}.png"))
        imgs.append(im)
    if a.sheet:
        cols = 9
        s = Image.new("RGBA", (cols * (a.size + 8), ((len(imgs) + cols - 1) // cols) * (a.size + 8)), (245, 245, 247, 255))
        for i, im in enumerate(imgs):
            s.alpha_composite(im, ((i % cols) * (a.size + 8) + 4, (i // cols) * (a.size + 8) + 4))
        s.save(os.path.join(a.out, "sheet.png"))
    print(f"{len(names)} avatar in {a.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
