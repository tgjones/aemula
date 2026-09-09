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
/// The base (unprefixed) table, the CB table (rotate/shift and BIT/RES/SET) and
/// the ED table (block ops, 16-bit loads, ADC/SBC HL, NEG, IM, LD A,I/R,
/// RRD/RLD, RETN/RETI, IN/OUT) are filled in. The 0xDD / 0xFD bytes are still
/// shown as one-byte "prefix" placeholders so the linear sweep does not throw;
/// their IX/IY re-aiming table and the DD CB / FD CB double prefix are added
/// alongside the matching microcode.
/// </summary>
public class Z80Disassembler : Disassembler
{
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
            0x01 => Do2("LD BC, 0x"),
            0x02 => Do0("LD (BC), A"),
            0x03 => Do0("INC BC"),
            0x04 => Do0("INC B"),
            0x05 => Do0("DEC B"),
            0x06 => Do1("LD B, 0x"),
            0x07 => Do0("RLCA"),
            0x08 => Do0("EX AF, AF'"),
            0x09 => Do0("ADD HL, BC"),
            0x0A => Do0("LD A, (BC)"),
            0x0B => Do0("DEC BC"),
            0x0C => Do0("INC C"),
            0x0D => Do0("DEC C"),
            0x0E => Do1("LD C, 0x"),
            0x0F => Do0("RRCA"),

            0x10 => Do1("DJNZ 0x"),
            0x11 => Do2("LD DE, 0x"),
            0x12 => Do0("LD (DE), A"),
            0x13 => Do0("INC DE"),
            0x14 => Do0("INC D"),
            0x15 => Do0("DEC D"),
            0x16 => Do1("LD D, 0x"),
            0x17 => Do0("RLA"),
            0x18 => Do1("JR 0x"),
            0x19 => Do0("ADD HL, DE"),
            0x1A => Do0("LD A, (DE)"),
            0x1B => Do0("DEC DE"),
            0x1C => Do0("INC E"),
            0x1D => Do0("DEC E"),
            0x1E => Do1("LD E, 0x"),
            0x1F => Do0("RRA"),

            0x20 => Do1("JR NZ, 0x"),
            0x21 => Do2("LD HL, 0x"),
            0x22 => Do2("LD (0x", suffix: "), HL"),
            0x23 => Do0("INC HL"),
            0x24 => Do0("INC H"),
            0x25 => Do0("DEC H"),
            0x26 => Do1("LD H, 0x"),
            0x27 => Do0("DAA"),
            0x28 => Do1("JR Z, 0x"),
            0x29 => Do0("ADD HL, HL"),
            0x2A => Do2("LD HL, (0x", suffix: ")"),
            0x2B => Do0("DEC HL"),
            0x2C => Do0("INC L"),
            0x2D => Do0("DEC L"),
            0x2E => Do1("LD L, 0x"),
            0x2F => Do0("CPL"),

            0x30 => Do1("JR NC, 0x"),
            0x31 => Do2("LD SP, 0x"),
            0x32 => Do2("LD (0x", suffix: "), A"),
            0x33 => Do0("INC SP"),
            0x34 => Do0("INC (HL)"),
            0x35 => Do0("DEC (HL)"),
            0x36 => Do1("LD (HL), 0x"),
            0x37 => Do0("SCF"),
            0x38 => Do1("JR C, 0x"),
            0x39 => Do0("ADD HL, SP"),
            0x3A => Do2("LD A, (0x", suffix: ")"),
            0x3B => Do0("DEC SP"),
            0x3C => Do0("INC A"),
            0x3D => Do0("DEC A"),
            0x3E => Do1("LD A, 0x"),
            0x3F => Do0("CCF"),

            0x76 => Do0("HALT", hasNext: false),
            >= 0x40 and <= 0x7F => Do0($"LD {Reg((opcode >> 3) & 0x7)}, {Reg(opcode & 0x7)}"),

            >= 0x80 and <= 0x87 => Do0($"ADD A, {Reg(opcode & 0x7)}"),
            >= 0x88 and <= 0x8F => Do0($"ADC A, {Reg(opcode & 0x7)}"),
            >= 0x90 and <= 0x97 => Do0($"SUB {Reg(opcode & 0x7)}"),
            >= 0x98 and <= 0x9F => Do0($"SBC A, {Reg(opcode & 0x7)}"),
            >= 0xA0 and <= 0xA7 => Do0($"AND {Reg(opcode & 0x7)}"),
            >= 0xA8 and <= 0xAF => Do0($"XOR {Reg(opcode & 0x7)}"),
            >= 0xB0 and <= 0xB7 => Do0($"OR {Reg(opcode & 0x7)}"),
            >= 0xB8 and <= 0xBF => Do0($"CP {Reg(opcode & 0x7)}"),

            0xC0 => Do0("RET NZ"),
            0xC1 => Do0("POP BC"),
            0xC2 => Do2("JP NZ, 0x", jumpType: JumpType.Jump),
            0xC3 => Do2("JP 0x", hasNext: false, jumpType: JumpType.Jump),
            0xC4 => Do2("CALL NZ, 0x", jumpType: JumpType.Call),
            0xC5 => Do0("PUSH BC"),
            0xC6 => Do1("ADD A, 0x"),
            0xC7 => Do0("RST 0x00", hasNext: false),
            0xC8 => Do0("RET Z"),
            0xC9 => Do0("RET", hasNext: false),
            0xCA => Do2("JP Z, 0x", jumpType: JumpType.Jump),
            0xCB => DecodeCb(),
            0xCC => Do2("CALL Z, 0x", jumpType: JumpType.Call),
            0xCD => Do2("CALL 0x", jumpType: JumpType.Call),
            0xCE => Do1("ADC A, 0x"),
            0xCF => Do0("RST 0x08", hasNext: false),

            0xD0 => Do0("RET NC"),
            0xD1 => Do0("POP DE"),
            0xD2 => Do2("JP NC, 0x", jumpType: JumpType.Jump),
            0xD3 => Do1("OUT (0x", suffix: "), A"),
            0xD4 => Do2("CALL NC, 0x", jumpType: JumpType.Call),
            0xD5 => Do0("PUSH DE"),
            0xD6 => Do1("SUB 0x"),
            0xD7 => Do0("RST 0x10", hasNext: false),
            0xD8 => Do0("RET C"),
            0xD9 => Do0("EXX"),
            0xDA => Do2("JP C, 0x", jumpType: JumpType.Jump),
            0xDB => Do1("IN A, (0x", suffix: ")"),
            0xDC => Do2("CALL C, 0x", jumpType: JumpType.Call),
            0xDD => Prefix("DD"),
            0xDE => Do1("SBC A, 0x"),
            0xDF => Do0("RST 0x18", hasNext: false),

            0xE0 => Do0("RET PO"),
            0xE1 => Do0("POP HL"),
            0xE2 => Do2("JP PO, 0x", jumpType: JumpType.Jump),
            0xE3 => Do0("EX (SP), HL"),
            0xE4 => Do2("CALL PO, 0x", jumpType: JumpType.Call),
            0xE5 => Do0("PUSH HL"),
            0xE6 => Do1("AND 0x"),
            0xE7 => Do0("RST 0x20", hasNext: false),
            0xE8 => Do0("RET PE"),
            0xE9 => Do0("JP (HL)", hasNext: false),
            0xEA => Do2("JP PE, 0x", jumpType: JumpType.Jump),
            0xEB => Do0("EX DE, HL"),
            0xEC => Do2("CALL PE, 0x", jumpType: JumpType.Call),
            0xED => DecodeEd(),
            0xEE => Do1("XOR 0x"),
            0xEF => Do0("RST 0x28", hasNext: false),

            0xF0 => Do0("RET P"),
            0xF1 => Do0("POP AF"),
            0xF2 => Do2("JP P, 0x", jumpType: JumpType.Jump),
            0xF3 => Do0("DI"),
            0xF4 => Do2("CALL P, 0x", jumpType: JumpType.Call),
            0xF5 => Do0("PUSH AF"),
            0xF6 => Do1("OR 0x"),
            0xF7 => Do0("RST 0x30", hasNext: false),
            0xF8 => Do0("RET M"),
            0xF9 => Do0("LD SP, HL"),
            0xFA => Do2("JP M, 0x", jumpType: JumpType.Jump),
            0xFB => Do0("EI"),
            0xFC => Do2("CALL M, 0x", jumpType: JumpType.Call),
            0xFD => Prefix("FD"),
            0xFE => Do1("CP 0x"),
            0xFF => Do0("RST 0x38", hasNext: false),
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

        // CB page: [x x][y y y][z z z]. x picks the family, z the r[] operand.
        DisassembledInstruction DecodeCb()
        {
            var sub = MemoryCallbacks.Read((ushort)(address + 1));
            var x = sub >> 6;
            var y = (sub >> 3) & 0x7;
            var z = sub & 0x7;

            var rot = new[] { "RLC", "RRC", "RL", "RR", "SLA", "SRA", "SLL", "SRL" };
            var text = x switch
            {
                0 => $"{rot[y]} {Reg(z)}",
                1 => $"BIT {y}, {Reg(z)}",
                2 => $"RES {y}, {Reg(z)}",
                _ => $"SET {y}, {Reg(z)}",
            };

            return DoHelper(text, 2, $"{opcode:X2} {sub:X2}", true, null);
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
                        return EdShort(y == 6 ? "IN (C)" : $"IN {Reg(y)}, (C)");
                    case 1:
                        return EdShort(y == 6 ? "OUT (C), 0" : $"OUT (C), {Reg(y)}");
                    case 2:
                        return EdShort($"{(q == 0 ? "SBC" : "ADC")} HL, {rp[p]}");
                    case 3:
                        return q == 0
                            ? EdAbsolute($"LD (0x", $"), {rp[p]}")
                            : EdAbsolute($"LD {rp[p]}, (0x", ")");
                    case 4:
                        return EdShort("NEG");
                    case 5:
                        return EdShort(y == 1 ? "RETI" : "RETN", hasNext: false);
                    case 6:
                        return EdShort($"IM {im[y]}");
                    default: // z == 7
                        return EdShort(y switch
                        {
                            0 => "LD I, A",
                            1 => "LD R, A",
                            2 => "LD A, I",
                            3 => "LD A, R",
                            4 => "RRD",
                            5 => "RLD",
                            _ => "NOP*",
                        });
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

            return EdShort($"DB 0xED, 0x{sub:X2}");

            DisassembledInstruction EdShort(string text, bool hasNext = true) =>
                DoHelper(text, 2, $"{opcode:X2} {sub:X2}", hasNext, null);

            DisassembledInstruction EdAbsolute(string prefix, string suffix)
            {
                var lo = MemoryCallbacks.Read((ushort)(address + 2));
                var hi = MemoryCallbacks.Read((ushort)(address + 3));
                return DoHelper(
                    $"{prefix}{hi:X2}{lo:X2}{suffix}",
                    4,
                    $"{opcode:X2} {sub:X2} {lo:X2} {hi:X2}",
                    true,
                    null);
            }
        }

        DisassembledInstruction Do0(string text, bool hasNext = true)
        {
            return DoHelper(text, 1, $"{opcode:X2}", hasNext, null);
        }

        DisassembledInstruction Do1(string prefix, string suffix = "")
        {
            var operand = MemoryCallbacks.Read((ushort)(address + 1));

            return DoHelper(
                $"{prefix}{operand:X2}{suffix}",
                2,
                $"{opcode:X2} {operand:X2}",
                true,
                null);
        }

        DisassembledInstruction Do2(
            string prefix,
            string suffix = "",
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
                $"{prefix}{operandHi:X2}{operandLo:X2}{suffix}",
                3,
                $"{opcode:X2} {operandLo:X2} {operandHi:X2}",
                hasNext,
                jumpTarget);
        }

        // A lone prefix byte. The real decode consumes the following byte(s); as
        // a placeholder it is shown on its own so the sweep keeps going.
        DisassembledInstruction Prefix(string name)
        {
            return DoHelper($"DB 0x{opcode:X2} ({name} prefix)", 1, $"{opcode:X2}", true, null);
        }

        DisassembledInstruction DoHelper(
            string disassembly,
            byte instructionSizeInBytes,
            string rawBytes,
            bool hasNext,
            JumpTarget? jumpTarget)
        {
            return new DisassembledInstruction(
                opcode,
                address,
                $"{address:X4}",
                instructionSizeInBytes,
                rawBytes,
                disassembly,
                hasNext ? (ushort)(address + instructionSizeInBytes) : null,
                jumpTarget);
        }
    }
}
