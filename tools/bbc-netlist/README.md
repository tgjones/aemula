# BBC Micro netlist extractor

Rerunnable extractor that turns amb5l's searchable Issue 7 mainboard schematic
(`bbc_micro_mainboard_iss7_20210909.pdf`, from the
[stardot thread](https://stardot.org.uk/forums/viewtopic.php?t=23329)) into a
raw netlist in the ZX80 netlist format. The PDF is not checked in.

```sh
pip install pymupdf
python3 extract.py /path/to/bbc_micro_mainboard_iss7_20210909.pdf --out /tmp/bbc-raw
```

Writes `netlist.txt` (COMPONENTS, NETS, then a DIAGNOSTICS block), `diagnostics.txt`
(the diagnostics alone) and `pins.json` (one record per pin: reference, pin number,
drawn pin name, net, position, whether it carries a no-connect cross).

No hand corrections are applied here; they are layered on top of `netlist.txt`
afterwards by `apply_corrections.py`, so the extraction can be regenerated at any time:

```sh
python3 apply_corrections.py /tmp/bbc-raw/netlist.txt ../../src/Aemula/Emulation/Systems/BbcMicro/docs/bbc-netlist.txt
```

The corrections are pin-based and each is checked against the raw extraction first.

## Sanity checks

`check_netlist.py` checks the corrected netlist (`pins.json` is optional and tells it which pins
carry a no-connect cross):

```sh
python3 check_netlist.py ../../src/Aemula/Emulation/Systems/BbcMicro/docs/bbc-netlist.txt /tmp/bbc-raw/pins.json
```

It verifies that power/ground pins that are drawn are on the right rail (most ICs have them
implicit), that every input of a pinned-out IC is driven, and that the A0-A15 / D0-D7 pins of the
CPU, VIAs, ACIA, ROMs and the other address/data users sit on the net of that name. It carries
pinouts only for the common logic parts, the 6502, 6522, 6845, 6850 and the ROMs/DRAM; the other
chips are listed as unchecked. Sheet omissions it is told to accept are in `ACCEPTED`.

## Tracing a merge

`--why LABEL1 LABEL2` prints the chain of wire joins that ties two labels together,
which is how a wrong join (two nets that should be separate) is located:

```sh
python3 extract.py iss7.pdf --out /tmp/bbc-raw --why A0 2MHZE
```

## What the drawing's colours mean

| Stroke | Meaning |
|---|---|
| blue `(0.26, 0, 1)` | wire; joins at ends, T-junctions and red junction dots, never at an undotted crossing |
| fat blue `(0, 0, 1)` | bus; carries no nets (buses join by label), and a ripper's end on a bus joins nothing |
| orange | symbol body |
| brown | pin stub, drawn body-edge to connection point (longer than 2.5pt; shorter axis-aligned ones leave an inversion bubble) |
| lone brown zero-length stroke | the connection point of a symbol drawn without stubs (transistors, switches, links) |
| black short strokes | power-symbol stem and bar |
| brown-orange diagonals | no-connect cross |

Text: black is a reference, value, pin number or net label; blue is a pin name; red is an
off-page connector label (its arrow sits level with, or directly above or below, the label).
An overbar is a thin black line over the text.

## Conventions in the output

- Gate packages are drawn one symbol per gate; they share an IC number and are told apart by
  pin number (the section letter is dropped).
- Diodes are unnumbered on the drawing; pins are named `A` and `K` from the symbol geometry.
- The relay, links and switches are unnumbered too: their pins are numbered by position
  (and a diagnostic says so).
- Power rails are named by the label on the power symbol (`+5V`, `0V`, `-5V`); labels that
  share a name are one net.

## Diagnostics worth reading first

`dangling` (a net with one pin and no no-connect cross), `passive-only` (an unlabelled net
touching only R/C/D), `multiple-outputs` (an unlabelled net joining two outputs),
`suspicious` (input and output of one package tied together, or both bus sides of a
transceiver), `carries several labels` (two names on one wire group) and labels that sit on
no wire. The ZX80 errors were all of these kinds.
