namespace Aemula.Emulation.Systems.BbcMicro;

public sealed partial class BbcMicroSystem
{
    // The screen memory address for the 6845's current MA and RA outputs.
    //
    // In the bitmap modes the screen is stored a character column at a time,
    // eight bytes per character, so the RAM address is MA0-MA7 (the character
    // within the row of 256), then RA0-RA2 below them, with MA8-MA11 above
    // them going through the IC39 adder. The OS scrolls the screen by moving
    // the 6845's start address, so the 6845 runs on past the top of RAM at
    // &8000 (MA12 is address bit 15): the adder then subtracts the size of
    // the screen (16K, 8K, 20K or 10K, selected by IC32's outputs C0 and C1,
    // the hardware scroll bits) to wrap back to its start.
    //
    // In Mode 7 (MA13, TTXVDU) the SAA5050's character codes are stored one
    // byte each, so MA0-MA9 are the low bits of the RAM address, placed in
    // the top 1K of a 16K bank (&3C00), and MA11 is address bit 14, which is
    // how the OS's screen at &7C00 is reached from the 6845 address &2800.
    internal ushort VideoAddressFor(ushort ma, byte ra)
    {
        var high = VideoAdder(ma);

        if ((ma & 0x2000) != 0)
        {
            return (ushort)(0x3C00 | (ma & 0x3FF) | ((high & 8) << 11));
        }

        return (ushort)((high << 11) | ((ma & 0xFF) << 3) | (ra & 7));
    }

    // The adder's outputs, VAA0-VAA3: MA8-MA11 plus the wrap.
    private int VideoAdder(ushort ma)
    {
        var wrap = (ma & 0x1000) != 0;

        // IC36, IC40's first gate and IC27's fourth gate (an open-collector
        // output with a pull-up) make the adder's B inputs. With MA12 low
        // they are all high, so the adder adds 15 plus the carry in and
        // passes MA8-MA11 through. With it high they are the screen size
        // subtracted: minus its two's complement, less the carry in. IC36's
        // gates used are the one on pins 3-6 (first here) and the one on
        // pins 9-11 and 8 (second).
        _ic40.A1 = _ic32.Q4;
        _ic40.B1 = wrap;
        _ic27.A4 = wrap;
        _ic27.B4 = _ic40.Y1;
        _ic36.A1 = wrap;
        _ic36.B1 = _ic32.Q4;
        _ic36.C1 = _ic32.Q5;
        _ic36.A2 = _ic32.Q5;
        _ic36.B2 = _ic40.Y1;
        _ic36.C2 = wrap;

        _ic39.A1 = (ma & 0x100) != 0;
        _ic39.A2 = (ma & 0x200) != 0;
        _ic39.A3 = (ma & 0x400) != 0;
        _ic39.A4 = (ma & 0x800) != 0;
        _ic39.B1 = _ic36.Y1;
        _ic39.B2 = _ic36.Y2;
        _ic39.B3 = _ic40.Y1;
        _ic39.B4 = _ic27.Y4 ?? true;
        _ic39.C0 = true;

        return (_ic39.S1 ? 1 : 0) | (_ic39.S2 ? 2 : 0) | (_ic39.S3 ? 4 : 0) | (_ic39.S4 ? 8 : 0);
    }
}
