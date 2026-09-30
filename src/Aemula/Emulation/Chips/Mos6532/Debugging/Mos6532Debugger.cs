using Aemula.Debugging;
using Aemula.Debugging.LogicAnalyzer;

namespace Aemula.Emulation.Chips.Mos6532.Debugging;

public sealed class Mos6532Debugger(Mos6532Chip riot) : ChipDebugger
{
    public override ChannelGroup CreateChannelGroup()
    {
        return new ChannelGroup("RIOT",
        [
            Channel.Bus("Address", 7, () => riot.A),
            Channel.Bus("Data", 8, () => riot.DB),
            Channel.Digital("R/W", () => riot.RW),
            Channel.Digital("RS", () => riot.RS),
            Channel.Digital("CS1", () => riot.CS1),
            Channel.Digital("CS2", () => riot.CS2),
            Channel.Digital("RES", () => riot.Res),
            Channel.Digital("IRQ", () => riot.Irq),
            Channel.Bus("PA", 8, () => riot.PA),
            Channel.Bus("PB", 8, () => riot.PB),
            Channel.Digital("PHI2", () => riot.Phi2),
        ]);
    }
}
