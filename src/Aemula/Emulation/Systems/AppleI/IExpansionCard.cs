namespace Aemula.Emulation.Systems.AppleI;

/// <summary>
/// A card plugged into the Apple I's single 44-pin expansion connector. The
/// Apple I has exactly one such connector, so <see cref="AppleISystem"/> holds
/// at most one of these.
/// </summary>
/// <remarks>
/// <para>
/// The connector carries the raw 6502 bus. Rather than a data struct, the
/// electrically-interesting pins are modelled as properties in the same idiom
/// as the chip classes: connector inputs are <c>set</c>-only (a card does its
/// edge-triggered work inside the <see cref="Phi2"/> setter, exactly as
/// <c>Mos6820Chip</c> works off its <c>E</c> pin), and anything the card can
/// drive back onto an open-collector line is a nullable output where
/// <c>null</c> means "not driving this cycle" (the tri-state convention
/// <c>Ttl8T97Chip</c> uses).
/// </para>
/// <para>
/// <see cref="AppleISystem"/> drives every input once per φ2 CPU memory cycle
/// and then pulses <see cref="Phi2"/>. That is the only rate a card is clocked
/// at: no expansion card on this machine has a free-running clock of its own.
/// </para>
/// </remarks>
public interface IExpansionCard
{
    /// <summary>Address bus, A0-A15.</summary>
    ushort Address { set; }

    /// <summary>Read/write line: <see langword="true"/> on a CPU read cycle.</summary>
    bool RW { set; }

    /// <summary>RDY. Not used by the cassette card; present for future cards.</summary>
    bool Rdy { set; }

    /// <summary>RES#, low while the machine is held in reset.</summary>
    bool ResetBar { set; }

    /// <summary>DMA#. Not used by the cassette card; present for future cards.</summary>
    bool DmaBar { set; }

    /// <summary>
    /// The shared data bus, D0-D7 (see <c>Mos6820Chip.DB</c> for the same
    /// bidirectional-latch treatment). <see cref="AppleISystem"/> writes the
    /// current bus byte here before pulsing <see cref="Phi2"/>; on a read cycle
    /// the card the address selects overwrites it with its answer, and
    /// <see cref="DrivesData"/> then says whether to hand that byte to the CPU.
    /// </summary>
    byte DataBus { get; set; }

    /// <summary>
    /// The φ2 clock. The setter is where the card does its per-cycle work;
    /// <see cref="AppleISystem"/> pulses it false→true→false each CPU cycle.
    /// </summary>
    bool Phi2 { set; }

    /// <summary>
    /// <see langword="true"/> during a cycle in which the card answered a read
    /// (its own address decode matched), so <see cref="AppleISystem"/> should
    /// take <see cref="DataBus"/> as the CPU's read data instead of the
    /// motherboard's open-bus value.
    /// </summary>
    bool DrivesData { get; }

    /// <summary>
    /// Open-collector IRQ#: <c>false</c> while the card is pulling the line
    /// low, <c>null</c> otherwise. Not asserted by the cassette card.
    /// </summary>
    bool? IrqBar { get; }

    /// <summary>
    /// A card that can hold the CPU (a DMA card) drives RDY low here;
    /// <c>null</c> otherwise. Not asserted by the cassette card.
    /// </summary>
    bool? RdyOut { get; }

    /// <summary>Return the card to its power-on state.</summary>
    void Reset();
}
