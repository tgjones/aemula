using System;
using System.Collections.Generic;
using Aemula.Emulation.Chips.Mos6502.UI;
using Aemula.UI;

namespace Aemula.Emulation.Chips.Mos6502;

public partial class Mos6502Chip
{
    public Mos6502Pins Pins;

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

            // TODO: NMI, IRQ, RDY

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

                // IRQ is level-sensitive (reacts to a low signal level).
                // So as long as it's low, and so as long as interrupts are enabled,
                // we keep setting the lowest bit of the IRQ counter.
                if (!_irqPin && !P.I)
                {
                    _irqCounter |= 1;
                }

                if (Pins.Sync)
                {
                    Pins.Sync = false;

                    _ir = _data;
                    _tr = 0;

                    // For IRQ to be triggered, the IRQ pin must have been low in the cycle _before_ SYNC.
                    // We're currently in the cycle _after_ SYNC, so we check if the 3rd bit is set.
                    if ((_irqCounter & 0b100) != 0)
                    {
                        _brkFlags |= BrkFlags.Irq;
                        _irqCounter = 0;
                    }

                    // For NMI to be triggered, the NMI pin must have been set low at any cycle before SYNC.
                    if ((_nmiCounter & 0xFFFC) != 0)
                    {
                        _brkFlags = BrkFlags.Nmi;
                        _nmiCounter = 0;
                    }

                    // Only keep lower 2 bits of IRQ counter.
                    _irqCounter &= 0b11;

                    if (_brkFlags != BrkFlags.None)
                    {
                        _ir = 0;
                    }
                    else
                    {
                        PC++;
                    }
                }

                if (_brkFlags == BrkFlags.Reset)
                {
                    _resetTimer++;

                    switch (_resetTimer)
                    {
                        case 1:
                            break;

                        case 2:
                            Pins.Sync = true;
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
                Pins.RW = true;

                ExecuteInstruction(ref Pins);

                // Increment timing register.
                _tr++;

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

    public Mos6502Chip(Mos6502Options options)
    {
        _bcdEnabled = options.BcdEnabled;

        _phi0 = true;
        _resetPin = true;
        _brkFlags = BrkFlags.Reset;
        _nmiPin = true;
        _irqPin = true;

        // These initial register values are from Visual 6502.
        PC = 0xFF;
        X = 0xC0;
        SP = 0xC0;
        P.Z = true;

        Pins = new Mos6502Pins
        {
            Sync = false,
            Res = true,
            RW = true,
        };

        // These initial bus values are from Visual 6502.
        _address = 0x00FF;
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

    public IEnumerable<DebuggerWindow> CreateDebuggerWindows()
    {
        yield return new CpuStateWindow(this);
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
