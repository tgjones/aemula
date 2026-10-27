#!/usr/bin/env node
// Drives jsbeeb's UserVia (a bare 6522 with nothing on its ports) through a set
// of scripted timer/IRQ scenarios and writes what it observed to a fixture that
// Mos6522ChipJsbeebTests replays against Aemula's Mos6522Chip.
//
//   node via-timing.mjs <output file>
//
// Fixture lines (cycle = 1 MHz cycle, 0-based):
//   scenario <name> <cycles>
//   w <cycle> <reg hex> <value hex>     register write during that cycle
//   r <cycle> <reg hex> <value hex>     register read during that cycle, and what it returned
//   i <cycle> <0|1>                     IRQ line as the CPU sees it at the start of that cycle
import { writeFileSync } from "node:fs";
import { Scheduler } from "jsbeeb/src/scheduler.js";
import { UserVia } from "jsbeeb/src/via.js";

const ORB = 0, T1CL = 4, T1CH = 5, T1LL = 6, T1LH = 7, T2CL = 8, T2CH = 9, ACR = 0xb, IFR = 0xd, IER = 0xe;

const w = (c, reg, val) => ({ c, op: "w", reg, val });
const r = (c, reg) => ({ c, op: "r", reg });

const scenarios = [
    {
        name: "t1-one-shot-n5",
        cycles: 24,
        ops: [w(0, IER, 0xc0), w(1, T1CL, 5), w(2, T1CH, 0), r(5, T1CL), r(9, IFR), r(10, IFR), r(11, T1CL), r(12, IFR), r(14, T1CH), r(20, IFR)],
    },
    {
        name: "t1-one-shot-n0",
        cycles: 12,
        ops: [w(0, IER, 0xc0), w(1, T1CL, 0), w(2, T1CH, 0), r(5, IFR), r(7, IFR)],
    },
    {
        name: "t1-one-shot-n1",
        cycles: 12,
        ops: [w(0, IER, 0xc0), w(1, T1CL, 1), w(2, T1CH, 0), r(5, IFR), r(7, IFR)],
    },
    {
        name: "t1-free-run-n3",
        cycles: 40,
        ops: [w(0, IER, 0xc0), w(1, ACR, 0x40), w(2, T1CL, 3), w(3, T1CH, 0), r(8, T1CL), r(13, IFR), r(14, IFR), r(15, T1CL), r(24, IFR), r(25, T1CL), r(34, IFR)],
    },
    {
        name: "t1-counter-16bit",
        cycles: 20,
        ops: [w(0, T1CL, 0x02), w(1, T1CH, 0x01), r(4, T1CL), r(4, T1CH), r(10, T1CL), r(10, T1CH), r(17, T1CL), r(17, T1CH)],
    },
    {
        name: "t1-pb7-one-shot",
        cycles: 20,
        ops: [w(0, ACR, 0x80), w(1, T1CL, 4), w(2, T1CH, 0), r(3, ORB), r(6, ORB), r(7, ORB), r(8, ORB), r(12, ORB)],
    },
    {
        name: "t1-pb7-free-run",
        cycles: 30,
        ops: [w(0, ACR, 0xc0), w(1, T1CL, 2), w(2, T1CH, 0), r(5, ORB), r(6, ORB), r(7, ORB), r(10, ORB), r(11, ORB), r(14, ORB), r(15, ORB)],
    },
    {
        name: "t1-latch-write-clears-flag",
        cycles: 20,
        ops: [w(0, T1CL, 2), w(1, T1CH, 0), r(6, IFR), w(7, T1LH, 5), r(8, IFR)],
    },
    {
        name: "t1-clear-on-the-cycle-it-fires",
        cycles: 16,
        ops: [w(0, IER, 0xc0), w(1, T1CL, 3), w(2, T1CH, 0), w(6, IFR, 0x40), r(7, IFR), w(8, IFR, 0x40), r(9, IFR), r(10, IFR)],
    },
    {
        name: "t2-one-shot-n4",
        cycles: 24,
        ops: [w(0, IER, 0xa0), w(1, T2CL, 4), w(2, T2CH, 0), r(5, T2CL), r(7, IFR), r(8, IFR), r(9, T2CL), r(10, IFR), r(14, T2CL), r(15, T2CH)],
    },
    {
        name: "t2-keeps-counting-after-timeout",
        cycles: 20,
        ops: [w(0, T2CL, 2), w(1, T2CH, 0), r(5, T2CL), r(5, T2CH), r(8, T2CL), r(8, T2CH)],
    },
    {
        name: "t2-flag-cleared-by-high-write",
        cycles: 24,
        ops: [w(0, T2CL, 2), w(1, T2CH, 0), r(8, IFR), w(9, T2CL, 3), w(10, T2CH, 0), r(11, IFR), r(16, IFR)],
    },
    {
        name: "ier-enable-disable",
        cycles: 16,
        ops: [w(0, T1CL, 2), w(1, T1CH, 0), w(6, IER, 0xc0), r(7, IER), r(7, IFR), w(8, IER, 0x40), r(9, IER), r(9, IFR)],
    },
];

const lines = [];

for (const s of scenarios) {
    const cpu = { interrupt: 0 };
    const scheduler = new Scheduler();
    const via = new UserVia(cpu, scheduler, false, { read: () => 0xff, write: () => {} });
    lines.push(`scenario ${s.name} ${s.cycles}`);

    for (let c = 0; c < s.cycles; c++) {
        const at = c * 2;
        if (scheduler.epoch < at) scheduler.polltime(at - scheduler.epoch);
        lines.push(`i ${c} ${cpu.interrupt & 0x02 ? 0 : 1}`);
        for (const op of s.ops.filter((o) => o.c === c)) {
            if (op.op === "w") {
                via.write(op.reg, op.val);
                lines.push(`w ${c} ${op.reg.toString(16)} ${op.val.toString(16)}`);
            } else {
                const v = via.read(op.reg);
                lines.push(`r ${c} ${op.reg.toString(16)} ${v.toString(16)}`);
            }
        }
    }
}

writeFileSync(process.argv[2], lines.join("\n") + "\n");
