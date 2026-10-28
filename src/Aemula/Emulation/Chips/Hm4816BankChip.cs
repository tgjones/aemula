namespace Aemula.Emulation.Chips;

/// <summary>
/// Eight 16K x 1 dynamic RAMs (HM4816, a 4116 equivalent) sharing their
/// address pins and strobes, so that chip n holds bit n of every byte. The
/// 14-bit cell address arrives as two 7-bit halves on the same pins: the row,
/// latched as /RAS falls, then the column, latched as /CAS falls. Refresh is
/// not modelled - the cells never decay.
///
/// A read drives the data outputs from the falling edge of /CAS until /CAS
/// rises. A write is an early write: /WE already low as /CAS falls stores the
/// data inputs, and the outputs stay high-impedance.
/// </summary>
public sealed class Hm4816BankChip
{
    private readonly byte[] _cells = new byte[0x4000];

    private byte _row;
    private bool _outputEnabled;
    private byte _output;

    private bool _rasBar = true;

    /// <summary>
    /// Row address strobe, active low.
    /// </summary>
    public bool RasBar
    {
        set
        {
            if (_rasBar && !value)
            {
                _row = Address;
            }

            _rasBar = value;
        }
    }

    private bool _casBar = true;

    /// <summary>
    /// Column address strobe, active low.
    /// </summary>
    public bool CasBar
    {
        set
        {
            if (_casBar && !value && !_rasBar)
            {
                var cell = (Address << 7) | _row;

                if (!WeBar)
                {
                    _cells[cell] = Din;
                    _outputEnabled = false;
                }
                else
                {
                    _output = _cells[cell];
                    _outputEnabled = true;
                }
            }
            else if (value)
            {
                _outputEnabled = false;
            }

            _casBar = value;
        }
    }

    /// <summary>
    /// Write enable, active low.
    /// </summary>
    public bool WeBar { private get; set; } = true;

    /// <summary>
    /// The multiplexed address pins A0-A6.
    /// </summary>
    public byte Address { private get; set; }

    /// <summary>
    /// The data inputs, one bit per chip.
    /// </summary>
    public byte Din { private get; set; }

    /// <summary>
    /// The data outputs, one bit per chip; null while high-impedance.
    /// </summary>
    public byte? Dout => _outputEnabled ? _output : null;

    /// <summary>
    /// The stored byte at a cell address (column in bits 7-13, row in bits
    /// 0-6), without side effects, for tests and the debugger.
    /// </summary>
    public byte Peek(int cell) => _cells[cell & 0x3FFF];

    public void Poke(int cell, byte value) => _cells[cell & 0x3FFF] = value;
}
