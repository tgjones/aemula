using System;
using System.Threading.Tasks;
using Aemula.Emulation.Chips.Mos6502;
using FlawlessChips;

namespace Aemula.Tests.Emulation.Chips.Mos6502;

internal sealed class Mos6502ChipTestHelper
{
    private readonly Func<ushort, byte> _readMemory;
    private readonly Action<ushort, byte> _writeMemory;

    private readonly Func<ushort, Mos6502RegisterState, Mos6502MemoryAccessResult>? _handleMemoryAccess;

    private readonly Mos6502Chip _chip;

    private readonly Flawless6502? _referenceChip;

    private readonly RollingLogger _logger = new();

    private int _cycle;
    private bool _validateRegisters;

    public ushort PC => _chip.PC;

    public Mos6502ChipTestHelper(
        Func<ushort, byte> readMemory,
        Action<ushort, byte> writeMemory,
        bool doReferenceComparison,
        Func<ushort, Mos6502RegisterState, Mos6502MemoryAccessResult>? handleMemoryAccess = null)
    {
        _readMemory = readMemory;
        _writeMemory = writeMemory;

        _handleMemoryAccess = handleMemoryAccess;

        _chip = new Mos6502Chip(Mos6502Options.Default);

        _referenceChip = doReferenceComparison
            ? new Flawless6502()
            : null;
    }

    public async Task Startup()
    {
        // Assert RESET and initialize other inputs

        if (_referenceChip != null)
        {
            _referenceChip.SetLow(Flawless6502.NodeIds.res);
            _referenceChip.SetHigh(Flawless6502.NodeIds.clk0);
            _referenceChip.SetHigh(Flawless6502.NodeIds.rdy);
            _referenceChip.SetLow(Flawless6502.NodeIds.so);
            _referenceChip.SetHigh(Flawless6502.NodeIds.irq);
            _referenceChip.SetHigh(Flawless6502.NodeIds.nmi);
            _referenceChip.StabilizeChip();
        }

        _chip.Res = false;
        _chip.Phi0 = true;
        // TODO: RDY
        // TODO: SO
        // TODO: IRQ
        // TODO: NMI

        // Run for 8 cycles so that RESET fully takes effect
        for (var i = 0; i < 8; i++)
        {
            _referenceChip?.SetLow(Flawless6502.NodeIds.clk0);
            _chip.Phi0 = false;

            _referenceChip?.SetHigh(Flawless6502.NodeIds.clk0);
            _chip.Phi0 = true;
        }

        // Deassert RESET so the chip can continue running normally
        _referenceChip?.SetHigh(Flawless6502.NodeIds.res);
        _chip.Res = true;
    }

    public async Task Tick()
    {
        await SetPhi0(false);

        if (_validateRegisters)
        {
            await ValidateRegisterState();
            _validateRegisters = false;
        }

        await SetPhi0(true);

        // Memory read or write occurs on phi0 high.
        if (_chip.Pins.RW)
        {
            if (_handleMemoryAccess != null)
            {
                var result = _handleMemoryAccess(_chip.Pins.Address, GetChipRegisterState());
                switch (result)
                {
                    case Mos6502MemoryAccessResult(Mos6502MemoryAccessResultKind.Continue, _):
                        break;

                    case Mos6502MemoryAccessResult(Mos6502MemoryAccessResultKind.StopTest, _):
                        return;

                    case Mos6502MemoryAccessResult(Mos6502MemoryAccessResultKind.FailTest, var errorMessage):
                        Assert.Fail(errorMessage!);
                        break;

                    default:
                        throw new InvalidOperationException();
                }
            }

            var value = _readMemory(_chip.Pins.Address);

            _chip.Pins.Data = value;

            if (_referenceChip != null)
            {
                _referenceChip?.SetBus(Flawless6502.NodeIds.db, value);
                _logger.Add(new MemoryLog(_chip.Pins.Address, value, true));
            }
        }
        else
        {
            if (_referenceChip != null)
            {
                _logger.Add(new MemoryLog(_chip.Pins.Address, _chip.Pins.Data, false));
            }

            _writeMemory(_chip.Pins.Address, _chip.Pins.Data);
        }

        if (_chip.Pins.Sync)
        {
            if (_referenceChip != null)
            {
                _logger.Add(new DisassemblyLog(_chip.Pins.Address, _readMemory));
            }

            // We wait another half cycle before comparing registers.
            // This is because the real chip (in this case, represented by
            // Flawless6502) updates its registers half a cycle later than we do.
            // That's fine, since our goal is only to be pin-accurate, not
            // internal-state accurate.
            _validateRegisters = true;
        }

        _cycle++;
    }

    private async Task SetPhi0(bool value)
    {
        _referenceChip?.SetNode(
            Flawless6502.NodeIds.clk0, 
            value 
                ? NodeValue.PulledHigh 
                : NodeValue.PulledLow);

        _chip.Phi0 = value;

        await ValidatePinState();
    }

    private async Task ValidatePinState()
    {
        if (_referenceChip == null)
        {
            return;
        }

        var isDataBusValid = _chip.Phi2 && !_chip.Pins.RW && _chip.Res;

        var chipPinState = new Mos6502PinState(
            Phi2: _chip.Phi2,
            Address: _chip.Pins.Address,
            Data: isDataBusValid ? _chip.Pins.Data : (byte)0,
            RW: _chip.Pins.RW,
            Sync: _chip.Pins.Sync,
            Res: _chip.Pins.Res);

        _logger.Add(new PinsLog(_cycle, chipPinState));

        var referenceChipPinState = new Mos6502PinState(
            Phi2: _referenceChip.IsHigh(Flawless6502.NodeIds.clk2out),
            Address: _referenceChip.GetBus(Flawless6502.NodeIds.ab),
            Data: isDataBusValid ? _referenceChip.GetBus(Flawless6502.NodeIds.db) : (byte)0,
            RW: _referenceChip.IsHigh(Flawless6502.NodeIds.rw),
            Sync: _referenceChip.IsHigh(Flawless6502.NodeIds.sync),
            Res: _referenceChip.IsHigh(Flawless6502.NodeIds.res));

        if (!chipPinState.Equals(referenceChipPinState))
        {
            _logger.DumpToConsole();

            await Assert.That(chipPinState).IsEqualTo(referenceChipPinState);
        }
    }

    private async Task ValidateRegisterState()
    {
        if (_referenceChip == null)
        {
            return;
        }

        var chipRegisterState = GetChipRegisterState();

        _logger.Add(new RegistersLog(chipRegisterState));

        var referenceChipRegisterState = new Mos6502RegisterState(
            PC: _referenceChip.GetPC(),
            A: _referenceChip.GetBus(Flawless6502.NodeIds.a),
            X: _referenceChip.GetBus(Flawless6502.NodeIds.x),
            Y: _referenceChip.GetBus(Flawless6502.NodeIds.y),
            SP: _referenceChip.GetBus(Flawless6502.NodeIds.s),
            P: Mos6502ProcessorFlagsState.FromByte(_referenceChip.GetBus(Flawless6502.NodeIds.p)));

        if (!chipRegisterState.Equals(referenceChipRegisterState))
        {
            _logger.DumpToConsole();

            await Assert.That(chipRegisterState).IsEqualTo(referenceChipRegisterState);
        }
    }

    private Mos6502RegisterState GetChipRegisterState() => new(
        PC: _chip.PC,
        A: _chip.A,
        X: _chip.X,
        Y: _chip.Y,
        SP: _chip.SP,
        P: Mos6502ProcessorFlagsState.FromProcessorFlags(_chip.P));

    private sealed record MemoryLog(ushort Address, byte Data, bool IsRead) 
        : ILoggable
    {
        public string ToLogEntry()
        {
            return IsRead
                ? $"READ     ${Address:X4} => ${Data:X2}"
                : $"WRITE    ${Address:X4} <= ${Data:X2}";
        }
    }

    private sealed record DisassemblyLog(ushort Address, Func<ushort, byte> ReadMemory) 
        : ILoggable
    {
        public string ToLogEntry()
        {
            // Note that this will be wrong if the memory was modified since
            // this log object was created. But we're only using this for
            // test logging so that's acceptable.
            var disassembledInstruction = Mos6502Chip.DisassembleInstruction(
                Address,
                ReadMemory,
                []);

            return $"-> {disassembledInstruction.Disassembly}";
        }
    }

    private sealed record PinsLog(int Cycle, Mos6502PinState Pins)
        : ILoggable
    {
        public string ToLogEntry() => $"Cycle {Cycle:D6}   {Pins}";
    }

    private sealed record RegistersLog(Mos6502RegisterState Registers)
        : ILoggable
    {
        public string ToLogEntry() => Registers.ToString();
    }
}

internal readonly record struct Mos6502MemoryAccessResult(
    Mos6502MemoryAccessResultKind Kind, 
    string? ErrorMessage);

internal enum Mos6502MemoryAccessResultKind
{
    Continue,
    StopTest,
    FailTest,
}
