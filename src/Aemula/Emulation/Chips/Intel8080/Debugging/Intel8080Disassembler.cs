using System;
using System.Collections.Generic;
using Aemula.Debugging;

namespace Aemula.Emulation.Chips.Intel8080.Debugging;

public class Intel8080Disassembler : Disassembler
{
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
            0x01 => Do2("LXI B, 0x"),
            0x02 => Do0("STAX B"),
            0x03 => Do0("INX B"),
            0x04 => Do0("INR B"),
            0x05 => Do0("DCR B"),
            0x06 => Do1("MVI B, 0x"),
            0x07 => Do0("RLC"),

            0x09 => Do0("DAD B"),
            0x0A => Do0("LDAX B"),
            0x0B => Do0("DCX B"),
            0x0C => Do0("INR C"),
            0x0D => Do0("DCR C"),
            0x0E => Do1("MVI C, 0x"),
            0x0F => Do0("RRC"),

            0x11 => Do2("LXI D, 0x"),
            0x12 => Do0("STAX D"),
            0x13 => Do0("INX D"),
            0x14 => Do0("INR D"),
            0x15 => Do0("DCR D"),
            0x16 => Do1("MVI D, 0x"),
            0x17 => Do0("RAL"),

            0x19 => Do0("DAD D"),
            0x1A => Do0("LDAX D"),
            0x1B => Do0("DCX D"),
            0x1C => Do0("INR E"),
            0x1D => Do0("DCR E"),
            0x1E => Do1("MVI E, 0x"),
            0x1F => Do0("RAR"),

            0x21 => Do2("LXI H, 0x"),
            0x22 => Do2("SHLD 0x"),
            0x23 => Do0("INX H"),
            0x24 => Do0("INR H"),
            0x25 => Do0("DCR H"),
            0x26 => Do1("MVI H, 0x"),
            0x27 => Do0("DAA"),

            0x29 => Do0("DAD H"),
            0x2A => Do2("LHLD 0x"),
            0x2B => Do0("DCX H"),
            0x2C => Do0("INR L"),
            0x2D => Do0("DCR L"),
            0x2E => Do1("MVI L, 0x"),
            0x2F => Do0("CMA"),

            0x31 => Do2("LXI SP, 0x"),
            0x32 => Do2("STA 0x"),
            0x33 => Do0("INX SP"),
            0x34 => Do0("INR M"),
            0x35 => Do0("DCR M"),
            0x36 => Do1("MVI M, 0x"),
            0x37 => Do0("STC"),

            0x39 => Do0("DAD SP"),
            0x3A => Do2("LDA 0x"),
            0x3B => Do0("DCX SP"),
            0x3C => Do0("INR A"),
            0x3D => Do0("DCR A"),
            0x3E => Do1("MVI A, 0x"),
            0x3F => Do0("CMC"),

            0x40 => Do0("MOV B, B"),
            0x41 => Do0("MOV B, C"),
            0x42 => Do0("MOV B, D"),
            0x43 => Do0("MOV B, E"),
            0x44 => Do0("MOV B, H"),
            0x45 => Do0("MOV B, L"),
            0x46 => Do0("MOV B, M"),
            0x47 => Do0("MOV B, A"),

            0x48 => Do0("MOV C, B"),
            0x49 => Do0("MOV C, C"),
            0x4A => Do0("MOV C, D"),
            0x4B => Do0("MOV C, E"),
            0x4C => Do0("MOV C, H"),
            0x4D => Do0("MOV C, L"),
            0x4E => Do0("MOV C, M"),
            0x4F => Do0("MOV C, A"),

            0x50 => Do0("MOV D, B"),
            0x51 => Do0("MOV D, C"),
            0x52 => Do0("MOV D, D"),
            0x53 => Do0("MOV D, E"),
            0x54 => Do0("MOV D, H"),
            0x55 => Do0("MOV D, L"),
            0x56 => Do0("MOV D, M"),
            0x57 => Do0("MOV D, A"),

            0x58 => Do0("MOV E, B"),
            0x59 => Do0("MOV E, C"),
            0x5A => Do0("MOV E, D"),
            0x5B => Do0("MOV E, E"),
            0x5C => Do0("MOV E, H"),
            0x5D => Do0("MOV E, L"),
            0x5E => Do0("MOV E, M"),
            0x5F => Do0("MOV E, A"),

            0x60 => Do0("MOV H, B"),
            0x61 => Do0("MOV H, C"),
            0x62 => Do0("MOV H, D"),
            0x63 => Do0("MOV H, E"),
            0x64 => Do0("MOV H, H"),
            0x65 => Do0("MOV H, L"),
            0x66 => Do0("MOV H, M"),
            0x67 => Do0("MOV H, A"),

            0x68 => Do0("MOV L, B"),
            0x69 => Do0("MOV L, C"),
            0x6A => Do0("MOV L, D"),
            0x6B => Do0("MOV L, E"),
            0x6C => Do0("MOV L, H"),
            0x6D => Do0("MOV L, L"),
            0x6E => Do0("MOV L, M"),
            0x6F => Do0("MOV L, A"),

            0x70 => Do0("MOV M, B"),
            0x71 => Do0("MOV M, C"),
            0x72 => Do0("MOV M, D"),
            0x73 => Do0("MOV M, E"),
            0x74 => Do0("MOV M, H"),
            0x75 => Do0("MOV M, L"),
            0x76 => Do0("HLT", hasNext: false),
            0x77 => Do0("MOV M, A"),

            0x78 => Do0("MOV A, B"),
            0x79 => Do0("MOV A, C"),
            0x7A => Do0("MOV A, D"),
            0x7B => Do0("MOV A, E"),
            0x7C => Do0("MOV A, H"),
            0x7D => Do0("MOV A, L"),
            0x7E => Do0("MOV A, M"),
            0x7F => Do0("MOV A, A"),

            0x80 => Do0("ADD B"),
            0x81 => Do0("ADD C"),
            0x82 => Do0("ADD D"),
            0x83 => Do0("ADD E"),
            0x84 => Do0("ADD H"),
            0x85 => Do0("ADD L"),
            0x86 => Do0("ADD M"),
            0x87 => Do0("ADD A"),

            0x88 => Do0("ADC B"),
            0x89 => Do0("ADC C"),
            0x8A => Do0("ADC D"),
            0x8B => Do0("ADC E"),
            0x8C => Do0("ADC H"),
            0x8D => Do0("ADC L"),
            0x8E => Do0("ADC M"),
            0x8F => Do0("ADC A"),

            0x90 => Do0("SUB B"),
            0x91 => Do0("SUB C"),
            0x92 => Do0("SUB D"),
            0x93 => Do0("SUB E"),
            0x94 => Do0("SUB H"),
            0x95 => Do0("SUB L"),
            0x96 => Do0("SUB M"),
            0x97 => Do0("SUB A"),

            0x98 => Do0("SBB B"),
            0x99 => Do0("SBB C"),
            0x9A => Do0("SBB D"),
            0x9B => Do0("SBB E"),
            0x9C => Do0("SBB H"),
            0x9D => Do0("SBB L"),
            0x9E => Do0("SBB M"),
            0x9F => Do0("SBB A"),

            0xA0 => Do0("ANA B"),
            0xA1 => Do0("ANA C"),
            0xA2 => Do0("ANA D"),
            0xA3 => Do0("ANA E"),
            0xA4 => Do0("ANA H"),
            0xA5 => Do0("ANA L"),
            0xA6 => Do0("ANA M"),
            0xA7 => Do0("ANA A"),

            0xA8 => Do0("XRA B"),
            0xA9 => Do0("XRA C"),
            0xAA => Do0("XRA D"),
            0xAB => Do0("XRA E"),
            0xAC => Do0("XRA H"),
            0xAD => Do0("XRA L"),
            0xAE => Do0("XRA M"),
            0xAF => Do0("XRA A"),

            0xB0 => Do0("ORA B"),
            0xB1 => Do0("ORA C"),
            0xB2 => Do0("ORA D"),
            0xB3 => Do0("ORA E"),
            0xB4 => Do0("ORA H"),
            0xB5 => Do0("ORA L"),
            0xB6 => Do0("ORA M"),
            0xB7 => Do0("ORA A"),

            0xB8 => Do0("CMP B"),
            0xB9 => Do0("CMP C"),
            0xBA => Do0("CMP D"),
            0xBB => Do0("CMP E"),
            0xBC => Do0("CMP H"),
            0xBD => Do0("CMP L"),
            0xBE => Do0("CMP M"),
            0xBF => Do0("CMP A"),

            0xC0 => Do0("RNZ"),
            0xC1 => Do0("POP B"),
            0xC2 => Do2("JNZ 0x", jumpType: JumpType.Jump),
            0xC3 => Do2("JMP 0x", hasNext: false, JumpType.Jump),
            0xC4 => Do2("CNZ 0x", jumpType: JumpType.Call),
            0xC5 => Do0("PUSH B"),
            0xC6 => Do1("ADI 0x"),
            0xC7 => Do0("RST 0", hasNext: false),

            0xC8 => Do0("RZ"),
            0xC9 => Do0("RET", hasNext: false),
            0xCA => Do2("JZ 0x", jumpType: JumpType.Jump),
            0xCC => Do2("CZ 0x", jumpType: JumpType.Call),
            0xCD => Do2("CALL 0x", jumpType: JumpType.Call),
            0xCE => Do1("ACI 0x"),
            0xCF => Do0("RST 1", hasNext: false),

            0xD0 => Do0("RNC"),
            0xD1 => Do0("POP D"),
            0xD2 => Do2("JNC 0x", jumpType: JumpType.Jump),
            0xD3 => Do1("OUT 0x"),
            0xD4 => Do2("CNC 0x", jumpType: JumpType.Call),
            0xD5 => Do0("PUSH D"),
            0xD6 => Do1("SUI 0x"),
            0xD7 => Do0("RST 2", hasNext: false),

            0xD8 => Do0("RC"),
            0xDA => Do2("JC 0x", jumpType: JumpType.Jump),
            0xDB => Do1("IN 0x"),
            0xDC => Do2("CC 0x", jumpType: JumpType.Call),
            0xDE => Do1("SBI 0x"),
            0xDF => Do0("RST 3", hasNext: false),

            0xE0 => Do0("RPO"),
            0xE1 => Do0("POP H"),
            0xE2 => Do2("JPO 0x", jumpType: JumpType.Jump),
            0xE3 => Do0("XTHL"),
            0xE4 => Do2("CPO 0x", jumpType: JumpType.Call),
            0xE5 => Do0("PUSH H"),
            0xE6 => Do1("ANI 0x"),
            0xE7 => Do0("RST 4", hasNext: false),

            0xE8 => Do0("RPE"),
            0xE9 => Do0("PCHL", hasNext: false),
            0xEB => Do0("XCHG"),
            0xEC => Do2("CPE 0x", jumpType: JumpType.Call),
            0xEA => Do2("JPE 0x", jumpType: JumpType.Jump),
            0xEE => Do1("XRI 0x"),
            0xEF => Do0("RST 5", hasNext: false),

            0xF0 => Do0("RP"),
            0xF1 => Do0("POP PSW"),
            0xF2 => Do2("JP 0x", jumpType: JumpType.Jump),

            0xF3 => Do0("DI"),
            0xF4 => Do2("CP 0x", jumpType: JumpType.Call),
            0xF5 => Do0("PUSH PSW"),
            0xF6 => Do1("ORI 0x"),
            0xF7 => Do0("RST 6", hasNext: false),

            0xF8 => Do0("RM"),
            0xF9 => Do0("SPHL"),
            0xFA => Do2("JM 0x", jumpType: JumpType.Jump),
            0xFB => Do0("EI"),
            0xFC => Do2("CM 0x", jumpType: JumpType.Call),
            0xFE => Do1("CPI 0x"),
            0xFF => Do0("RST 7", hasNext: false),

            _ => throw new InvalidOperationException($"Opcode 0x{opcode:X2} not supported"),
        };

        DisassembledInstruction Do0(string prefix, bool hasNext = true)
        {
            return DoHelper(
                prefix,
                1,
                $"{opcode:X2}",
                hasNext,
                null);
        }

        DisassembledInstruction Do1(string prefix)
        {
            var operand = MemoryCallbacks.Read((ushort)(address + 1));

            return DoHelper(
                $"{prefix}{operand:X2}",
                2,
                $"{opcode:X2} {operand:X2}",
                true,
                null);
        }

        DisassembledInstruction Do2(string prefix, bool hasNext = true, JumpType? jumpType = null)
        {
            var operandLo = MemoryCallbacks.Read((ushort)(address + 1));
            var operandHi = MemoryCallbacks.Read((ushort)(address + 2));
            var operand = (ushort)(operandHi << 8 | operandLo);

            JumpTarget? jumpTarget = jumpType != null
                ? new JumpTarget(JumpType.Jump, operand)
                : null;

            return DoHelper(
                $"{prefix}{operandHi:X2}{operandLo:X2}",
                3,
                $"{opcode:X2} {operandLo:X2} {operandHi:X2}",
                hasNext,
                jumpTarget);
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
