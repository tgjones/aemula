using System;
using System.Collections.Generic;
using Aemula.Debugging;

namespace Aemula.Emulation.Chips.Z80.Debugging;

/// <summary>
/// Text disassembly for the Z80, mirroring <c>Intel8080Disassembler</c>: a
/// <see cref="DisassembleInstruction"/> switch over the opcode byte with
/// <c>Do0</c> / <c>Do1</c> / <c>Do2</c> helpers for the 0-, 1- and 2-operand-byte
/// forms, and <c>OnReset</c> seeding the reset vector at 0x0000.
///
/// The base (unprefixed) table, the CB table (rotate/shift and BIT/RES/SET),
/// the ED table (block ops, 16-bit loads, ADC/SBC HL, NEG, IM, LD A,I/R,
/// RRD/RLD, RETN/RETI, IN/OUT) and the DD / FD index-register table (with the
/// DD CB / FD CB double prefix) are filled in. A DD / FD re-aims HL -> IX/IY,
/// H/L -> IXH/IXL (IYH/IYL) and (HL) -> (IX+d); a DD / FD in front of an opcode
/// that names none of them is shown as an inert "DD" / "FD" prefix followed by
/// the opcode decoded on its own.
///
/// Every Do0/Do1/Do2/DoHelper call passes its mnemonic and operand text
/// separately (rather than one pre-joined string) so each can be colored
/// independently in the debugger UI; MnemonicCategory is looked up from the
/// mnemonic via <see cref="MnemonicCategories"/> rather than repeated at
/// every call site, since it depends only on the mnemonic itself, while
/// OperandKind varies with the addressing shape at each call site and so is
/// passed explicitly (or computed from the opcode's own register-index bits
/// where a single switch arm covers both register and (HL)-indirect forms).
/// </summary>
public class Z80Disassembler : Disassembler
{
    // Category per mnemonic - a fixed fact about that mnemonic regardless of
    // which addressing form or call site produced it, so it's looked up once
    // here instead of repeated at every one of the many call sites below.
    // Throws (via DoHelper) if a mnemonic is used without an entry, catching
    // a missed case in this switch's large surface area immediately rather
    // than it silently rendering as an uncategorized Other.
    private static readonly Dictionary<string, MnemonicCategory> MnemonicCategories = new()
    {
        ["NOP"] = MnemonicCategory.Other,
        ["LD"] = MnemonicCategory.LoadStore,
        ["INC"] = MnemonicCategory.Arithmetic,
        ["DEC"] = MnemonicCategory.Arithmetic,
        ["RLCA"] = MnemonicCategory.Logic,
        ["EX"] = MnemonicCategory.Transfer,
        ["ADD"] = MnemonicCategory.Arithmetic,
        ["RRCA"] = MnemonicCategory.Logic,
        ["DJNZ"] = MnemonicCategory.Branch,
        ["RLA"] = MnemonicCategory.Logic,
        ["JR"] = MnemonicCategory.Branch,
        ["RRA"] = MnemonicCategory.Logic,
        ["DAA"] = MnemonicCategory.Arithmetic,
        ["CPL"] = MnemonicCategory.Logic,
        ["SCF"] = MnemonicCategory.FlagOp,
        ["CCF"] = MnemonicCategory.FlagOp,
        ["HALT"] = MnemonicCategory.Other,
        ["ADC"] = MnemonicCategory.Arithmetic,
        ["SUB"] = MnemonicCategory.Arithmetic,
        ["SBC"] = MnemonicCategory.Arithmetic,
        ["AND"] = MnemonicCategory.Logic,
        ["XOR"] = MnemonicCategory.Logic,
        ["OR"] = MnemonicCategory.Logic,
        ["CP"] = MnemonicCategory.Arithmetic,
        ["RET"] = MnemonicCategory.Return,
        ["POP"] = MnemonicCategory.Stack,
        ["JP"] = MnemonicCategory.Branch,
        ["CALL"] = MnemonicCategory.Call,
        ["PUSH"] = MnemonicCategory.Stack,
        ["RST"] = MnemonicCategory.Call,
        ["OUT"] = MnemonicCategory.IO,
        ["IN"] = MnemonicCategory.IO,
        ["EXX"] = MnemonicCategory.Transfer,
        ["DI"] = MnemonicCategory.FlagOp,
        ["EI"] = MnemonicCategory.FlagOp,
        ["RLC"] = MnemonicCategory.Logic,
        ["RRC"] = MnemonicCategory.Logic,
        ["RL"] = MnemonicCategory.Logic,
        ["RR"] = MnemonicCategory.Logic,
        ["SLA"] = MnemonicCategory.Logic,
        ["SRA"] = MnemonicCategory.Logic,
        ["SLL"] = MnemonicCategory.Logic,
        ["SRL"] = MnemonicCategory.Logic,
        ["BIT"] = MnemonicCategory.Logic,
        ["RES"] = MnemonicCategory.Logic,
        ["SET"] = MnemonicCategory.Logic,
        ["NEG"] = MnemonicCategory.Arithmetic,
        ["RETN"] = MnemonicCategory.Return,
        ["RETI"] = MnemonicCategory.Return,
        ["IM"] = MnemonicCategory.FlagOp,
        ["RRD"] = MnemonicCategory.Logic,
        ["RLD"] = MnemonicCategory.Logic,
        ["LDI"] = MnemonicCategory.LoadStore,
        ["LDD"] = MnemonicCategory.LoadStore,
        ["LDIR"] = MnemonicCategory.LoadStore,
        ["LDDR"] = MnemonicCategory.LoadStore,
        ["CPI"] = MnemonicCategory.Arithmetic,
        ["CPD"] = MnemonicCategory.Arithmetic,
        ["CPIR"] = MnemonicCategory.Arithmetic,
        ["CPDR"] = MnemonicCategory.Arithmetic,
        ["INI"] = MnemonicCategory.IO,
        ["IND"] = MnemonicCategory.IO,
        ["INIR"] = MnemonicCategory.IO,
        ["INDR"] = MnemonicCategory.IO,
        ["OUTI"] = MnemonicCategory.IO,
        ["OUTD"] = MnemonicCategory.IO,
        ["OTIR"] = MnemonicCategory.IO,
        ["OTDR"] = MnemonicCategory.IO,
        ["NOP*"] = MnemonicCategory.Other,
        ["DB"] = MnemonicCategory.Other,
    };

    public Z80Disassembler(DebuggerMemoryCallbacks memoryCallbacks)
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
            0x01 => Do2("LD", "BC, 0x", OperandKind.Immediate),
            0x02 => Do0("LD", "(BC), A", OperandKind.Indirect),
            0x03 => Do0("INC", "BC", OperandKind.Register),
            0x04 => Do0("INC", "B", OperandKind.Register),
            0x05 => Do0("DEC", "B", OperandKind.Register),
            0x06 => Do1("LD", "B, 0x", OperandKind.Immediate),
            0x07 => Do0("RLCA"),
            0x08 => Do0("EX", "AF, AF'", OperandKind.Register),
            0x09 => Do0("ADD", "HL, BC", OperandKind.Register),
            0x0A => Do0("LD", "A, (BC)", OperandKind.Indirect),
            0x0B => Do0("DEC", "BC", OperandKind.Register),
            0x0C => Do0("INC", "C", OperandKind.Register),
            0x0D => Do0("DEC", "C", OperandKind.Register),
            0x0E => Do1("LD", "C, 0x", OperandKind.Immediate),
            0x0F => Do0("RRCA"),

            0x10 => Do1("DJNZ", "0x", OperandKind.Immediate),
            0x11 => Do2("LD", "DE, 0x", OperandKind.Immediate),
            0x12 => Do0("LD", "(DE), A", OperandKind.Indirect),
            0x13 => Do0("INC", "DE", OperandKind.Register),
            0x14 => Do0("INC", "D", OperandKind.Register),
            0x15 => Do0("DEC", "D", OperandKind.Register),
            0x16 => Do1("LD", "D, 0x", OperandKind.Immediate),
            0x17 => Do0("RLA"),
            0x18 => Do1("JR", "0x", OperandKind.Immediate),
            0x19 => Do0("ADD", "HL, DE", OperandKind.Register),
            0x1A => Do0("LD", "A, (DE)", OperandKind.Indirect),
            0x1B => Do0("DEC", "DE", OperandKind.Register),
            0x1C => Do0("INC", "E", OperandKind.Register),
            0x1D => Do0("DEC", "E", OperandKind.Register),
            0x1E => Do1("LD", "E, 0x", OperandKind.Immediate),
            0x1F => Do0("RRA"),

            0x20 => Do1("JR", "NZ, 0x", OperandKind.Immediate),
            0x21 => Do2("LD", "HL, 0x", OperandKind.Immediate),
            0x22 => Do2("LD", "(0x", OperandKind.Indirect, "), HL"),
            0x23 => Do0("INC", "HL", OperandKind.Register),
            0x24 => Do0("INC", "H", OperandKind.Register),
            0x25 => Do0("DEC", "H", OperandKind.Register),
            0x26 => Do1("LD", "H, 0x", OperandKind.Immediate),
            0x27 => Do0("DAA"),
            0x28 => Do1("JR", "Z, 0x", OperandKind.Immediate),
            0x29 => Do0("ADD", "HL, HL", OperandKind.Register),
            0x2A => Do2("LD", "HL, (0x", OperandKind.Indirect, ")"),
            0x2B => Do0("DEC", "HL", OperandKind.Register),
            0x2C => Do0("INC", "L", OperandKind.Register),
            0x2D => Do0("DEC", "L", OperandKind.Register),
            0x2E => Do1("LD", "L, 0x", OperandKind.Immediate),
            0x2F => Do0("CPL"),

            0x30 => Do1("JR", "NC, 0x", OperandKind.Immediate),
            0x31 => Do2("LD", "SP, 0x", OperandKind.Immediate),
            0x32 => Do2("LD", "(0x", OperandKind.Indirect, "), A"),
            0x33 => Do0("INC", "SP", OperandKind.Register),
            0x34 => Do0("INC", "(HL)", OperandKind.Indirect),
            0x35 => Do0("DEC", "(HL)", OperandKind.Indirect),
            0x36 => Do1("LD", "(HL), 0x", OperandKind.Indirect),
            0x37 => Do0("SCF"),
            0x38 => Do1("JR", "C, 0x", OperandKind.Immediate),
            0x39 => Do0("ADD", "HL, SP", OperandKind.Register),
            0x3A => Do2("LD", "A, (0x", OperandKind.Indirect, ")"),
            0x3B => Do0("DEC", "SP", OperandKind.Register),
            0x3C => Do0("INC", "A", OperandKind.Register),
            0x3D => Do0("DEC", "A", OperandKind.Register),
            0x3E => Do1("LD", "A, 0x", OperandKind.Immediate),
            0x3F => Do0("CCF"),

            0x76 => Do0("HALT", hasNext: false),
            >= 0x40 and <= 0x7F => LdRR((opcode >> 3) & 0x7, opcode & 0x7),

            >= 0x80 and <= 0x87 => Alu("ADD", opcode & 0x7, hasDestA: true),
            >= 0x88 and <= 0x8F => Alu("ADC", opcode & 0x7, hasDestA: true),
            >= 0x90 and <= 0x97 => Alu("SUB", opcode & 0x7),
            >= 0x98 and <= 0x9F => Alu("SBC", opcode & 0x7, hasDestA: true),
            >= 0xA0 and <= 0xA7 => Alu("AND", opcode & 0x7),
            >= 0xA8 and <= 0xAF => Alu("XOR", opcode & 0x7),
            >= 0xB0 and <= 0xB7 => Alu("OR", opcode & 0x7),
            >= 0xB8 and <= 0xBF => Alu("CP", opcode & 0x7),

            0xC0 => Do0("RET", "NZ"),
            0xC1 => Do0("POP", "BC", OperandKind.Register),
            0xC2 => Do2("JP", "NZ, 0x", OperandKind.Address, jumpType: JumpType.Jump),
            0xC3 => Do2("JP", "0x", OperandKind.Address, hasNext: false, jumpType: JumpType.Jump),
            0xC4 => Do2("CALL", "NZ, 0x", OperandKind.Address, jumpType: JumpType.Call),
            0xC5 => Do0("PUSH", "BC", OperandKind.Register),
            0xC6 => Do1("ADD", "A, 0x", OperandKind.Immediate),
            0xC7 => Do0("RST", "0x00", OperandKind.Address, hasNext: false),
            0xC8 => Do0("RET", "Z"),
            0xC9 => Do0("RET", hasNext: false),
            0xCA => Do2("JP", "Z, 0x", OperandKind.Address, jumpType: JumpType.Jump),
            0xCB => DecodeCb(),
            0xCC => Do2("CALL", "Z, 0x", OperandKind.Address, jumpType: JumpType.Call),
            0xCD => Do2("CALL", "0x", OperandKind.Address, jumpType: JumpType.Call),
            0xCE => Do1("ADC", "A, 0x", OperandKind.Immediate),
            0xCF => Do0("RST", "0x08", OperandKind.Address, hasNext: false),

            0xD0 => Do0("RET", "NC"),
            0xD1 => Do0("POP", "DE", OperandKind.Register),
            0xD2 => Do2("JP", "NC, 0x", OperandKind.Address, jumpType: JumpType.Jump),
            0xD3 => Do1("OUT", "(0x", OperandKind.Indirect, "), A"),
            0xD4 => Do2("CALL", "NC, 0x", OperandKind.Address, jumpType: JumpType.Call),
            0xD5 => Do0("PUSH", "DE", OperandKind.Register),
            0xD6 => Do1("SUB", "0x", OperandKind.Immediate),
            0xD7 => Do0("RST", "0x10", OperandKind.Address, hasNext: false),
            0xD8 => Do0("RET", "C"),
            0xD9 => Do0("EXX"),
            0xDA => Do2("JP", "C, 0x", OperandKind.Address, jumpType: JumpType.Jump),
            0xDB => Do1("IN", "A, (0x", OperandKind.Indirect, ")"),
            0xDC => Do2("CALL", "C, 0x", OperandKind.Address, jumpType: JumpType.Call),
            0xDD => DecodeDdFd(iy: false),
            0xDE => Do1("SBC", "A, 0x", OperandKind.Immediate),
            0xDF => Do0("RST", "0x18", OperandKind.Address, hasNext: false),

            0xE0 => Do0("RET", "PO"),
            0xE1 => Do0("POP", "HL", OperandKind.Register),
            0xE2 => Do2("JP", "PO, 0x", OperandKind.Address, jumpType: JumpType.Jump),
            0xE3 => Do0("EX", "(SP), HL", OperandKind.Indirect),
            0xE4 => Do2("CALL", "PO, 0x", OperandKind.Address, jumpType: JumpType.Call),
            0xE5 => Do0("PUSH", "HL", OperandKind.Register),
            0xE6 => Do1("AND", "0x", OperandKind.Immediate),
            0xE7 => Do0("RST", "0x20", OperandKind.Address, hasNext: false),
            0xE8 => Do0("RET", "PE"),
            0xE9 => Do0("JP", "(HL)", OperandKind.Indirect, hasNext: false),
            0xEA => Do2("JP", "PE, 0x", OperandKind.Address, jumpType: JumpType.Jump),
            0xEB => Do0("EX", "DE, HL", OperandKind.Register),
            0xEC => Do2("CALL", "PE, 0x", OperandKind.Address, jumpType: JumpType.Call),
            0xED => DecodeEd(),
            0xEE => Do1("XOR", "0x", OperandKind.Immediate),
            0xEF => Do0("RST", "0x28", OperandKind.Address, hasNext: false),

            0xF0 => Do0("RET", "P"),
            0xF1 => Do0("POP", "AF", OperandKind.Register),
            0xF2 => Do2("JP", "P, 0x", OperandKind.Address, jumpType: JumpType.Jump),
            0xF3 => Do0("DI"),
            0xF4 => Do2("CALL", "P, 0x", OperandKind.Address, jumpType: JumpType.Call),
            0xF5 => Do0("PUSH", "AF", OperandKind.Register),
            0xF6 => Do1("OR", "0x", OperandKind.Immediate),
            0xF7 => Do0("RST", "0x30", OperandKind.Address, hasNext: false),
            0xF8 => Do0("RET", "M"),
            0xF9 => Do0("LD", "SP, HL", OperandKind.Register),
            0xFA => Do2("JP", "M, 0x", OperandKind.Address, jumpType: JumpType.Jump),
            0xFB => Do0("EI"),
            0xFC => Do2("CALL", "M, 0x", OperandKind.Address, jumpType: JumpType.Call),
            0xFD => DecodeDdFd(iy: true),
            0xFE => Do1("CP", "0x", OperandKind.Immediate),
            0xFF => Do0("RST", "0x38", OperandKind.Address, hasNext: false),
        };

        static string Reg(int index) => index switch
        {
            0 => "B",
            1 => "C",
            2 => "D",
            3 => "E",
            4 => "H",
            5 => "L",
            6 => "(HL)",
            _ => "A",
        };

        // 0x40-0x7F "LD r, r'" range: a single switch arm covers both pure
        // register-to-register moves and ones touching (HL), since Reg(6) is
        // "(HL)" - the operand kind is computed from the same index bits
        // rather than sniffed from the resulting text.
        DisassembledInstruction LdRR(int destIndex, int srcIndex)
        {
            var kind = destIndex == 6 || srcIndex == 6 ? OperandKind.Indirect : OperandKind.Register;
            return Do0("LD", $"{Reg(destIndex)}, {Reg(srcIndex)}", kind);
        }

        // 0x80-0xBF ALU range: same register-or-(HL) shape as LdRR above,
        // just with a single source operand and (for ADD/ADC/SBC) an
        // explicit "A," destination that CP/SUB/AND/XOR/OR don't show.
        DisassembledInstruction Alu(string mnemonic, int regIndex, bool hasDestA = false)
        {
            var kind = regIndex == 6 ? OperandKind.Indirect : OperandKind.Register;
            var operand = hasDestA ? $"A, {Reg(regIndex)}" : Reg(regIndex);
            return Do0(mnemonic, operand, kind);
        }

        // CB page: [x x][y y y][z z z]. x picks the family, z the r[] operand.
        DisassembledInstruction DecodeCb()
        {
            var sub = MemoryCallbacks.Read((ushort)(address + 1));
            var x = sub >> 6;
            var y = (sub >> 3) & 0x7;
            var z = sub & 0x7;

            var rot = new[] { "RLC", "RRC", "RL", "RR", "SLA", "SRA", "SLL", "SRL" };
            var kind = z == 6 ? OperandKind.Indirect : OperandKind.Register;

            var (mnemonic, operand) = x switch
            {
                0 => (rot[y], Reg(z)),
                1 => ("BIT", $"{y}, {Reg(z)}"),
                2 => ("RES", $"{y}, {Reg(z)}"),
                _ => ("SET", $"{y}, {Reg(z)}"),
            };

            return DoHelper(mnemonic, operand, kind, 2, $"{opcode:X2} {sub:X2}", true, null);
        }

        // ED page. rp[] here is BC DE HL SP; a two-byte address operand follows
        // the LD (nn),dd / LD dd,(nn) forms.
        DisassembledInstruction DecodeEd()
        {
            var sub = MemoryCallbacks.Read((ushort)(address + 1));
            var x = sub >> 6;
            var y = (sub >> 3) & 0x7;
            var z = sub & 0x7;
            var p = y >> 1;
            var q = y & 1;

            var rp = new[] { "BC", "DE", "HL", "SP" };
            var im = new[] { "0", "0", "1", "2", "0", "0", "1", "2" };

            if (x == 1)
            {
                switch (z)
                {
                    case 0:
                        return y == 6
                            ? EdShort("IN", "(C)", OperandKind.Indirect)
                            : EdShort("IN", $"{Reg(y)}, (C)", OperandKind.Indirect);
                    case 1:
                        return y == 6
                            ? EdShort("OUT", "(C), 0", OperandKind.Indirect)
                            : EdShort("OUT", $"(C), {Reg(y)}", OperandKind.Indirect);
                    case 2:
                        return EdShort(q == 0 ? "SBC" : "ADC", $"HL, {rp[p]}", OperandKind.Register);
                    case 3:
                        return q == 0
                            ? EdAbsolute("LD", "(0x", $"), {rp[p]}")
                            : EdAbsolute("LD", $"{rp[p]}, (0x", ")");
                    case 4:
                        return EdShort("NEG");
                    case 5:
                        return EdShort(y == 1 ? "RETI" : "RETN", hasNext: false);
                    case 6:
                        return EdShort("IM", im[y], OperandKind.Immediate);
                    default: // z == 7
                        return y switch
                        {
                            0 => EdShort("LD", "I, A", OperandKind.Register),
                            1 => EdShort("LD", "R, A", OperandKind.Register),
                            2 => EdShort("LD", "A, I", OperandKind.Register),
                            3 => EdShort("LD", "A, R", OperandKind.Register),
                            4 => EdShort("RRD"),
                            5 => EdShort("RLD"),
                            _ => EdShort("NOP*"),
                        };
                }
            }

            if (x == 2 && y >= 4 && z <= 3)
            {
                var stem = z switch { 0 => "LD", 1 => "CP", 2 => "IN", _ => "OT" };
                var dir = (y & 1) == 0 ? "I" : "D";
                var rep = y >= 6 ? "R" : "";
                // OUTI/OUTD break the "OT" stem pattern the repeats use.
                var name = z == 3 && y < 6 ? $"OUT{dir}" : $"{stem}{dir}{rep}";
                return EdShort(name);
            }

            return EdShort("DB", $"0xED, 0x{sub:X2}", OperandKind.Immediate);

            DisassembledInstruction EdShort(string mnemonic, string operand = "", OperandKind operandKind = OperandKind.None, bool hasNext = true) =>
                DoHelper(mnemonic, operand, operandKind, 2, $"{opcode:X2} {sub:X2}", hasNext, null);

            DisassembledInstruction EdAbsolute(string mnemonic, string operandPrefix, string operandSuffix)
            {
                var lo = MemoryCallbacks.Read((ushort)(address + 2));
                var hi = MemoryCallbacks.Read((ushort)(address + 3));
                return DoHelper(
                    mnemonic,
                    $"{operandPrefix}{hi:X2}{lo:X2}{operandSuffix}",
                    OperandKind.Indirect,
                    4,
                    $"{opcode:X2} {sub:X2} {lo:X2} {hi:X2}",
                    true,
                    null);
            }
        }

        // DD / FD page: as the unprefixed table but with HL -> IX/IY,
        // H/L -> IXH/IXL (IYH/IYL) and (HL) -> (IX+d). A further DD/FD/ED or an
        // opcode naming none of those leaves the escape inert.
        DisassembledInstruction DecodeDdFd(bool iy)
        {
            var ix = iy ? "IY" : "IX";
            var ixh = iy ? "IYH" : "IXH";
            var ixl = iy ? "IYL" : "IXL";
            var sub = MemoryCallbacks.Read((ushort)(address + 1));

            if (sub == 0xCB)
            {
                return DecodeDdFdCb(ix);
            }

            if (sub is 0xDD or 0xFD or 0xED || !IndexAimsAt(sub))
            {
                return DoHelper("DB", $"0x{opcode:X2} ({ix} prefix)", OperandKind.Immediate, 1, $"{opcode:X2}", true, null);
            }

            var x = sub >> 6;
            var y = (sub >> 3) & 0x7;
            var z = sub & 0x7;
            var d = MemoryCallbacks.Read((ushort)(address + 2));
            var mem = $"({ix}{Displacement(d)})";

            string Sub(int idx) => idx switch { 4 => ixh, 5 => ixl, _ => Reg(idx) };
            OperandKind SubKind(int idx) => idx == 6 ? OperandKind.Indirect : OperandKind.Register;

            DisassembledInstruction Short(string mnemonic, string operand = "", OperandKind operandKind = OperandKind.None, bool hasNext = true) =>
                DoHelper(mnemonic, operand, operandKind, 2, $"{opcode:X2} {sub:X2}", hasNext, null);

            DisassembledInstruction Disp(string mnemonic, string operand, OperandKind operandKind) =>
                DoHelper(mnemonic, operand, operandKind, 3, $"{opcode:X2} {sub:X2} {d:X2}", true, null);

            DisassembledInstruction Word(string mnemonic, string operandPrefix, string operandSuffix, OperandKind operandKind)
            {
                var lo = MemoryCallbacks.Read((ushort)(address + 2));
                var hi = MemoryCallbacks.Read((ushort)(address + 3));
                return DoHelper(
                    mnemonic,
                    $"{operandPrefix}{hi:X2}{lo:X2}{operandSuffix}",
                    operandKind,
                    4,
                    $"{opcode:X2} {sub:X2} {lo:X2} {hi:X2}",
                    true,
                    null);
            }

            switch (sub)
            {
                case 0x09: return Short("ADD", $"{ix}, BC", OperandKind.Register);
                case 0x19: return Short("ADD", $"{ix}, DE", OperandKind.Register);
                case 0x29: return Short("ADD", $"{ix}, {ix}", OperandKind.Register);
                case 0x39: return Short("ADD", $"{ix}, SP", OperandKind.Register);
                case 0x21: return Word("LD", $"{ix}, 0x", "", OperandKind.Immediate);
                case 0x22: return Word("LD", "(0x", $"), {ix}", OperandKind.Indirect);
                case 0x2A: return Word("LD", $"{ix}, (0x", ")", OperandKind.Indirect);
                case 0x23: return Short("INC", ix, OperandKind.Register);
                case 0x2B: return Short("DEC", ix, OperandKind.Register);
                case 0xE1: return Short("POP", ix, OperandKind.Register);
                case 0xE3: return Short("EX", $"(SP), {ix}", OperandKind.Indirect);
                case 0xE5: return Short("PUSH", ix, OperandKind.Register);
                case 0xE9: return Short("JP", $"({ix})", OperandKind.Indirect, hasNext: false);
                case 0xF9: return Short("LD", $"SP, {ix}", OperandKind.Register);
            }

            if (x == 0 && z == 4)
            {
                return y == 6 ? Disp("INC", mem, OperandKind.Indirect) : Short("INC", Sub(y), SubKind(y));
            }

            if (x == 0 && z == 5)
            {
                return y == 6 ? Disp("DEC", mem, OperandKind.Indirect) : Short("DEC", Sub(y), SubKind(y));
            }

            if (x == 0 && z == 6)
            {
                if (y == 6)
                {
                    var n = MemoryCallbacks.Read((ushort)(address + 3));
                    return DoHelper(
                        "LD",
                        $"{mem}, 0x{n:X2}",
                        OperandKind.Indirect,
                        4,
                        $"{opcode:X2} {sub:X2} {d:X2} {n:X2}",
                        true,
                        null);
                }

                var imm = MemoryCallbacks.Read((ushort)(address + 2));
                return DoHelper(
                    "LD",
                    $"{Sub(y)}, 0x{imm:X2}",
                    OperandKind.Immediate,
                    3,
                    $"{opcode:X2} {sub:X2} {imm:X2}",
                    true,
                    null);
            }

            if (x == 1)
            {
                if (z == 6)
                {
                    return Disp("LD", $"{Reg(y)}, {mem}", OperandKind.Indirect);
                }

                if (y == 6)
                {
                    return Disp("LD", $"{mem}, {Reg(z)}", OperandKind.Indirect);
                }

                var kind = SubKind(y) == OperandKind.Indirect || SubKind(z) == OperandKind.Indirect
                    ? OperandKind.Indirect
                    : OperandKind.Register;
                return Short("LD", $"{Sub(y)}, {Sub(z)}", kind);
            }

            if (x == 2)
            {
                var aluMnemonics = new[] { "ADD", "ADC", "SUB", "SBC", "AND", "XOR", "OR", "CP" };
                var aluHasDestA = new[] { true, true, false, true, false, false, false, false };
                var mnemonic = aluMnemonics[y];

                if (z == 6)
                {
                    return Disp(mnemonic, aluHasDestA[y] ? $"A, {mem}" : mem, OperandKind.Indirect);
                }

                return Short(mnemonic, aluHasDestA[y] ? $"A, {Sub(z)}" : Sub(z), SubKind(z));
            }

            return DoHelper("DB", $"0x{opcode:X2} ({ix} prefix)", OperandKind.Immediate, 1, $"{opcode:X2}", true, null);
        }

        // DD CB d op / FD CB d op: the CB operation on (IX+d), plus - when the
        // op byte's z field is a register - the undocumented copy of the result
        // into that register.
        DisassembledInstruction DecodeDdFdCb(string ix)
        {
            var sub = MemoryCallbacks.Read((ushort)(address + 1));
            var d = MemoryCallbacks.Read((ushort)(address + 2));
            var op = MemoryCallbacks.Read((ushort)(address + 3));
            var x = op >> 6;
            var y = (op >> 3) & 0x7;
            var z = op & 0x7;
            var mem = $"({ix}{Displacement(d)})";
            var rot = new[] { "RLC", "RRC", "RL", "RR", "SLA", "SRA", "SLL", "SRL" };
            var copy = z == 6 ? "" : $", {Reg(z)}";

            var (mnemonic, operand) = x switch
            {
                0 => (rot[y], $"{mem}{copy}"),
                1 => ("BIT", $"{y}, {mem}"),
                2 => ("RES", $"{y}, {mem}{copy}"),
                _ => ("SET", $"{y}, {mem}{copy}"),
            };

            return DoHelper(mnemonic, operand, OperandKind.Indirect, 4, $"{opcode:X2} {sub:X2} {d:X2} {op:X2}", true, null);
        }

        // Signed (IX+d) displacement, e.g. "+0x05" / "-0x03".
        static string Displacement(byte d)
        {
            var signed = (sbyte)d;
            return signed < 0 ? $"-0x{-signed:X2}" : $"+0x{signed:X2}";
        }

        // Whether a DD/FD escape re-aims this opcode (names HL, H, L or (HL)).
        static bool IndexAimsAt(int sub)
        {
            var x = sub >> 6;
            var y = (sub >> 3) & 0x7;
            var z = sub & 0x7;

            return x switch
            {
                0 => sub is 0x09 or 0x19 or 0x29 or 0x39
                        or 0x21 or 0x22 or 0x2A or 0x23 or 0x2B
                    || (z is 4 or 5 or 6 && y is 4 or 5 or 6),
                1 => sub != 0x76 && (y is 4 or 5 or 6 || z is 4 or 5 or 6),
                2 => z is 4 or 5 or 6,
                _ => sub is 0xE1 or 0xE3 or 0xE5 or 0xE9 or 0xF9,
            };
        }

        DisassembledInstruction Do0(string mnemonic, string operand = "", OperandKind operandKind = OperandKind.None, bool hasNext = true)
        {
            return DoHelper(mnemonic, operand, operandKind, 1, $"{opcode:X2}", hasNext, null);
        }

        DisassembledInstruction Do1(string mnemonic, string operandPrefix, OperandKind operandKind = OperandKind.None, string operandSuffix = "")
        {
            var operand = MemoryCallbacks.Read((ushort)(address + 1));

            return DoHelper(
                mnemonic,
                $"{operandPrefix}{operand:X2}{operandSuffix}",
                operandKind,
                2,
                $"{opcode:X2} {operand:X2}",
                true,
                null);
        }

        DisassembledInstruction Do2(
            string mnemonic,
            string operandPrefix,
            OperandKind operandKind = OperandKind.None,
            string operandSuffix = "",
            bool hasNext = true,
            JumpType? jumpType = null)
        {
            var operandLo = MemoryCallbacks.Read((ushort)(address + 1));
            var operandHi = MemoryCallbacks.Read((ushort)(address + 2));
            var operand = (ushort)((operandHi << 8) | operandLo);

            JumpTarget? jumpTarget = jumpType != null
                ? new JumpTarget(jumpType.Value, operand)
                : null;

            return DoHelper(
                mnemonic,
                $"{operandPrefix}{operandHi:X2}{operandLo:X2}{operandSuffix}",
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
