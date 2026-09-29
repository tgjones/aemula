using System;
using System.Collections.Generic;

namespace Aemula.Debugging;

public abstract class Disassembler(DebuggerMemoryCallbacks memoryCallbacks)
{
    protected readonly DebuggerMemoryCallbacks MemoryCallbacks = memoryCallbacks;

    public readonly DisassemblyEntry[] Cache = new DisassemblyEntry[0x10000];

    // How many times each address has been the target of an instruction
    // fetch - incremented unconditionally on every OnAddressExecuting call
    // (unlike Cache, which only records the first disassembly of an
    // address), so it stays a true execution count for the heatmap in
    // DisassemblyWindow rather than a "seen at least once" flag.
    public readonly int[] ExecutionCounts = new int[0x10000];

    internal bool Changed;

    // Longest instruction on any supported CPU (Z80 DD CB d op).
    private const int MaxInstructionSizeInBytes = 4;

    private readonly List<ushort> _invalidated = [];

    public void Reset()
    {
        Array.Clear(Cache);
        Array.Clear(ExecutionCounts);
        _invalidated.Clear();

        var startAddresses = new List<ushort>();
        var labels = new Dictionary<ushort, string>();
        OnReset(startAddresses, labels);

        DisassembleAddresses(startAddresses);

        foreach (var label in labels)
        {
            Cache[label.Key].Label = label.Value;
        }
    }

    private void DisassembleAddresses(List<ushort> addresses)
    {
        var queue = new Queue<ushort>(addresses);

        var visited = new HashSet<ushort>();
        while (queue.Count > 0)
        {
            var address = queue.Dequeue();

            if (!visited.Add(address) || Cache[address].Instruction != null)
            {
                continue;
            }

            var disassembledInstruction = DisassembleInstruction(address);

            Cache[address].Instruction = disassembledInstruction;

            if (disassembledInstruction.Next != null)
            {
                queue.Enqueue(disassembledInstruction.Next.Value);
            }

            if (disassembledInstruction.JumpTarget != null)
            {
                queue.Enqueue(disassembledInstruction.JumpTarget.Value.Address);

                if (disassembledInstruction.JumpTarget.Value.Type == JumpType.Call)
                {
                    Cache[disassembledInstruction.JumpTarget.Value.Address].Label = "Subroutine";
                }
            }
        }

        Changed = true;
    }

    protected abstract void OnReset(
        List<ushort> startAddresses,
        Dictionary<ushort, string> labels);

    protected abstract DisassembledInstruction DisassembleInstruction(ushort address);

    /// <summary>
    /// For an indexed/indirect instruction, what address and byte value its
    /// operand actually resolves to right now - only meaningful for the
    /// instruction currently at PC (X/Y or the pointed-to memory could be
    /// anything the next time any other row's instruction runs), so callers
    /// should only call this for that one row. Returns null for addressing
    /// modes where the operand already is the effective address (or there is
    /// none) and for chips that don't implement this at all.
    /// </summary>
    public virtual (ushort Address, byte Value)? TryGetEffectiveAddress(in DisassembledInstruction instruction) => null;

    public void OnAddressExecuting(ushort address)
    {
        ExecutionCounts[address]++;

        if (Cache[address].Instruction != null)
        {
            return;
        }

        DisassembleAddresses([address]);
    }

    /// <summary>
    /// Called when the CPU writes a byte. Drops any cached instruction that
    /// covers <paramref name="address"/>, since its bytes are no longer what
    /// was disassembled (code copied into RAM after the disassembler first
    /// followed a call to it, self-modifying code). The dropped instructions
    /// are disassembled again by <see cref="ReseedInvalidated"/>, not here,
    /// so the write has landed in memory by the time it's read back.
    /// </summary>
    public void OnDataWritten(ushort address)
    {
        for (var back = 0; back < MaxInstructionSizeInBytes && back <= address; back++)
        {
            var start = address - back;
            if (Cache[start].Instruction is { } instruction && start + instruction.InstructionSizeInBytes > address)
            {
                Cache[start].Instruction = null;
                _invalidated.Add((ushort)start);
            }
        }
    }

    /// <summary>
    /// Disassembles again, from what's in memory now, the instructions that
    /// <see cref="OnDataWritten"/> dropped.
    /// </summary>
    public void ReseedInvalidated()
    {
        if (_invalidated.Count == 0)
        {
            return;
        }

        var addresses = _invalidated.ToArray();
        _invalidated.Clear();
        DisassembleAddresses([.. addresses]);
    }
}

public record struct DisassemblyEntry(string Label, DisassembledInstruction? Instruction);
