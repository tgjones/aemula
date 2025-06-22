using System.Collections.Generic;
using Aemula.Core.Debugging;

namespace Aemula.Chips.Mos6502.Debugging;

public class Mos6502Disassembler : Disassembler
{
    private readonly Dictionary<ushort, string> _startAddresses;
    private readonly Dictionary<ushort, string> _equates;

    public Mos6502Disassembler(
        DebuggerMemoryCallbacks memoryCallbacks, 
        Dictionary<ushort, string> equates,
        bool hasNmi = true,
        bool hasIrq = true)
        : base(memoryCallbacks)
    {
        _equates = equates;

        _startAddresses = new Dictionary<ushort, string>();
        if (hasNmi)
        {
            _startAddresses.Add(0xFFFA, "NMI");
        }
        _startAddresses.Add(0xFFFC, "RESET");
        if (hasIrq)
        {
            _startAddresses.Add(0xFFFE, "IRQ / BRK");
        }
    }

    protected override void OnReset(List<ushort> startAddresses, Dictionary<ushort, string> labels)
    {
        foreach (var startAddress in _startAddresses)
        {
            var targetAddress = MemoryCallbacks.ReadWord(startAddress.Key);

            // We assume that a vector set to 0x0000 or 0xFFFF is invalid.
            // It might actually be valid - but that's okay,
            // we'll disassemble it when it's actually executed.
            if (targetAddress == 0x0000 || targetAddress == 0xFFFF)
            {
                continue;
            }

            startAddresses.Add(targetAddress);

            if (labels.TryGetValue(targetAddress, out var label))
            {
                label += $", {startAddress.Value}";
            }
            else
            {
                label = startAddress.Value;
            }
            labels[targetAddress] = label;
        }
    }

    protected override DisassembledInstruction DisassembleInstruction(ushort address)
    {
        return Mos6502.DisassembleInstruction(
            address,
            MemoryCallbacks.Read,
            _equates);
    }
}
