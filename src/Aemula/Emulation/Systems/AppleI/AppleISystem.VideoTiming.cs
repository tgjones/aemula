using Aemula.Emulation.Chips;

namespace Aemula.Emulation.Systems.AppleI;

// The horizontal/vertical counter chain (ICD6-ICD9, ICD11, ICD15 on the
// schematic) that generates composite sync. Traced from
// http://retro.hansotten.nl/uploads/apple1/a1%20circuit.pdf (Terminal
// Section, sheet 1 of 3).
//
// Confirmed from the schematic: a Pierce oscillator (crystal ZQ1 plus a pair
// of 7404 inverters, ICD12:B/ICD12:C) produces the 14.31818MHz dot clock;
// ICD6 (74160) and ICD7 (74161) are synchronously cascaded (ICD7's ENT tied
// to ICD6's RCO) off one shared clock, same for ICD11/ICD15, and ICD8/ICD9
// feed six Q-outputs labelled V0-V5 - almost certainly the 2513 character
// generator's A4-A9 address bits. What the rendered schematic tiles didn't
// resolve at readable pin-for-pin detail is *which* clock the ICD6-9/11/15
// bank actually shares (the traceable wire leaves sheet 1 as a labelled net,
// "O2", that on sheet 2 immediately disappears into a jumper-selectable
// cluster - ICC1 (7404) plus four discrete transistors - that's only
// populated for a 6800 substitution, per the schematic's own Note 7). So
// rather than a gate-for-gate reproduction, the divide-by-14 here is done
// directly (dot clock / 14, exactly matching the 6502's real 1.022727MHz
// with no remainder - unlike Apple II, there's no long-cycle stretch to
// reproduce), and the two counters' preset-reload values are chosen to land
// on a clean 65-character-time line - the same line length Apple II uses
// off the same crystal for the same NTSC target - rather than the literal
// PE wiring (illegible at the available render resolution). HSync/VSync are
// decoded from the resulting counts rather than the exact gate netlist
// (ICC5:A/ICC9:B/ICC10 and friends) for the same reason. ICD8/ICD9's exact
// reset/comparison behaviour is real character-memory-write territory (a
// cursor-column comparator built from several more gates and two more
// registers) - left free-running here and picked back up once that
// comparator is wired up.
public sealed partial class AppleISystem
{
    // ICD6/ICD7: horizontal character-position counter. Cascaded (ICD7.Ent =
    // ICD6.Rco) so together they act as one mod-65 counter - 65 being the
    // confirmed character-times-per-line count (14.31818MHz / 14 dots per
    // character-time / 65 character-times per line lands exactly on NTSC's
    // 15734.26Hz line rate).
    private readonly Ttl74160Chip _horizontalCounterLow;
    private readonly Ttl74161Chip _horizontalCounterHigh;

    // ICD8/ICD9: the character-generator address counter (V0-V5). Cascaded
    // the same way, free-running mod 256 - the real reset/compare logic is
    // the character-memory delay line's write-cursor comparator, not yet
    // wired up.
    private readonly Ttl74161Chip _characterAddressLow;
    private readonly Ttl74161Chip _characterAddressHigh;

    // ICD11/ICD15: vertical line counter. Cascaded (ICD15.Ent = ICD11.Rco),
    // clocked once per horizontal line (off ICD7's RCO) rather than every
    // character-time. Free-running mod 256 lines/frame - a round-number
    // stand-in for NTSC's 262/263, which two 4-bit stages can't reach (max
    // 255) without preset-reload wiring this build doesn't have confirmed.
    private readonly Ttl74161Chip _verticalCounterLow;
    private readonly Ttl74161Chip _verticalCounterHigh;

    // Divides the 14.31818MHz dot clock by 14 into the shared clock for the
    // CPU and all six counters above - see the file header for why this is
    // done directly rather than as a traced gate chain.
    private byte _dotDivider;
    private bool _lastCharacterRate;

    public bool HSync => HorizontalCount >= 155;

    public bool VSync => VerticalCount >= 252;

    public bool CompositeSync => HSync || VSync;

    internal bool Phi0ForTests => _dotDivider < 7;

    private int HorizontalCount =>
        NibbleValue(_horizontalCounterHigh.Qa, _horizontalCounterHigh.Qb, _horizontalCounterHigh.Qc, _horizontalCounterHigh.Qd) * 10 +
        NibbleValue(_horizontalCounterLow.Qa, _horizontalCounterLow.Qb, _horizontalCounterLow.Qc, _horizontalCounterLow.Qd);

    private int VerticalCount =>
        (NibbleValue(_verticalCounterHigh.Qa, _verticalCounterHigh.Qb, _verticalCounterHigh.Qc, _verticalCounterHigh.Qd) << 4) |
        NibbleValue(_verticalCounterLow.Qa, _verticalCounterLow.Qb, _verticalCounterLow.Qc, _verticalCounterLow.Qd);

    private static int NibbleValue(bool qa, bool qb, bool qc, bool qd) =>
        (qa ? 1 : 0) | (qb ? 2 : 0) | (qc ? 4 : 0) | (qd ? 8 : 0);

    private void TickVideoTiming()
    {
        var characterRate = _dotDivider < 7;
        var characterRateRisingEdge = characterRate && !_lastCharacterRate;
        _lastCharacterRate = characterRate;

        Cpu.Phi0 = characterRate;

        if (characterRateRisingEdge)
        {
            DoCpuMemoryAccess();
            TickCounters();
        }

        _dotDivider++;
        if (_dotDivider == 14)
        {
            _dotDivider = 0;
        }
    }

    private void TickCounters()
    {
        _horizontalCounterLow.Enp = true;
        _horizontalCounterLow.Ent = true;

        _horizontalCounterHigh.Enp = true;
        _horizontalCounterHigh.Ent = _horizontalCounterLow.Rco;

        // Reloads both stages together the tick after the combined count
        // hits its natural max (159), landing back on a preset (95) chosen
        // so the wrap spans exactly 65 states - see the file header.
        var horizontalReload = _horizontalCounterHigh.Rco;

        _horizontalCounterLow.Load = !horizontalReload;
        _horizontalCounterLow.A = true;  // preset 5 = 0b0101
        _horizontalCounterLow.B = false;
        _horizontalCounterLow.C = true;
        _horizontalCounterLow.D = false;

        _horizontalCounterHigh.Load = !horizontalReload;
        _horizontalCounterHigh.A = true; // preset 9 = 0b1001
        _horizontalCounterHigh.B = false;
        _horizontalCounterHigh.C = false;
        _horizontalCounterHigh.D = true;

        _characterAddressLow.Enp = true;
        _characterAddressLow.Ent = true;

        _characterAddressHigh.Enp = true;
        _characterAddressHigh.Ent = _characterAddressLow.Rco;

        _verticalCounterLow.Enp = true;
        _verticalCounterLow.Ent = horizontalReload;

        _verticalCounterHigh.Enp = true;
        _verticalCounterHigh.Ent = _verticalCounterLow.Rco;

        PulseClock(_horizontalCounterLow);
        PulseClock(_horizontalCounterHigh);
        PulseClock(_characterAddressLow);
        PulseClock(_characterAddressHigh);
        PulseClock(_verticalCounterLow);
        PulseClock(_verticalCounterHigh);
    }

    private static void PulseClock(Ttl74160Chip chip)
    {
        chip.Clk = false;
        chip.Clk = true;
    }

    private static void PulseClock(Ttl74161Chip chip)
    {
        chip.Clk = false;
        chip.Clk = true;
    }
}
