#!/usr/bin/env node
// Programs jsbeeb's CRTC with a set of register sets (the BBC's standard modes
// and a few nonstandard ones), clocks it one character at a time, and writes the
// outputs it produced to a fixture that Motorola6845ChipJsbeebTests replays
// against Aemula's Motorola6845Chip.
//
//   node crtc-timing.mjs <output file>
//
// Fixture lines (clock = 0-based character clock; every value is hex):
//   scenario <name> <clocks> <R0..R17 written, comma separated, "-" for not written>
//   e <clock> <ra> <flags>   outputs changed at the start of that clock
//   m <clock> <ma>           MA sampled at every event and every 11th clock
// flags: 1 DISPEN, 2 HS, 4 VS, 8 cursor. The state before clock 0 is the first
// event. The chip is started cleanly: counters at zero and display enabled, as
// after a reset.
import { writeFileSync } from "node:fs";
import { Video } from "jsbeeb/src/video.js";

const mode0 = [0x7f, 0x50, 0x62, 0x28, 0x26, 0x00, 0x20, 0x22, 0x01, 0x07, 0x67, 0x08, 0x06, 0x00, 0x06, 0x30];
const mode4 = [0x3f, 0x28, 0x31, 0x24, 0x26, 0x00, 0x20, 0x22, 0x01, 0x07, 0x67, 0x08, 0x06, 0x00, 0x06, 0x10];
const mode7 = [0x3f, 0x28, 0x33, 0x24, 0x1e, 0x02, 0x19, 0x1b, 0x93, 0x12, 0x72, 0x13, 0x28, 0x00, 0x28, 0x21];

const scenarios = [
    { name: "mode0", clocks: 128 * 312 * 2 + 1000, regs: mode0 },
    { name: "mode4", clocks: 64 * 312 * 2 + 1000, regs: mode4 },
    { name: "mode7", clocks: 64 * 312 * 3 + 1000, regs: mode7 },
    // Tiny frame so blink modes, vertical adjust and interlace cycle many times.
    { name: "small-blink16", clocks: 20000, regs: [19, 12, 15, 0x32, 5, 3, 4, 5, 0x00, 3, 0x42, 0x03, 0x00, 0x10, 0x00, 0x15] },
    { name: "small-blink32", clocks: 30000, regs: [19, 12, 15, 0x32, 5, 3, 4, 5, 0x00, 3, 0x62, 0x03, 0x00, 0x10, 0x00, 0x15] },
    { name: "small-cursor-off", clocks: 5000, regs: [19, 12, 15, 0x32, 5, 3, 4, 5, 0x00, 3, 0x22, 0x03, 0x00, 0x10, 0x00, 0x15] },
    { name: "small-interlace-sync", clocks: 20000, regs: [19, 12, 15, 0x32, 5, 3, 4, 5, 0x01, 3, 0x42, 0x03, 0x00, 0x10, 0x00, 0x15] },
    { name: "small-interlace-video", clocks: 20000, regs: [19, 12, 15, 0x32, 5, 3, 4, 5, 0x03, 3, 0x42, 0x03, 0x00, 0x10, 0x00, 0x15] },
    { name: "small-skew1", clocks: 5000, regs: [19, 12, 15, 0x32, 5, 3, 4, 5, 0x10, 3, 0x42, 0x03, 0x00, 0x10, 0x00, 0x15] },
    { name: "small-skew2", clocks: 5000, regs: [19, 12, 15, 0x32, 5, 3, 4, 5, 0x20, 3, 0x42, 0x03, 0x00, 0x10, 0x00, 0x15] },
    { name: "small-display-off", clocks: 5000, regs: [19, 12, 15, 0x32, 5, 3, 4, 5, 0x30, 3, 0x42, 0x03, 0x00, 0x10, 0x00, 0x15] },
    // R6 > R4: vertical displayed never ends, so there are no frame counts.
    { name: "small-r6-gt-r4", clocks: 5000, regs: [19, 12, 15, 0x32, 5, 3, 9, 5, 0x00, 3, 0x42, 0x03, 0x00, 0x10, 0x00, 0x15] },
    // R7 beyond the frame: no vertical sync at all.
    { name: "small-no-vsync", clocks: 5000, regs: [19, 12, 15, 0x32, 5, 3, 4, 40, 0x00, 3, 0x42, 0x03, 0x00, 0x10, 0x00, 0x15] },
    // Widest hsync, and a vsync width of 0 that runs the full 16 lines. (An hsync width of 0 is left out:
    // jsbeeb never ends that pulse, which no real chip does.)
    { name: "small-wide-sync", clocks: 8000, regs: [19, 12, 15, 0x0f, 5, 3, 4, 5, 0x00, 3, 0x42, 0x03, 0x00, 0x10, 0x00, 0x15] },
    { name: "small-narrow-sync", clocks: 8000, regs: [19, 12, 15, 0x11, 5, 3, 4, 5, 0x00, 3, 0x42, 0x03, 0x00, 0x10, 0x00, 0x15] },
    // Start address and cursor at the top of the 14-bit space, so MA wraps.
    { name: "small-address-wrap", clocks: 5000, regs: [19, 12, 15, 0x32, 5, 3, 4, 5, 0x00, 3, 0x42, 0x03, 0x3f, 0xf0, 0x3f, 0xf5] },
];

const lines = [];

for (const s of scenarios) {
    const video = new Video(false, 1024 * 768, () => {});
    const cpu = { videoRead: () => 0 };
    const via = { setVBlankInt() {}, cb2changecallback: null };
    video.reset(cpu, via, true);

    for (let r = 0; r < s.regs.length; r++) {
        video.crtc.write(0, r);
        video.crtc.write(1, s.regs[r]);
    }
    // Start as a chip fresh out of reset: frame start, display on.
    video.endOfFrame();
    video.addr = video.lineStartAddr;
    video.dispEnableSet(1 << 1);
    video.dispEnableSet(1 << 2);

    lines.push(`scenario ${s.name} ${s.clocks.toString(16)} ${s.regs.map((v) => v.toString(16)).join(",")}`);

    const mask = (1 << 0) | (1 << 1) | (1 << 2) | (1 << 4);
    let lastKey = "";

    for (let clock = 0; clock < s.clocks; clock++) {
        const regs = video.regs;
        const disp = (video.dispEnabled & mask) === mask ? 1 : 0;
        const hs = video.inHSync ? 2 : 0;
        const vs = video.inVSync ? 4 : 0;
        const cursor =
            video.addr === video.cursorPos &&
            video.cursorOn &&
            !video.cursorOff &&
            video.cursorOnThisFrame &&
            video.dispEnabled & (1 << 1) &&
            video.dispEnabled & (1 << 0) &&
            video.horizCounter < regs[1]
                ? 8
                : 0;
        const ra = video.externalRowAddress();
        const key = `${ra.toString(16)} ${(disp | hs | vs | cursor).toString(16)}`;
        const changed = key !== lastKey;
        if (changed) {
            lines.push(`e ${clock.toString(16)} ${key}`);
            lastKey = key;
        }
        if (changed || clock % 11 === 0) lines.push(`m ${clock.toString(16)} ${video.addr.toString(16)}`);
        video.polltime(1);
    }
}

writeFileSync(process.argv[2], lines.join("\n") + "\n");
