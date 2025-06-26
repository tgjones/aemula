using System.Collections.Generic;
using Aemula.Core.Debugging;

namespace Aemula.Systems.Chip8.Debugging;

internal sealed class Chip8Disassembler : Disassembler
{
    public Chip8Disassembler(DebuggerMemoryCallbacks memoryCallbacks)
        : base(memoryCallbacks)
    {
    }

    protected override DisassembledInstruction DisassembleInstruction(ushort address)
    {
        return Chip8.Disassemble(address, MemoryCallbacks);
    }

    protected override void OnReset(List<ushort> startAddresses, Dictionary<ushort, string> labels)
    {
        labels[Chip8.ProgramStart] = "Program Start";

        startAddresses.Add(Chip8.ProgramStart);
    }
}
