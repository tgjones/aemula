#!/usr/bin/env python3
"""Extract a netlist from amb5l's searchable BBC Micro Issue 7 mainboard schematic.

    pip install pymupdf
    python3 extract.py bbc_micro_mainboard_iss7_20210909.pdf --out /tmp/bbc-raw

The PDF is one A3 OrCAD export with a real text layer and vector geometry; no
OCR is involved. Colours identify what a stroke is:

    (0.26, 0.00, 1.00)  wire                    -> nets, joined by touching / red dots
    (0.00, 0.00, 1.00)  fat bus line            -> ignored (buses connect by label)
    (0.80, 0.50, 0.02)  part body outline       -> groups pins into a part
    (0.67, 0.53, 0.27)  pin stub                -> a zero-length tick marks the pin's outer end
    (0.00, 0.00, 0.00)  power-symbol stem + bar -> wire end that a rail label sits on
    red filled circle                           -> junction dot

Text: black 2.9pt = pin number, blue = pin name, black 2.7pt = reference / value /
net label, red = off-page connector label.

Outputs (in --out):
    netlist.txt      COMPONENTS / NETS in the ZX80 netlist format, plus DIAGNOSTICS
    diagnostics.txt  the diagnostics alone
    pins.json        per-pin detail (pin number, pin name, position, net)

The extractor does not apply hand corrections; those are layered on top of
netlist.txt by a separate patch step.
"""
import argparse
import collections
import json
import math
import os
import re
import sys

import fitz  # PyMuPDF

WIRE = (0.26, 0.0, 1.0)
BODY = (0.8, 0.5, 0.02)
STUB = (0.67, 0.53, 0.27)
BLACK = (0.0, 0.0, 0.0)
EPS = 0.35  # PDF points; wire ends and pin ends land within ~0.15 of each other

REF_RE = re.compile(r'^(IC|RL|U|A|R|C|D|TR|Q|VR|L|PL|SK|SW|LK|X|S|J|T|FB|BZ|LS|CT|RV|RP|RN|XTAL|CON|SKT|TP|K|B|P|F)\d+[A-Z]?$')
POWER_RE = re.compile(r'^[+-]?\d+(\.\d+)?V$')


NUMBER_REACH = 2.5
SEQUENTIAL = re.compile(r'^(74\w*(74|107|109|112|123|161|163|164|165|173|174|175|191|193|195|273|374|393)|CD40(13|20|24)|555)$')
STUBLESS_REF = re.compile(r'^(Q|SW|S|LK)\d+$')
OUTPUT_NAME = re.compile(r'^(Q[A-H]?|[1-4]?Y\d?|[1-4]Q|OUT\d?|RCO|TC|CO)$')
LABEL_REACH = 0.5  # net labels sit with their baseline on the wire
# component values, tolerances and device names are never net labels
NOT_A_LABEL = re.compile(r'^(\d+(\.\d+)?[pnuµkKMRFH]?\d*[VHF]?|\d+%|1N\d+|BC\d+|2N\d+|NF|ELECT|\d+(\.\d+)?MHz|\d+x \w+)$')
ORANGE_LINES = []
BUS_LINES = []
NC_MARKS = []


def assign(edges, unmatched_cost):
    """Min-cost matching of (cost, left, right) edges; leaving a left node unmatched costs
    `unmatched_cost`. Solved per connected cluster so the cubic Hungarian stays tiny."""
    d = DSU()
    for _, l, r in edges:
        d.union(('l', l), ('r', r))
    clusters = collections.defaultdict(lambda: ([], set(), set()))
    for e in edges:
        c = clusters[d.find(('l', e[1]))]
        c[0].append(e)
        c[1].add(e[1])
        c[2].add(e[2])
    out = []
    for es, ls, rs in clusters.values():
        ls, rs = sorted(ls), sorted(rs)
        li = {v: i for i, v in enumerate(ls)}
        ri = {v: i for i, v in enumerate(rs)}
        n, m = len(ls), len(rs) + len(ls)  # one private dummy column per left node
        big = 1e6
        cost = [[big] * m for _ in range(n)]
        for c, l, r in es:
            cost[li[l]][ri[r]] = min(cost[li[l]][ri[r]], c)
        for i in range(n):
            cost[i][len(rs) + i] = unmatched_cost
        # Hungarian algorithm (rows <= columns)
        INF = float('inf')
        u, v, p, way = [0.0] * (n + 1), [0.0] * (m + 1), [0] * (m + 1), [0] * (m + 1)
        for i in range(1, n + 1):
            p[0] = i
            j0 = 0
            minv = [INF] * (m + 1)
            used = [False] * (m + 1)
            while True:
                used[j0] = True
                i0, delta, j1 = p[j0], INF, 0
                for j in range(1, m + 1):
                    if not used[j]:
                        cur = cost[i0 - 1][j - 1] - u[i0] - v[j]
                        if cur < minv[j]:
                            minv[j], way[j] = cur, j0
                        if minv[j] < delta:
                            delta, j1 = minv[j], j
                for j in range(m + 1):
                    if used[j]:
                        u[p[j]] += delta
                        v[j] -= delta
                    else:
                        minv[j] -= delta
                j0 = j1
                if p[j0] == 0:
                    break
            while j0:
                j1 = way[j0]
                p[j0] = p[j1]
                j0 = j1
        for j in range(1, len(rs) + 1):
            if p[j]:
                out.append((ls[p[j] - 1], rs[j - 1]))
    return out


def same(col, target):
    return col is not None and all(abs(a - b) < 0.02 for a, b in zip(col, target))


def dist_pt_seg(px, py, ax, ay, bx, by):
    dx, dy = bx - ax, by - ay
    L2 = dx * dx + dy * dy
    if L2 < 1e-9:
        return math.hypot(px - ax, py - ay)
    t = max(0.0, min(1.0, ((px - ax) * dx + (py - ay) * dy) / L2))
    return math.hypot(px - (ax + t * dx), py - (ay + t * dy))


def seg_rect_dist(w, bb):
    """Distance from a segment to a rectangle (0 when they touch or cross)."""
    x0, y0, x1, y1 = bb
    def inside(x, y):
        return x0 <= x <= x1 and y0 <= y <= y1
    if inside(w[0], w[1]) or inside(w[2], w[3]):
        return 0.0
    edges = ((x0, y0, x1, y0), (x1, y0, x1, y1), (x1, y1, x0, y1), (x0, y1, x0, y0))
    best = min(min(dist_pt_seg(x, y, *w) for x, y in ((x0, y0), (x1, y0), (x1, y1), (x0, y1))),
               min(dist_pt_seg(w[0], w[1], *e) for e in edges), min(dist_pt_seg(w[2], w[3], *e) for e in edges))
    if any(seg_cross(w, e) for e in edges):
        return 0.0
    return best


def seg_cross(a, b):
    """Interior crossing point of two segments, or None."""
    (x1, y1, x2, y2), (x3, y3, x4, y4) = a, b
    d = (x2 - x1) * (y4 - y3) - (y2 - y1) * (x4 - x3)
    if abs(d) < 1e-9:
        return None
    t = ((x3 - x1) * (y4 - y3) - (y3 - y1) * (x4 - x3)) / d
    u = ((x3 - x1) * (y2 - y1) - (y3 - y1) * (x2 - x1)) / d
    if 0 <= t <= 1 and 0 <= u <= 1:
        return (x1 + t * (x2 - x1), y1 + t * (y2 - y1))
    return None


class DSU:
    def __init__(self):
        self.p = {}

    def find(self, x):
        self.p.setdefault(x, x)
        while self.p[x] != x:
            self.p[x] = self.p[self.p[x]]
            x = self.p[x]
        return x

    def union(self, a, b):
        a, b = self.find(a), self.find(b)
        if a != b:
            self.p[a] = b


class Grid:
    """Uniform spatial hash over bounding boxes."""

    def __init__(self, cell=8.0):
        self.cell = cell
        self.cells = collections.defaultdict(list)

    def add(self, idx, x0, y0, x1, y1):
        c = self.cell
        for i in range(int(x0 // c), int(x1 // c) + 1):
            for j in range(int(y0 // c), int(y1 // c) + 1):
                self.cells[(i, j)].append(idx)

    def query(self, x0, y0, x1, y1):
        c = self.cell
        out = set()
        for i in range(int(x0 // c), int(x1 // c) + 1):
            for j in range(int(y0 // c), int(y1 // c) + 1):
                out.update(self.cells.get((i, j), ()))
        return out


def load(pdf):
    page = fitz.open(pdf)[0]
    wires, stubs, ticks, bodies, dots, blacks, bubbles = [], [], [], [], [], [], []
    rect_bodies = []
    global ORANGE_LINES, BUS_LINES, NC_MARKS
    BUS_LINES = []
    NC_MARKS = []
    ORANGE_LINES = []
    for d in page.get_drawings():
        col = d.get('color')
        fill = d.get('fill')
        items = d['items']
        if same(fill, (1.0, 0.0, 0.0)) and d['type'] == 'fs':
            r = d['rect']
            dots.append(((r.x0 + r.x1) / 2, (r.y0 + r.y1) / 2))
            continue
        if col is None:
            continue
        for it in items:
            if same(col, WIRE) and it[0] == 'l':
                a, b = it[1], it[2]
                if math.hypot(a.x - b.x, a.y - b.y) > 0.05:
                    wires.append((a.x, a.y, b.x, b.y))
            elif same(col, (0.5, 0.25, 0.0)) and it[0] == 'l':
                NC_MARKS.append(((it[1].x + it[2].x) / 2, (it[1].y + it[2].y) / 2))
            elif same(col, (0.0, 0.0, 1.0)) and it[0] == 'l':
                a, b = it[1], it[2]
                if math.hypot(a.x - b.x, a.y - b.y) > 0.05:
                    BUS_LINES.append((a.x, a.y, b.x, b.y))
            elif same(col, STUB):
                if it[0] == 'l':
                    a, b = it[1], it[2]
                    if math.hypot(a.x - b.x, a.y - b.y) <= 0.05:
                        ticks.append((a.x, a.y))
                    else:
                        stubs.append((a.x, a.y, b.x, b.y))
                else:
                    r = d['rect']
                    bubbles.append((r.x0, r.y0, r.x1, r.y1))
            elif same(col, BODY):
                if it[0] == 're':
                    r = it[1]
                    bodies.append((r.x0, r.y0, r.x1, r.y1))
                    rect_bodies.append((r.x0, r.y0, r.x1, r.y1))
                elif it[0] == 'l':
                    a, b = it[1], it[2]
                    bodies.append((min(a.x, b.x), min(a.y, b.y), max(a.x, b.x), max(a.y, b.y)))
                    ORANGE_LINES.append((a.x, a.y, b.x, b.y))
                elif it[0] == 'c':
                    xs = [q.x for q in it[1:]]
                    ys = [q.y for q in it[1:]]
                    bodies.append((min(xs), min(ys), max(xs), max(ys)))
                elif it[0] == 'qu':
                    q = it[1]
                    r = q.rect
                    bodies.append((r.x0, r.y0, r.x1, r.y1))
            elif same(col, BLACK) and it[0] == 'l':
                a, b = it[1], it[2]
                if 0.05 < math.hypot(a.x - b.x, a.y - b.y) < 5:  # skips the sheet frame
                    blacks.append((a.x, a.y, b.x, b.y))
    texts = []
    for b in page.get_text('rawdict')['blocks']:
        for l in b.get('lines', []):
            for s in l['spans']:
                t = ''.join(c['c'] for c in s['chars']).strip()
                if not t:
                    continue
                d = (round(l['dir'][0]), round(l['dir'][1]))
                parts = [(t, tuple(s['bbox']))]
                first, _, rest = t.partition(' ')
                if rest.strip() and REF_RE.match(first):
                    # "IC46B 74LS74" is one span: split reference from type using the glyph boxes
                    cs = s['chars']
                    k = next(i for i, c in enumerate(cs) if c['c'] == ' ')
                    bb1 = (min(c['bbox'][0] for c in cs[:k]), min(c['bbox'][1] for c in cs[:k]), max(c['bbox'][2] for c in cs[:k]), max(c['bbox'][3] for c in cs[:k]))
                    rs = [c for c in cs[k + 1:] if c['c'] != ' ']
                    bb2 = (min(c['bbox'][0] for c in rs), min(c['bbox'][1] for c in rs), max(c['bbox'][2] for c in rs), max(c['bbox'][3] for c in rs))
                    parts = [(first, bb1), (rest.strip(), bb2)]
                for txt, bb in parts:
                    texts.append(dict(text=txt, bbox=bb, color=s['color'], size=round(s['size'], 1), dir=d))
    return wires, stubs, ticks, bodies, bubbles, dots, blacks, texts, rect_bodies


def rect_dist(a, b):
    dx = max(a[0] - b[2], b[0] - a[2], 0)
    dy = max(a[1] - b[3], b[1] - a[3], 0)
    return math.hypot(dx, dy)


def explain(labels, texts, wires, edges):
    """Print the shortest chain of wire joins between the wires carrying two labels."""
    adj = collections.defaultdict(list)
    for a, b, why in edges:
        adj[a].append((b, why))
        adj[b].append((a, why))
    ends = [[t['wire'] for t in texts if t['text'] == lab and t.get('wire') is not None] for lab in labels]
    if not all(ends):
        print(f'--why: label not found on a wire: {[l for l, e in zip(labels, ends) if not e]}', file=sys.stderr)
        return
    best = None
    for src in ends[0]:
        prev = {src: None}
        dq = collections.deque([src])
        while dq:
            u = dq.popleft()
            for v, why in adj[u]:
                if v not in prev:
                    prev[v] = (u, why)
                    dq.append(v)
        for dst in ends[1]:
            if dst in prev:
                path, u = [], dst
                while prev[u]:
                    path.append((u, prev[u][1]))
                    u = prev[u][0]
                if best is None or len(path) < len(best[1]):
                    best = (src, path)
    if best is None:
        print('--why: the two labels are on different wire groups (joined only by name)', file=sys.stderr)
        return
    print(f'--why {labels[0]} -> {labels[1]}: {len(best[1])} joins', file=sys.stderr)
    print('  start', [round(v, 1) for v in wires[best[0]]], file=sys.stderr)
    for u, why in reversed(best[1]):
        print('  via', why, '->', [round(v, 1) for v in wires[u]], file=sys.stderr)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('pdf')
    ap.add_argument('--out', required=True)
    ap.add_argument('--why', nargs=2, metavar='LABEL', help='print the chain of wire joins that ties two net labels together')
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)

    wires, stubs, ticks, bodies, bubbles, dots, blacks, texts, rect_bodies = load(args.pdf)
    diag = []
    nblue = len(wires)
    wires = wires + blacks  # power-symbol stems join like wires; plain labels never attach to them

    # ---- pins: stub with a tick at one end ----
    # Each stub is drawn from the body edge (start) to the connection point (end), then capped
    # with a zero-length stroke. Clock-triangle strokes share the colour but end inside the body.
    rects = [b for b in rect_bodies]
    pins = []  # dict(x,y,ix,iy)
    for (ax, ay, bx, by) in stubs:
        if math.hypot(ax - bx, ay - by) < 2.5:
            # a short axis-aligned stub leaving an inversion bubble is a pin; other short strokes
            # (the diagonals of a clock triangle) are decoration
            axis = abs(ax - bx) < 0.05 or abs(ay - by) < 0.05
            if not (axis and any(rect_dist((ax, ay, ax, ay), bb) < 0.3 for bb in bubbles)):
                continue
        if any(r[0] + 0.3 < bx < r[2] - 0.3 and r[1] + 0.3 < by < r[3] - 0.3 for r in rects):
            continue
        pins.append(dict(x=bx, y=by, ix=ax, iy=ay))

    # ---- part components: body items + bubbles clustered by touching bounding boxes ----
    shapes = [(b, 'body') for b in bodies] + [(b, 'bubble') for b in bubbles]
    sg = Grid(8)
    for i, (b, _) in enumerate(shapes):
        sg.add(i, b[0] - 1, b[1] - 1, b[2] + 1, b[3] + 1)
    sd = DSU()
    for i, (b, _) in enumerate(shapes):
        sd.find(i)
        for j in sg.query(b[0] - 1, b[1] - 1, b[2] + 1, b[3] + 1):
            if j > i and rect_dist(b, shapes[j][0]) < 0.3:
                sd.union(i, j)
    # a pin belongs to the shape its inner end touches
    for p in pins:
        best, bd = None, 1e9
        for j in sg.query(p['ix'] - 2, p['iy'] - 2, p['ix'] + 2, p['iy'] + 2):
            d = rect_dist((p['ix'], p['iy'], p['ix'], p['iy']), shapes[j][0])
            if d < bd:
                best, bd = j, d
        if best is None or bd > 0.6:
            p['comp'] = None
            diag.append(f'pin at {p["x"]:.1f},{p["y"]:.1f} touches no body')
        else:
            p['comp'] = sd.find(best)

    comps = collections.defaultdict(lambda: dict(bbox=None, pins=[]))
    for i, (b, _) in enumerate(shapes):
        c = comps[sd.find(i)]
        c['bbox'] = b if c['bbox'] is None else (min(c['bbox'][0], b[0]), min(c['bbox'][1], b[1]), max(c['bbox'][2], b[2]), max(c['bbox'][3], b[3]))
    for p in pins:
        if p['comp'] is not None:
            comps[p['comp']]['pins'].append(p)
    # Capacitor plates are two separate outlines, each carrying one stub: join such one-pin pairs.
    singles = [k for k, c in comps.items() if len(c['pins']) == 1]
    merged = set()
    for i, k in enumerate(singles):
        if k in merged:
            continue
        best, bd = None, 6.0
        for k2 in singles:
            if k2 != k and k2 not in merged:
                d = rect_dist(comps[k]['bbox'], comps[k2]['bbox'])
                if d < bd:
                    best, bd = k2, d
        if best is not None:
            a, b = comps[k], comps[best]
            a['pins'] += b['pins']
            a['bbox'] = (min(a['bbox'][0], b['bbox'][0]), min(a['bbox'][1], b['bbox'][1]), max(a['bbox'][2], b['bbox'][2]), max(a['bbox'][3], b['bbox'][3]))
            merged.add(best)
            merged.add(k)
            b['pins'] = []
    pinless = [c for k, c in comps.items() if not c['pins']]
    comps = {k: c for k, c in comps.items() if c['pins']}
    # pin extents widen the symbol bbox (labels sit around the whole symbol)
    for c in comps.values():
        for p in c['pins']:
            c['bbox'] = (min(c['bbox'][0], p['x']), min(c['bbox'][1], p['y']), max(c['bbox'][2], p['x']), max(c['bbox'][3], p['y']))

    # ---- classify text ----
    for t in texts:
        r, g, b = (t['color'] >> 16) & 255, (t['color'] >> 8) & 255, t['color'] & 255
        if (r, g, b) == (0, 0, 0):
            t['kind'] = 'pinno' if 2.65 <= t['size'] < 3.0 and re.fullmatch(r'\d{1,2}', t['text']) else 'text'
        elif (r, g, b) == (0xff, 0, 0):
            t['kind'] = 'offpage'
        elif (r, g, b) == (0, 0, 0xcc):
            t['kind'] = 'pinname'
        else:
            t['kind'] = 'other'
        t['cx'] = (t['bbox'][0] + t['bbox'][2]) / 2
        t['cy'] = (t['bbox'][1] + t['bbox'][3]) / 2

    # ---- pin numbers -> pins (greedy by distance, each pin gets one) ----
    pgrid = Grid(6)
    for i, p in enumerate(pins):
        pgrid.add(i, min(p['x'], p['ix']) - 1, min(p['y'], p['iy']) - 1, max(p['x'], p['ix']) + 1, max(p['y'], p['iy']) + 1)
    cands = []
    for ti, t in enumerate(texts):
        if t['kind'] != 'pinno':
            continue
        bb = t['bbox']
        for pi in pgrid.query(bb[0] - 4, bb[1] - 4, bb[2] + 4, bb[3] + 4):
            p = pins[pi]
            x0, x1 = min(p['x'], p['ix']), max(p['x'], p['ix'])
            y0, y1 = min(p['y'], p['iy']), max(p['y'], p['iy'])
            if y1 - y0 < 0.1:  # horizontal stub: the digits sit on the stub, above or below the line
                gap = max(x0 - 0.5 - bb[2], bb[0] - (x1 + 2.0), 0)
                d = min(abs(bb[3] - p['y']), abs(bb[1] - p['y']) + 0.8) + 2 * gap
            else:  # vertical stub: the rotated digits sit beside it, on either side depending on the symbol
                gap_y = max(y0 - 0.5 - bb[3], bb[1] - (y1 + 2.0), 0)
                d = min(abs(bb[0] - 0.45 - p['x']), abs(bb[2] - 0.05 - p['x'])) + 2 * gap_y
            if d < NUMBER_REACH:
                cands.append((d, ti, pi))
    used_t = set()
    for ti, pi in assign(cands, NUMBER_REACH):
        used_t.add(ti)
        pins[pi]['num'] = texts[ti]['text']
    for p in pins:
        p.setdefault('num', '?')
    for ti, t in enumerate(texts):
        if t['kind'] == 'pinno' and ti not in used_t:
            t['kind'] = 'text'  # a bare number that is not a pin: value or label
    for p in pins:
        if 'num' not in p:
            p['num'] = '?'
            diag.append(f'pin stub at {p["x"]:.0f},{p["y"]:.0f} has no pin number')

    # ---- pin names (blue) -> pins ----
    cands = []
    for ti, t in enumerate(texts):
        if t['kind'] != 'pinname':
            continue
        for pi in pgrid.query(t['cx'] - 12, t['cy'] - 12, t['cx'] + 12, t['cy'] + 12):
            p = pins[pi]
            cands.append((math.hypot(t['cx'] - p['ix'], t['cy'] - p['iy']), ti, pi))
    cands.sort()
    used_t, used_p = set(), set()
    for d, ti, pi in cands:
        if ti in used_t or pi in used_p or d > 12:
            continue
        used_t.add(ti)
        used_p.add(pi)
        pins[pi]['name'] = texts[ti]['text']

    # ---- overbars: a thin black horizontal line just above a text span ----
    def overbar(t):
        bb = t['bbox']
        for (ax, ay, bx, by) in blacks:
            if abs(ay - by) < 0.01 and bb[0] - 0.8 <= min(ax, bx) and max(ax, bx) <= bb[2] + 0.8 and bb[1] - 1.4 <= ay <= bb[1] + 0.9 and abs(ax - bx) > 1.5:
                return True
        return False

    # ---- refs and values ----
    # Rotated "A8"/"A9" next to a bus ripper are address-line labels; the A<n> decoupling-capacitor
    # references are all drawn horizontally.
    refs = [t for t in texts if t['kind'] == 'text' and REF_RE.match(t['text'])
            and not (re.match(r'A\d+$', t['text']) and t['dir'] != (1, 0))]
    # Transistors, switches and links are drawn without pin stubs: each lead ends in a lone tick
    # stroke, which is the connection point. Number it from the nearest leftover digit.
    stub_ends = [(x, y) for st in stubs for x, y in ((st[0], st[1]), (st[2], st[3]))]
    orphan_ticks = []
    for t in ticks:
        if not any(math.hypot(t[0] - x, t[1] - y) < 0.2 for x, y in stub_ends) and not any(math.hypot(t[0] - x, t[1] - y) < 0.3 for x, y in orphan_ticks):
            orphan_ticks.append(t)
    leftover = [(ti, t) for ti, t in enumerate(texts) if t['kind'] == 'text' and re.fullmatch(r'\d{1,2}', t['text'])]
    used_ticks = set()

    def add_stubless(pts, b):
        cand = []
        for pi_, (x, y) in enumerate(pts):
            for ti, t in leftover:
                d = rect_dist((x, y, x, y), t['bbox'])
                if d < 3.0:
                    cand.append((d, ti, pi_))
        got = {pi_: ti for ti, pi_ in assign(cand, 3.0)}
        new = []
        for pi_, (x, y) in enumerate(pts):
            p = dict(x=x, y=y, ix=x, iy=y, comp=None)
            p['num'] = texts[got[pi_]]['text'] if pi_ in got else '?'
            if pi_ in got:
                texts[got[pi_]]['kind'] = 'pinno'
                leftover[:] = [(ti, t) for ti, t in leftover if ti != got[pi_]]
            new.append(p)
        pins.extend(new)
        comps[('stubless', len(comps))] = dict(stubless=True, bbox=(min(b[0], min(x for x, _ in pts)), min(b[1], min(y for _, y in pts)), max(b[2], max(x for x, _ in pts)), max(b[3], max(y for _, y in pts))), pins=new)

    # Transistors: a body outline carrying exactly three lone ticks, with a Q reference nearby. The
    # outline of one can touch a neighbouring symbol's, so the ticks are also grouped on their own.
    q_texts = [t for t in texts if t['kind'] == 'text' and re.fullmatch(r'Q\d+', t['text'])]

    def has_q(box):
        return any(rect_dist(box, t['bbox']) < 14 for t in q_texts)

    pd = DSU()
    for i, c in enumerate(pinless):
        pd.find(i)
        for j in range(i + 1, len(pinless)):
            if rect_dist(c['bbox'], pinless[j]['bbox']) < 1.0:
                pd.union(i, j)
    gathered = {}
    for i, c in enumerate(pinless):
        g = gathered.setdefault(pd.find(i), dict(bbox=c['bbox']))
        g['bbox'] = (min(g['bbox'][0], c['bbox'][0]), min(g['bbox'][1], c['bbox'][1]), max(g['bbox'][2], c['bbox'][2]), max(g['bbox'][3], c['bbox'][3]))
    for g in gathered.values():
        b = g['bbox']
        pts = [(x, y) for (x, y) in orphan_ticks if b[0] - 0.6 <= x <= b[2] + 0.6 and b[1] - 0.6 <= y <= b[3] + 0.6 and (x, y) not in used_ticks]
        if len(pts) == 3 and has_q(b):
            add_stubless(pts, b)
            used_ticks.update(pts)
    rest = [t for t in orphan_ticks if t not in used_ticks]
    rd = DSU()
    for i, a_ in enumerate(rest):
        rd.find(i)
        for j in range(i + 1, len(rest)):
            if math.hypot(a_[0] - rest[j][0], a_[1] - rest[j][1]) < 7.5:
                rd.union(i, j)
    rgroups = collections.defaultdict(list)
    for i, t in enumerate(rest):
        rgroups[rd.find(i)].append(t)
    for g in rgroups.values():
        gb = (min(x for x, _ in g), min(y for _, y in g), max(x for x, _ in g), max(y for _, y in g))
        if len(g) == 3 and has_q(gb):
            add_stubless(g, gb)
            used_ticks.update(g)
    # Links and switches: every remaining loose tick belongs to the nearest S / SW / LK reference.
    link_refs = [t for t in texts if t['kind'] == 'text' and re.fullmatch(r'(SW|S|LK)\d+', t['text'])]
    by_link = collections.defaultdict(list)
    for tk in orphan_ticks:
        if tk in used_ticks:
            continue
        best, bd = None, 12.0
        for li, t in enumerate(link_refs):
            d = rect_dist((tk[0], tk[1], tk[0], tk[1]), t['bbox'])
            if d < bd:
                best, bd = li, d
        if best is not None:
            by_link[best].append(tk)
    for li, g in by_link.items():
        if len(g) >= 2:
            add_stubless(g, (min(x for x, _ in g), min(y for _, y in g), max(x for x, _ in g), max(y for _, y in g)))
            used_ticks.update(g)
    leftover = None

    comp_list = [c for c in comps.values() if c['pins']]
    cands_ref = []
    for ci, c in enumerate(comp_list):
        for ri, t in enumerate(refs):
            d = rect_dist(c['bbox'], t['bbox'])
            # transistors, switches and links are the symbols drawn without pin stubs
            if c.get('stubless') != bool(STUBLESS_REF.match(t['text'])) and (c.get('stubless') or re.match(r'^(Q|SW)\d+$', t['text'])):
                continue
            if d < 14:
                cands_ref.append((d, ci, ri))
    used_c, used_r = set(), set()
    for ci, ri in assign(cands_ref, 14.0):
        used_c.add(ci)
        used_r.add(ri)
        comp_list[ci]['ref'] = refs[ri]['text']
        comp_list[ci]['reft'] = refs[ri]
        refs[ri]['is_ref'] = True
    for ci, c in enumerate(comp_list):
        if 'ref' not in c:
            # the relay is drawn as two symbols (coil, contacts) with a single reference
            rl = [o for o in comp_list if o.get('ref') and o['ref'].startswith('RL') and rect_dist(o['bbox'], c['bbox']) < 30]
            if rl:
                c['ref'], c['reft'] = rl[0]['ref'], rl[0]['reft']
                c['secondary'] = True
                continue
            c['ref'] = None
            diag.append(f'symbol at {c["bbox"][0]:.0f},{c["bbox"][1]:.0f} with {len(c["pins"])} pins has no reference')


    # Diodes carry no pin numbers. Name the pins A (anode) and K (cathode) from the symbol: the
    # cathode bar is the longer of the two lines square to the stub axis, and the cathode stub is
    # the one nearer it.
    for c in comp_list:
        if not (c['ref'] and c['ref'].startswith('D') and len(c['pins']) == 2 and all(p['num'] == '?' for p in c['pins'])):
            continue
        b = c['bbox']
        lines_in = [l for l in ORANGE_LINES if b[0] - 0.3 <= min(l[0], l[2]) and max(l[0], l[2]) <= b[2] + 0.3 and b[1] - 0.3 <= min(l[1], l[3]) and max(l[1], l[3]) <= b[3] + 0.3]
        p0, p1 = c['pins']
        vertical = abs(p0['x'] - p0['ix']) < 0.1
        perp = [l for l in lines_in if (abs(l[1] - l[3]) < 0.01 if vertical else abs(l[0] - l[2]) < 0.01) and math.hypot(l[0] - l[2], l[1] - l[3]) > 1.5]
        if not perp:
            continue
        bar = max(perp, key=lambda l: math.hypot(l[0] - l[2], l[1] - l[3]))
        pos = lambda p: (p['iy'] - bar[1]) if vertical else (p['ix'] - bar[0])
        k, a = (p0, p1) if abs(pos(p0)) < abs(pos(p1)) else (p1, p0)
        k['num'], a['num'] = 'K', 'A'
        for p in (k, a):
            p['num_inferred'] = True
    # Anything else unnumbered (relay coil and contacts): number by position and say so.
    for c in comp_list:
        un = [p for p in c['pins'] if p['num'] == '?']
        if un and len(un) == len(c['pins']):
            base = 2 if c.get('secondary') else 0
            for k, p in enumerate(sorted(un, key=lambda q: (q['y'], q['x'])), 1 + base):
                p['num'] = str(k)
            diag.append(f'{c["ref"] or "symbol at %.0f,%.0f" % c["bbox"][:2]}: pins were unnumbered on the drawing; numbered 1..{len(un)} top to bottom')
    for p in pins:
        if p['num'] == '?':
            diag.append(f'pin stub at {p["x"]:.0f},{p["y"]:.0f} has no pin number')

    # values: nearest plain text to the ref that is not itself a ref / wire label
    plain = [t for t in texts if t['kind'] == 'text' and not t.get('is_ref') and not POWER_RE.match(t['text'])
             and not (re.match(r'A\d+$', t['text']) and t['dir'] != (1, 0))]
    vgrid = Grid(8)
    for i, t in enumerate(plain):
        vgrid.add(i, *t['bbox'])
    for c in comp_list:
        if c['ref'] is None or c.get('secondary'):
            continue
        rt = c['reft']
        best, bd = None, 1e9
        for src in (rt['bbox'], None):
            # first the text next to the reference, then anything hugging the symbol (an IC's type
            # is often printed under the body while the reference is above it)
            box = src if src else c['bbox']
            reach = 3.5 if src else 3.0
            for i in vgrid.query(box[0] - 6, box[1] - 6, box[2] + 6, box[3] + 6):
                if plain[i].get('is_value'):
                    continue
                d = rect_dist(box, plain[i]['bbox'])
                if d < bd and d < reach:
                    best, bd = i, d
            if best is not None:
                break
        c['value'] = plain[best]['text'] if best is not None and not c['ref'].startswith('RL') else ''
        if best is not None:
            plain[best]['is_value'] = True

    # A package drawn as several gate symbols may carry its type only in a note such as "2x 7438".
    notes = [t for t in texts if t['kind'] == 'text' and re.fullmatch(r'\d+x\s*\S+', t['text'])]
    for c in comp_list:
        if c['ref'] and c['ref'].startswith('IC') and not c.get('value') and not c.get('secondary'):
            near = [n for n in notes if rect_dist(c['reft']['bbox'], n['bbox']) < 30]
            if near:
                n = min(near, key=lambda q: rect_dist(c['reft']['bbox'], q['bbox']))
                c['value'] = re.sub(r'^\d+x\s*', '', n['text'])
                n['is_value'] = True  # one note can cover both packages it names
    # A cross over a pin end marks it as deliberately unconnected.
    for p in pins:
        p['nc'] = any(math.hypot(p['x'] - x, p['y'] - y) < 2.0 for x, y in NC_MARKS)

    # ---- wire connectivity ----
    wg = Grid(6)
    for i, w in enumerate(wires):
        wg.add(i, min(w[0], w[2]) - 1, min(w[1], w[3]) - 1, max(w[0], w[2]) + 1, max(w[1], w[3]) + 1)
    bus_grid = Grid(8)
    for k, b in enumerate(BUS_LINES):
        bus_grid.add(k, min(b[0], b[2]) - 1, min(b[1], b[3]) - 1, max(b[0], b[2]) + 1, max(b[1], b[3]) + 1)

    def on_bus(x, y):
        return any(dist_pt_seg(x, y, *BUS_LINES[k]) < 0.6 for k in bus_grid.query(x - 1, y - 1, x + 1, y + 1))
    # wire-coloured strokes lying along a bus line are part of the bus drawing, not nets
    bus_like = {i for i, w in enumerate(wires[:nblue]) if on_bus(w[0], w[1]) and on_bus(w[2], w[3]) and on_bus((w[0] + w[2]) / 2, (w[1] + w[3]) / 2)}
    live_ends = []
    for i, w in enumerate(wires):
        diagonal = abs(w[0] - w[2]) > 0.1 and abs(w[1] - w[3]) > 0.1
        live_ends.append([] if i in bus_like else [(x, y) for x, y in ((w[0], w[1]), (w[2], w[3])) if not (diagonal and on_bus(x, y))])
    wd = DSU()
    wire_edges = []
    for i, w in enumerate(wires):
        wd.find(i)
        for j in wg.query(min(w[0], w[2]) - 1, min(w[1], w[3]) - 1, max(w[0], w[2]) + 1, max(w[1], w[3]) + 1):
            if j <= i or i in bus_like or j in bus_like:
                continue
            v = wires[j]
            if i >= nblue and j >= nblue:
                # power symbol: the stem's end lands on the middle of its bar. Two neighbouring bars
                # may touch end to end without being the same rail.
                def tee(a, b):
                    return any(dist_pt_seg(x, y, *b) < EPS and math.hypot(x - b[0], y - b[1]) > EPS and math.hypot(x - b[2], y - b[3]) > EPS
                               for x, y in ((a[0], a[1]), (a[2], a[3])))
                if tee(w, v) or tee(v, w):
                    wd.union(i, j)
                continue
            # an end of one on the other (end-to-end and T joins); a ripper's end on a bus joins nothing
            if any(dist_pt_seg(x, y, *v) < EPS for x, y in live_ends[i]) or \
               any(dist_pt_seg(x, y, *w) < EPS for x, y in live_ends[j]):
                wd.union(i, j)
                wire_edges.append((i, j, 'touch'))
    # junction dots join everything they sit on (covers dotted crossings)
    for (x, y) in dots:
        on = [j for j in wg.query(x - 1, y - 1, x + 1, y + 1) if dist_pt_seg(x, y, *wires[j]) < EPS]
        for j in on[1:]:
            wd.union(on[0], j)
            wire_edges.append((on[0], j, f'junction dot at {x:.1f},{y:.1f}'))
    # A few wires are drawn with a visible break (1.5pt or so, nothing in it) where the export dropped a
    # piece; collinear facing ends that close are one wire.
    bridged = 0
    for i in range(nblue):
        if i in bus_like:
            continue
        w = wires[i]
        for ex, ey in live_ends[i]:
            for j in wg.query(ex - 2.6, ey - 2.6, ex + 2.6, ey + 2.6):
                if j <= i or j >= nblue or j in bus_like:
                    continue
                for fx, fy in live_ends[j]:
                    gap = math.hypot(ex - fx, ey - fy)
                    if not 0.5 < gap <= 2.0 or (abs(ex - fx) > 0.1 and abs(ey - fy) > 0.1):
                        continue
                    if wd.find(i) != wd.find(j):
                        wd.union(i, j)
                        wire_edges.append((i, j, f'across a break at {(ex + fx) / 2:.1f},{(ey + fy) / 2:.1f}'))
                        bridged += 1
                        diag.append(f'info: wire break bridged at {(ex + fx) / 2:.1f},{(ey + fy) / 2:.1f}')
    diag.append(f'info: {bridged} wire breaks bridged')
    # undotted crossings are left unjoined, but noted: they are where a missed dot would hide
    undotted = 0
    dot_grid = Grid(4)
    for i, (x, y) in enumerate(dots):
        dot_grid.add(i, x, y, x, y)
    for i, w in enumerate(wires):
        for j in wg.query(min(w[0], w[2]) - 1, min(w[1], w[3]) - 1, max(w[0], w[2]) + 1, max(w[1], w[3]) + 1):
            if j <= i or j >= nblue or i >= nblue or wd.find(i) == wd.find(j):
                continue
            c = seg_cross(w, wires[j])
            if c and not any(math.hypot(dots[k][0] - c[0], dots[k][1] - c[1]) < 0.8 for k in dot_grid.query(c[0] - 1, c[1] - 1, c[0] + 1, c[1] + 1)):
                undotted += 1
    diag.append(f'info: {undotted} undotted wire crossings left unjoined (deliberate)')

    # pin ends join the wire they land on, and any other pin end at the same spot
    for pi, p in enumerate(pins):
        hits = [j for j in wg.query(p['x'] - 1, p['y'] - 1, p['x'] + 1, p['y'] + 1) if dist_pt_seg(p['x'], p['y'], *wires[j]) < EPS]
        p['wires'] = hits
        for j in hits[1:]:
            wd.union(hits[0], j)
            wire_edges.append((hits[0], j, f'pin end at {p["x"]:.1f},{p["y"]:.1f}'))
    pin_pin = DSU()
    for pi, p in enumerate(pins):
        for pj in pgrid.query(p['x'] - 1, p['y'] - 1, p['x'] + 1, p['y'] + 1):
            if pj > pi and math.hypot(pins[pj]['x'] - p['x'], pins[pj]['y'] - p['y']) < EPS:
                pin_pin.union(pi, pj)

    # node ids: a wire component, or a pin-pin group with no wire
    def node_of_pin(pi):
        p = pins[pi]
        if p['wires']:
            return ('w', wd.find(p['wires'][0]))
        return ('p', pin_pin.find(pi))

    nd = DSU()
    for pi, p in enumerate(pins):
        n = node_of_pin(pi)
        nd.find(n)
        for pj in pgrid.query(p['x'] - 1, p['y'] - 1, p['x'] + 1, p['y'] + 1):
            if pj != pi and math.hypot(pins[pj]['x'] - p['x'], pins[pj]['y'] - p['y']) < EPS:
                nd.union(n, node_of_pin(pj))
    for i in range(len(wires)):
        nd.find(('w', wd.find(i)))

    # ---- net labels ----
    labelled = collections.defaultdict(set)  # node -> label names
    for t in texts:
        if t['kind'] not in ('text', 'offpage') or t.get('is_ref') or t.get('is_value'):
            continue
        name = t['text']
        bb = t['bbox']
        is_power = bool(POWER_RE.match(name))
        reach = 3.5 if is_power else LABEL_REACH
        if t['kind'] == 'offpage':
            # off-page connector: the label sits a few points past the arrow, which ends on the wire
            cands = []
            for j in wg.query(bb[0] - 8, bb[1] - 8, bb[2] + 8, bb[3] + 8):
                if j >= nblue:
                    continue
                w = wires[j]
                for x, y in ((w[0], w[1]), (w[2], w[3])):
                    # the arrow sits level with its label, or directly above / below it
                    level = abs(y - t['cy']) if t['dir'] == (1, 0) else abs(x - t['cx'])
                    column = max(bb[0] - 1.5 - x, x - bb[2] - 1.5, 0)
                    if level > 2.0 and column > 0:
                        continue
                    cands.append((rect_dist((x, y, x, y), bb) + 2 * min(level, column), level, column, j))
            if t['dir'] == (1, 0) and any(c[1] <= 1.0 and c[2] <= 3.0 for c in cands):
                # an arrow tip level with the label and abutting it (either end: the label can sit
                # before or after the arrow) beats a wire end that merely lies under or over it (the
                # corner of a neighbouring pin's wire can sit inside the label's span)
                cands = [c for c in cands if c[1] <= 1.0 and c[2] <= 3.0]
            best, bd = None, 1e9
            for d, _, _, j in cands:
                if d < bd:
                    best, bd = j, d
            t['wire'] = best if best is not None and bd <= 8.0 else None
            continue
        if not is_power and NOT_A_LABEL.match(name):
            t['wire'] = None
            continue
        t['wire'] = None
        # rail names normally sit on black power symbols; a few are written like ordinary labels on a
        # blue wire (IC22.2/.4, IC42.1), so a rail name that finds no symbol is tried as a plain label
        for as_power in ((True, False) if is_power else (False,)):
            reach_ = reach if as_power else LABEL_REACH
            best, bd = None, 1e9
            for j in wg.query(bb[0] - reach_, bb[1] - reach_, bb[2] + reach_, bb[3] + reach_):
                if (j >= nblue) != as_power:
                    continue  # power symbols are black strokes, every other label sits on a blue wire
                w = wires[j]
                # distance from the text box (not just its centre) to the wire
                if as_power:
                    d = dist_pt_seg(t['cx'], t['cy'], *w)
                elif abs(w[1] - w[3]) < 0.01:
                    # horizontal wire: the label's baseline sits on it (or, for a few, its top hangs below it)
                    gap = max(min(w[0], w[2]) - bb[2], bb[0] - max(w[0], w[2]), 0)
                    d = min(abs(bb[3] - w[1]), abs(bb[1] - w[1]) + 0.3) + gap
                elif t['dir'] == (0.0, 1.0) and abs(w[0] - w[2]) < 0.01:
                    # vertical wire, upward-reading label: the text's left edge sits on the wire, so
                    # of two wires within the text's width the one at that edge is the label's
                    gap = max(min(w[1], w[3]) - bb[3], bb[1] - max(w[1], w[3]), 0)
                    d = abs(w[0] - bb[0]) + gap
                else:
                    d = seg_rect_dist(w, bb)
                if d < bd:
                    best, bd = j, d
            if best is not None and bd <= reach_:
                t['wire'] = best
                break
    # label text => net name
    for t in texts:
        if t.get('wire') is None:
            continue
        n = nd.find(('w', wd.find(t['wire'])))
        nm = t['text']
        if t['kind'] != 'offpage' and not POWER_RE.match(nm) and overbar(t):
            nm = '/' + nm
        labelled[n].add(nm)
        t['used'] = True

    if args.why:
        explain(args.why, texts, wires, wire_edges)

    # ---- name nets ----
    members = collections.defaultdict(list)  # node root -> [(ref, pin)]
    for ci, c in enumerate(comp_list):
        for p in c['pins']:
            p['ref'] = re.sub(r'^([A-Z]+\d+)[A-Z]$', r'\1', c['ref']) if c['ref'] else f'?{ci}'
    for pi, p in enumerate(pins):
        if 'ref' not in p:
            p['ref'] = '?'
        members[nd.find(node_of_pin(pi))].append(pi)

    name_dsu = DSU()  # merge nodes carrying the same label (labels are global)
    by_label = {}
    for n, names in labelled.items():
        for nm in names:
            if nm in by_label:
                name_dsu.union(n, by_label[nm])
            else:
                by_label[nm] = n
    # nodes sharing a global name
    final = collections.defaultdict(lambda: dict(names=set(), pins=[]))
    for n, names in labelled.items():
        final[name_dsu.find(n)]['names'] |= names
    for n, pl in members.items():
        final[name_dsu.find(n)]['pins'] += pl
    # nodes that carry a label but no pins still appear in diagnostics via names
    nets = {}
    auto = 0
    ordered = sorted(final.items(), key=lambda kv: (min((pins[p]['y'], pins[p]['x']) for p in kv[1]['pins']) if kv[1]['pins'] else (1e9, 1e9)))
    for root, info in ordered:
        names = sorted(info['names'])
        if names:
            # prefer a name without punctuation noise; report disagreements
            nm = names[0]
            if len(names) > 1:
                diag.append(f'net {nm}: carries several labels {names}')
        else:
            auto += 1
            nm = f'_N{auto:03d}'
        nets[nm] = sorted(info['pins'], key=lambda i: (pins[i]['ref'], pins[i]['num']))

    ic_type = {}
    for c in comp_list:
        if c['ref'] and c.get('value'):
            ic_type[re.sub(r'^([A-Z]+\d+)[A-Z]$', r'\1', c['ref'])] = c['value']

    # ---- diagnostics per net ----
    def pkey(i):
        return f'{pins[i]["ref"]}.{pins[i]["num"]}'
    for nm, pl in nets.items():
        auto_name = nm.startswith('_N')
        npins = len(pl)
        active = [i for i in pl if not re.match(r'^(R|C|D)\d', pins[i]['ref'])]
        if npins == 0:
            continue
        if npins == 1:
            if not pins[pl[0]]['nc']:
                diag.append(f'dangling: {nm} has one pin ({pkey(pl[0])}{" " + pins[pl[0]]["name"] if pins[pl[0]].get("name") else ""})')
            continue
        elif auto_name and not active:
            diag.append(f'passive-only: {nm} joins {" ".join(pkey(i) for i in pl)}')
        if auto_name:
            outs = [i for i in pl if OUTPUT_NAME.match(pins[i].get('name', '')) and pins[i]['ref'].startswith('IC')]
            if len({pins[i]['ref'] for i in outs}) >= 2:
                diag.append(f'multiple-outputs: {nm} joins outputs {" ".join(pkey(i) + "(" + pins[i]["name"] + ")" for i in outs)}')
        # both ends of the same part on the same net (a buffer shorted to itself)
        byref = collections.defaultdict(list)
        for i in pl:
            byref[pins[i]['ref']].append(i)
        for ref, lst in byref.items():
            if len(lst) >= 2 and ref.startswith('IC'):
                nums = [pins[i]['num'] for i in lst]
                names = [pins[i].get('name', '') for i in lst]
                # An input tied to an output of the same package is the tell-tale of a misread buffer
                # (the ZX80's 74LS365 read as "both ends on the same bus"). Counters and flip-flops
                # legitimately feed an output back, so they are left out.
                ins = [n for n in names if re.match(r'^(A|B|D|I|IN|CLK)\d*$', n or '')]
                outs = [n for n in names if OUTPUT_NAME.match(n or '')]
                a_side = [n for n in names if re.fullmatch(r'A\d', n or '')]
                b_side = [n for n in names if re.fullmatch(r'B\d', n or '')]
                if (ins and outs and not SEQUENTIAL.match(ic_type.get(ref, ''))) or (a_side and b_side):
                    diag.append(f'suspicious: {nm} ties input and output pins of {ref} together ({",".join(sorted(nums))})')

    unattached = [t for t in texts if t['kind'] in ('text', 'offpage') and not t.get('used') and not t.get('is_ref') and not t.get('is_value')]
    for t in unattached:
        diag.append(f'label "{t["text"]}" at {t["cx"]:.0f},{t["cy"]:.0f} not on any wire')

    # ---- write ----
    comps_out = {}
    for c in comp_list:
        ref = c['ref']
        if ref is None:
            continue
        m = re.match(r'^([A-Z]+)(\d+)([A-Z]?)$', ref)
        base = m.group(1) + m.group(2)
        e = comps_out.setdefault(base, dict(values=set(), pins=0))
        if c.get('value'):
            e['values'].add(c['value'])
        e['pins'] += len(c['pins'])

    def ref_sort(r):
        m = re.match(r'^([A-Z?]+)(\d*)', r)
        return (m.group(1), int(m.group(2) or 0))

    lines = ['# BBC Micro Issue 7 netlist, raw extraction of "bbc_micro_mainboard_iss7_20210909.pdf" (amb5l).',
             '# Generated by tools/bbc-netlist/extract.py from the PDF vector geometry + text layer. No corrections applied.',
             '# Format: COMPONENT lines: ref | type | value.  NET lines: netname: ref.pin ref.pin ...',
             '# A leading / means the label is overbarred (active low). Auto-named nets start with _N.',
             '# Gate packages are drawn as separate symbols that share an IC number; sections are told apart by pin number.',
             '# Bus connectivity (A0-A15, D0-D7, ...) is by label, not by wire.',
             '#', '## COMPONENTS']
    for base in sorted(comps_out, key=ref_sort):
        v = comps_out[base]['values']
        val = ' / '.join(sorted(v))
        lines.append(f'{base} | {val} |' if base.startswith('IC') else f'{base} | | {val}')
    lines.append('')
    lines.append('## NETS')
    for nm in sorted(nets, key=lambda s: (s.startswith('_N'), s.lstrip('/').lower())):
        pl = nets[nm]
        if not pl:
            continue
        lines.append(f'{nm}: ' + ' '.join(sorted({pkey(i) for i in pl}, key=lambda s: (ref_sort(s.split('.')[0]), s))))
    lines.append('')
    lines.append('## DIAGNOSTICS (read before trusting a net)')
    lines += [f'# {d}' for d in diag]
    open(os.path.join(args.out, 'netlist.txt'), 'w').write('\n'.join(lines) + '\n')
    open(os.path.join(args.out, 'diagnostics.txt'), 'w').write('\n'.join(diag) + '\n')
    pj = []
    for nm, pl in nets.items():
        for i in pl:
            p = pins[i]
            pj.append(dict(ref=p['ref'], pin=p['num'], name=p.get('name', ''), nc=p['nc'], net=nm, x=round(p['x'], 2), y=round(p['y'], 2)))
    json.dump(pj, open(os.path.join(args.out, 'pins.json'), 'w'), indent=0)
    print(f'{len(wires)} wires, {len(pins)} pins, {len(comp_list)} symbols, {len(nets)} nets, {len(diag)} diagnostics', file=sys.stderr)


if __name__ == '__main__':
    main()
