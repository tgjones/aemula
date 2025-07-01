using System.Collections.Generic;
using Aemula.Debugging;
using Aemula.Emulation.Chips.Mos6502.Debugging;
using Aemula.Emulation.Systems.Nes.UI;
using Aemula.UI;

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
        return new Mos6502Disassembler(MemoryCallbacks, Equates);
    }

    protected override void TickSystem()
    {
        base.TickSystem();

        if (_nes.Cpu.CpuCore.Pins.Sync)
        {
            OnAddressExecuting(_nes.Cpu.CpuCore.Pins.Address);
        }
    }

    public override IEnumerable<DebuggerWindow> CreateDebuggerWindows()
    {
        foreach (var debuggerWindow in base.CreateDebuggerWindows())
        {
            yield return debuggerWindow;
        }

        foreach (var debuggerWindow in _nes.Cpu.CreateDebuggerWindows())
        {
            yield return debuggerWindow;
        }

        foreach (var debuggerWindow in _nes.Ppu.CreateDebuggerWindows())
        {
            yield return debuggerWindow;
        }

        yield return new BreakpointsWindow(this);

        yield return new MemoryEditor(1, _nes.ReadByteDebug, _nes.WriteByteDebug);

        yield return new PatternTableWindow(_nes);
    }
}
