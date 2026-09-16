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

    public void Reset()
    {
        Array.Clear(Cache);
        Array.Clear(ExecutionCounts);

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

#pragma warning disable CA1822 // Mark members as static
#pragma warning disable IDE0060 // Remove unused parameter
    public void OnDataWritten(ushort address)
#pragma warning restore IDE0060 // Remove unused parameter
#pragma warning restore CA1822 // Mark members as static
    {
        // TODO: Invalidate cache for this address.
    }
}

public record struct DisassemblyEntry(string Label, DisassembledInstruction? Instruction);
