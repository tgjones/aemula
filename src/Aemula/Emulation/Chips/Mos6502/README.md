# MOS Technology 6502

## Data sheets

* [MOS6500 Preliminary Data Sheet (1976)](https://www.mdawson.net/vic20chrome/cpu/mos_6500_mpu_preliminary_may_1976.pdf)
* [MOS6500 Family (1985)](http://archive.6502.org/datasheets/mos_6500_mpu_nov_1985.pdf)
  * Good timing chart on page 4
* [R650X and R651X](http://archive.6502.org/datasheets/rockwell_r650x_r651x.pdf)
* [UM6502](http://pdf.datasheetcatalog.com/datasheet/UMC/mXyztwtz.pdf)

## Manuals

* [MOS MCS6500 Family Hardware Manual](http://archive.6502.org/books/mcs6500_family_hardware_manual.pdf)
  * Appendix A has a useful cycle-by-cycle description of each instruction
* [MOS MCS6500 Family Programming Manual](http://archive.6502.org/books/mcs6500_family_programming_manual.pdf)
* [Synertek SY6500/MCS6500 Hardware Manual](http://archive.6502.org/datasheets/synertek_hardware_manual.pdf)

## Information

* [6502 Instruction Set](https://www.masswerk.at/6502/6502_instruction_set.html)
* [All 256 opcodes](http://visual6502.org/wiki/index.php?title=6502_all_256_Opcodes)
* [6502 Timing States](http://www.visual6502.org/wiki/index.php?title=6502_Timing_States)
* [Opcode matrix](http://www.oxyron.de/html/opcodes02.html)
* [Slides from Visual 6502 Presentation](http://www.visual6502.org/docs/6502_in_action_14_web.pdf)

## Other implementations

* [EDL](https://github.com/SavourySnaX/EDL/blob/master/chips/Accurate/m6502.edl)
* [Chips](https://github.com/floooh/chips/blob/master/chips/m6502.h)
  * [Andre Weissflog's blog post about his cycle-stepped 6502 emulator](https://floooh.github.io/2019/12/13/cycle-stepped-6502.html) has been very helpful.
* [go6502](https://github.com/zellyn/go6502)

## Timing

* [How to implement bus sharing / DMA on a 6502 system](https://retrocomputing.stackexchange.com/questions/12718/how-to-implement-bus-sharing-dma-on-a-6502-system)

* From the MCS6500 Family Hardware Manual:
  > The timing of all data transfers is controlled by the system clock. The clock itself is actually
two non-overlapping square waves. This two-phase clock system can best be thought of as two alternating
positive-going pulses. This text will refer to the clocks as Phase One and Phase Two. A Phase One
clock pulse is the positive pulse during which the address lines change and a Phase Two clock pulse
is the positive pulse during which the data is transferred. The timing of the signals on the Address Bus,
Data Bus, and R/W line are shown in Figures 1.5 through 1.8... In particular, the address lines and
the R/W line will stabilize during Phase One, and all data transfers will take place during Phase Two.