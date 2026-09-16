namespace Aemula.Debugging;

public readonly record struct DisassembledInstruction(
    ushort Opcode,
    ushort AddressNumeric,
    string Address,
    byte InstructionSizeInBytes,
    string RawBytes,
    string Mnemonic,
    MnemonicCategory MnemonicCategory,
    string Operand,
    OperandKind OperandKind,
    ushort? Next,
    JumpTarget? JumpTarget)
{
    // Convenience for plain-text consumers (Mos6502ChipTestHelper,
    // Ricoh2A03ChipTestHelper, and DisassemblyWindow's own copy-to-clipboard
    // action) that just want one line of text, computed on demand rather
    // than stored, now that Mnemonic and Operand are held separately.
    public string Disassembly => Operand.Length > 0 ? $"{Mnemonic} {Operand}" : Mnemonic;
}

public readonly record struct JumpTarget(JumpType Type, ushort Address);

public enum JumpType
{
    Jump,
    Call,
}

/// <summary>
/// What kind of operation a mnemonic performs, independent of which chip or
/// addressing mode produced it - used to color-code the disassembly listing
/// so the same category reads the same way across the Mos6502/Z80/Intel8080
/// families.
/// </summary>
public enum MnemonicCategory
{
    /// <summary>Not (yet) categorized - rendered in the plain text color.</summary>
    Other,
    Branch,
    Call,
    Return,
    LoadStore,
    Arithmetic,
    Logic,
    Stack,
    Transfer,
    FlagOp,
    IO,
}

/// <summary>
/// What shape an instruction's operand has - used alongside
/// <see cref="MnemonicCategory"/> to color-code the operand text separately
/// from the mnemonic.
/// </summary>
public enum OperandKind
{
    None,
    Immediate,
    Address,
    Indirect,
    Register,
}
