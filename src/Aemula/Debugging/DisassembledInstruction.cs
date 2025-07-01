namespace Aemula.Debugging;

public readonly record struct DisassembledInstruction(
    ushort Opcode,
    ushort AddressNumeric,
    string Address,
    byte InstructionSizeInBytes,
    string RawBytes,
    string Disassembly,
    ushort? Next,
    JumpTarget? JumpTarget);

public readonly record struct JumpTarget(JumpType Type, ushort Address);

public enum JumpType
{
    Jump,
    Call,
}
