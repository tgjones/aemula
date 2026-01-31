using System;
using System.Threading.Tasks;
using Aemula.Emulation.Chips.Mos6502;
using Aemula.Emulation.Chips.Ricoh2A03;
using Aemula.Tests.Emulation.Chips.Mos6502;
using FlawlessChips;

namespace Aemula.Tests.Emulation.Chips.Ricoh2A03;

internal sealed class Ricoh2A03ChipTestHelper
{
    private readonly Func<ushort, byte> _readMemory;
    private readonly Action<ushort, byte> _writeMemory;

    private readonly Ricoh2A03Chip _chip;

    private readonly Flawless2A03 _referenceChip;

    private readonly RollingLogger _logger = new();

    private int _cycle;
    private bool _validateRegisters;

    public ushort PC => _chip.PC;

    public bool CpuCoreSync => _chip.CpuCoreSync;

    public Ricoh2A03ChipTestHelper(
        Func<ushort, byte> readMemory,
        Action<ushort, byte> writeMemory)
    {
        _readMemory = readMemory;
        _writeMemory = writeMemory;

        _chip = new Ricoh2A03Chip();

        _referenceChip = new Flawless2A03();
    }

    public async Task Startup()
    {
        // Assert RESET and initialize other inputs

        _referenceChip.SetLow(Flawless2A03.NodeIds.res);
        _referenceChip.SetBus(Flawless2A03.NodeIds.db, (byte)0);
        //_referenceChip.SetHigh(Flawless2A03.NodeIds.rdy);
        _referenceChip.SetLow(Flawless2A03.NodeIds.so);
        _referenceChip.SetHigh(Flawless2A03.NodeIds.irq);
        _referenceChip.SetHigh(Flawless2A03.NodeIds.nmi);
        _referenceChip.StabilizeChip();

        _chip.Res = false;
        // TODO: RDY
        // TODO: SO
        // TODO: IRQ
        // TODO: NMI

        // Run for a few cycles so that RESET fully takes effect.
        // To make for easier comparison, we take this opportunity
        // to synchronize the reference chip clocking with our own.
        {
            var startLookingAtClk2Out = false;
            var isClk2OutHighCounter = 0;
            while (true)
            {
                _referenceChip.SetLow(Flawless2A03.NodeIds.clk_in);
                _referenceChip.SetHigh(Flawless2A03.NodeIds.clk_in);

                if (!_referenceChip.IsHigh(Flawless2A03.NodeIds.c_clk2out))
                {
                    startLookingAtClk2Out = true;
                }
                else if (startLookingAtClk2Out && _referenceChip.IsHigh(Flawless2A03.NodeIds.c_clk2out))
                {
                    isClk2OutHighCounter++;
                    if (isClk2OutHighCounter == 12)
                    {
                        break;
                    }
                }
            }
        }

        {
            var startLookingAtClk2Out = false;
            var isClk2OutHighCounter = 0;
            while (true)
            {
                _chip.Clk = false;
                _chip.Clk = true;

                if (!_chip.CpuCorePhi2)
                {
                    startLookingAtClk2Out = true;
                }
                else if (startLookingAtClk2Out && _chip.CpuCorePhi2)
                {
                    isClk2OutHighCounter++;
                    if (isClk2OutHighCounter == 12)
                    {
                        break;
                    }
                }
            }
        }

        // Deassert RESET so the chip can continue running normally
        _referenceChip.SetHigh(Flawless2A03.NodeIds.res);
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
        if (_chip.RW)
        {
            var value = _readMemory(_chip.Address);

            _chip.Data = value;

            _referenceChip.SetBus(Flawless2A03.NodeIds.db, value);
            _logger.Add(new MemoryLog(_chip.Address, value, true));
        }
        else
        {
            _logger.Add(new MemoryLog(_chip.Address, _chip.Data, false));
            _writeMemory(_chip.Address, _chip.Data);
        }

        if (_chip.CpuCoreSync)
        {
            _logger.Add(new DisassemblyLog(_chip.Address, _readMemory));

            // We wait another half cycle before comparing registers.
            // This is because the real chip (in this case, represented by
            // Flawless2A03) updates its registers half a cycle later than we do.
            // That's fine, since our goal is only to be pin-accurate, not
            // internal-state accurate.
            _validateRegisters = true;
        }

        _cycle++;
    }

    private async Task SetPhi0(bool value)
    {
        if (value == _chip.CpuCorePhi2)
        {
            throw new InvalidOperationException();
        }

        // We want to clock until we're at the very last master clock cycle of the
        // 6502 core's phi2 cycle.
        for (var i = 0; i < 6; i++)
        {
            _referenceChip.SetNode(Flawless2A03.NodeIds.clk_in, NodeValue.PulledLow);

            _chip.Clk = false;

            await ValidatePinState();

            _referenceChip.SetNode(Flawless2A03.NodeIds.clk_in, NodeValue.PulledHigh);

            _chip.Clk = true;

            await ValidatePinState();
        }

        if (value != _chip.CpuCorePhi2)
        {
            throw new InvalidOperationException();
        }
    }

    private async Task ValidatePinState()
    {
        if (_referenceChip == null)
        {
            return;
        }

        var isDataBusValid = _chip.CpuCorePhi2 && !_chip.RW && _chip.Res;

        var chipPinState = new Ricoh2A03PinState(
            Clk: _chip.Clk,
            M2: _chip.M2,
            CorePhi2: _chip.CpuCorePhi2,
            Address: _chip.Address,
            Data: isDataBusValid ? _chip.Data : (byte)0,
            RW: _chip.RW,
            CoreSync: _chip.CpuCoreSync,
            Rst: _chip.Res);

        _logger.Add(new PinsLog(_cycle, chipPinState));

        var referenceChipPinState = new Ricoh2A03PinState(
            Clk: _referenceChip.IsHigh(Flawless2A03.NodeIds.clk_in),
            M2: _referenceChip.IsHigh(Flawless2A03.NodeIds.phi2),
            CorePhi2: _referenceChip.IsHigh(Flawless2A03.NodeIds.c_clk2out),
            Address: _referenceChip.GetBus(Flawless2A03.NodeIds.ab),
            Data: isDataBusValid ? _referenceChip.GetBus(Flawless2A03.NodeIds.db) : (byte)0,
            RW: _referenceChip.IsHigh(Flawless2A03.NodeIds.rw),
            CoreSync: _referenceChip.IsHigh(Flawless2A03.NodeIds.sync),
            Rst: _referenceChip.IsHigh(Flawless2A03.NodeIds.res));

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

        var referenceChipRegisterState = new Ricoh2A03RegisterState(
            PC: _referenceChip.GetPC(),
            A: _referenceChip.GetBus(Flawless2A03.NodeIds.a),
            X: _referenceChip.GetBus(Flawless2A03.NodeIds.x),
            Y: _referenceChip.GetBus(Flawless2A03.NodeIds.y),
            SP: _referenceChip.GetBus(Flawless2A03.NodeIds.s),
            P: Mos6502ProcessorFlagsState.FromByte(_referenceChip.GetBus(Flawless2A03.NodeIds.p)));

        if (!chipRegisterState.Equals(referenceChipRegisterState))
        {
            _logger.DumpToConsole();

            await Assert.That(chipRegisterState).IsEqualTo(referenceChipRegisterState);
        }
    }

    private Ricoh2A03RegisterState GetChipRegisterState() => new(
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

#error Cleanup this logging prefixes

    private sealed record PinsLog(int Cycle, Ricoh2A03PinState Pins)
        : ILoggable
    {
        public string ToLogEntry() => $"Cycle {Cycle:D6}   {Pins}";
    }

    private sealed record RegistersLog(Ricoh2A03RegisterState Registers)
        : ILoggable
    {
        public string ToLogEntry() => Registers.ToString();
    }
}
