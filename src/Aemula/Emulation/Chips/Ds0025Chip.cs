namespace Aemula.Emulation.Chips;

/// <summary>
/// Dual high-current MOS clock driver: level-shifts a TTL-level input up to
/// the swing a dynamic PMOS shift register needs. Per the datasheet's
/// internal schematic (with the TTL input in logic "0", Q1 is off and Q2
/// pulls the output high toward V+; when the input goes high, Q1 turns on
/// and pulls the output low toward V-) each channel inverts - National's own
/// application notes on this part call this out explicitly ("MOS logic is
/// inverted from normal TTL"). Propagation delay is out of scope, same as
/// every other TTL-ish part in this codebase; the two channels are
/// otherwise independent pass-throughs, matching the real chip.
/// </summary>
public sealed class Ds0025Chip
{
    public bool In1 { private get; set; }
    public bool Out1 => !In1;

    public bool In2 { private get; set; }
    public bool Out2 => !In2;
}
