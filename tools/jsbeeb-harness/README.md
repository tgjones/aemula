# jsbeeb harness

Headless [jsbeeb](https://github.com/mattgodbolt/jsbeeb) runner used as the
reference oracle for the BBC Micro. Lives outside the Aemula solution.

```sh
cd tools/jsbeeb-harness
npm install
node run.mjs --out /tmp/run1 --boot --cycles 20000 --ram 0x0000:16 --trace --screenshot
```

Writes `state.json` (cycle counts, registers, hex RAM ranges, boot text),
`pctrace.txt` (`<cycle> <pc>` per instruction) and `frame.png` (1024x625).
See the header of `run.mjs` for all flags. The default model, `B-DFS0.9`, is a
Model B with OS 1.20, BASIC 2 and the 8271 FDC; jsbeeb fits sideways RAM in
banks Aemula does not have, so only compare RAM that the OS owns.

Requires Node >= 24.15 (jsbeeb's requirement).

## 6522 timing fixture

```sh
node via-timing.mjs ../../src/Aemula.Tests/Emulation/Chips/Mos6522/Assets/jsbeeb-via-timing.txt
```

Drives jsbeeb's `UserVia` through scripted timer/IRQ scenarios and records
every register read and the IRQ line at the start of every cycle; replayed by
`Mos6522ChipJsbeebTests`.

## 6845 timing fixture

```sh
node crtc-timing.mjs ../../src/Aemula.Tests/Emulation/Chips/Motorola6845/Assets/jsbeeb-crtc-timing.txt
```

Programs jsbeeb's CRTC with the BBC's Mode 0/4/7 register sets and a range of
nonstandard ones, clocks it a character at a time and records every change of
RA/DISPEN/HS/VS/cursor plus periodic MA samples; replayed by
`Motorola6845ChipJsbeebTests`.
