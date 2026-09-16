using Aemula.Debugging;
using Aemula.Emulation.Chips.Z80.Debugging;

namespace Aemula.Emulation.Systems.ZX80.Debugging;

public sealed class ZX80Debugger : Debugger
{
    private readonly ZX80System _system;

    private bool _previousM1 = true;

    public ZX80Debugger(ZX80System system)
        : base(system, new DebuggerMemoryCallbacks(system.ReadByteDebug, system.WriteByteDebug))
    {
        _system = system;
    }

    protected override Disassembler CreateDisassembler()
    {
        return new Z80Disassembler(MemoryCallbacks);
    }

    protected override void TickSystem()
    {
        base.TickSystem();

        // /M1 stays low for the whole 3-T opcode-fetch machine cycle; only its
        // falling edge (T1, where the address bus is freshly latched to PC) marks
        // the start of a new instruction fetch - a level check would report every
        // T-state it stays low as its own fetch.
        var m1 = _system.Cpu.M1;
        if (!m1 && _previousM1)
        {
            OnAddressExecuting(_system.Cpu.Address);
        }
        _previousM1 = m1;
    }
}
