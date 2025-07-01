using System.Collections.Generic;
using Aemula.Debugging;

namespace Aemula.Emulation.Systems.Chip8.Debugging;

internal sealed class Chip8Disassembler : Disassembler
{
    public Chip8Disassembler(DebuggerMemoryCallbacks memoryCallbacks)
        : base(memoryCallbacks)
    {
    }

    protected override DisassembledInstruction DisassembleInstruction(ushort address)
    {
        return Chip8System.Disassemble(address, MemoryCallbacks);
    }

    protected override void OnReset(List<ushort> startAddresses, Dictionary<ushort, string> labels)
    {
        labels[Chip8System.ProgramStart] = "Program Start";

        startAddresses.Add(Chip8System.ProgramStart);
    }
}
