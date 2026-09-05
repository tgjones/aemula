using Aemula.Emulation.Chips;

namespace Aemula.Emulation.Systems.AppleI;

// The character-memory delay line: seven Signetics2504Chip (six character-
// code bit-planes plus one cursor tracker), one Signetics2519Chip line
// buffer, a Ds0025Chip two-phase clock driver, and the write mechanism that
// lets the CPU inject a new character into the recirculating stream at the
// cursor's position - designators ICC3/ICC4/ICC11A/ICC11B/ICC14/ICD4A/
// ICD4B/ICD5A/ICD5B/ICD14A/ICD14B on the schematic.
//
// Confirmed from the schematic (http://retro.hansotten.nl/uploads/apple1/a1
// %20circuit.pdf, Terminal Section): ICC4 and ICC14 are 74157 quad 2:1
// muxes whose S select is a net drawn with an overline ("WRITE-bar") and
// whose OE-bar is tied to a net named "CLR" - i.e. the mux passes PIA
// display data (RD1-RD4 into ICC4; RD5/RD7 into two of ICC14's four
// channels) through to each 2504's IN pin when WRITE-bar is false (a write
// is committing this cycle), and each 2504's own OUT (self-recirculate)
// through when WRITE-bar is true. One PIA data bit (RD6/PB5) is genuinely
// not wired to either mux - six of the seven PIA display bits carry the
// character code, not all seven. ICC14 also has a spare channel wired
// I0=GND/I1=CURS, i.e. it forces the cursor bit to 0 during a write and
// passes it through unchanged otherwise - confirmed directly from the
// schematic. What ICC14's spare channel doesn't explain on its own is how
// the cursor bit gets set again one position later: Chris Espinosa (an
// original Apple engineer, quoted at
// https://www.righto.com/2022/04/inside-apple-1s-shift-register-memory.html)
// describes "flip-flops 2 and 3 at C13 [ICC13, a 74175] re-set the cursor
// bit on the next character clock; this advances the cursor one position" -
// confirmed as involving ICC13 and an AND gate (ICC12:C) producing a net
// named "WC2" from the schematic, but the exact gate truth table wasn't
// fully pinned down from the rendered schematic tiles in the time available.
// Reproduced here as equivalent C# sequencing (clear on the write cycle, set
// exactly one character-clock later) rather than a literal gate netlist.
public sealed partial class AppleISystem
{
    // ICD5A, ICD5B, ICD4A, ICD4B, ICD14A, ICD14B - the six character-code
    // bit-planes. Index order is this class's own choice (not confirmed
    // pin-for-pin from the schematic beyond "six of PB0-PB6, skipping
    // PB5") - see CharacterDataPiaBit below. Self-consistent between write
    // and read, which is what correctness actually depends on.
    private readonly Signetics2504Chip[] _characterBits =
    [
        new Signetics2504Chip(),
        new Signetics2504Chip(),
        new Signetics2504Chip(),
        new Signetics2504Chip(),
        new Signetics2504Chip(),
        new Signetics2504Chip(),
    ];

    // ICC11B - the seventh 2504, carrying a single circulating 1-bit cursor
    // marker rather than a character-code bit-plane (see the file header).
    private readonly Signetics2504Chip _cursorBit = new();

    // ICC3 - buffers the 40 character codes of whatever row the character-
    // code bit-planes are currently outputting. Wired for real (its IN pins
    // do follow the character bits' OUT pins, matching the schematic), but
    // nothing currently reads its own OUT pins back out - see
    // AppleISystem.Video.cs for why the video draw path reads the character
    // bit-plane rings directly instead.
    private readonly Signetics2519Chip _lineBuffer = new();

    // ICC11A - two-phase clock driver for the whole bank above. Its two
    // channels invert (see Ds0025Chip's remarks); that inversion has no
    // observable effect here since nothing but this file's own Phi1/Phi2
    // pulses ever reads Out1/Out2.
    private readonly Ds0025Chip _shiftClockDriver = new();

    // ICC4 (four channels, driving the first four character bit-planes) and
    // ICC14 (two of its four channels, driving the remaining two - the
    // other two are the schematic's cursor-clear channel, reproduced
    // separately below rather than through this chip; see the file header).
    private readonly Ttl74157Chip _writeMuxA = new();
    private readonly Ttl74157Chip _writeMuxB = new();

    // Which PIA PB bit (of PB0-PB6) each entry of _characterBits reads from
    // - PB5 (RD6 on the schematic) is the one bit that's genuinely not
    // wired to either write mux; see the file header.
    private static readonly int[] CharacterDataPiaBit = [0, 1, 2, 3, 4, 6];

    // Set on CB2's falling edge (Mos6820Chip's own handshake-mode logic
    // pulses CB2 low the instant the CPU commits a write to $D012/ORB) and
    // cleared once that byte actually gets latched into the character bits,
    // which can only happen on the specific character-clock where the
    // recirculating cursor bit is the one currently at the write point -
    // i.e. real, hardware-accurate variable latency, up to just over one
    // full 1024-cycle rotation in the worst case. Also driven out onto PB7
    // (PIA.PortB's one input-configured bit) every cycle, since disassembling
    // the real WozMon ROM's ECHO routine ($FFEF: BIT $D012 / BMI $FFEF/
    // STA $D012) shows the CPU spins while bit 7 reads 1 and writes once it
    // reads 0 - i.e. bit 7 is a busy flag, not a ready flag.
    private bool _pendingWrite;

    // True for exactly the one character-clock following a committed write -
    // see the file header's ICC13/WC2 discussion.
    private bool _cursorSetPending;

    private bool _lastCb2 = true;

    // Bookkeeping only - not extra hardware state. The real chips only ever
    // know "the bit at Out right now"; this just names which of the 1024
    // ring positions that bit is, so AppleISystem.Video.cs's simplified
    // direct-index read (see that file) can find the same character a write
    // here just landed on. Wraps without scrolling - see that file's remarks
    // on the scroll gap.
    private int _ringPosition;

    internal bool CursorOutForTests => _cursorBit.Out;
    internal int RingPositionForTests => _ringPosition;

    // A pure recirculating register has no power-on state of its own to
    // fall back on (see Signetics2504Chip.Poke's remarks) - real hardware
    // must have some reset-time path that seeds the cursor ring with
    // exactly one '1' bit, or CURS could never become true and nothing
    // could ever be typed. Seeded at the position _ringPosition (itself
    // reset to 0 by field initialization) will read as row 0, column 0 on
    // the very next character-time, so the cursor starts at the top-left
    // of the screen.
    private void ResetCharacterMemory()
    {
        foreach (var chip in _characterBits)
        {
            chip.Clear();
        }

        _cursorBit.Clear();
        _cursorBit.Poke(1023, true);

        _pendingWrite = false;
        _cursorSetPending = false;
        _lastCb2 = true;
        _ringPosition = 0;
    }

    private void TickCharacterMemory()
    {
        var cb2 = Pia.Cb2;
        if (_lastCb2 && !cb2)
        {
            _pendingWrite = true;
        }
        _lastCb2 = cb2;

        var cursorHere = _cursorBit.Out;
        var commitWrite = _pendingWrite && cursorHere;
        var writeBar = !commitWrite;

        _writeMuxA.G = false;
        _writeMuxA.S = writeBar;
        _writeMuxA.A1 = ReadCharacterDataBit(0);
        _writeMuxA.B1 = _characterBits[0].Out;
        _writeMuxA.A2 = ReadCharacterDataBit(1);
        _writeMuxA.B2 = _characterBits[1].Out;
        _writeMuxA.A3 = ReadCharacterDataBit(2);
        _writeMuxA.B3 = _characterBits[2].Out;
        _writeMuxA.A4 = ReadCharacterDataBit(3);
        _writeMuxA.B4 = _characterBits[3].Out;

        _writeMuxB.G = false;
        _writeMuxB.S = writeBar;
        _writeMuxB.A1 = ReadCharacterDataBit(4);
        _writeMuxB.B1 = _characterBits[4].Out;
        _writeMuxB.A2 = ReadCharacterDataBit(5);
        _writeMuxB.B2 = _characterBits[5].Out;

        _characterBits[0].In = _writeMuxA.Y1;
        _characterBits[1].In = _writeMuxA.Y2;
        _characterBits[2].In = _writeMuxA.Y3;
        _characterBits[3].In = _writeMuxA.Y4;
        _characterBits[4].In = _writeMuxB.Y1;
        _characterBits[5].In = _writeMuxB.Y2;

        _cursorBit.In = commitWrite ? false : _cursorSetPending || cursorHere;
        _cursorSetPending = commitWrite;

        _lineBuffer.In1 = _characterBits[0].Out;
        _lineBuffer.In2 = _characterBits[1].Out;
        _lineBuffer.In3 = _characterBits[2].Out;
        _lineBuffer.In4 = _characterBits[3].Out;
        _lineBuffer.In5 = _characterBits[4].Out;
        _lineBuffer.In6 = _characterBits[5].Out;

        PulseCharacterMemoryClock();

        if (commitWrite)
        {
            _pendingWrite = false;

            // Restores CB2 high (handshake mode's "paired C1 active
            // transition" - see Mos6820Chip's remarks) on CB1's configured
            // active edge (rising, per WozMon's own CRB init). CB1's IRQ1
            // flag also sets as a side effect, same as real 6821 behaviour,
            // but Pia.Irqb is never wired to Cpu.Irq anywhere in this
            // system - no net for it was found on the schematic - so it
            // has no observable effect.
            Pia.Cb1 = false;
            Pia.Cb1 = true;
        }

        Pia.PB = (byte)(_pendingWrite ? 0x80 : 0x00);

        _ringPosition = (_ringPosition + 1) % 1024;
    }

    private bool ReadCharacterDataBit(int index) =>
        (Pia.PortB & (1 << CharacterDataPiaBit[index])) != 0;

    // Routed through the real Ds0025Chip so its inversion (see that class's
    // remarks) is actually load-bearing here, not just instantiated and
    // ignored: driving its inputs high-then-low is what produces the
    // rising edge on Phi1/Phi2 that the 2504s shift on.
    private void PulseCharacterMemoryClock()
    {
        _shiftClockDriver.In1 = true;
        _shiftClockDriver.In2 = true;
        SetPhase1(_shiftClockDriver.Out1);
        SetPhase2(_shiftClockDriver.Out2);

        _shiftClockDriver.In1 = false;
        _shiftClockDriver.In2 = false;
        SetPhase1(_shiftClockDriver.Out1);
        SetPhase2(_shiftClockDriver.Out2);

        _lineBuffer.Clk = false;
        _lineBuffer.Clk = true;
    }

    private void SetPhase1(bool value)
    {
        foreach (var chip in _characterBits)
        {
            chip.Phi1 = value;
        }
        _cursorBit.Phi1 = value;
    }

    private void SetPhase2(bool value)
    {
        foreach (var chip in _characterBits)
        {
            chip.Phi2 = value;
        }
        _cursorBit.Phi2 = value;
    }

    // See AppleISystem.Video.cs. logicalPosition is a ring position in the
    // same sense _ringPosition is - "whichever position was at the write
    // point on the character-time this data was (or would be) committed" -
    // not a fixed array slot: a genuinely recirculating register has no
    // such thing, since Signetics2504Chip.Phi2 physically moves every bit
    // one array slot on every clock. A bit written while _ringPosition read
    // L is at array index ((current _ringPosition) - 1 - L) mod 1024 - 0
    // the tick right after it's written, all the way up to 1023 (Out) one
    // full rotation later, which is what makes L a stable enough concept to
    // call a "position" at all.
    internal byte PeekCharacterCode(int logicalPosition)
    {
        var arrayIndex = ((_ringPosition - 1 - logicalPosition) % 1024 + 1024) % 1024;

        byte value = 0;

        for (var i = 0; i < _characterBits.Length; i++)
        {
            if (_characterBits[i].Peek(arrayIndex))
            {
                value |= (byte)(1 << i);
            }
        }

        return value;
    }
}
