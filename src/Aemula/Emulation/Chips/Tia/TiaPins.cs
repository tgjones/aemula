namespace Aemula.Emulation.Chips.Tia;

public struct TiaPins
{
    /// <summary>
    /// Ready pin.
    /// </summary>
    public bool Rdy;

    /// <summary>
    /// Combined vertical and horizontal sync pin.
    /// </summary>
    public bool Sync;

    /// <summary>
    /// Address pins A0..A5.
    /// </summary>
    public byte Address;

    /// <summary>
    /// Processor data pins. Pins 0 to 5 are inputs only.
    /// </summary>
    public byte Data05;

    /// <summary>
    /// Processor data pins. Pins 6 and 7 are bidirectional.
    /// </summary>
    public byte Data67;

    /// <summary>
    /// Read/write pin. Read = true, write = false.
    /// </summary>
    public bool RW;

    /// <summary>
    /// Video luminance output (3 pins, LUM0..LUM2).
    /// </summary>
    public byte Lum;

    // TODO: This should be a single pin. From the spec:
    // "A digital phase shifter is included on this chip to provide a
    // single color output with fifteen (15) phase angles."
    // But for now we just output a 4-bit colour.
    /// <summary>
    /// Video color output.
    /// </summary>
    public byte Col;

    /// <summary>
    /// Combined vertical and horizontal blank output.
    /// </summary>
    public bool Blk;

    /// <summary>
    /// Color delay input.
    /// </summary>
    public bool Del;

    /// <summary>
    /// Audio output 0.
    /// </summary>
    public bool Aud0;

    /// <summary>
    /// Audio output 1.
    /// </summary>
    public bool Aud1;

    // TODO: May need to split these into separate pins.
    /// <summary>
    /// Dumped and latched inputs.
    /// Dumped inputs (I0..I3) are used for paddles.
    /// Latched inputs (I4..I5) are used for joystick / paddle triggers.
    /// </summary>
    public byte I;
}
