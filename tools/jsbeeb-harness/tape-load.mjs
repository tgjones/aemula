#!/usr/bin/env node
// Loads a one-line BASIC program from a UEF in jsbeeb's headless MachineSession
// and writes two fixtures that BbcMicroTapeLoadJsbeebTests replays against
// BbcMicroSystem.
//
//   node tape-load.mjs <tape-program.uef> <jsbeeb-tape-load.txt>
//
// The UEF is built here (one block holding `10 PRINT "TAPE OK"`, loaded at &1900)
// and written to the first file so that the two emulators read the same bytes.
//
// The trace is not compared fetch for fetch: the tape's bits reach the ACIA at
// times that depend on where each emulator's tape clock starts, so the polling
// loops repeat a different number of times. What the two must agree on is
// which code runs, and in what order it is first reached. Fixture lines:
//   p <pc hex>      an address fetched for the first time during LOAD, in order,
//                   not counting any address already run by the boot or *TAPE
//   ram <hex>       the program as BASIC holds it after the LOAD
import { writeFileSync } from "node:fs";
import { MachineSession } from "jsbeeb/machine-session";

const [uefPath, out] = process.argv.slice(2);
if (!uefPath || !out) {
    console.error("usage: node tape-load.mjs <tape-program.uef> <output file>");
    process.exit(2);
}

const hex = (n, w = 2) => (n & ((1 << (w * 4)) - 1)).toString(16).padStart(w, "0");

const crc = (bytes) => {
    let c = 0;
    for (const b of bytes) {
        c ^= b << 8;
        for (let i = 0; i < 8; i++) c = c & 0x8000 ? ((c << 1) ^ 0x1021) & 0xffff : (c << 1) & 0xffff;
    }
    return c;
};

const text = [...Buffer.from('ñ "TAPE OK"', "latin1")];
const program = [0x0d, 0x00, 0x0a, text.length + 4, ...text, 0x0d, 0xff];

const header = [
    ...Buffer.from("T"), 0,
    0x00, 0x19, 0x00, 0x00,
    0x23, 0x80, 0x00, 0x00,
    0x00, 0x00,
    program.length & 0xff, program.length >> 8,
    0x80,
    0x00, 0x00, 0x00, 0x00,
];
const block = [0x2a, ...header, crc(header) >> 8, crc(header) & 0xff, ...program, crc(program) >> 8, crc(program) & 0xff];

const chunk = (id, data) => [id & 0xff, id >> 8, data.length & 0xff, (data.length >> 8) & 0xff, 0, 0, ...data];
const uef = Buffer.from([
    ...Buffer.from("UEF File!\0", "latin1"), 0x0a, 0x00,
    ...chunk(0x0110, [1200 & 0xff, 1200 >> 8]),
    ...chunk(0x0100, block),
    ...chunk(0x0110, [480 & 0xff, 480 >> 8]),
]);
writeFileSync(uefPath, uef);

const session = new MachineSession("B-DFS0.9");
await session.initialise();
const seen = new Set();
const fresh = [];
let recording = false;
session.onExecute((pc) => {
    if (seen.has(pc)) return;
    seen.add(pc);
    if (recording) fresh.push(pc);
});

await session.runUntilPrompt();
await session.type("*TAPE");
await session.runUntilPrompt();

await session.loadTape(uefPath);
recording = true;
await session.type('LOAD"T"');
await session.runUntilPrompt();

const lines = fresh.map((pc) => `p ${hex(pc, 4)}`);
lines.push(`ram ${Buffer.from(session.readMemory(0x1900, program.length)).toString("hex")}`);
writeFileSync(out, lines.join("\n") + "\n");
console.error(`${fresh.length} new addresses`);
session.destroy();
