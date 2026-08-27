namespace Aemula.Benchmarks;

// A purpose-built 4 KiB Atari 2600 cartridge image used as the fixed workload
// for Atari2600Benchmark. There is no assembler in this repo, so the 6507 code
// below is hand-assembled - the mnemonics in the comments are the source of
// truth and the bytes are laid out at their real $F000-page addresses so branch
// and jump targets can be checked by eye.
//
// The kernel deliberately touches every part of the per-tick path a real game
// would: it runs a full VSYNC / VBLANK / visible / overscan frame structure,
// halts the CPU on WSYNC every scanline (exercising the TIA RDY line), and in
// the visible region rewrites the playfield registers, both colour registers,
// a sprite position (RESP0) and the horizontal-motion path (HMP0 + HMOVE strobe)
// on every line, with a value that changes each line and each frame.
internal static class Atari2600TestKernel
{
    // TIA write registers (zero page).
    private const byte VSYNC = 0x00;
    private const byte WSYNC = 0x02;
    private const byte COLUPF = 0x08;
    private const byte COLUBK = 0x09;
    private const byte PF0 = 0x0D;
    private const byte PF1 = 0x0E;
    private const byte PF2 = 0x0F;
    private const byte RESP0 = 0x10;
    private const byte HMP0 = 0x20;
    private const byte HMOVE = 0x2A;

    // Frame counter lives in RIOT RAM (not cleared by the kernel, it is written
    // before it is read).
    private const byte FrameCounter = 0x81;

    public static byte[] Image { get; } = Build();

    private static byte[] Build()
    {
        var rom = new byte[4096]; // $F000-$FFFF, Cartridge.FromData -> Cartridge4K

        // Offset i in this array is address $F000 + i. The comments give the
        // absolute address of each instruction.
        byte[] code =
        [
            // $F000 RESET:
            0x78,             // SEI
            0xD8,             // CLD
            0xA2, 0xFF,       // LDX #$FF
            0x9A,             // TXS
            0xA9, 0x00,       // LDA #$00
            // $F007 CLEAR:  (zero $01-$FF: TIA shadow + RIOT RAM; $00 is written explicitly below)
            0x95, 0x00,       // STA $00,X
            0xCA,             // DEX
            0xD0, 0xFB,       // BNE CLEAR            ; $F00C - 5 = $F007

            // $F00C MAIN:
            0xA9, 0x02,       // LDA #$02
            0x85, VSYNC,      // STA VSYNC            ; sync on
            0x85, WSYNC,      // STA WSYNC
            0x85, WSYNC,      // STA WSYNC
            0x85, WSYNC,      // STA WSYNC
            0xA9, 0x00,       // LDA #$00
            0x85, VSYNC,      // STA VSYNC            ; sync off
            0xA2, 0x25,       // LDX #$25             ; 37 VBLANK lines
            // $F01C VBLANK:
            0x85, WSYNC,      // STA WSYNC
            0xCA,             // DEX
            0xD0, 0xFB,       // BNE VBLANK           ; $F021 - 5 = $F01C

            0xE6, FrameCounter, // INC $81
            0xA5, FrameCounter, // LDA $81
            0xA2, 0xC0,       // LDX #$C0             ; 192 visible lines
            // $F027 VISIBLE:
            0x85, WSYNC,      // STA WSYNC
            0x85, COLUBK,     // STA COLUBK
            0x85, COLUPF,     // STA COLUPF
            0x85, PF0,        // STA PF0
            0x85, PF1,        // STA PF1
            0x85, PF2,        // STA PF2
            0x86, HMP0,       // STX HMP0
            0x85, RESP0,      // STA RESP0            ; strobe sprite position
            0x85, HMOVE,      // STA HMOVE            ; strobe horizontal motion
            0x18,             // CLC
            0x69, 0x01,       // ADC #$01             ; vary the value per line
            0xCA,             // DEX
            0xD0, 0xE8,       // BNE VISIBLE          ; $F03F - 24 = $F027

            0xA2, 0x1E,       // LDX #$1E             ; 30 overscan lines
            // $F041 OVERSCAN:
            0x85, WSYNC,      // STA WSYNC
            0xCA,             // DEX
            0xD0, 0xFB,       // BNE OVERSCAN         ; $F046 - 5 = $F041

            0x4C, 0x0C, 0xF0, // JMP MAIN             ; $F00C
        ];

        code.CopyTo(rom, 0);

        // 6502/6507 vectors. $FFFC (RESET) is the one the 6507 actually fetches;
        // NMI/IRQ are wired here too so a stray BRK lands somewhere sane.
        // $FFxx maps to rom[0x0Fxx].
        rom[0x0FFA] = 0x00; rom[0x0FFB] = 0xF0; // NMI   -> $F000
        rom[0x0FFC] = 0x00; rom[0x0FFD] = 0xF0; // RESET -> $F000
        rom[0x0FFE] = 0x00; rom[0x0FFF] = 0xF0; // IRQ   -> $F000

        return rom;
    }
}
