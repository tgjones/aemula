#!/usr/bin/env node
// Runs a BBC Micro Model B in jsbeeb's headless MachineSession and dumps its
// state into an output directory for Aemula tests to compare against:
//
//   state.json    model, cycle count, CPU registers, requested RAM ranges (hex)
//   pctrace.txt   "<cycle> <pc>" per executed instruction (with --trace)
//   frame.png     the last fully painted 1024x625 frame (with --screenshot)
//
// Usage: node run.mjs --out <dir> [options]
//   --model <name>       jsbeeb model (default "B-DFS0.9": Model B, OS 1.20, BASIC 2, 8271 + DFS 0.90)
//   --boot               run until the BASIC prompt first, then start counting
//   --cycles <n>         run n CPU cycles after boot/power-on
//   --frames <n>         run n painted frames instead of a cycle count
//   --type <text>        type text at the keyboard (after boot) before running
//   --ram <addr>:<len>   dump RAM range (repeatable; numbers may be 0x-prefixed hex)
//   --trace              record the PC trace for the counted run
//   --screenshot         write frame.png
import { mkdirSync, writeFileSync } from "node:fs";
import path from "node:path";
import { parseArgs } from "node:util";
import { MachineSession } from "jsbeeb/machine-session";

const { values: opts } = parseArgs({
    options: {
        out: { type: "string" },
        model: { type: "string", default: "B-DFS0.9" },
        boot: { type: "boolean", default: false },
        cycles: { type: "string" },
        frames: { type: "string" },
        type: { type: "string" },
        ram: { type: "string", multiple: true, default: [] },
        trace: { type: "boolean", default: false },
        screenshot: { type: "boolean", default: false },
    },
});

if (!opts.out) {
    console.error("usage: node run.mjs --out <dir> [--model M] [--boot] [--cycles N | --frames N] [--type T] [--ram A:L]... [--trace] [--screenshot]");
    process.exit(2);
}
mkdirSync(opts.out, { recursive: true });

const num = (s) => Number(s);
const session = new MachineSession(opts.model);
await session.initialise();

let bootText = null;
if (opts.boot) bootText = await session.boot();
if (opts.type) await session.type(opts.type);

const startCycles = session.elapsedCycles;
const trace = [];
const stopWatching = opts.trace ? session.onExecute((pc) => trace.push(`${session.elapsedCycles} ${pc.toString(16).padStart(4, "0")}`)) : null;

let run = null;
if (opts.frames !== undefined) run = await session.runFrames(num(opts.frames));
else if (opts.cycles !== undefined) run = await session.runFor(num(opts.cycles));
stopWatching?.();

const ram = opts.ram.map((spec) => {
    const [a, l] = spec.split(":").map(num);
    const bytes = session.readMemory(a, l);
    return { address: a, length: l, hex: Buffer.from(bytes).toString("hex") };
});

const regs = session.registers();
const state = {
    model: opts.model,
    startCycles,
    endCycles: session.elapsedCycles,
    frames: session.frameCount,
    run,
    bootText,
    registers: { pc: regs.pc, a: regs.a, x: regs.x, y: regs.y, s: regs.s, p: regs.p },
    ram,
};
writeFileSync(path.join(opts.out, "state.json"), JSON.stringify(state, null, 2));
if (opts.trace) writeFileSync(path.join(opts.out, "pctrace.txt"), trace.join("\n") + "\n");
if (opts.screenshot) writeFileSync(path.join(opts.out, "frame.png"), await session.screenshot());
session.destroy();
