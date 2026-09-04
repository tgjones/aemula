using Aemula.Debugging;
using Aemula.Emulation.Chips.Mos6502;
using Aemula.Emulation.Systems.AppleI.Debugging;
using Aemula.Emulation.Systems.AppleI.Roms;

namespace Aemula.Emulation.Systems.AppleI;

// Phase 0 scaffolding only (see docs/apple-i-plan.md) - the CPU exists and
// the Monitor ROM is in place, but nothing is wired up yet: no address
// decode, no DRAM muxing, no PIA, no video timing. Tick() is a no-op until
// Phase 2 wires the real 74154/74157/8T97 decode chain through to the CPU.
public sealed partial class AppleISystem : EmulatedSystem
{
    // The board's master oscillator: 4x the NTSC colour subcarrier
    // (3.579545MHz), the same crystal AppleIISystem ticks at (ZQ1 on the
    // schematic, 14.31818MHz). The CPU clock (1.022727MHz, exactly 2/7 of
    // the subcarrier) and the video dot clock are both synchronous
    // divisions of this one oscillator - see the plan's "Composite video"
    // section.
    public override ulong CyclesPerSecond => 14_318_180;

    public readonly Mos6502Chip Cpu;

    // 8K RAM (both onboard MK4096 banks populated), mapped at $0000-$1FFF -
    // see the plan's chip inventory (ICA11-18/ICB11-18). Real row/column
    // multiplexing (74157s) and RAS/CAS generation land in Phase 2; this is
    // just the storage for now.
    private readonly byte[] _ram = new byte[0x2000];

    // The Monitor ROM (WozMon), mapped at $FF00-$FFFF.
    private readonly byte[] _rom = WozMonitor.Image;

    public AppleISystem()
    {
        Cpu = new Mos6502Chip(Mos6502Options.Default);

        Cpu.Res = false;
        Cpu.Res = true;
    }

    public override void LoadProgram(string filePath)
    {
        // No cassette support yet (see the plan's "Target configuration" -
        // out of scope until the Phase 5 stretch goal), and the Monitor ROM
        // is fixed, so there's nothing to load from filePath yet.
        Reset();

        RaiseProgramLoaded();
    }

    public override void Reset()
    {
        Cpu.Res = false;
        Cpu.Res = true;
    }

    public override void Tick()
    {
    }

    internal byte ReadByteDebug(ushort address)
    {
        if (address >= 0xFF00)
        {
            return _rom[address - 0xFF00];
        }

        if (address < 0x2000)
        {
            return _ram[address];
        }

        return 0xFF;
    }

    internal void WriteByteDebug(ushort address, byte value)
    {
        if (address < 0x2000)
        {
            _ram[address] = value;
        }
    }

    public override Debugger CreateDebugger()
    {
        return new AppleIDebugger(this);
    }
}
