using System;
using Aemula.Emulation.Chips.Ricoh2C02;
using Aemula.Emulation.Output;

namespace Aemula.Emulation.Systems.Nes;

// The 2C02 builds the entire analog NTSC waveform itself - sync, blanking,
// colour burst and an 8-level luma/phase DAC on a single pin - so unlike the
// Atari 2600 there is no encoder circuit to model here. Ricoh2C02Chip.Video.cs
// reproduces the DAC + 12-phase chroma behaviourally and hands out one
// piecewise-constant cell value per 12x-f_SC grid step via NextVideoCell();
// this file does only the two trivial analog steps left: scale the chip's
// arbitrary DAC units onto the shared composite-video byte scale, and
// box-decimate the 12x-f_SC stream 3:1 down to the 4x-f_SC rate Television.Decode
// expects. "Chip = behaviour, system = analog sum", the same split the Apple II
// and Atari 2600 files use.
public sealed partial class NesSystem : IHasTelevision
{
    // Fed one sample at a time, live, from the same tick that produced it - see
    // AppleIISystem.Television / Atari2600System.Television for the reasoning.
    public Television Television { get; } = new();

    // Landmark levels on the shared composite-video byte scale Television.Decode
    // expects: sync tip 0, blanking 64, reference white 224. NtscYiqDecoder
    // reconstructs gain from the sync<->blanking span, so those two points must
    // be exact; WhiteLevel is the nominal reference and does not anchor anything
    // here (see the DAC map below).
    private const byte SyncLevel = 0;
    private const byte BlankingLevel = 64;
    private const byte WhiteLevel = 224;

    // Chip DAC code (Ricoh2C02Chip.Video.cs "arbitrary units") -> composite byte.
    // Linear, anchored on two points so the decoder's sync<->blanking gain stays
    // exact: DacSyncLow (0, sync tip) -> SyncLevel (0), DacSyncHigh (518,
    // blanking) -> BlankingLevel (64). Every other tap rides the same line:
    //   byte = Clamp(round(cell * 64 / 518), 0, 255)
    // NES reference white (luma DAC 1962 units) lands at ~242, above the 224
    // nominal - the 2C02 genuinely runs white hot, the same call
    // AppleIISystem.CompositeVideo.cs documents for its hot white - and
    // luma+chroma peaks simply clamp at 255. Precomputed once into a LUT keyed
    // by DAC cell value; sized 2048 because the largest code the chip emits is
    // DacLumaHigh's 1962.
    private static readonly byte[] DacCodeToByte = BuildDacCodeToByte();

    private static byte[] BuildDacCodeToByte()
    {
        var table = new byte[2048];
        for (var cell = 0; cell < table.Length; cell++)
        {
            var scaled = (int)Math.Round(
                cell * (double)(BlankingLevel - SyncLevel)
                / (Ricoh2C02Chip.DacSyncHigh - Ricoh2C02Chip.DacSyncLow));
            table[cell] = (byte)Math.Clamp(scaled, 0, 255);
        }
        return table;
    }

    // 3:1 box decimator. The 2C02 output is piecewise-constant on the 12x-f_SC
    // grid, so a 4x-f_SC sample spanning exactly three 12x cells IS the exact
    // time-average of those three cells - no windowing approximation. Runs
    // continuously and is never reset on dot or line boundaries (cell boundaries
    // do not align to dots - 8 cells/dot is not divisible by 3).
    private int _decimatePhase;         // 0..2
    private int _decimateAccumulator;

    // Most recently emitted composite-video sample, for parity with the other
    // systems' Analog scope channels. One Tick() emits at most one sample (two
    // 12x cells per tick, one Television sample per three cells), so this lags
    // the accumulator between emissions.
    public byte CurrentCompositeVideoSample { get; private set; }

    private void TickCompositeVideo()
    {
        // One master tick is two 12x-f_SC cells (12 x f_SC = 2 x master clock).
        for (var i = 0; i < 2; i++)
        {
            _decimateAccumulator += DacCodeToByte[Ppu.NextVideoCell()];

            if (++_decimatePhase == 3)
            {
                _decimatePhase = 0;

                var sample = (byte)(_decimateAccumulator / 3);
                Television.Decode(sample);
                CurrentCompositeVideoSample = sample;

                _decimateAccumulator = 0;
            }
        }
    }
}
