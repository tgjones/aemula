#!/usr/bin/env node
// Records what jsbeeb's video ULA does with a video byte, and how its screen address is
// formed, for VideoUlaChipJsbeebTests and BbcMicroVideoAddressTests to replay.
//
//   node ula-video.mjs <output file>
//
// Fixture lines (every value is hex):
//   t <mode> <byte> <16 palette indices>   the logical colour (0-F) of each of the 16 16MHz ticks a
//                                          video byte lasts, for pixel-rate bits <mode> (control bits 3-2)
//   p <flash> <value> <rgb>                the colour a palette write of <value> gives, with the control
//                                          register's flash bit <flash>; rgb is R=1, G=2, B=4
//   a <ma> <ra> <c> <address>              the RAM address read for CRTC address <ma>, row <ra> and
//                                          IC32 outputs C1,C0 = <c> (the hardware scroll size)
import { writeFileSync } from "node:fs";
import { Video } from "jsbeeb/src/video.js";

const video = new Video(false, 1024 * 768, () => {});
video.reset({ videoRead: (address) => address }, { setVBlankInt() {} }, true);

const lines = [];

// jsbeeb's drain tables are indexed by (mode << 12) | (byte << 4).
for (let mode = 0; mode < 4; mode++) {
    for (let byte = 0; byte < 256; byte++) {
        const base = (mode << 12) | (byte << 4);
        let digits = "";
        for (let i = 0; i < 16; i++) digits += video.table4bpp[base + i].toString(16);
        lines.push(`t ${mode.toString(16)} ${byte.toString(16)} ${digits}`);
    }
}

const rgb = (colour) => ((colour & 0xff) > 0x7f ? 1 : 0) | (((colour >>> 8) & 0xff) > 0x7f ? 2 : 0) | (((colour >>> 16) & 0xff) > 0x7f ? 4 : 0);

for (let flash = 0; flash < 2; flash++) {
    video.ula.write(0, flash);
    for (let value = 0; value < 256; value++) {
        video.ula.write(1, value);
        lines.push(`p ${flash} ${value.toString(16)} ${rgb(video.ulaPal[value >>> 4]).toString(16)}`);
    }
}

// Mode 7 (MA13) reads only with MA12 clear, as the hardware wraps with MA12 and the OS never sets
// both; jsbeeb ignores MA12 there.
let seed = 12345;
const next = () => (seed = (Math.imul(seed, 1103515245) + 12345) >>> 0) >>> 8;
for (let n = 0; n < 4000; n++) {
    const ma = next() & 0x3fff;
    if (ma & 0x2000 && ma & 0x1000) continue;
    const ra = next() & 7;
    const c = next() & 3;
    video.addr = ma;
    video.scanlineCounter = ra;
    video.setScreenHwScroll(c);
    lines.push(`a ${ma.toString(16)} ${ra.toString(16)} ${c.toString(16)} ${video.readVideoMem().toString(16)}`);
}

writeFileSync(process.argv[2], lines.join("\n") + "\n");
