using System.Collections.Generic;
using Aemula.Emulation.Chips.Mos6502;
using Aemula.UI;

namespace Aemula.Emulation.Chips.Ricoh2A03;

public sealed partial class Ricoh2A03Chip
{
    private const ushort OamDmaAddress = 0x4014;
    private const ushort OamDataAddress = 0x2004;

    private readonly Mos6502Chip _cpuCore;
    private readonly DmaUnit _dmaUnit;

    // Pin values.
    private ushort? _address; // Null if not being driven by DMA unit.
    private bool? _rw; // Null if not being driven by DMA unit.
    private bool _clk;
    private int _clockCounter;
    private bool _phi0;
    private bool _m2;

    internal Mos6502Chip CpuCore => _cpuCore;

    public ushort PC => _cpuCore.PC;

    public byte X => _cpuCore.X;

    public byte Y => _cpuCore.Y;

    public byte A => _cpuCore.A;

    public byte SP => _cpuCore.SP;

    public ProcessorFlags P => _cpuCore.P;

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

            if (_clockCounter == 9 && _m2 == false)
            {
                // Phi2 rises 3 master clock cycles before Phi0 rises.
                // By this point the address bus is already stable
                // so it gives slow cartridges longer to respond.
                OnM2Rising();
                _m2 = true;
            }
            else if (_clockCounter == 12)
            {
                _clockCounter = 0;

                _phi0 = !_phi0;

                // The core is clocked whether or not a DMA transfer is running.
                // A transfer halts it through RDY - it sits repeating the opcode
                // fetch it was on - and the DMA unit overrides the address and
                // R/W pins for the cycles it takes the bus.
                _cpuCore.Phi0 = _phi0;

                if (_phi0)
                {
                    OnPhi2Rising();
                }
                else
                {
                    OnPhi0Falling();

                    // Phi2 falls at the same time as the CPU's Phi0.
                    _m2 = false;
                }
            }
        }
    }

    // Shouldn't be exposed.
    public bool CpuCorePhi2 => _cpuCore.Phi2;

    // Shouldn't be exposed.
    public bool CpuCoreSync => _cpuCore.Sync;
    public bool FinishedReset => _cpuCore.FinishedReset;

    public bool M2 => _m2;

    public bool RW => _rw ?? _cpuCore.RW;

    public ushort Address => _address ?? _cpuCore.Address;

    public byte Data
    {
        get => _cpuCore.Data;
        set => _cpuCore.Data = value;
    }

    public bool Nmi
    {
        set => _cpuCore.Nmi = value;
    }

    public bool Irq
    {
        set => _cpuCore.Irq = value;
    }

    public bool Rdy
    {
        set => _cpuCore.Rdy = value;
    }

    public Ricoh2A03Chip()
    {
        _cpuCore = new Mos6502Chip(new Mos6502Options(false))
        {
            X = 0x00,
            SP = 0x00
        };
        _cpuCore.P.Z = true;

        _dmaUnit = new DmaUnit();
    }

    private void OnPhi0Falling()
    {
        // A new CPU cycle starts here: the core has just put this cycle's
        // address on its pins, so this is where the DMA unit decides whether to
        // take the bus off it.
        _dmaUnit.Cycle(this);
    }

    private void OnM2Rising()
    {
        // TODO: APU stuff.
    }

    private void OnPhi2Rising()
    {
        // The internal $4000-$401F registers are decoded here rather than at M2
        // rising, even though M2 gives the rest of the board a head start: the
        // core only drives a write value onto the data pins when Phi2 goes high,
        // so at M2 time the bus still holds the previous cycle's byte.
        //
        // The decode is off the pins rather than the core: a DMA transfer
        // sourced from page $40 puts $4000-$40FF on the bus, and these
        // registers answer that just as they would a CPU access.
        var address = Address;

        if (address < 0x4000 || address > 0x401F)
        {
            return;
        }

        if (RW)
        {
            _cpuCore.Data = address switch
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
                    _dmaUnit.Request(this, Data);
                    break;

                default:
                    // TODO: sound generation and joystick.
                    break;
            }
        }
    }

    public void CreateDebuggerWindows(List<DebuggerWindow> result)
    {
        _cpuCore.CreateDebuggerWindows(result);
    }
}
