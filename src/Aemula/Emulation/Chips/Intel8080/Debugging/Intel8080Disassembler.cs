using System;
using System.Collections.Generic;
using Aemula.Debugging;

namespace Aemula.Emulation.Chips.Intel8080.Debugging;

/// <summary>
/// Every Do0/Do1/Do2/DoHelper call passes its mnemonic and operand text
/// separately (rather than one pre-joined string) so each can be colored
/// independently in the debugger UI; MnemonicCategory is looked up from the
/// mnemonic via <see cref="MnemonicCategories"/> rather than repeated at
/// every call site, since it depends only on the mnemonic itself, while
/// OperandKind varies with the addressing shape at each call site and so is
/// passed explicitly (or computed from the opcode's own register-index bits
/// where a single switch arm covers both register and M-indirect forms, e.g.
/// the MOV and ALU blocks).
/// </summary>
public class Intel8080Disassembler : Disassembler
{
    // Category per mnemonic - a fixed fact about that mnemonic regardless of
    // which call site produced it, so it's looked up once here instead of
    // repeated at every one of the many call sites below. Throws (via
    // DoHelper) if a mnemonic is used without an entry, catching a missed
    // case in this switch's large surface area immediately rather than it
    // silently rendering as an uncategorized Other.
    private static readonly Dictionary<string, MnemonicCategory> MnemonicCategories = new()
    {
        ["NOP"] = MnemonicCategory.Other,
        ["LXI"] = MnemonicCategory.LoadStore,
        ["STAX"] = MnemonicCategory.LoadStore,
        ["INX"] = MnemonicCategory.Arithmetic,
        ["INR"] = MnemonicCategory.Arithmetic,
        ["DCR"] = MnemonicCategory.Arithmetic,
        ["MVI"] = MnemonicCategory.LoadStore,
        ["RLC"] = MnemonicCategory.Logic,
        ["DAD"] = MnemonicCategory.Arithmetic,
        ["LDAX"] = MnemonicCategory.LoadStore,
        ["DCX"] = MnemonicCategory.Arithmetic,
        ["RRC"] = MnemonicCategory.Logic,
        ["RAL"] = MnemonicCategory.Logic,
        ["RAR"] = MnemonicCategory.Logic,
        ["DAA"] = MnemonicCategory.Arithmetic,
        ["SHLD"] = MnemonicCategory.LoadStore,
        ["CMA"] = MnemonicCategory.Logic,
        ["LHLD"] = MnemonicCategory.LoadStore,
        ["STA"] = MnemonicCategory.LoadStore,
        ["STC"] = MnemonicCategory.FlagOp,
        ["LDA"] = MnemonicCategory.LoadStore,
        ["CMC"] = MnemonicCategory.FlagOp,
        ["MOV"] = MnemonicCategory.LoadStore,
        ["HLT"] = MnemonicCategory.Other,
        ["ADD"] = MnemonicCategory.Arithmetic,
        ["ADC"] = MnemonicCategory.Arithmetic,
        ["SUB"] = MnemonicCategory.Arithmetic,
        ["SBB"] = MnemonicCategory.Arithmetic,
        ["ANA"] = MnemonicCategory.Logic,
        ["XRA"] = MnemonicCategory.Logic,
        ["ORA"] = MnemonicCategory.Logic,
        ["CMP"] = MnemonicCategory.Arithmetic,
        ["RNZ"] = MnemonicCategory.Return,
        ["POP"] = MnemonicCategory.Stack,
        ["JNZ"] = MnemonicCategory.Branch,
        ["JMP"] = MnemonicCategory.Branch,
        ["CNZ"] = MnemonicCategory.Call,
        ["PUSH"] = MnemonicCategory.Stack,
        ["ADI"] = MnemonicCategory.Arithmetic,
        ["RST"] = MnemonicCategory.Call,
        ["RZ"] = MnemonicCategory.Return,
        ["RET"] = MnemonicCategory.Return,
        ["JZ"] = MnemonicCategory.Branch,
        ["CZ"] = MnemonicCategory.Call,
        ["CALL"] = MnemonicCategory.Call,
        ["ACI"] = MnemonicCategory.Arithmetic,
        ["RNC"] = MnemonicCategory.Return,
        ["JNC"] = MnemonicCategory.Branch,
        ["OUT"] = MnemonicCategory.IO,
        ["CNC"] = MnemonicCategory.Call,
        ["SUI"] = MnemonicCategory.Arithmetic,
        ["RC"] = MnemonicCategory.Return,
        ["JC"] = MnemonicCategory.Branch,
        ["IN"] = MnemonicCategory.IO,
        ["CC"] = MnemonicCategory.Call,
        ["SBI"] = MnemonicCategory.Arithmetic,
        ["RPO"] = MnemonicCategory.Return,
        ["XTHL"] = MnemonicCategory.Stack,
        ["JPO"] = MnemonicCategory.Branch,
        ["CPO"] = MnemonicCategory.Call,
        ["ANI"] = MnemonicCategory.Logic,
        ["RPE"] = MnemonicCategory.Return,
        ["PCHL"] = MnemonicCategory.Branch,
        ["XCHG"] = MnemonicCategory.Transfer,
        ["CPE"] = MnemonicCategory.Call,
        ["JPE"] = MnemonicCategory.Branch,
        ["XRI"] = MnemonicCategory.Logic,
        ["RP"] = MnemonicCategory.Return,
        ["JP"] = MnemonicCategory.Branch,
        ["DI"] = MnemonicCategory.FlagOp,
        ["CP"] = MnemonicCategory.Call,
        ["ORI"] = MnemonicCategory.Logic,
        ["RM"] = MnemonicCategory.Return,
        ["SPHL"] = MnemonicCategory.Transfer,
        ["JM"] = MnemonicCategory.Branch,
        ["EI"] = MnemonicCategory.FlagOp,
        ["CM"] = MnemonicCategory.Call,
        ["CPI"] = MnemonicCategory.Arithmetic,
    };

    public Intel8080Disassembler(DebuggerMemoryCallbacks memoryCallbacks)
        : base(memoryCallbacks)
    {
    }

    protected override void OnReset(List<ushort> startAddresses, Dictionary<ushort, string> labels)
    {
        startAddresses.Add(0x0000);
    }

    protected override DisassembledInstruction DisassembleInstruction(ushort address)
    {
        var opcode = MemoryCallbacks.Read(address);

        return opcode switch
        {
            0x00 => Do0("NOP"),
            0x01 => Do2("LXI", "B, 0x", OperandKind.Immediate),
            0x02 => Do0("STAX", "B", OperandKind.Indirect),
            0x03 => Do0("INX", "B", OperandKind.Register),
            0x04 => Do0("INR", "B", OperandKind.Register),
            0x05 => Do0("DCR", "B", OperandKind.Register),
            0x06 => Do1("MVI", "B, 0x", OperandKind.Immediate),
            0x07 => Do0("RLC"),

            0x09 => Do0("DAD", "B", OperandKind.Register),
            0x0A => Do0("LDAX", "B", OperandKind.Indirect),
            0x0B => Do0("DCX", "B", OperandKind.Register),
            0x0C => Do0("INR", "C", OperandKind.Register),
            0x0D => Do0("DCR", "C", OperandKind.Register),
            0x0E => Do1("MVI", "C, 0x", OperandKind.Immediate),
            0x0F => Do0("RRC"),

            0x11 => Do2("LXI", "D, 0x", OperandKind.Immediate),
            0x12 => Do0("STAX", "D", OperandKind.Indirect),
            0x13 => Do0("INX", "D", OperandKind.Register),
            0x14 => Do0("INR", "D", OperandKind.Register),
            0x15 => Do0("DCR", "D", OperandKind.Register),
            0x16 => Do1("MVI", "D, 0x", OperandKind.Immediate),
            0x17 => Do0("RAL"),

            0x19 => Do0("DAD", "D", OperandKind.Register),
            0x1A => Do0("LDAX", "D", OperandKind.Indirect),
            0x1B => Do0("DCX", "D", OperandKind.Register),
            0x1C => Do0("INR", "E", OperandKind.Register),
            0x1D => Do0("DCR", "E", OperandKind.Register),
            0x1E => Do1("MVI", "E, 0x", OperandKind.Immediate),
            0x1F => Do0("RAR"),

            0x21 => Do2("LXI", "H, 0x", OperandKind.Immediate),
            0x22 => Do2("SHLD", "0x", OperandKind.Address),
            0x23 => Do0("INX", "H", OperandKind.Register),
            0x24 => Do0("INR", "H", OperandKind.Register),
            0x25 => Do0("DCR", "H", OperandKind.Register),
            0x26 => Do1("MVI", "H, 0x", OperandKind.Immediate),
            0x27 => Do0("DAA"),

            0x29 => Do0("DAD", "H", OperandKind.Register),
            0x2A => Do2("LHLD", "0x", OperandKind.Address),
            0x2B => Do0("DCX", "H", OperandKind.Register),
            0x2C => Do0("INR", "L", OperandKind.Register),
            0x2D => Do0("DCR", "L", OperandKind.Register),
            0x2E => Do1("MVI", "L, 0x", OperandKind.Immediate),
            0x2F => Do0("CMA"),

            0x31 => Do2("LXI", "SP, 0x", OperandKind.Immediate),
            0x32 => Do2("STA", "0x", OperandKind.Address),
            0x33 => Do0("INX", "SP", OperandKind.Register),
            0x34 => Do0("INR", "M", OperandKind.Indirect),
            0x35 => Do0("DCR", "M", OperandKind.Indirect),
            0x36 => Do1("MVI", "M, 0x", OperandKind.Indirect),
            0x37 => Do0("STC"),

            0x39 => Do0("DAD", "SP", OperandKind.Register),
            0x3A => Do2("LDA", "0x", OperandKind.Address),
            0x3B => Do0("DCX", "SP", OperandKind.Register),
            0x3C => Do0("INR", "A", OperandKind.Register),
            0x3D => Do0("DCR", "A", OperandKind.Register),
            0x3E => Do1("MVI", "A, 0x", OperandKind.Immediate),
            0x3F => Do0("CMC"),

            0x76 => Do0("HLT", hasNext: false),
            >= 0x40 and <= 0x7F => MovRR((opcode >> 3) & 0x7, opcode & 0x7),

            >= 0x80 and <= 0x87 => Alu("ADD", opcode & 0x7),
            >= 0x88 and <= 0x8F => Alu("ADC", opcode & 0x7),
            >= 0x90 and <= 0x97 => Alu("SUB", opcode & 0x7),
            >= 0x98 and <= 0x9F => Alu("SBB", opcode & 0x7),
            >= 0xA0 and <= 0xA7 => Alu("ANA", opcode & 0x7),
            >= 0xA8 and <= 0xAF => Alu("XRA", opcode & 0x7),
            >= 0xB0 and <= 0xB7 => Alu("ORA", opcode & 0x7),
            >= 0xB8 and <= 0xBF => Alu("CMP", opcode & 0x7),

            0xC0 => Do0("RNZ"),
            0xC1 => Do0("POP", "B", OperandKind.Register),
            0xC2 => Do2("JNZ", "0x", OperandKind.Address, jumpType: JumpType.Jump),
            0xC3 => Do2("JMP", "0x", OperandKind.Address, hasNext: false, JumpType.Jump),
            0xC4 => Do2("CNZ", "0x", OperandKind.Address, jumpType: JumpType.Call),
            0xC5 => Do0("PUSH", "B", OperandKind.Register),
            0xC6 => Do1("ADI", "0x", OperandKind.Immediate),
            0xC7 => Do0("RST", "0", OperandKind.Address, hasNext: false),

            0xC8 => Do0("RZ"),
            0xC9 => Do0("RET", hasNext: false),
            0xCA => Do2("JZ", "0x", OperandKind.Address, jumpType: JumpType.Jump),
            0xCC => Do2("CZ", "0x", OperandKind.Address, jumpType: JumpType.Call),
            0xCD => Do2("CALL", "0x", OperandKind.Address, jumpType: JumpType.Call),
            0xCE => Do1("ACI", "0x", OperandKind.Immediate),
            0xCF => Do0("RST", "1", OperandKind.Address, hasNext: false),

            0xD0 => Do0("RNC"),
            0xD1 => Do0("POP", "D", OperandKind.Register),
            0xD2 => Do2("JNC", "0x", OperandKind.Address, jumpType: JumpType.Jump),
            0xD3 => Do1("OUT", "0x", OperandKind.Immediate),
            0xD4 => Do2("CNC", "0x", OperandKind.Address, jumpType: JumpType.Call),
            0xD5 => Do0("PUSH", "D", OperandKind.Register),
            0xD6 => Do1("SUI", "0x", OperandKind.Immediate),
            0xD7 => Do0("RST", "2", OperandKind.Address, hasNext: false),

            0xD8 => Do0("RC"),
            0xDA => Do2("JC", "0x", OperandKind.Address, jumpType: JumpType.Jump),
            0xDB => Do1("IN", "0x", OperandKind.Immediate),
            0xDC => Do2("CC", "0x", OperandKind.Address, jumpType: JumpType.Call),
            0xDE => Do1("SBI", "0x", OperandKind.Immediate),
            0xDF => Do0("RST", "3", OperandKind.Address, hasNext: false),

            0xE0 => Do0("RPO"),
            0xE1 => Do0("POP", "H", OperandKind.Register),
            0xE2 => Do2("JPO", "0x", OperandKind.Address, jumpType: JumpType.Jump),
            0xE3 => Do0("XTHL"),
            0xE4 => Do2("CPO", "0x", OperandKind.Address, jumpType: JumpType.Call),
            0xE5 => Do0("PUSH", "H", OperandKind.Register),
            0xE6 => Do1("ANI", "0x", OperandKind.Immediate),
            0xE7 => Do0("RST", "4", OperandKind.Address, hasNext: false),

            0xE8 => Do0("RPE"),
            0xE9 => Do0("PCHL", hasNext: false),
            0xEB => Do0("XCHG"),
            0xEC => Do2("CPE", "0x", OperandKind.Address, jumpType: JumpType.Call),
            0xEA => Do2("JPE", "0x", OperandKind.Address, jumpType: JumpType.Jump),
            0xEE => Do1("XRI", "0x", OperandKind.Immediate),
            0xEF => Do0("RST", "5", OperandKind.Address, hasNext: false),

            0xF0 => Do0("RP"),
            0xF1 => Do0("POP", "PSW", OperandKind.Register),
            0xF2 => Do2("JP", "0x", OperandKind.Address, jumpType: JumpType.Jump),

            0xF3 => Do0("DI"),
            0xF4 => Do2("CP", "0x", OperandKind.Address, jumpType: JumpType.Call),
            0xF5 => Do0("PUSH", "PSW", OperandKind.Register),
            0xF6 => Do1("ORI", "0x", OperandKind.Immediate),
            0xF7 => Do0("RST", "6", OperandKind.Address, hasNext: false),

            0xF8 => Do0("RM"),
            0xF9 => Do0("SPHL"),
            0xFA => Do2("JM", "0x", OperandKind.Address, jumpType: JumpType.Jump),
            0xFB => Do0("EI"),
            0xFC => Do2("CM", "0x", OperandKind.Address, jumpType: JumpType.Call),
            0xFE => Do1("CPI", "0x", OperandKind.Immediate),
            0xFF => Do0("RST", "7", OperandKind.Address, hasNext: false),

            _ => throw new InvalidOperationException($"Opcode 0x{opcode:X2} not supported"),
        };

        static string Reg(int index) => index switch
        {
            0 => "B",
            1 => "C",
            2 => "D",
            3 => "E",
            4 => "H",
            5 => "L",
            6 => "M",
            _ => "A",
        };

        // 0x40-0x7F "MOV d, s" range: a single switch arm covers both pure
        // register-to-register moves and ones touching M (memory via HL) -
        // the operand kind is computed from the same index bits rather than
        // sniffed from the resulting text.
        DisassembledInstruction MovRR(int destIndex, int srcIndex)
        {
            var kind = destIndex == 6 || srcIndex == 6 ? OperandKind.Indirect : OperandKind.Register;
            return Do0("MOV", $"{Reg(destIndex)}, {Reg(srcIndex)}", kind);
        }

        // 0x80-0xBF ALU range: same register-or-M shape as MovRR above, with
        // a single source operand - unlike Z80, 8080 syntax never shows an
        // explicit "A," destination since every one of these ops implicitly
        // targets the accumulator.
        DisassembledInstruction Alu(string mnemonic, int regIndex)
        {
            var kind = regIndex == 6 ? OperandKind.Indirect : OperandKind.Register;
            return Do0(mnemonic, Reg(regIndex), kind);
        }

        DisassembledInstruction Do0(string mnemonic, string operand = "", OperandKind operandKind = OperandKind.None, bool hasNext = true)
        {
            return DoHelper(mnemonic, operand, operandKind, 1, $"{opcode:X2}", hasNext, null);
        }

        DisassembledInstruction Do1(string mnemonic, string operandPrefix, OperandKind operandKind = OperandKind.None)
        {
            var operand = MemoryCallbacks.Read((ushort)(address + 1));

            return DoHelper(
                mnemonic,
                $"{operandPrefix}{operand:X2}",
                operandKind,
                2,
                $"{opcode:X2} {operand:X2}",
                true,
                null);
        }

        DisassembledInstruction Do2(string mnemonic, string operandPrefix, OperandKind operandKind = OperandKind.None, bool hasNext = true, JumpType? jumpType = null)
        {
            var operandLo = MemoryCallbacks.Read((ushort)(address + 1));
            var operandHi = MemoryCallbacks.Read((ushort)(address + 2));
            var operand = (ushort)(operandHi << 8 | operandLo);

            // NB: this always tags the jump target as JumpType.Jump, even when
            // called with jumpType: JumpType.Call (every CALL/Cxx site below) -
            // pre-existing behavior carried over unchanged from before this
            // mnemonic/operand split; fixing which targets get labeled
            // "Subroutine" is unrelated to that split and out of scope here.
            JumpTarget? jumpTarget = jumpType != null
                ? new JumpTarget(JumpType.Jump, operand)
                : null;

            return DoHelper(
                mnemonic,
                $"{operandPrefix}{operandHi:X2}{operandLo:X2}",
                operandKind,
                3,
                $"{opcode:X2} {operandLo:X2} {operandHi:X2}",
                hasNext,
                jumpTarget);
        }

        DisassembledInstruction DoHelper(
            string mnemonic,
            string operand,
            OperandKind operandKind,
            byte instructionSizeInBytes,
            string rawBytes,
            bool hasNext,
            JumpTarget? jumpTarget)
        {
            if (!MnemonicCategories.TryGetValue(mnemonic, out var category))
            {
                throw new InvalidOperationException($"No MnemonicCategory mapping for mnemonic {mnemonic}");
            }

            return new DisassembledInstruction(
                opcode,
                address,
                $"{address:X4}",
                instructionSizeInBytes,
                rawBytes,
                mnemonic,
                category,
                operand,
                operandKind,
                hasNext ? (ushort)(address + instructionSizeInBytes) : null,
                jumpTarget);
        }
    }
}
