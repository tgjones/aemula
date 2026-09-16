namespace Aemula.Emulation.Chips.Mos6502;

/// <summary>
/// Addressing modes whose actual target address depends on live register
/// state (X/Y, or - for JMP indirect - a pointer read from memory), so it
/// can only be resolved for the instruction currently at PC, not any other
/// row in a disassembly listing. Every other addressing mode's operand
/// already *is* the effective address (or has none), so this is None for
/// those - see Mos6502Disassembler.TryGetEffectiveAddress.
/// </summary>
public enum Mos6502EffectiveAddressKind
{
    None,
    ZeroPageX,
    ZeroPageY,
    AbsoluteX,
    AbsoluteY,
    IndexedIndirectX,
    IndirectIndexedY,
    Indirect,
}
