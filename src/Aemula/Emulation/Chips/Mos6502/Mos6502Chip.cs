using System;
using System.Collections.Generic;
using Aemula.Emulation.Chips.Mos6502.UI;
using Aemula.UI;
using Aemula.UI.LogicAnalyzer;

namespace Aemula.Emulation.Chips.Mos6502;

public partial class Mos6502Chip
{
    internal bool FinishedReset => _brkFlags != BrkFlags.Reset;

    // Pins

    private ushort _address;
    public ushort Address => _address;

    // TODO: Make this tri-state: read, write, or high-impedance.
    private byte _data;
    public byte Data
    {
        get => _data;
        set => _data = value;
    }

    private bool _sync;
    public bool Sync => _sync;

    // Registers
    public byte A;
    public byte X;
    public byte Y;

    // Program counter
    public ushort PC;

    // Stack pointer
    public byte SP;

    // Processor flags
    public ProcessorFlags P;

    /// <summary>
    /// Instruction register - stores opcode of instruction being executed.
    /// </summary>
    private byte _ir;

    /// <summary>
    /// Timing register - stores the progress through the current instruction, from 0 to 7.
    /// </summary>
    private byte _tr;

    private BrkFlags _brkFlags;
    private byte _resetTimer;

    private ushort _ad;
    private byte _sp;

    private byte? _dataOutputRegister;

    private bool _nmiPin;
    private bool _irqPin;
    private ushort _nmiCounter;
    private ushort _irqCounter;

    private bool _rw;
    private bool _rdy;

    private readonly bool _bcdEnabled;

    internal byte TR => _tr;

    private bool _phi0;

    public bool Phi0
    {
        set
        {
            if (_phi0 == value)
            {
                return;
            }

            _phi0 = value;

            if (!_resetPin)
            {
                return;
            }

            // TODO: NMI, IRQ

            if (value)
            {
                // Transitioning from low to high.
                // Will be reading / writing data bus.
                // We send the already-calculated values out to the address and data pins.

                if (_dataOutputRegister != null)
                {
                    _data = _dataOutputRegister.Value;
                    _dataOutputRegister = null;
                }
            }
            else
            {
                // Transitioning from high to low.
                // Will be executing instruction.

                // Real 6502 hardware only stalls on RDY during a read cycle
                // - a cycle already in progress as a write always completes.
                // Both of this codebase's Rdy consumers (TiaChip's WSYNC,
                // Ricoh2A03Chip's OAM DMA) only ever assert Rdy as part of -
                // or immediately after - the write that requests it, so the
                // very next cycle is always a fresh opcode fetch (Sync)
                // already; gating on Sync here is exactly equivalent to
                // "stall on the next read cycle" for both real use cases,
                // without needing to know a not-yet-decoded future cycle's
                // read/write nature in general. Every field involved in an
                // opcode fetch (_ir, _tr, PC, _address, _rw, _data) is left
                // untouched below, so the exact same fetch simply repeats
                // every cycle until Rdy deasserts. PC has already been
                // incremented for that fetch by then - it happens within the
                // fetch cycle, before the stall - which is the state a real
                // core freezes in too.
                if (_rdy && _sync)
                {
                    return;
                }

                // IRQ is level-sensitive (reacts to a low signal level).
                // So as long as it's low, and so as long as interrupts are enabled,
                // we keep setting the lowest bit of the IRQ counter.
                if (!_irqPin && !P.I)
                {
                    _irqCounter |= 1;
                }

                if (_sync)
                {
                    _sync = false;

                    // An interrupt hijacked this fetch: the byte the core read
                    // is thrown away and BRK runs in its place. The decision
                    // (and the matching PC increment) was made at the start of
                    // the fetch cycle, at the bottom of this method.
                    _ir = _brkFlags != BrkFlags.None
                        ? (byte)0
                        : _data;

                    _tr = 0;
                }

                if (_brkFlags == BrkFlags.Reset)
                {
                    _resetTimer++;

                    switch (_resetTimer)
                    {
                        case 1:
                            break;

                        case 2:
                            _sync = true;
                            PC = (ushort)((_data << 8) | (PC & 0xFF));
                            _address = PC;
                            break;
                    }

                    if (_resetTimer <= 2)
                    {
                        return;
                    }
                }

                // Assume we're going to read.
                _rw = true;

                ExecuteInstruction();

                // Increment timing register.
                _tr++;

                if (_sync)
                {
                    // ExecuteInstruction has just put PC on the address bus for
                    // an opcode fetch, so that fetch cycle starts here. The real
                    // core increments PC within the fetch cycle and only latches
                    // the opcode into IR at the end of it, so the increment
                    // belongs here rather than with the latch above - that is
                    // the state an RDY halt freezes the core in, and RDY can
                    // hold it there for 500-odd cycles during an OAM DMA.
                    //
                    // Interrupts are resolved on the same side of the cycle, for
                    // the same reason: the core knows whether the fetch is
                    // hijacked before it decides whether to increment. The
                    // counters have been shifted one time less here than at the
                    // end of the fetch, hence the masks.

                    // For IRQ to be triggered, the IRQ pin must have been low in
                    // the cycle _before_ SYNC, which is the 2nd bit here.
                    if ((_irqCounter & 0b10) != 0)
                    {
                        _brkFlags |= BrkFlags.Irq;
                        _irqCounter = 0;
                    }

                    // For NMI to be triggered, the NMI pin must have been set low at any cycle before SYNC.
                    if ((_nmiCounter & 0xFFFE) != 0)
                    {
                        _brkFlags = BrkFlags.Nmi;
                        _nmiCounter = 0;
                    }

                    // Only keep the bottom bit of the IRQ counter.
                    _irqCounter &= 0b1;

                    if (_brkFlags == BrkFlags.None)
                    {
                        PC++;
                    }
                }

                // Increment interrupt counters.
                _irqCounter <<= 1;
                _nmiCounter <<= 1;
            }
        }
    }

    public bool Phi1 => !_phi0;

    public bool Phi2 => _phi0;

    private bool _resetPin;

    public bool Res
    {
        get => _resetPin; // Shouldn't be accessible
        set
        {
            _resetPin = value;

            if (!value)
            {
                _brkFlags = BrkFlags.Reset;
            }
            else if (value && !_resetPin)
            {
                _resetTimer = 0;
            }
        }
    }

    public bool Nmi
    {
        // Exposed for testing, even though this is a write-only pin.
        internal get => _nmiPin;
        set
        {
            // NMI is edge-sensitive (triggered by high-to-low transition).
            if (!value && _nmiPin)
            {
                _nmiCounter |= 1;
            }
            _nmiPin = value;
        }
    }

    public bool Irq
    {
        // Exposed for testing, even though this is a write-only pin.
        internal get => _irqPin;
        set
        {
            // IRQ is level-sensitive (reacts to a low signal level).
            _irqPin = value;
        }
    }

    /// <summary>
    /// Read/write pin. True for read, false for write.
    /// </summary>
    public bool RW => _rw;

    /// <summary>
    /// Ready pin (input). Freezes the CPU - repeating the same opcode fetch
    /// every cycle - for as long as this is asserted true, starting from the
    /// next opcode-fetch boundary (see Phi0's setter).
    /// </summary>
    public bool Rdy
    {
        // Exposed for testing, even though this is a write-only pin.
        internal get => _rdy;
        set => _rdy = value;
    }

    public Mos6502Chip(Mos6502Options options)
    {
        _bcdEnabled = options.BcdEnabled;

        _phi0 = true;

        // These initial register values are from Visual 6502.
        PC = 0xFF;
        X = 0xC0;
        SP = 0xC0;
        P.Z = true;

        // These initial bus values are from Visual 6502.
        _resetPin = true;
        _brkFlags = BrkFlags.Reset;
        _nmiPin = true;
        _irqPin = true;
        _address = 0x00FF;
        _data = 0x00;
        _sync = false;
        _rw = true;
    }

    public void Startup()
    {
        Res = false;
        Res = true;
    }

    [Flags]
    private enum BrkFlags
    {
        None = 0,
        Irq = 1,
        Nmi = 2,
        Reset = 4,
    }

    internal void CreateDebuggerWindows(List<DebuggerWindow> result)
    {
        result.Add(new CpuStateWindow(this));
    }

    /// <summary>
    /// Owned here (rather than by each system that embeds a 6502) so every
    /// system gets the same channel list for free.
    /// </summary>
    internal ChannelGroup CreateChannelGroup()
    {
        return new ChannelGroup("MOS6502",
        [
            Channel.Bus("Address", 16, () => Address),
            Channel.Bus("Data", 8, () => Data),
            Channel.Digital("R/W", () => RW),
            Channel.Digital("SYNC", () => Sync),
            Channel.Digital("RDY", () => Rdy),
            Channel.Digital("IRQ", () => Irq),
            Channel.Digital("NMI", () => Nmi),
            Channel.Digital("PHI2", () => Phi2),
        ]);
    }
}

public readonly struct DecodedInstruction
{
    public readonly ushort Address;
    public readonly string Disassembly;
    public readonly ushort InstructionSizeInBytes;

    internal DecodedInstruction(ushort address, string disassembly, ushort instructionSizeInBytes)
    {
        Address = address;
        Disassembly = disassembly;
        InstructionSizeInBytes = instructionSizeInBytes;
    }
}
