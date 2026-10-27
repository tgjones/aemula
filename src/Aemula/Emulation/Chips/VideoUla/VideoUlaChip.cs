namespace Aemula.Emulation.Chips.VideoUla;

/// <summary>
/// The BBC Micro's video ULA (IC6, Ferranti "VIDPROC"), reduced to what the
/// rest of the board needs from it before any video is generated: the clocks
/// it divides down from the 16MHz crystal oscillator, and its two write-only
/// registers.
///
/// Its 8MHz, 4MHz and 2MHz outputs are the board's only source of clocks: the
/// 6502's clock is made from the 2MHz output by the synchroniser built around
/// IC30/IC34, and the 1MHz bus clock (1MHZE) is that same output divided by
/// two. The 6845's character clock (CCLK) is also made here: 1MHz, or 2MHz
/// when bit 4 of the control register is set.
///
/// The three divided clocks come from one free-running 4-bit counter, so every
/// output changes on a rising edge of the 16MHz clock with the 8MHz output
/// rising on odd counts and the 2MHz output on count 4. The real chip's
/// propagation delays between its outputs are not modelled.
/// </summary>
public sealed class VideoUlaChip
{
    private const byte ControlCrtcClock2MHz = 0x10;

    private byte _counter;

    /// <summary>
    /// Advances the divider by one cycle of the 16MHz clock.
    /// </summary>
    public void Tick()
    {
        _counter = (byte)((_counter + 1) & 0xF);
    }

    /// <summary>
    /// 8MHz clock output (pin 7).
    /// </summary>
    public bool Clock8MHz => (_counter & 1) != 0;

    /// <summary>
    /// 4MHz clock output (pin 6).
    /// </summary>
    public bool Clock4MHz => (_counter & 2) != 0;

    /// <summary>
    /// 2MHz clock output (pin 5).
    /// </summary>
    public bool Clock2MHz => (_counter & 4) != 0;

    /// <summary>
    /// Character clock for the 6845 (CCLK, pin 28).
    /// </summary>
    public bool CrtcClock => (Control & ControlCrtcClock2MHz) != 0
        ? Clock2MHz
        : (_counter & 8) != 0;

    /// <summary>
    /// The control register, written at &amp;FE20.
    /// </summary>
    public byte Control { get; private set; }

    /// <summary>
    /// The palette register, written at &amp;FE21: the last value written.
    /// </summary>
    public byte Palette { get; private set; }

    /// <summary>
    /// A write from the CPU. <paramref name="a0"/> (the chip's A0 pin) selects
    /// the palette register rather than the control register.
    /// </summary>
    public void Write(bool a0, byte value)
    {
        if (a0)
        {
            Palette = value;
        }
        else
        {
            Control = value;
        }
    }
}
