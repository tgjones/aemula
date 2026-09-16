using System.Collections.Generic;
using Aemula.Debugging;
using Aemula.Emulation.Chips.Mos6502.Debugging;

namespace Aemula.Emulation.Systems.Nes.Debugging;

public sealed class NesDebugger : Debugger
{
    private static readonly Dictionary<ushort, string> Equates = new()
    {
        { 0x2000, "PPU_CTRL" },
        { 0x2001, "PPU_MASK" },
        { 0x2002, "PPU_STATUS" },
        { 0x2003, "OAM_ADDR" },
        { 0x2004, "OAM_DATA" },
        { 0x2005, "PPU_SCROLL" },
        { 0x2006, "PPU_ADDR" },
        { 0x2007, "PPU_DATA" },
    };

    private readonly NesSystem _nes;
    private readonly Mos6502Debugger _mos6502Debugger;

    public NesDebugger(NesSystem nes)
        : base(nes, CreateMemoryCallbacks(nes))
    {
        _nes = nes;

        _mos6502Debugger = new Mos6502Debugger(nes.Cpu.CpuCore);
        _mos6502Debugger.RegisterStepModes(this);

        StepModes.Add(new DebuggerStepMode("Step PPU Cycle", () => true));
    }

    private static DebuggerMemoryCallbacks CreateMemoryCallbacks(NesSystem nes)
    {
        return new DebuggerMemoryCallbacks(nes.ReadByteDebug, nes.WriteByteDebug);
    }

    protected override Disassembler CreateDisassembler()
    {
        return new Mos6502Disassembler(
            MemoryCallbacks,
            Equates,
            registerCallbacks: new Mos6502RegisterCallbacks(() => _nes.Cpu.CpuCore.X, () => _nes.Cpu.CpuCore.Y));
    }

    protected override void TickSystem()
    {
        base.TickSystem();

        if (_nes.Cpu.CpuCoreSync && _nes.Cpu.FinishedReset)
        {
            OnAddressExecuting(_nes.Cpu.Address);
        }
    }
}
