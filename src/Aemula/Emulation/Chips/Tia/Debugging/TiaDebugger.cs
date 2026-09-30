using Aemula.Debugging;
using Aemula.Debugging.LogicAnalyzer;

namespace Aemula.Emulation.Chips.Tia.Debugging;

public sealed class TiaDebugger(TiaChip tia) : ChipDebugger
{
    public override ChannelGroup CreateChannelGroup()
    {
        return new ChannelGroup("TIA",
        [
            Channel.Bus("Address", 6, () => tia.Address),
            Channel.Bus("Data0-5", 6, () => tia.Data05),
            Channel.Bus("Data6-7", 2, () => tia.Data67),
            Channel.Digital("R/W", () => tia.RW),
            Channel.Digital("RDY", () => tia.Rdy),
            Channel.Digital("SYNC", () => tia.Sync),
            Channel.Digital("BLK", () => tia.Blk),
            Channel.Bus("LUM", 3, () => tia.Lum),
            Channel.Bus("COL", 4, () => tia.Col),
            Channel.Digital("DEL", () => tia.Del),
            Channel.Digital("AUD0", () => tia.Aud0),
            Channel.Digital("AUD1", () => tia.Aud1),
            Channel.Bus("I", 6, () => tia.I),
            Channel.Digital("CS0", () => tia.CS0),
            Channel.Digital("CS1", () => tia.CS1),
            Channel.Digital("CS2", () => tia.CS2),
            Channel.Digital("CS3", () => tia.CS3),
            Channel.Digital("OSC", () => tia.Osc),
            Channel.Digital("PHI0", () => tia.Phi0),
            Channel.Digital("PHI2", () => tia.Phi2),
        ]);
    }
}
