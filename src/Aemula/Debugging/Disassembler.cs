using System;
using System.Collections.Generic;

namespace Aemula.Debugging;

public abstract class Disassembler
{
    protected readonly DebuggerMemoryCallbacks MemoryCallbacks;

    public readonly DisassemblyEntry[] Cache;

    internal bool Changed;

    public Disassembler(DebuggerMemoryCallbacks memoryCallbacks)
    {
        Cache = new DisassemblyEntry[0x10000];

        MemoryCallbacks = memoryCallbacks;
    }

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

        DisassembleAddresses(new List<ushort> { address });
    }

    public void OnDataWritten(ushort address)
    {
        // TODO: Invalidate cache for this address.
    }
}

public record struct DisassemblyEntry(string Label, DisassembledInstruction? Instruction);
