using Aemula.Debugging;
using Aemula.Emulation.Chips.Z80;
using Aemula.Emulation.Systems.ZX80.Debugging;

namespace Aemula.Emulation.Systems.ZX80;

public sealed partial class ZX80System : EmulatedSystem
{
    // X1: a 6.5MHz ceramic resonator, the board's only clock source. The Z80
    // itself runs at half this (divided down by a flip-flop in the sync
    // chain) but the video counters and shift register are also driven off
    // this same master oscillator, so it's what the system ticks at rather
    // than the CPU's own divided-down rate.
    public override ulong CyclesPerSecond => 6_500_000;

    public readonly Z80Chip Cpu;

    public ZX80System()
    {
        Cpu = new Z80Chip();
    }

    public override void Tick()
    {
    }

    internal byte ReadByteDebug(ushort address)
    {
        return 0;
    }

    internal void WriteByteDebug(ushort address, byte value)
    {
    }

    public override Debugger CreateDebugger()
    {
        return new ZX80Debugger(this);
    }
}
