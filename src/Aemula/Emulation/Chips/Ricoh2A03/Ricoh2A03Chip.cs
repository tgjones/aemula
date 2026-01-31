using System.Collections.Generic;
using Aemula.Emulation.Chips.Mos6502;
using Aemula.UI;

namespace Aemula.Emulation.Chips.Ricoh2A03;

public sealed partial class Ricoh2A03Chip
{
    private const ushort OamDmaAddress = 0x4014;

    private readonly Mos6502Chip _cpuCore;
    private readonly DmaUnit _dmaUnit;

    private bool _clk;
    private int _clockCounter;
    private bool _phi0;
    private bool _phi2;

    internal Mos6502Chip CpuCore => _cpuCore;

    public ushort PC => _cpuCore.PC;

    public byte X => _cpuCore.X;

    public byte Y => _cpuCore.Y;

    public byte A => _cpuCore.A;

    public byte SP => _cpuCore.SP;

    public ProcessorFlags P => _cpuCore.P;

    public byte PByte => _cpuCore.PByte;

    public bool Res
    {

        get => _cpuCore.Res; // Shouldn't be accessible
        set => _cpuCore.Res = value;
    }

    /// <summary>
    /// Master clock input. 
    /// Clocks an internal divide-by-12 counter to drive the CPU core.
    /// </summary>
    public bool Clk
    {
        get => _clk; // TODO: Shouldn't be accessible
        set
        {
            if (_clk == value)
            {
                return;
            }

            _clk = value;

            _clockCounter++;

            if (_clockCounter == 9 && _phi2 == false)
            {
                // Phi2 rises 3 master clock cycles before Phi0 rises.
                // By this point the address bus is already stable
                // so it gives slow cartridges longer to respond.
                _phi2 = true;
            }
            else if (_clockCounter == 12)
            {
                _clockCounter = 0;

                _phi0 = !_phi0;
                _cpuCore.Phi0 = _phi0;

                // Phi2 falls at the same time as the CPU's Phi0.
                if (!_phi0)
                {
                    _phi2 = false;
                }
            }
        }
    }

    // Shouldn't be exposed.
    public bool CpuCorePhi2 => _cpuCore.Phi2;

    // Shouldn't be exposed.
    public bool CpuCoreSync => _cpuCore.Pins.Sync;

    public bool M2 => _phi2;

    public bool RW => _cpuCore.Pins.RW;

    public ushort Address => _cpuCore.Pins.Address;

    public byte Data
    {
        get => _cpuCore.Pins.Data;
        set => _cpuCore.Pins.Data = value;
    }

    public bool Nmi
    {
        set => _cpuCore.Pins.Nmi = value;
    }

    public Ricoh2A03Chip()
    {
        _cpuCore = new Mos6502Chip(new Mos6502Options(false));

        _cpuCore.X = 0x00;
        _cpuCore.SP = 0x00;
        _cpuCore.P.Z = true;

        _dmaUnit = new DmaUnit();
    }

    public void Cycle()
    {
        //// TODO: APU stuff.

        //ref var pins = ref CpuCore.Pins;

        //_dmaUnit.Cycle(ref pins);

        //if (_dmaUnit.DmaState != DmaState.Inactive)
        //{
        //    return;
        //}

        //CpuCore.Tick();

        //var address = pins.Address;

        //if (address >= 0x4000 && address <= 0x401F)
        //{
        //    if (pins.RW)
        //    {
        //        pins.Data = address switch
        //        {
        //            // Write-only
        //            OamDmaAddress => 0,

        //            // TODO: sound generation and joystick.
        //            _ => 0
        //        };
        //    }
        //    else
        //    {
        //        switch (address)
        //        {
        //            case OamDmaAddress:
        //                _dmaUnit.SetHiByte(pins.Data);

        //                // Tell CPU we want to pause it at the next read cycle.
        //                pins.Rdy = true;

        //                break;

        //            default:
        //                // TODO: sound generation and joystick.
        //                break;
        //        }
        //    }
        //}

        //// Did CPU become paused on this cycle? If so it means we previously requested it
        //// to pause so that we can start a DMA transfer.
        //if (pins.RW && pins.Rdy)
        //{
        //    _dmaUnit.DmaState = DmaState.Pending;
        //}
    }

    public IEnumerable<DebuggerWindow> CreateDebuggerWindows()
    {
        foreach (var debuggerWindow in _cpuCore.CreateDebuggerWindows())
        {
            yield return debuggerWindow;
        }
    }
}
