using System.Collections.Generic;
using Aemula.Debugging;

namespace Aemula.Emulation.Chips.Mos6502.Debugging;

public class Mos6502Disassembler : Disassembler
{
    private readonly Dictionary<ushort, string> _startAddresses;
    private readonly Dictionary<ushort, string> _equates;
    private readonly Mos6502RegisterCallbacks _registerCallbacks;

    public Mos6502Disassembler(
        DebuggerMemoryCallbacks memoryCallbacks,
        Dictionary<ushort, string> equates,
        bool hasNmi = true,
        bool hasIrq = true,
        Mos6502RegisterCallbacks registerCallbacks = default)
        : base(memoryCallbacks)
    {
        _equates = equates;
        _registerCallbacks = registerCallbacks;

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
        return Mos6502Chip.DisassembleInstruction(
            address,
            MemoryCallbacks.Read,
            _equates);
    }

    public override (ushort Address, byte Value)? TryGetEffectiveAddress(in DisassembledInstruction instruction)
    {
        // No register callbacks wired up (most test/tooling call sites, which
        // have no use for this) - nothing to resolve against.
        if (_registerCallbacks.ReadX == null)
        {
            return null;
        }

        var kind = Mos6502Chip.GetEffectiveAddressKind((byte)instruction.Opcode);
        var address = instruction.AddressNumeric;

        ushort effective;
        switch (kind)
        {
            case Mos6502EffectiveAddressKind.ZeroPageX:
            {
                var zp = MemoryCallbacks.Read((ushort)(address + 1));
                effective = (byte)(zp + _registerCallbacks.ReadX());
                break;
            }

            case Mos6502EffectiveAddressKind.ZeroPageY:
            {
                var zp = MemoryCallbacks.Read((ushort)(address + 1));
                effective = (byte)(zp + _registerCallbacks.ReadY());
                break;
            }

            case Mos6502EffectiveAddressKind.AbsoluteX:
            {
                var baseAddress = MemoryCallbacks.ReadWord((ushort)(address + 1));
                effective = (ushort)(baseAddress + _registerCallbacks.ReadX());
                break;
            }

            case Mos6502EffectiveAddressKind.AbsoluteY:
            {
                var baseAddress = MemoryCallbacks.ReadWord((ushort)(address + 1));
                effective = (ushort)(baseAddress + _registerCallbacks.ReadY());
                break;
            }

            case Mos6502EffectiveAddressKind.IndexedIndirectX:
            {
                var zp = MemoryCallbacks.Read((ushort)(address + 1));
                var pointer = (byte)(zp + _registerCallbacks.ReadX());
                effective = (ushort)(MemoryCallbacks.Read(pointer) | (MemoryCallbacks.Read((byte)(pointer + 1)) << 8));
                break;
            }

            case Mos6502EffectiveAddressKind.IndirectIndexedY:
            {
                var zp = MemoryCallbacks.Read((ushort)(address + 1));
                var baseAddress = (ushort)(MemoryCallbacks.Read(zp) | (MemoryCallbacks.Read((byte)(zp + 1)) << 8));
                effective = (ushort)(baseAddress + _registerCallbacks.ReadY());
                break;
            }

            case Mos6502EffectiveAddressKind.Indirect:
            {
                // JMP (nnnn): a real 6502 hardware bug means that if the
                // pointer's low byte is 0xFF, the high byte is read from the
                // start of the same page rather than the next one - the
                // increment doesn't carry into the page byte.
                var pointer = MemoryCallbacks.ReadWord((ushort)(address + 1));
                var pointerHi = (ushort)((pointer & 0xFF00) | (byte)(pointer + 1));
                effective = (ushort)(MemoryCallbacks.Read(pointer) | (MemoryCallbacks.Read(pointerHi) << 8));
                break;
            }

            default:
                return null;
        }

        return (effective, MemoryCallbacks.Read(effective));
    }
}
