using System.Collections.Generic;
using Aemula.Emulation.Chips.Mos6502;
using Aemula.UI;

namespace Aemula.Emulation.Chips.Ricoh2A03;

public sealed partial class Ricoh2A03Chip
{
    private const ushort OamDmaAddress = 0x4014;
    private readonly DmaUnit _dmaUnit;

    public readonly Mos6502Chip CpuCore;

    public Ricoh2A03Chip()
    {
        CpuCore = new Mos6502Chip(new Mos6502Options(false, Mos6502CompatibilityMode.Normal));

        _dmaUnit = new DmaUnit();
    }

    public void Cycle()
    {
        // TODO: APU stuff.

        ref var pins = ref CpuCore.Pins;

        _dmaUnit.Cycle(ref pins);

        if (_dmaUnit.DmaState != DmaState.Inactive)
        {
            return;
        }

        CpuCore.Tick();

        var address = pins.Address;

        if (address >= 0x4000 && address <= 0x401F)
        {
            if (pins.RW)
            {
                pins.Data = address switch
                {
                    // Write-only
                    OamDmaAddress => 0,

                    // TODO: sound generation and joystick.
                    _ => 0
                };
            }
            else
            {
                switch (address)
                {
                    case OamDmaAddress:
                        _dmaUnit.SetHiByte(pins.Data);

                        // Tell CPU we want to pause it at the next read cycle.
                        pins.Rdy = true;

                        break;

                    default:
                        // TODO: sound generation and joystick.
                        break;
                }
            }
        }

        // Did CPU become paused on this cycle? If so it means we previously requested it
        // to pause so that we can start a DMA transfer.
        if (pins.RW && pins.Rdy)
        {
            _dmaUnit.DmaState = DmaState.Pending;
        }
    }

    public IEnumerable<DebuggerWindow> CreateDebuggerWindows()
    {
        foreach (var debuggerWindow in CpuCore.CreateDebuggerWindows())
        {
            yield return debuggerWindow;
        }
    }
}
