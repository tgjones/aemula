# Motorola 6820 Chip

[Wikipedia entry](https://en.wikipedia.org/wiki/Peripheral_Interface_Adapter)

## Datasheets

* [MC6820/MC6821 Hardware Manual excerpt (as used in the Apple 1)](https://www.axdn.com/apple1/6820_hardware_manual.pdf) - the primary source for this implementation; includes the full CA1/CA2/CB1/CB2 transition and control-register tables.
* [W65C21 Datasheet](https://www.westerndesigncenter.com/wdc/documentation/w65c21s.pdf) - modern reprint of the same register-level behaviour under the 6821's more common pin names (CS0/CS1/CS2 rather than CS1/CS2/CS3).

## Other implementations

* [MAME](https://github.com/mamedev/mame/blob/master/src/devices/machine/6821pia.cpp)
