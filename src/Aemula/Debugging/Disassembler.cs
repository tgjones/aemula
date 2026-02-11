using System;
using System.Collections.Generic;

namespace Aemula.Debugging;

public abstract class Disassembler(DebuggerMemoryCallbacks memoryCallbacks)
{
    protected readonly DebuggerMemoryCallbacks MemoryCallbacks = memoryCallbacks;

    public readonly DisassemblyEntry[] Cache = new DisassemblyEntry[0x10000];

    internal bool Changed;

    public void Reset()
    {
        Array.Clear(Cache);

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

    public void OnAddressExecuting(ushort address)
    {
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
