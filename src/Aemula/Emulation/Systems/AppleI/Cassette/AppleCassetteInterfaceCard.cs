using System;
using Aemula.Emulation.Chips;
using Aemula.Emulation.Systems.AppleI.Roms;

namespace Aemula.Emulation.Systems.AppleI.Cassette;

/// <summary>
/// The Apple Cassette Interface: the small card Woz sold to plug into the Apple
/// I's expansion connector for loading and saving programs on an ordinary audio
/// cassette recorder.
/// </summary>
/// <remarks>
/// <para>
/// On the card: two 256×4 PROMs holding the firmware (<see cref="AciRom"/>), one
/// 74LS74 dual flip-flop (one half is the tape-out bit toggled on every I/O
/// access, the other synchronises the comparator), and an LM311 comparator that
/// squares the incoming tape audio into a logic level.
/// </para>
/// <para>
/// It decodes $C000-$C1FF off the connector. $C100-$C1FF reads the firmware
/// directly. $C000-$C0FF is I/O: every access toggles the tape-out flip-flop,
/// and on a read the synchronised comparator output replaces PROM address line
/// A0, so the firmware polls e.g. $C081 and watches the returned byte flip as
/// the tape level flips. The FSK bit timing is entirely in the firmware's delay
/// loops; the card has no timing element of its own.
/// </para>
/// <para>
/// The two audio jacks are not modelled here - a <c>CassetteDeck</c> peripheral
/// is patched to <see cref="CassetteInput"/> and <see cref="CassetteOutput"/> by
/// whoever assembles the rig. Unpatched, the jacks are dead: the comparator sees
/// silence and the tape-out signal goes nowhere.
/// </para>
/// <para>
/// The address decode is modelled behaviourally (window compare + the A8 split)
/// rather than as individual gates - the ACI schematic wasn't transcribed
/// gate-by-gate - but the 74LS74 and LM311, which is where the interesting
/// timing/analog behaviour lives, are the real chip models.
/// </para>
/// </remarks>
public sealed class AppleCassetteInterfaceCard : IExpansionCard
{
    private readonly byte[] _rom = AciRom.Image;
    private readonly Ttl7474Chip _flipFlops = new();
    private readonly Lm311Chip _comparator = new();

    /// <summary>
    /// The tape-in lead: the incoming tape level for this cycle, sampled into
    /// the comparator. Set by the rig assembler to a connected deck's playback
    /// lead; unpatched, returns silence.
    /// </summary>
    public Func<float> CassetteInput { get; set; } = static () => 0f;

    /// <summary>
    /// The tape-out lead: the tape-out flip-flop level (1 or 0) for this cycle.
    /// Set by the rig assembler to a connected deck's record lead; unpatched,
    /// discarded.
    /// </summary>
    public Action<float> CassetteOutput { get; set; } = static _ => { };

    public ushort Address { private get; set; }

    public bool RW { private get; set; }

    public bool Rdy { set { } }

    public bool ResetBar { set { } }

    public bool DmaBar { set { } }

    public byte DataBus { get; set; }

    public bool DrivesData { get; private set; }

    public bool? IrqBar => null;

    public bool? RdyOut => null;

    private bool _phi2;

    public bool Phi2
    {
        set
        {
            var risingEdge = value && !_phi2;
            _phi2 = value;
            if (risingEdge)
            {
                ClockCycle();
            }
        }
    }

    private void ClockCycle()
    {
        // The comparator runs continuously off the tape input; sample the tape
        // one φ2 cycle's worth and re-clock the synchroniser regardless of what
        // the CPU is addressing.
        _comparator.Input = CassetteInput();
        _flipFlops.D2 = _comparator.Out;
        _flipFlops.Clk2 = false;
        _flipFlops.Clk2 = true;

        var cardSelected = (Address & 0xFE00) == 0xC000; // $C000-$C1FF
        var ioAccess = cardSelected && (Address & 0x0100) == 0; // $C000-$C0FF

        DrivesData = false;

        if (ioAccess)
        {
            // Any access here toggles the tape-out flip-flop (Q -> D).
            _flipFlops.D1 = _flipFlops.Qn1;
            _flipFlops.Clk1 = false;
            _flipFlops.Clk1 = true;

            if (RW)
            {
                // The synchronised comparator output drives PROM A0 in this
                // region, replacing the CPU's A0.
                var index = (Address & 0xFE) | (_flipFlops.Q2 ? 1 : 0);
                DataBus = _rom[index];
                DrivesData = true;
            }
        }
        else if (cardSelected && RW)
        {
            // $C100-$C1FF: firmware, addressed normally.
            DataBus = _rom[Address & 0xFF];
            DrivesData = true;
        }

        // The tape-out flip-flop only ever moves on an I/O access above, so
        // pushing its level every cycle is the same signal as pushing it only
        // when it changes - the deck sees a steady line between toggles.
        CassetteOutput(_flipFlops.Q1 ? 1f : 0f);
    }

    /// <summary>
    /// The byte this card would return for a read of <paramref name="address"/>
    /// without disturbing any state - for the debugger's memory view. Null if
    /// the card doesn't decode the address.
    /// </summary>
    public byte? PeekDebug(ushort address)
    {
        var cardSelected = (address & 0xFE00) == 0xC000;
        if (!cardSelected)
        {
            return null;
        }

        var index = (address & 0x0100) == 0
            ? (address & 0xFE) | (_flipFlops.Q2 ? 1 : 0)
            : address & 0xFF;

        return _rom[index];
    }

    public void Reset()
    {
        _flipFlops.Clr1 = false;
        _flipFlops.Clr1 = true;
        _flipFlops.Clr2 = false;
        _flipFlops.Clr2 = true;
        _comparator.Reset();
    }
}
