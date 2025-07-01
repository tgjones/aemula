using Aemula.Debugging;

namespace Aemula.Emulation.Systems.Chip8;

partial class Chip8System
{
    internal static DisassembledInstruction Disassemble(
        ushort address,
        DebuggerMemoryCallbacks memoryCallbacks)
    {
        var opcodeHi = memoryCallbacks.Read((ushort)(address + 0));
        var opcodeLo = memoryCallbacks.Read((ushort)(address + 1));
        var opcode = (ushort)((opcodeHi << 8) | opcodeLo);

        ushort? next = opcode != 0x00EE && opcode != 0x100
            ? (ushort)(address + 2)
            : null;

        JumpTarget? jumpTarget = (opcode & 0xF000) switch
        {
            0x1000 => new JumpTarget(JumpType.Jump, (ushort)(opcode & 0x0FFF)),
            0x2000 => new JumpTarget(JumpType.Call, (ushort)(opcode & 0x0FFF)),
            // Ignore indirect jumps.
            // 0xB000 => (ushort)((opcode & 0x0FFF) + _v[0]),
            _ => null,
        };

        return new DisassembledInstruction(
            opcode,
            address,
            $"{address:X4}",
            2,
            $"{opcodeHi:X2} {opcodeLo:X2}",
            DisassembleImpl(opcode),
            next,
            jumpTarget);
    }

    private static string DisassembleImpl(ushort opcode)
    {
        var x = (opcode & 0x0F00) >> 8;
        var y = (opcode & 0x00F0) >> 4;

        return (opcode & 0xF000) switch
        {
            0x0000 => opcode switch
            {
                // 00E0 - CLS - Clear the display.
                0x00E0 => "CLS",

                // 00EE - RET - Return from a subroutine.
                0x00EE => "RET",

                _ => "",
            },

            // 1nnn - JP addr - Jump to location nnn.
            0x1000 => $"JP #{opcode & 0x0FFF:X4}",

            // 2nnn - CALL addr - Call subroutine at nnn.
            0x2000 => $"CALL #{opcode & 0x0FFF:X4}",

            // 3xkk - SE Vx, byte - Skip next instruction if Vx = kk.
            0x3000 => $"SE V{x:X}, #{opcode:X2}",

            // 4xkk - SNE Vx, byte - Skip next instruction if Vx != kk.
            0x4000 => $"SNE V{x:X}, #{opcode:X2}",

            // 5xy0 - SE Vx, Vy - Skip next instruction if Vx = Vy.
            0x5000 => $"SE V{x:X}, V{y:X}",

            // 6xkk - LD Vx, byte - Set Vx = kk.
            0x6000 => $"LD V{x:X}, #{opcode:X2}",

            // 7xkk - ADD Vx, byte - Set Vx = Vx + kk.
            0x7000 => $"ADD V{x:X}, #{opcode:X2}",

            0x8000 => (opcode & 0x000F) switch
            {
                // 8xy0 - LD Vx, Vy - Set Vx = Vy.
                0x0 => $"LD V{x:X}, V{y:X}",

                // 8xy1 - OR Vx, Vy - Set Vx = Vx OR Vy.
                0x1 => $"OR V{x:X}, V{y:X}",

                // 8xy2 - AND Vx, Vy - Set Vx = Vx AND Vy.
                0x2 => $"AND V{x:X}, V{y:X}",

                // 8xy3 - XOR Vx, Vy - Set Vx = Vx XOR Vy.
                0x3 => $"XOR V{x:X}, V{y:X}",

                // 8xy4 - ADD Vx, Vy - Set Vx = Vx + Vy, set VF = carry.
                0x4 => $"ADD V{x:X}, V{y:X}",

                // 8xy5 - SUB Vx, Vy - Set Vx = Vx - Vy, set VF = NOT borrow.
                0x5 => $"SUB V{x:X}, V{y:X}",

                // 8xy6 - SHR Vx {, Vy} - Set Vx = Vx SHR 1.
                0x6 => $"SHR V{x:X}, V{y:X}",

                // 8xy7 - SUBN Vx, Vy - Set Vx = Vy - Vx, set VF = NOT borrow.
                0x7 => $"SUBN V{x:X}, V{y:X}",

                // 8xyE - SHL Vx {, Vy} - Set Vx = Vx SHL 1.
                0xE => $"SHL V{x:X}, V{y:X}",

                _ => "",
            },

            // 9xy0 - SNE Vx, Vy - Skip next instruction if Vx != Vy.
            0x9000 => $"SNE V{x:X}, V{y:X}",

            // Annn - LD I, addr - Set I = nnn.
            0xA000 => $"LD I, #{opcode & 0xFFF:X4}",

            // Bnnn - JP V0, addr - Jump to location nnn + V0.
            0xB000 => $"JP V0, #{opcode & 0xFFF:X4}",

            // Cxkk - RND Vx, byte - Set Vx = random byte AND kk.
            0xC000 => $"RND V{x:X}, #{opcode:X2}",

            // Dxyn - DRW Vx, Vy, nibble - Display n-byte sprite starting at memory location I
            // at (Vx, Vy), set VF = collision.
            0xD000 => $"DRW V{x:X}, V{y:X}, {opcode & 0x000F}",

            0xE000 => (opcode & 0x00FF) switch
            {
                // Ex9E - SKP Vx - Skip next instruction if key with the value of Vx is pressed.
                0x009E => $"SKP V{x:X}",

                // ExA1 - SKNP Vx - Skip next instruction if key with the value of Vx is not pressed.
                0x00A1 => $"SKNP V{x:X}",

                _ => "",
            },

            0xF000 => (opcode & 0x00FF) switch
            {
                // Fx07 - LD Vx, DT - Set Vx = delay timer value.
                0x0007 => $"LD V{x:X}, DT",

                // Fx0A - LD Vx, K - Wait for a key press, store the value of the key in Vx.
                0x000A => $"LD V{x:X}, K",

                // Fx15 - LD DT, Vx - Set delay timer = Vx.
                0x0015 => $"LD DT, V{x:X}",

                // Fx18 - LD ST, Vx - Set sound timer = Vx.
                0x0018 => $"LD ST, V{x:X}",

                // Fx1E - ADD I, Vx - Set I = I + Vx.
                0x001E => $"ADD I, V{x:X}",

                // Fx29 - LD F, Vx - Set I = location of sprite for digit Vx.
                0x0029 => $"LD F, V{x:X}",

                // Fx33 - LD B, Vx - Store BCD representation of Vx in memory locations I, I+1, and I+2.
                0x0033 => $"LD B, V{x:X}",

                // Fx55 - LD [I], Vx - Stores registers V0 through Vx in memory starting at location I.
                0x0055 => $"LD [I] V{x:X}",

                // Fx65 - LD Vx, [I] - Read registers V0 through Vx from memory starting at location I.
                0x0065 => $"LD V{x:X}, [I]",

                _ => ""
            },

            _ => ""
        };
    }
}
