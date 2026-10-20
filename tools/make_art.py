"""Illustrazioni dell'app nello stile della papera (contorno nero spesso, colori piatti,
ombra a mezzaluna in basso a sinistra). Uso: python3 tools/make_art.py"""
from PIL import Image, ImageDraw, ImageChops
import os, math

HERE = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.join(HERE, '..', 'src', 'DuckPrinterManager', 'Assets')
S = 2048
K = (0, 0, 0, 255); Y = (255, 195, 67, 255); YS = (255, 162, 33, 255); OR = (255, 101, 0, 255)
W = (255, 255, 255, 255); WS = (226, 230, 236, 255)
G = (95, 203, 122, 255); GS = (52, 168, 83, 255)
R = (241, 112, 123, 255); RS = (214, 64, 76, 255)
B = (147, 189, 255, 255); BS = (100, 150, 235, 255)
L = 88

class Canvas:
    def __init__(self): self.img = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    def mask(self, box, r, ellipse=False):
        m = Image.new('L', (S, S), 0); d = ImageDraw.Draw(m)
        (d.ellipse(box, fill=255) if ellipse else d.rounded_rectangle(box, r, fill=255)); return m
    def shape(self, box, r, base, top, dx=90, dy=90, ellipse=False, lines=(), outline=True):
        m = self.mask(box, r, ellipse); layer = Image.new('RGBA', (S, S), (0, 0, 0, 0))
        layer.paste(Image.new('RGBA', (S, S), base), (0, 0), m)
        x0, y0, x1, y1 = box
        inner = self.mask((x0 + dx, y0 - 300, x1 + 300, y1 - dy), r, ellipse)
        layer.paste(Image.new('RGBA', (S, S), top), (0, 0), ImageChops.multiply(inner, m))
        d = ImageDraw.Draw(layer)
        for (a, b, yy) in lines: self.line(d, a, b, yy)
        self.img.alpha_composite(layer)
        if outline:
            d = ImageDraw.Draw(self.img)
            (d.ellipse(box, outline=K, width=L) if ellipse else d.rounded_rectangle(box, r, outline=K, width=L))
    def line(self, d, a, b, yy, w=0.75):
        d.line([(a, yy), (b, yy)], fill=K, width=int(L * w))
        for xx in (a, b): d.ellipse((xx - L * w / 2, yy - L * w / 2, xx + L * w / 2, yy + L * w / 2), fill=K)
    def stroke(self, pts, w=1.0, color=K):
        d = ImageDraw.Draw(self.img); lw = int(L * w)
        d.line(pts, fill=color, width=lw, joint='curve')
        for p in (pts[0], pts[-1]): d.ellipse((p[0] - lw / 2, p[1] - lw / 2, p[0] + lw / 2, p[1] + lw / 2), fill=color)
    def save(self, name, size=512):
        self.img.resize((size, size), Image.LANCZOS).save(os.path.join(ASSETS, name))

def printer(c, ox=0, oy=0, sc=1.0):
    t = lambda x, y: (ox + x * sc, oy + y * sc)
    def box(a, b, cc, d): return (*t(a, b), *t(cc, d))
    c.shape(box(560, 170, 1488, 820), 70 * sc, WS, W, 70 * sc, 0, lines=[(t(730, 0)[0], t(1320, 0)[0], t(0, 390)[1]), (t(730, 0)[0], t(1180, 0)[0], t(0, 560)[1])])
    c.shape(box(150, 660, 1898, 1520), 230 * sc, YS, Y, 110 * sc, 120 * sc)
    d = ImageDraw.Draw(c.img)
    d.ellipse(box(1560, 820, 1720, 980), fill=OR, outline=K, width=int(L * 0.8))
    d.ellipse(box(1605, 850, 1650, 895), fill=W)
    d.rounded_rectangle(box(430, 1150, 1618, 1270), 60 * sc, fill=K)
    c.shape(box(610, 1210, 1438, 1900), 70 * sc, WS, W, 70 * sc, 90 * sc, lines=[(t(770, 0)[0], t(1280, 0)[0], t(0, 1430)[1]), (t(770, 0)[0], t(1160, 0)[0], t(0, 1600)[1])])

def badge(c, cx, cy, r, base, top, glyph):
    c.shape((cx - r, cy - r, cx + r, cy + r), 0, base, top, dx=60, dy=60, ellipse=True)
    a = r * 0.5
    if glyph in ('plus', 'minus'):
        c.stroke([(cx - a, cy), (cx + a, cy)], 1.1, W)
        if glyph == 'plus': c.stroke([(cx, cy - a), (cx, cy + a)], 1.1, W)
    elif glyph == 'check':
        c.stroke([(cx - a, cy + a * 0.05), (cx - a * 0.25, cy + a * 0.7), (cx + a, cy - a * 0.55)], 1.2, W)

def duck(c, x, y, size):
    dk = Image.open(os.path.join(ASSETS, 'duck.png')).convert('RGBA').resize((size, size), Image.LANCZOS)
    c.img.alpha_composite(dk, (int(x), int(y)))

# --- installa / rimuovi
c = Canvas(); printer(c, 0, 60, 0.92); badge(c, 1590, 1560, 360, GS, G, 'plus'); c.save('art/install.png')
c = Canvas(); printer(c, 0, 60, 0.92); badge(c, 1590, 1560, 360, RS, R, 'minus'); c.save('art/remove.png')

# --- computer (due monitor)
c = Canvas()
def monitor(c, x, y, w, h, stand=True):
    cx = x + w / 2
    if stand:
        c.shape((cx - 170, y + h - 60, cx + 170, y + h + 230), 30, WS, W, 50, 0)
        c.shape((cx - 420, y + h + 170, cx + 420, y + h + 400), 90, YS, Y, 60, 50)
    c.shape((x, y, x + w, y + h), 90, WS, W, 60, 60)
    c.shape((x + 120, y + 120, x + w - 120, y + h - 120), 40, BS, B, 70, 70)
monitor(c, 760, 170, 1150, 820, stand=False)
monitor(c, 160, 560, 1150, 820)
c.save('art/computers.png')

# --- driver (documento .inf con ingranaggio)
c = Canvas()
d0 = (430, 160, 1520, 1820)
c.shape(d0, 110, WS, W, 90, 90, lines=[(640, 1200, 520), (640, 1300, 720), (640, 1060, 920)])
cx, cy, rr = 1400, 1420, 420
pts = []
for i in range(48):
    ang = i / 48 * 2 * math.pi
    rad = rr if (i // 3) % 2 == 0 else rr * 0.78
    pts.append((cx + rad * math.cos(ang), cy + rad * math.sin(ang)))
m = Image.new('L', (S, S), 0); ImageDraw.Draw(m).polygon(pts, fill=255)
lay = Image.new('RGBA', (S, S), (0, 0, 0, 0)); lay.paste(Image.new('RGBA', (S, S), YS), (0, 0), m)
m2 = Image.new('L', (S, S), 0); ImageDraw.Draw(m2).polygon([(x + 60, y - 60) for x, y in pts], fill=255)
lay.paste(Image.new('RGBA', (S, S), Y), (0, 0), ImageChops.multiply(m, m2))
c.img.alpha_composite(lay)
ImageDraw.Draw(c.img).line(pts + [pts[0]], fill=K, width=L, joint='curve')
c.shape((cx - 150, cy - 150, cx + 150, cy + 150), 0, WS, W, 30, 30, ellipse=True)
c.save('art/driver.png')

# --- fatto: papera + spunta
c = Canvas(); duck(c, 26, 33, 1700); badge(c, 1598, 1598, 360, GS, G, 'check'); c.save('art/done.png')  # spunta in basso a destra
print('ok')

# =====================================================================================
#  Illustrazioni di MAC Auth, Log, Impostazioni
# =====================================================================================
def id_card(c, x, y, w, h):
    c.shape((x, y, x + w, y + h), 120, WS, W, 70, 70)
    r = h * 0.2
    ax, ay = x + w * 0.27, y + h * 0.42
    c.shape((ax - r, ay - r, ax + r, ay + r), 0, YS, Y, 40, 40, ellipse=True)
    d = ImageDraw.Draw(c.img)
    c.shape((ax - r * 1.5, ay + r * 0.9, ax + r * 1.5, ay + r * 2.2), 80, YS, Y, 40, 0)
    for i, ln in enumerate((0.55, 0.42, 0.48)):
        c.line(d, x + w * 0.52, x + w * (0.52 + ln * 0.75), y + h * (0.32 + i * 0.17))

def pencil_badge(c, cx, cy, r):
    c.shape((cx - r, cy - r, cx + r, cy + r), 0, (230, 120, 0, 255), (255, 159, 46, 255), dx=60, dy=60, ellipse=True)
    a = r * 0.5
    c.stroke([(cx - a, cy + a), (cx + a * 0.75, cy - a * 0.75)], 1.5, W)
    c.stroke([(cx - a, cy + a), (cx - a * 0.55, cy + a * 0.55)], 0.9, K)

def magnifier(c, cx, cy, r):
    c.stroke([(cx + r * 0.7, cy + r * 0.7), (cx + r * 1.55, cy + r * 1.55)], 2.2, K)
    c.shape((cx - r, cy - r, cx + r, cy + r), 0, BS, B, 70, 70, ellipse=True)
    d = ImageDraw.Draw(c.img)
    d.arc((cx - r * 0.62, cy - r * 0.62, cx + r * 0.62, cy + r * 0.62), 200, 260, fill=W, width=int(L * 0.7))

def gear(c, cx, cy, rr, teeth=8):
    pts = []
    n = teeth * 6
    for i in range(n):
        ang = i / n * 2 * math.pi
        rad = rr if (i // 3) % 2 == 0 else rr * 0.78
        pts.append((cx + rad * math.cos(ang), cy + rad * math.sin(ang)))
    m = Image.new('L', (S, S), 0); ImageDraw.Draw(m).polygon(pts, fill=255)
    lay = Image.new('RGBA', (S, S), (0, 0, 0, 0)); lay.paste(Image.new('RGBA', (S, S), YS), (0, 0), m)
    m2 = Image.new('L', (S, S), 0); ImageDraw.Draw(m2).polygon([(x + 60, y - 60) for x, y in pts], fill=255)
    lay.paste(Image.new('RGBA', (S, S), Y), (0, 0), ImageChops.multiply(m, m2))
    c.img.alpha_composite(lay)
    ImageDraw.Draw(c.img).line(pts + [pts[0]], fill=K, width=L, joint='curve')
    c.shape((cx - rr * 0.36, cy - rr * 0.36, cx + rr * 0.36, cy + rr * 0.36), 0, WS, W, 30, 30, ellipse=True)

# ricerca MAC: tessera + lente
c = Canvas(); id_card(c, 170, 360, 1360, 900); magnifier(c, 1430, 1250, 330); c.save('art/mac_search.png')
# nuova utenza: tessera + piu'
c = Canvas(); id_card(c, 170, 420, 1400, 920); badge(c, 1560, 1440, 360, GS, G, 'plus'); c.save('art/user_new.png')
# modifica utenza: tessera + matita
c = Canvas(); id_card(c, 170, 420, 1400, 920); pencil_badge(c, 1560, 1440, 360); c.save('art/user_edit.png')

# lucchetto (accesso richiesto)
c = Canvas()
d = ImageDraw.Draw(c.img)
d.arc((560, 260, 1490, 1190), 180, 360, fill=K, width=int(L * 2.2))
d.line([(560 + L * 1.1, 725), (560 + L * 1.1, 980)], fill=K, width=int(L * 2.2))
d.line([(1490 - L * 1.1, 725), (1490 - L * 1.1, 980)], fill=K, width=int(L * 2.2))
d.arc((560 + L * 0.9, 260 + L * 0.9, 1490 - L * 0.9, 1190 - L * 0.9), 180, 360, fill=WS, width=int(L * 0.5))
c.shape((330, 900, 1720, 1820), 170, YS, Y, 110, 110)
c.shape((925, 1180, 1125, 1380), 0, K, K, 0, 0, ellipse=True, outline=False)
c.stroke([(1025, 1300), (1025, 1520)], 1.6, K)
c.save('art/lock.png')

# log: appunti con foglio e righe
c = Canvas()
c.shape((330, 260, 1720, 1900), 140, YS, Y, 100, 100)
c.shape((470, 470, 1580, 1760), 60, WS, W, 70, 70, lines=[(640, 1400, 700), (640, 1280, 900), (640, 1360, 1100), (640, 1180, 1300), (640, 1320, 1500)])
c.shape((770, 140, 1280, 440), 90, (140, 146, 156, 255), (180, 186, 196, 255), 40, 40)
c.save('art/log.png')

# active directory: server con led
c = Canvas()
for i, yy in enumerate((260, 820, 1380)):
    c.shape((360, yy, 1690, yy + 470), 110, WS, W, 70, 70)
    d = ImageDraw.Draw(c.img)
    c.line(d, 560, 1050, yy + 235)
    d.ellipse((1380, yy + 175, 1500, yy + 295), fill=(G if i != 1 else OR), outline=K, width=int(L * 0.7))
c.save('art/server.png')

# impostazioni: due ingranaggi
c = Canvas(); gear(c, 860, 900, 560, 8); gear(c, 1500, 1500, 360, 7); c.save('art/settings.png')
print('extra ok')
