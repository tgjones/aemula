#!/usr/bin/env node
// Records what jsbeeb's SAA5050 shows for pages of character codes, for Saa5050ChipJsbeebTests to replay.
//
//   node teletext.mjs <output file>
//
// Fixture lines (every value is hex):
//   page <id> <ra0> <flashFields> <codes of the first row ...>
//       starts a page: <flashFields> DEW pulses have gone by, then the field starts with the 6845's row
//       address bit 0 at <ra0>; every row of the page is then given as
//   row <codes>                           the 7-bit character code of each cell of a row of text
//   line <cell> <cell> ...                the next scan line of the page's current row, one <cell> per
//                                         character: <12 half-dots, bit 0 first><foreground><background>
import { writeFileSync } from "node:fs";
import { Teletext } from "jsbeeb/src/teletext.js";

const hex = (value, digits = 1) => value.toString(16).padStart(digits, "0");

// The 32-bit pixel word of a glyph row is a sum of fixed patterns, one per half-dot, so it can be turned
// back into the half-dots that made it.
const patterns = [0x7, 0x14, 0x34, 0xe0, 0x280, 0x680, 0x1c00, 0x5000, 0xd000, 0x38000, 0xa0000, 0x1a0000];
const halfDotsOf = new Map();
for (let dots = 0; dots < 4096; dots++) {
    let word = 0;
    for (let i = 0; i < 12; i++) if (dots & (1 << i)) word = (word + (1 << i) * patterns[i]) >>> 0;
    if (halfDotsOf.has(word)) throw new Error("half-dot patterns are ambiguous");
    halfDotsOf.set(word, dots);
}

const lines = [];

function record(id, rows, ra0, flashFields) {
    const t = new Teletext();
    for (let i = 0; i < flashFields; i++) {
        t.setDEW(true);
        t.setDEW(false);
    }

    t.setDEW(true);
    t.setDEW(false);
    t.setRA0(ra0 !== 0);

    lines.push(`page ${id} ${ra0} ${flashFields}`);
    for (const row of rows) {
        lines.push(`row ${row.map((code) => hex(code, 2)).join(" ")}`);
        for (let scan = 0; scan < 10; scan++) {
            const cells = [];
            for (const code of row) {
                t.dataQueue[0] = code;
                t.advance();

                const scanline = t.glyphScanline();
                const blank = t.cellConceal || (t.cellFlash && t.hideFlashing) || (t.secondHalfOfDouble && !t.dbl);
                const word = blank ? 0 : t.curGlyphs[t.cellGlyphIndex + scanline];
                const dots = blank ? 0 : halfDotsOf.get(word >>> 0);
                if (dots === undefined) throw new Error("unknown pixel word " + word);
                cells.push(`${hex(dots, 3)}${hex((t.cellPalette >> 2) & 7)}${hex(t.cellPalette >> 5)}`);
            }

            lines.push(`line ${cells.join(" ")}`);
            t.setDISPTMG(true);
            t.setDISPTMG(false);
        }
    }
}

const run = (start, count) => Array.from({ length: count }, (_, i) => start + i);

// Every printable character in plain text, then after each graphics colour, contiguous and separated.
const alphabet = [run(0x20, 40), run(0x48, 40), run(0x60, 32)];
const mosaic = (colourCode, separated) => alphabet.map((row) => [colourCode, ...(separated ? [26] : []), ...row.slice(0, 38)]);
for (const ra0 of [0, 1]) {
    record("alpha", alphabet, ra0, 20);
    record("graphics", mosaic(0x11, false), ra0, 20);
    record("separated", mosaic(0x16, true), ra0, 20);
}

// Random pages where control codes are as likely as anything, to find the interactions between them.
let seed = 4242;
const next = () => (seed = (Math.imul(seed, 1103515245) + 12345) >>> 0) >>> 8;
for (let page = 0; page < 6; page++) {
    const rows = [];
    for (let r = 0; r < 12; r++) {
        const row = [];
        for (let c = 0; c < 20; c++) {
            const roll = next() % 8;
            row.push(roll < 3 ? next() % 32 : 0x20 + (next() % 96));
        }
        rows.push(row);
    }

    for (const ra0 of [0, 1]) {
        for (const flashFields of [0, 20]) {
            record(`random${page}`, rows, ra0, flashFields);
        }
    }
}

writeFileSync(process.argv[2], lines.join("\n") + "\n");
