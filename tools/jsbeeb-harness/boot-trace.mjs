#!/usr/bin/env node
// Runs a Model B from power-on in jsbeeb's headless MachineSession up to the
// OS's first keyboard poll (the fetch at &F02A) and writes a fixture that
// BbcMicroBootJsbeebTests replays against BbcMicroSystem.
//
//   node boot-trace.mjs <output file>
//
// Fixture lines (cycle = 2MHz cycles since the first instruction fetch):
//   i <cycle> <pc hex>      an instruction fetch
//   c <pc hex>              checkpoint, taken just before the first fetch at that address
//   ram <hex>               zero page. Nothing else is meaningful yet: the OS has not initialised the rest of
//                           RAM by the first poll, and what it holds is the arbitrary power-on contents
//   via <name> <ora> <ddra> <orb> <ddrb> <acr> <pcr> <ier>   (hex bytes; the T1 latches are undefined after reset, so not compared)
import { writeFileSync } from "node:fs";
import { MachineSession } from "jsbeeb/machine-session";

const out = process.argv[2];
if (!out) {
    console.error("usage: node boot-trace.mjs <output file>");
    process.exit(2);
}

const POLL = 0xf02a;
const CHECKPOINTS = new Set([POLL]);

const session = new MachineSession("B-DFS0.9");
await session.initialise();
const cpu = session._machine.processor;
const hex = (n, w = 2) => (n & ((1 << (w * 4)) - 1)).toString(16).padStart(w, "0");
const lines = [];
const seen = new Set();

const viaLine = (name, v) =>
    `via ${name} ${hex(v.ora)} ${hex(v.ddra)} ${hex(v.orb)} ${hex(v.ddrb)} ${hex(v.acr)} ${hex(v.pcr)} ${hex(v.ier & 0x7f)}`;

session.onExecute((pc) => {
    lines.push(`i ${session.elapsedCycles} ${hex(pc, 4)}`);
    if (CHECKPOINTS.has(pc) && !seen.has(pc)) {
        seen.add(pc);
        const ram = session.readMemory(0, 0x100);
        lines.push(`c ${hex(pc, 4)}`);
        lines.push(`ram ${Buffer.from(ram.slice(0, 0x100)).toString("hex")}`);
        lines.push(viaLine("system", cpu.sysvia));
        lines.push(viaLine("user", cpu.uservia));
    }
    if (pc === POLL) cpu.stop();
});

await session.runFor(5_000_000);
if (!seen.has(POLL)) throw new Error("the OS never reached its first keyboard poll");
writeFileSync(out, lines.join("\n") + "\n");
session.destroy();
