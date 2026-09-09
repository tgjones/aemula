namespace Aemula.Emulation.Chips.Z80;

// CB-prefixed opcode microcode: the rotate/shift group and BIT / RES / SET.
//
// The opcode byte splits (z80.info/decoding.htm) as [x x][y y y][z z z]:
//   x == 0  rot[y] r[z]         RLC RRC RL RR SLA SRA SLL SRL
//   x == 1  BIT y,r[z]
//   x == 2  RES y,r[z]
//   x == 3  SET y,r[z]
// r[] is B C D E H L (HL) A, so z == 6 is the (HL) memory operand.
//
// Timing from the Zilog Z80 CPU User Manual: with a register operand the whole
// thing is the escape M1 plus one more 4-T M1 (8 T). With (HL) it is those two
// M1s, a 3-T read of (HL), a 1-T internal cycle, and - for everything except
// BIT - a 3-T write-back (15 T; BIT stops after the internal cycle at 12 T).
//
// Flag behaviour follows "The Undocumented Z80 Documented" (Sean Young) and
// lives in Z80Chip.Alu.cs: the CB rotates set S/Z/P from the result (the
// accumulator rotates do not), and BIT b,(HL) takes its undocumented bits 5/3
// from the high byte of MEMPTR, which it leaves unchanged.

public sealed partial class Z80Chip
{
    private void HandleCbPrefixed(int cycleKey)
    {
        var x = _ir >> 6;
        var y = (_ir >> 3) & 0x7;
        var z = _ir & 0x7;

        if (z != 6)
        {
            // Register operand - a pure second M1.
            if (cycleKey == OpcodeFetchT4)
            {
                ref var r = ref Register8(z);

                switch (x)
                {
                    case 0: r = AluRotateShift(y, r); break;
                    case 1: AluBit(y, r, r); break;
                    case 2: r = (byte)(r & ~(1 << y)); break;
                    case 3: r = (byte)(r | (1 << y)); break;
                }

                FinishPrefixedInstruction();
            }

            return;
        }

        // (HL) operand: read, one internal T-state, then (except BIT) write.
        switch (cycleKey)
        {
            case OpcodeFetchT4:
                SetNextCycle(MachineCycleType.MemoryRead);
                break;

            case MemoryReadT1:
                Address = HL.Value;
                break;

            case MemoryReadT3:
                switch (x)
                {
                    case 0: _tmp = AluRotateShift(y, Data); break;
                    case 1: AluBit(y, Data, WZ.W); break;
                    case 2: _tmp = (byte)(Data & ~(1 << y)); break;
                    case 3: _tmp = (byte)(Data | (1 << y)); break;
                }

                SetNextCycle(MachineCycleType.Internal);
                break;

            case InternalT1:
                if (x == 1) // BIT has no write-back
                {
                    FinishPrefixedInstruction();
                }
                else
                {
                    SetNextCycle(MachineCycleType.MemoryWrite);
                }
                break;

            case MemoryWriteT1:
                Address = HL.Value;
                Data = _tmp;
                break;

            case MemoryWriteT3:
                FinishPrefixedInstruction();
                break;
        }
    }

    // Every prefixed instruction ends the same way: drop back to the unprefixed
    // decode table and start the next opcode fetch. Clearing _prefix before the
    // staged M1's cycle transition runs lets that transition latch Q from the F
    // byte this instruction produced.
    private void FinishPrefixedInstruction()
    {
        _prefix = Z80Prefix.None;
        SetNextCycle(MachineCycleType.OpcodeFetch);
    }
}
