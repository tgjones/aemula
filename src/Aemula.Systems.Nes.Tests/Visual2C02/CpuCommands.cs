namespace Aemula.Systems.Nes.Tests.Visual2C02;

internal class CpuCommands
{
    // 0xABCC
    // A: 0/1 = idle, 2 = write, 3 = read
    // B: 0-7
    // CC: value (ignored for read)
    private static readonly ushort[] cpucmd_program =
    {
        0x2000, // $00 -> $2000
        0x2100, // $00 -> $2001
        0x2300, // $00 -> $2003, sprite DMA follows
        0x2400, 0x0018, 0x2401, 0x0018, 0x2400, 0x0018, 0x2410, 0x0018,
        0x2400, 0x0018, 0x2402, 0x0018, 0x2400, 0x0018, 0x2420, 0x0018,
        0x2400, 0x0018, 0x2403, 0x0018, 0x2400, 0x0018, 0x2430, 0x0018,
        0x2400, 0x0018, 0x2404, 0x0018, 0x2400, 0x0018, 0x2440, 0x0018,
        0x2400, 0x0018, 0x2405, 0x0018, 0x2400, 0x0018, 0x2450, 0x0018,
        0x2400, 0x0018, 0x2406, 0x0018, 0x2400, 0x0018, 0x2460, 0x0018,
        0x2400, 0x0018, 0x2407, 0x0018, 0x2400, 0x0018, 0x2470, 0x0018,
        0x2400, 0x0018, 0x2408, 0x0018, 0x2400, 0x0018, 0x2480, 0x0018,
        0x0001, // Anti-glitch
        0x23FC, // $FC -> $2003 - set last sprite
        0x24FF, 0x0018, 0x24FF, 0x0018, 0x24FF, 0x0018, 0x24FF, 0x0018,
        0x3200, // read $2002
        0x2500, // $00 -> $2005
        0x2500, // $00 -> $2005
        0x2090, // $90 -> $2000
        0x211E, // $1E -> $2001
        0x00FF, // terminator
    };

    private readonly Macros _macros;
    private ushort _address;
    private int _counter;
    private ushort _current;

    public CpuCommands(Macros macros)
    {
        _macros = macros;
    }

    // The I/O bus is attached to the CPU, so operate it the way a RP2A03 would
    // Each I/O lasts 24 clock edges - cycle 0 for setup, cycle 9 for read/write, and cycle 24 for end
    public void Next()
    {
        if ((_counter == 0) && (_address < cpucmd_program.Length))
        {
            _current = cpucmd_program[_address];
            if ((_current & 0x3000) != 0)
            {
                _counter = 24;
            }
            else
            {
                _counter = _current & 0x7FF;
                _macros.FloatBits(NodeGroups.io_db);
            }
        }
        if (_counter > 0)
        {
            var ce = ((_current & 0x2000) >> 13) != 0;
            var rw = ((_current & 0x1000) >> 12) != 0;
            var a = (byte)((_current & 0x700) >> 8);
            var d = (byte)(_current & 0xFF);
            if ((_counter == 24) && ce)
            {
                _macros.WriteBits(NodeGroups.io_ab, a);
                if (rw)
                {
                    _macros.FloatBits(NodeGroups.io_db);
                }
                else
                {
                    _macros.WriteBits(NodeGroups.io_db, d);
                }
                _macros.SetNode(NodeName.io_rw, rw);
            }
            if ((_counter == 16) && ce)
            {
                _macros.SetNode(NodeName.io_ce, false);
            }
            if (_counter == 1)
            {
                if (rw)
                {
                    d = _macros.ReadBits<byte>(NodeGroups.io_db);
                    // store result in the test program
                    //cpucmd_setCellValue(cpucmd_address * 8 + 5, d);
                }
                _macros.SetNode(NodeName.io_ce, true);
            }
            _counter--;
            if (_counter == 0)
                _address++;
        }
    }
}
