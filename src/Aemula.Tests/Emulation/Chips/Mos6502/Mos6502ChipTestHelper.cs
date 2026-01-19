//#define WRITE_STATE
//#define DO_REFERENCE_COMPARISON

using System;
using System.Threading.Tasks;
using Aemula.Emulation.Chips.Mos6502;
#if DO_REFERENCE_COMPARISON
using FlawlessChips;
#endif

namespace Aemula.Tests.Emulation.Chips.Mos6502;

internal sealed class Mos6502ChipTestHelper
{
    private readonly Func<ushort, byte> _readMemory;
    private readonly Action<ushort, byte> _writeMemory;

#if DO_REFERENCE_COMPARISON
    private readonly Flawless6502 _referenceChip;
#endif

    private readonly Mos6502Chip _chip;

    public Mos6502Chip Chip => _chip;

    public ushort PC => _chip.PC;

    public Mos6502ChipTestHelper(
        Func<ushort, byte> readMemory,
        Action<ushort, byte> writeMemory,
        bool bcdEnabled = true,
        Mos6502CompatibilityMode compatibilityMode = Mos6502CompatibilityMode.Normal)
    {
        _readMemory = readMemory;
        _writeMemory = writeMemory;

#if DO_REFERENCE_COMPARISON
        _referenceChip = new Flawless6502();
#endif

        _chip = new Mos6502Chip(new Mos6502Options(bcdEnabled, compatibilityMode));
    }

    public async Task Startup()
    {
        // Assert RESET and initialize other inputs

#if DO_REFERENCE_COMPARISON
        _referenceChip.SetLow(Flawless6502.NodeIds.res);
        _referenceChip.SetHigh(Flawless6502.NodeIds.clk0);
        _referenceChip.SetHigh(Flawless6502.NodeIds.rdy);
        _referenceChip.SetLow(Flawless6502.NodeIds.so);
        _referenceChip.SetHigh(Flawless6502.NodeIds.irq);
        _referenceChip.SetHigh(Flawless6502.NodeIds.nmi);
        _referenceChip.StabilizeChip();
#endif

        _chip.Res = false;
        _chip.Phi0 = true;
        // TODO: RDY
        // TODO: SO
        // TODO: IRQ
        // TODO: NMI

        // Run for 8 cycles so that RESET fully takes effect
        for (var i = 0; i < 8; i++)
        {
#if DO_REFERENCE_COMPARISON
            _referenceChip.SetLow(Flawless6502.NodeIds.clk0);
#endif
            _chip.Phi0 = false;

#if DO_REFERENCE_COMPARISON
            _referenceChip.SetHigh(Flawless6502.NodeIds.clk0);
#endif
            _chip.Phi0 = true;
        }

        // Deassert RESET so the chip can continue running normally
#if DO_REFERENCE_COMPARISON
        _referenceChip.SetHigh(Flawless6502.NodeIds.res);
#endif
        _chip.Res = true;
    }

    public async Task Tick()
    {
        await SetPhi0(false);
        await SetPhi0(true);

        // Memory read or write occurs on phi0 high.
        if (_chip.Pins.RW)
        {
            var value = _readMemory(_chip.Pins.Address);
            _chip.Pins.Data = value;
#if DO_REFERENCE_COMPARISON
            _referenceChip.SetBus(Flawless6502.NodeIds.db, value);
#endif
#if WRITE_STATE
            Console.WriteLine($"            READ      ${_chip.Pins.Address:X4} => ${value:X2}");
#endif
        }
        else
        {
#if WRITE_STATE
                Console.WriteLine($"            WRITE     ${_chip.Pins.Address:X4} <= ${_chip.Pins.Data:X2}");
#endif
            _writeMemory(_chip.Pins.Address, _chip.Pins.Data);
        }

#if WRITE_STATE
        if (_chip.Pins.Sync)
        {
            var disassembledInstruction = Mos6502Chip.DisassembleInstruction(
                _chip.Pins.Address,
                _readMemory,
                []);

            Console.WriteLine($"-> {disassembledInstruction.Disassembly}");
        }
#endif
    }

    private async Task SetPhi0(bool value)
    {
#if DO_REFERENCE_COMPARISON
        _referenceChip.SetNode(
            Flawless6502.NodeIds.clk0, 
            value 
                ? NodeValue.PulledHigh 
                : NodeValue.PulledLow);
#endif

        _chip.Phi0 = value;

#if WRITE_STATE
        WriteState();
#endif

#if DO_REFERENCE_COMPARISON
        await ValidateState();
#endif
    }

    private void WriteState()
    {
#if DO_REFERENCE_COMPARISON
        Console.WriteLine(
            "Reference - Ø2 {0}   PC {1:X4}   X {2:X2}   Y {3:X2}   A {4:X2}   SP {5:X2}   AB {6:X4}   DB {7:X2}   RW {8}   SYNC {9}   RES {10}   IR {11:X2}",
            _referenceChip.IsHigh(Flawless6502.NodeIds.clk2out) ? '1' : '0',
            _referenceChip.GetPC(),
            _referenceChip.GetBus(Flawless6502.NodeIds.x),
            _referenceChip.GetBus(Flawless6502.NodeIds.y),
            _referenceChip.GetBus(Flawless6502.NodeIds.a),
            _referenceChip.GetBus(Flawless6502.NodeIds.s),
            _referenceChip.GetBus(Flawless6502.NodeIds.ab),
            _referenceChip.GetBus(Flawless6502.NodeIds.db),
            _referenceChip.IsHigh(Flawless6502.NodeIds.rw) ? '1' : '0',
            _referenceChip.IsHigh(Flawless6502.NodeIds.sync) ? '1' : '0',
            _referenceChip.IsHigh(Flawless6502.NodeIds.res) ? '1' : '0',
            _referenceChip.GetBus(Flawless6502.NodeIds.ir));
#endif

        Console.WriteLine(
            "Aemula    - Ø2 {0}   PC {1:X4}   X {2:X2}   Y {3:X2}   A {4:X2}   SP {5:X2}   AB {6:X4}   DB {7:X2}   RW {8}   SYNC {9}   RES {10}",
            _chip.Phi2 ? '1' : '0',
            _chip.PC,
            _chip.X,
            _chip.Y,
            _chip.A,
            _chip.SP,
            _chip.Pins.Address,
            _chip.Pins.Data,
            _chip.Pins.RW ? '1' : '0',
            _chip.Pins.Sync ? '1' : '0',
            _chip.Res ? '1' : '0');
    }

#if DO_REFERENCE_COMPARISON
    private async Task ValidateState()
    {
        await Assert.That(_chip.A).IsEqualTo(_referenceChip.GetBus(Flawless6502.NodeIds.a));
        await Assert.That(_chip.X).IsEqualTo(_referenceChip.GetBus(Flawless6502.NodeIds.x));
        await Assert.That(_chip.Y).IsEqualTo(_referenceChip.GetBus(Flawless6502.NodeIds.y));

        await Assert.That(_chip.SP).IsEqualTo(_referenceChip.GetBus(Flawless6502.NodeIds.s));

        await Assert.That(_chip.Pins.Address).IsEqualTo(_referenceChip.GetBus(Flawless6502.NodeIds.ab));

        await Assert.That(_chip.Pins.Data).IsEqualTo(_referenceChip.GetBus(Flawless6502.NodeIds.db));

        await Assert.That(_chip.Pins.RW).IsEqualTo(_referenceChip.IsHigh(Flawless6502.NodeIds.rw));
        await Assert.That(_chip.Pins.Sync).IsEqualTo(_referenceChip.IsHigh(Flawless6502.NodeIds.sync));

        await Assert.That(_chip.PC).IsEqualTo(_referenceChip.GetPC());
    }
#endif
}
