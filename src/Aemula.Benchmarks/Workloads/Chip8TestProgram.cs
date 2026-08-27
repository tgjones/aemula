namespace Aemula.Benchmarks;

// A small hand-written CHIP-8 program used as the fixed workload for
// Chip8Benchmark. CHIP-8 opcodes are two big-endian bytes with no relative
// offsets, so this is written directly rather than assembled.
//
// The existing test ROMs (test_opcode.ch8 etc.) run once and then sit in a
// tight "display result" loop, which would make a throughput benchmark measure
// almost nothing. This program instead loops forever over a body that keeps
// every interesting opcode path warm: sprite draw + collision (Dxyn), the
// arithmetic/logic group (8xyN), random (Cxkk), BCD (Fx33), register block
// load/store (Fx55/Fx65), the timers (Fx15/Fx07), I arithmetic (Fx1E),
// skip-if-(not-)equal branching (3xkk/4xkk) and CALL/RET (2nnn/00EE).
internal static class Chip8TestProgram
{
    // Loaded at $200 (Chip8System.ProgramStart).
    public static byte[] Bytes { get; } =
    [
        // $200 CLS
        0x00, 0xE0,
        // $202 LD V0, $02          ; sprite x
        0x60, 0x02,
        // $204 LD V1, $03          ; sprite y
        0x61, 0x03,
        // $206 LD VA, $00          ; frame counter
        0x6A, 0x00,

        // ---- $208 main loop ----
        // $208 CALL $220           ; draw + arithmetic subroutine
        0x22, 0x20,
        // $20A ADD VA, $01
        0x7A, 0x01,
        // $20C LD DT, VA           ; Fx15  (arm delay timer)
        0xFA, 0x15,
        // $20E LD V5, DT           ; Fx07  (read it back)
        0xF5, 0x07,
        // $210 LD I, $300          ; scratch area, clear of the sprite font
        0xA3, 0x00,
        // $212 LD B, V0            ; Fx33  BCD of V0 -> [I..I+2]
        0xF0, 0x33,
        // $214 LD V2, [I]          ; Fx65  block read  (Array.Copy path)
        0xF2, 0x65,
        // $216 LD [I], V2          ; Fx55  block write (Array.Copy path)
        0xF2, 0x55,
        // $218 JP $208
        0x12, 0x08,
        // $21A..$21F padding
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00,

        // ---- $220 subroutine ----
        // $220 LD V3, $05          ; digit value
        0x63, 0x05,
        // $222 LD F, V3            ; Fx29  I = font sprite address for V3
        0xF3, 0x29,
        // $224 DRW V0, V1, 5       ; Dxyn  XOR sprite, set VF on collision
        0xD0, 0x15,
        // $226 ADD V0, $07
        0x70, 0x07,
        // $228 ADD V1, $05
        0x71, 0x05,
        // $22A RND V4, $1F         ; Cxkk
        0xC4, 0x1F,
        // $22C ADD V3, V4          ; 8xy4  (add with carry into VF)
        0x83, 0x44,
        // $22E AND V4, V5          ; 8xy2
        0x84, 0x52,
        // $230 SE V0, $20          ; 3xkk  skip next if V0 == $20
        0x30, 0x20,
        // $232 ADD I, V3           ; Fx1E
        0xF3, 0x1E,
        // $234 SNE V1, $00         ; 4xkk  skip next if V1 != $00 (usually taken)
        0x41, 0x00,
        // $236 LD V6, $01          ; only reached when V1 == 0
        0x66, 0x01,
        // $238 RET
        0x00, 0xEE,
    ];
}
