using System;
using System.Collections.Generic;
using Aemula.Emulation.Chips.Mos6502.UI;
using Aemula.UI;

namespace Aemula.Emulation.Chips.Mos6502;

public partial class Mos6502Chip
{
    public Mos6502Pins Pins;

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
    private byte? _dataOutputRegisterDelayed;

    private byte? _aluOut;
    private byte? _deferredX;
    private byte? _deferredY;
    private byte? _deferredSP;

    private bool _previousNmi;
    private ushort _nmiCounter;
    private ushort _irqCounter;

    private readonly bool _bcdEnabled;

    private readonly Mos6502CompatibilityMode _compatibilityMode;

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

            if (value)
            {
                // Transitioning from low to high.
                // Will be reading / writing data bus.
                // We send the already-calculated values out to the address and data pins.

                if (_dataOutputRegister != null)
                {
                    _dataOutputRegisterDelayed = Pins.Data;
                    Pins.Data = _dataOutputRegister.Value;
                    _dataOutputRegister = null;
                }
            }
            else
            {
                // Transitioning from high to low.
                // Will be executing instruction.

                if (_aluOut != null)
                {
                    A = _aluOut.Value;
                    _aluOut = null;
                }

                if (_deferredX != null)
                {
                    X = _deferredX.Value;
                    _deferredX = null;
                }

                if (_deferredY != null)
                {
                    Y = _deferredY.Value;
                    _deferredY = null;
                }

                if (_deferredSP != null)
                {
                    SP = _deferredSP.Value;
                    _deferredSP = null;
                }

                if (Pins.Sync)
                {
                    Pins.Sync = false;
                    _ir = _brkFlags == BrkFlags.Reset 
                        ? (byte)0 
                        : Pins.Data;
                    _tr = 0;
                    if (_brkFlags == BrkFlags.None)
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
                            PC = (ushort)((Pins.Data << 8) | (PC & 0xFF));
                            Pins.Address = PC;
                            break;
                    }

                    if (_resetTimer <= 2)
                    {
                        return;
                    }
                }

                if (_dataOutputRegisterDelayed != null)
                {
                    Pins.Data = _dataOutputRegisterDelayed.Value;
                    _dataOutputRegisterDelayed = null;
                }

                // Assume we're going to read.
                Pins.RW = true;

                ExecuteInstruction(ref Pins);

                // Fixup registers for nestest compatibility.
                if (_compatibilityMode == Mos6502CompatibilityMode.NesTest)
                {
                    if (_aluOut != null)
                    {
                        A = _aluOut.Value;
                        _aluOut = null;
                    }
                    if (_deferredX != null)
                    {
                        X = _deferredX.Value;
                        _deferredX = null;
                    }
                    if (_deferredY != null)
                    {
                        Y = _deferredY.Value;
                        _deferredY = null;
                    }
                    if (_deferredSP != null)
                    {
                        SP = _deferredSP.Value;
                        _deferredSP = null;
                    }
                }

                _tr++;
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
            var originalValue = _resetPin;

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

    public Mos6502Chip(Mos6502Options options)
    {
        _bcdEnabled = options.BcdEnabled;
        _compatibilityMode = options.CompatibilityMode;

        _phi0 = true;
        _resetPin = true;
        _brkFlags = BrkFlags.Reset;

        // These initial register values are from Visual 6502.
        PC = 0xFF;
        X = 0xC0;
        SP = 0xC0;

        Pins = new Mos6502Pins
        {
            Sync = false,
            Res = true,
            RW = true,
            Nmi = true,
            Irq = true,
            Address = 0x00FF,
        };
    }

    public void Tick()
    {
        ref var pins = ref Pins;

        if (pins.Sync || !pins.Irq || !pins.Nmi || pins.Rdy || pins.Res)
        {
            // NMI is edge-sensitive (triggered by high-to-low transitions).
            if (!pins.Nmi && pins.Nmi != _previousNmi)
            {
                _nmiCounter |= 1;
            }

            // IRQ is level-sensitive (reacts to a low signal level).
            if (!pins.Irq && !P.I)
            {
                _irqCounter |= 1;
            }

            // Check RDY pin, but only during a read cycle.
            if (pins.Rdy & pins.RW)
            {
                // When RDY is high, we "tick" the IRQ counter but not the NMI counter.
                _irqCounter <<= 1;
                return;
            }

            if (pins.Sync)
            {
                _ir = pins.Data;
                _tr = 0;
                pins.Sync = false;

                // For IRQ to be triggered, the IRQ pin must have been low in the cycle _before_ SYNC.
                // We're currently in the cycle _after_ SYNC, so we check if the 3rd bit is set.
                if ((_irqCounter & 0b100) != 0)
                {
                    _brkFlags |= BrkFlags.Irq;
                }

                // For NMI to be triggered, the NMI pin must have been set low at any cycle before SYNC.
                if ((_nmiCounter & 0xFFFC) != 0)
                {
                    _brkFlags = BrkFlags.Nmi;
                }

                // Reset gets priority over NMI or IRQ.
                if (pins.Res)
                {
                    _brkFlags = BrkFlags.Reset;
                }

                // Only keep lower 2 bits of IRQ and NMI counters.
                _irqCounter &= 0b11;
                _nmiCounter &= 0b11;

                if (_brkFlags != BrkFlags.None)
                {
                    _ir = 0;
                    pins.Res = false;
                }
                else
                {
                    PC++;
                }
            }
        }

        // Assume we're going to read.
        pins.RW = true;

        ExecuteInstruction(ref pins);

        // Increment timing register.
        _tr += 1;

        // Increment interrupt counters.
        _irqCounter <<= 1;
        _nmiCounter <<= 1;

        // Store NMI flag. We need this because NMI is edge-triggered.
        _previousNmi = pins.Nmi;
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

public enum Mos6502CompatibilityMode
{
    Normal,
    NesTest,
}
