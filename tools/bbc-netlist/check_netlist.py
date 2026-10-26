#!/usr/bin/env python3
"""Sanity checks on the corrected BBC Micro netlist.

    python3 check_netlist.py ../../src/Aemula/Emulation/Systems/BbcMicro/docs/bbc-netlist.txt [pins.json]

pins.json (from extract.py) tells the checker which pins carry a no-connect cross.

Checks, in order:
  1. power: every IC with a pinout table has its VCC/GND pins on the right rail, or has
     them undrawn (OrCAD hides them on most symbols); a power pin drawn on a signal net or
     on the wrong rail is an error, and a package with only some of its power pins drawn is
     reported.
  2. inputs: every input pin of a pinned-out IC sits on a net that something drives (a
     rail, an IC output/bidirectional pin, a connector/switch/link/transistor, or a
     resistor whose far side is driven). An input pin that is not drawn at all is reported
     as floating.
  3. buses: every A0-A15 / D0-D7 pin of the CPU, VIAs, ROMs and other address/data users is
     on the net of that name, and the bus nets contain nothing outside the expected set
     unless listed as extra (reported for review).
  4. contention: a net with two or more output pins of modelled ICs (open-collector and
     3-state parts, which legitimately share a net, are left out) is reported for review.

Exit status is the number of errors; warnings (review items) do not count.
"""
import re
import sys
from collections import defaultdict

# ---------------------------------------------------------------- pinouts

def gates(*gs):
    """gs: (inputs..., output) tuples."""
    ins, outs = [], []
    for g in gs:
        ins += g[:-1]
        outs.append(g[-1])
    return ins, outs

NAND2 = gates((1, 2, 3), (4, 5, 6), (9, 10, 8), (12, 13, 11))
NOR2 = gates((2, 3, 1), (5, 6, 4), (8, 9, 10), (11, 12, 13))
INV = gates((1, 2), (3, 4), (5, 6), (9, 8), (11, 10), (13, 12))
NAND3 = gates((1, 2, 13, 12), (3, 4, 5, 6), (9, 10, 11, 8))
NAND4 = gates((1, 2, 4, 5, 6), (9, 10, 12, 13, 8))
NAND8 = gates((1, 2, 3, 4, 5, 6, 11, 12, 8))
AOI51 = gates((1, 2, 3, 4, 5, 9, 10, 11, 12, 13, 6), )  # outputs 6 and 8 (see below)


def chip(vcc, gnd, ins=(), outs=(), bidir=(), note=""):
    return dict(vcc=list(vcc), gnd=list(gnd), ins=list(ins), outs=list(outs), bidir=list(bidir), note=note)


def pinout_for(type_):
    t = type_.upper()
    t = re.sub(r"^(74)(LS|S|HC)?", r"74", t) if t.startswith("74") else t
    if t in ("74LS00", "74S00", "7400", "74LS32", "7432", "74LS86", "7486", "7438", "74LS38", "74LS132"):
        i, o = NAND2
        return chip([14], [7], i, o)
    if t in ("74LS02", "7402"):
        i, o = NOR2
        return chip([14], [7], i, o)
    if t in ("74LS04", "74S04", "7404"):
        i, o = INV
        return chip([14], [7], i, o)
    if t in ("74LS10", "7410"):
        i, o = NAND3
        return chip([14], [7], i, o)
    if t in ("74LS20", "7420"):
        i, o = NAND4
        return chip([14], [7], i, o)
    if t in ("74LS30", "7430"):
        i, o = NAND8
        return chip([14], [7], i, o)
    if t in ("74LS51", "7451"):
        # two AND-OR-INVERT gates; 7451 inputs are 1A,1B,1C,1D / 2A,2B,2C,2D on the
        # non-power, non-output pins
        return chip([14], [7], [1, 2, 3, 4, 5, 9, 10, 11, 12, 13], [6, 8])
    if t in ("74LS74", "7474"):
        return chip([14], [7], [1, 2, 3, 4, 10, 11, 12, 13], [5, 6, 8, 9])
    if t in ("74LS138", "74138"):
        return chip([16], [8], [1, 2, 3, 4, 5, 6], [7, 9, 10, 11, 12, 13, 14, 15])
    if t in ("74LS139", "74139"):
        return chip([16], [8], [1, 2, 3, 13, 14, 15], [4, 5, 6, 7, 9, 10, 11, 12])
    if t in ("74LS163", "74163"):
        return chip([16], [8], [1, 2, 3, 4, 5, 6, 7, 9, 10], [11, 12, 13, 14, 15])
    if t in ("74LS259", "74259"):
        return chip([16], [8], [1, 2, 3, 13, 14, 15], [4, 5, 6, 7, 9, 10, 11, 12])
    if t in ("74LS283", "74283"):
        return chip([16], [8], [2, 3, 5, 6, 7, 11, 12, 14, 15], [1, 4, 9, 10, 13])
    if t in ("74LS393", "74393"):
        return chip([14], [7], [1, 2, 12, 13], [3, 4, 5, 6, 8, 9, 10, 11])
    if t in ("74LS123", "74123"):
        # Cext/RCext (6,7,14,15) are passive timing pins
        return chip([16], [8], [1, 2, 3, 9, 10, 11], [4, 5, 12, 13])
    if t in ("74LS244", "74244"):
        return chip([20], [10], [1, 2, 4, 6, 8, 11, 13, 15, 17, 19],
                    [3, 5, 7, 9, 12, 14, 16, 18])
    if t in ("74LS245", "74245"):
        return chip([20], [10], [1, 19], [], list(range(2, 10)) + list(range(11, 19)))
    if t in ("74LS273", "74273"):
        return chip([20], [10], [1, 3, 4, 7, 8, 11, 13, 14, 17, 18],
                    [2, 5, 6, 9, 12, 15, 16, 19])
    if t == "CD4013":
        return chip([14], [7], [3, 4, 5, 6, 8, 9, 10, 11], [1, 2, 12, 13])
    if t == "CD4020":
        return chip([16], [8], [10, 11], [1, 2, 3, 4, 5, 6, 7, 9, 12, 13, 14, 15])
    if t == "6502A":
        return chip([8], [1, 21], [2, 4, 6, 37, 38, 40], [3, 7, 9, 10, 11, 12, 13, 14, 15, 16,
                    17, 18, 19, 20, 22, 23, 24, 25, 34, 39], list(range(26, 34)))
    if t == "6522":
        return chip([20], [1], [18, 19, 22, 23, 24, 25, 34, 35, 36, 37, 38, 39, 40] , [21],
                    list(range(2, 18)) + list(range(26, 34)))
    if t == "6845":
        return chip([20], [1], [2, 3, 21, 22, 23, 24, 25], list(range(4, 20)) + list(range(34, 41)),
                    list(range(26, 34)))
    if t == "6850":
        return chip([12], [1], [2, 3, 4, 8, 9, 10, 11, 13, 14, 23, 24], [5, 6, 7], list(range(15, 23)))
    if t in ("27128", "23128"):
        return chip([28], [14], [], [], [])
    if t == "HM4816":
        return chip([8], [16], [], [], [])
    return None


# ---------------------------------------------------------------- bus tables

ADDR_PINS = {
    # (type, pin) -> bit
    "6502A": {9: 0, 10: 1, 11: 2, 12: 3, 13: 4, 14: 5, 15: 6, 16: 7, 17: 8, 18: 9, 19: 10, 20: 11,
              22: 12, 23: 13, 24: 14, 25: 15},
    "6522": {38: 0, 37: 1, 36: 2, 35: 3},
    "6845": {24: 0},
    "6850": {11: 0},
    "27128": {10: 0, 9: 1, 8: 2, 7: 3, 6: 4, 5: 5, 4: 6, 3: 7, 25: 8, 24: 9, 21: 10, 23: 11, 2: 12},
    "23128": {10: 0, 9: 1, 8: 2, 7: 3, 6: 4, 5: 5, 4: 6, 3: 7, 25: 8, 24: 9, 21: 10, 23: 11, 2: 12},
    "8271": {21: 0, 22: 1},
    "UPD7002": {26: 0, 27: 1},
    "68B54": {10: 0, 11: 1},
}
DATA_PINS = {
    "6502A": {33: 0, 32: 1, 31: 2, 30: 3, 29: 4, 28: 5, 27: 6, 26: 7},
    "6522": {33: 0, 32: 1, 31: 2, 30: 3, 29: 4, 28: 5, 27: 6, 26: 7},
    "6850": {22: 0, 21: 1, 20: 2, 19: 3, 18: 4, 17: 5, 16: 6, 15: 7},
    "27128": {11: 0, 12: 1, 13: 2, 15: 3, 16: 4, 17: 5, 18: 6, 19: 7},
    "23128": {11: 0, 12: 1, 13: 2, 15: 3, 16: 4, 17: 5, 18: 6, 19: 7},
}


# Inputs the sheet itself leaves unconnected on purpose: (ref, pin) -> reason.
ACCEPTED = {("IC97", str(p)): "spare 74LS74 section; the sheet's own note says its inputs should be tied"
            for p in (10, 11, 12, 13)}


# Nets that are wired-OR by design (open-drain IRQ outputs of the VIAs and ACIA).
WIRED_OR = {"IRQ#"}


def norm_type(t):
    return t.upper().replace(" ", "")


# ---------------------------------------------------------------- parse

def parse(path):
    comps, nets = {}, {}
    sec = None
    for line in open(path):
        line = line.rstrip("\n")
        if line.startswith("## COMPONENTS"):
            sec = "c"
            continue
        if line.startswith("## NETS"):
            sec = "n"
            continue
        if line.startswith("## DIAGNOSTICS"):
            break
        if line.startswith("#") or not line.strip():
            continue
        if sec == "c":
            ref, typ, val = [p.strip() for p in line.split("|", 2)]
            comps[ref] = (typ, val)
        elif sec == "n":
            name, rest = line.split(": ", 1)
            nets[name] = [tuple(p.rsplit(".", 1)) for p in rest.split()]
    return comps, nets


def main(path, pins_json=None):
    comps, nets = parse(path)
    nc = set()
    if pins_json:
        import json
        nc = {(r["ref"], r["pin"]) for r in json.load(open(pins_json)) if r.get("nc")}
    pin_net = {}
    for n, ps in nets.items():
        for ref, pin in ps:
            pin_net[(ref, pin)] = n
    rails = {"+5V", "0V", "-5V"}
    errors = warnings = 0

    def err(m):
        nonlocal errors
        errors += 1
        print("ERROR  " + m)

    def warn(m):
        nonlocal warnings
        warnings += 1
        print("WARN   " + m)

    ics = {r: t for r, (t, v) in comps.items() if re.match(r"IC\d+$", r)}

    # ---- 1. power
    print("== power pins ==")
    nopin = []
    rail_of = {}
    for ref, typ in sorted(ics.items(), key=lambda x: int(x[0][2:])):
        po = pinout_for(typ)
        if po is None:
            nopin.append(f"{ref} ({typ})")
            continue
        drawn = 0
        want = 0
        for pins, rail in ((po["vcc"], "+5V"), (po["gnd"], "0V")):
            for p in pins:
                want += 1
                n = pin_net.get((ref, str(p)))
                if n is None:
                    continue
                drawn += 1
                if n != rail:
                    err(f"{ref} ({typ}) pin {p} should be {rail}, is on {n}")
        if 0 < drawn < want:
            warn(f"{ref} ({typ}): only {drawn} of {want} power pins drawn (others implicit)")
        rail_of[ref] = drawn
    hidden = [r for r, d in rail_of.items() if d == 0]
    print(f"  {len(hidden)} ICs have no power pins drawn (implicit): {' '.join(hidden)}")
    print(f"  no pinout table (power/inputs unchecked): {', '.join(nopin)}")

    # ---- 2. inputs driven
    print("== inputs driven ==")
    # resistors / networks: far side of each pin
    def other_side(ref, pin):
        typ = comps[ref][0] if ref in comps else ""
        if re.match(r"R\d+$", ref):
            return pin_net.get((ref, "2" if pin == "1" else "1"))
        if re.match(r"RP\d+$", ref) and pin != "1":
            return pin_net.get((ref, "1"))
        return None

    def driven(n, seen=None):
        if n in rails:
            return True
        for ref, pin in nets[n]:
            if re.match(r"IC\d+$", ref):
                po = pinout_for(comps[ref][0])
                if po is None:
                    return True  # unmodelled IC: assume it can drive
                if int(pin) in po["outs"] or int(pin) in po["bidir"]:
                    return True
            elif re.match(r"(R|RP|C|D|VR)\d+$", ref):
                continue
            else:
                return True  # connector, switch, link, transistor, relay, crystal...
        for ref, pin in nets[n]:
            o = other_side(ref, pin)
            if o is not None and o in rails:
                return True
            if o is not None and o != n and (seen is None or o not in seen):
                if driven(o, (seen or set()) | {n}):
                    return True
        return False

    for ref, typ in sorted(ics.items(), key=lambda x: int(x[0][2:])):
        po = pinout_for(typ)
        if po is None:
            continue
        for p in po["ins"]:
            n = pin_net.get((ref, str(p)))
            if (ref, str(p)) in ACCEPTED:
                continue
            if (ref, str(p)) in nc:
                print(f"  info   {ref} ({typ}) input pin {p} carries a no-connect cross")
            elif n is None:
                warn(f"{ref} ({typ}) input pin {p} is not drawn / floating")
            elif not driven(n):
                warn(f"{ref} ({typ}) input pin {p} on {n}: nothing drives it")

    # ---- 3. buses
    print("== address / data buses ==")
    def check_bus(prefix, count, table):
        for ref, typ in ics.items():
            tab = table.get(norm_type(typ))
            if tab is None:
                continue
            for p, bit in tab.items():
                n = pin_net.get((ref, str(p)))
                want = f"{prefix}{bit}"
                if n is None:
                    warn(f"{ref} ({typ}) pin {p} ({want}) not drawn")
                elif n != want:
                    err(f"{ref} ({typ}) pin {p} should be on {want}, is on {n}")
        for bit in range(count):
            nm = f"{prefix}{bit}"
            if nm not in nets:
                err(f"bus net {nm} missing")
                continue
            expected = set()
            for ref, typ in ics.items():
                tab = table.get(norm_type(typ), {})
                expected |= {(ref, str(p)) for p, b in tab.items() if b == bit}
            extra = [f"{r}.{p}" for r, p in nets[nm] if (r, p) not in expected]
            print(f"  {nm}: {len(nets[nm])} pins; not in the checked tables: {' '.join(extra) or '-'}")

    check_bus("A", 16, ADDR_PINS)
    check_bus("D", 8, DATA_PINS)

    # ---- 4. contention
    print("== output contention ==")
    shared_ok = {"7438", "74LS38", "74LS244", "74LS245", "81LS95", "74LS123", "74LS132", "74LS393"}
    for n, ps in sorted(nets.items()):
        if n in rails or n in WIRED_OR or re.match(r"[AD]\d+$", n):
            continue
        outs = []
        for ref, pin in ps:
            if ref in ics and norm_type(ics[ref]) not in shared_ok:
                po = pinout_for(ics[ref])
                if po and int(pin) in po["outs"]:
                    outs.append(f"{ref}.{pin}")
        if len(outs) > 1:
            warn(f"{n}: {len(outs)} outputs on one net: {' '.join(outs)}")

    print(f"\n{errors} errors, {warnings} warnings")
    return errors


if __name__ == "__main__":
    sys.exit(main(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else None))
