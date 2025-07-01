using System.Collections.Generic;
using Aemula.Debugging;
using Aemula.Emulation.Chips.Mos6502.Debugging;
using Aemula.UI;

namespace Aemula.Emulation.Systems.Atari2600.Debugging;

internal sealed class Atari2600Debugger : Debugger
{
    private static readonly Dictionary<ushort, string> Equates = new()
    {
        // TIA write
        { 0x0000, "VSYNC" },
        { 0x0001, "VBLANK" },
        { 0x0002, "WSYNC" },
        { 0x0003, "RSYNC" },
        { 0x0004, "NUSIZ0" },
        { 0x0005, "NUSIZ1" },
        { 0x0006, "COLUP0" },
        { 0x0007, "COLUP1" },
        { 0x0008, "COLUPF" },
        { 0x0009, "COLUBK" },
        { 0x000A, "CTRLPF" },
        { 0x000B, "REFP0" },
        { 0x000C, "REFP1" },
        { 0x000D, "PF0" },
        { 0x000E, "PF1" },
        { 0x000F, "PF2" },
        { 0x0010, "RESP0" },
        { 0x0011, "RESP1" },
        { 0x0012, "RESM0" },
        { 0x0013, "RESM1" },
        { 0x0014, "RESBL" },
        { 0x0015, "AUDC0" },
        { 0x0016, "AUDC1" },
        { 0x0017, "AUDF0" },
        { 0x0018, "AUDF1" },
        { 0x0019, "AUDV0" },
        { 0x001A, "AUDV1" },
        { 0x001B, "GRP0" },
        { 0x001C, "GRP1" },
        { 0x001D, "ENAM0" },
        { 0x001E, "ENAM1" },
        { 0x001F, "ENABL" },
        { 0x0020, "HMP0" },
        { 0x0021, "HMP1" },
        { 0x0022, "HMM0" },
        { 0x0023, "HMM1" },
        { 0x0024, "HMBL" },
        { 0x0025, "VDELP0" },
        { 0x0026, "VDELP1" },
        { 0x0027, "VDELBL" },
        { 0x0028, "RESMP0" },
        { 0x0029, "RESMP1" },
        { 0x002A, "HMOVE" },
        { 0x002B, "HMCLR" },
        { 0x002C, "CXCLR" },
    };

    private readonly Atari2600System _system;
    private readonly Mos6502Debugger _mos6502Debugger;

    public Atari2600Debugger(Atari2600System system)
        : base(system, CreateMemoryCallbacks(system))
    {
        _system = system;

        _mos6502Debugger = new Mos6502Debugger(system.Cpu);
        _mos6502Debugger.RegisterStepModes(this);

        StepModes.Add(new DebuggerStepMode("Step Color Cycle", () => true));
    }

    private static DebuggerMemoryCallbacks CreateMemoryCallbacks(Atari2600System system)
    {
        return new DebuggerMemoryCallbacks(system.ReadByteDebug, system.WriteByteDebug);
    }

    protected override Disassembler CreateDisassembler()
    {
        return new Mos6502Disassembler(MemoryCallbacks, Equates, hasNmi: false, hasIrq: false);
    }

    protected override void TickSystem()
    {
        base.TickSystem();

        if (_system.Cpu.Pins.Sync)
        {
            OnAddressExecuting(_system.Cpu.Pins.Address);
        }
    }

    public override IEnumerable<DebuggerWindow> CreateDebuggerWindows()
    {
        foreach (var debuggerWindow in base.CreateDebuggerWindows())
        {
            yield return debuggerWindow;
        }

        foreach (var debuggerWindow in _system.CreateDebuggerWindows())
        {
            yield return debuggerWindow;
        }

        yield return new BreakpointsWindow(this);

        //yield return new ScreenDisplayWindow(_system.VideoOutput.DisplayBuffer);

        //yield return new MemoryEditor(1, _system.ReadByteDebug, _system.WriteByteDebug);
    }
}
