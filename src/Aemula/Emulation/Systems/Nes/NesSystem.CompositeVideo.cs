using System;
using Aemula.Emulation.Chips.Ricoh2C02;
using Aemula.Emulation.Output;
using Aemula.Emulation.Output.Ntsc;

namespace Aemula.Emulation.Systems.Nes;

// The 2C02 builds the entire analog NTSC waveform itself - sync, blanking,
// colour burst and an 8-level luma/phase DAC on a single pin - so unlike the
// Atari 2600 there is no encoder circuit to model here. Ricoh2C02Chip.Video.cs
// reproduces the DAC + 12-phase chroma behaviourally and hands out one
// piecewise-constant cell value per 12x-f_SC grid step via NextVideoCell();
// this file does only the two trivial analog steps left: scale the chip's
// arbitrary DAC units onto the shared composite-video byte scale (anchored on
// the 2C02 system palette so a settled screen decodes straight back to it),
// and resample the 12x-f_SC stream 3:1 down to the 4x-f_SC rate Television.Decode
// expects with a short band-limiting FIR. "Chip = behaviour, system = analog
// sum", the same split the Apple II and Atari 2600 files use.
public sealed partial class NesSystem
{
    // Landmark levels on the shared composite-video byte scale Television.Decode
    // expects: sync tip 0, blanking 64 (nominal reference white would be 224).
    // NtscYiqDecoder reconstructs its whole gain from the sync<->blanking span -
    // whiteRef == BlankingLevel + WhiteReferenceGainFromSyncSwing * (blanking -
    // sync) - so the DAC map below pins the sync tip and blanking exactly and
    // then maps the 2C02 palette's grey column onto that reconstructed span (see
    // AnchorGreyByte, which runs the same gain equation backwards).
    private const byte SyncLevel = 0;
    private const byte BlankingLevel = 64;

    // Rec.601 luma Y of Ricoh2C02Chip._systemPalette's grey column - the
    // achromatic ramp the 2C02's own DAC taps have to decode back to. Two
    // columns, indexed by the 2-bit luma code:
    //   GreyLumaHighY - the $x0 "grey" entries    ($00 $10 $20 $30)
    //   GreyLumaLowY  - the $xD "dark grey" entries ($0D $1D $2D $3D)
    // The 0.299/0.587/0.114 coefficients are exactly what NtscYiqDecoder's comb
    // filter + AGC recover luma with, so the round trip is tight. $20 and $30
    // are the same clipped near-white in the palette (both luma DAC taps clip at
    // 1962 too), so the two bright _h anchors coincide; $0D and $1D are both
    // pure black. Kept in sync with _systemPalette by hand, the same way
    // Atari2600System.CompositeVideo.cs's GreyDacLevels mirrors Palette.NtscPalette
    // row 0.
    private static readonly float[] GreyLumaHighY = { 84.000f, 150.826f, 237.174f, 237.174f };
    private static readonly float[] GreyLumaLowY = { 0.000f, 0.000f, 60.000f, 161.174f };

    // The chip DAC codes (Ricoh2C02Chip.Video.cs arbitrary-unit level table)
    // that NextVideoCell() can ever emit for active picture: the $xD / $x0 pair
    // per 2-bit luma code. Sync tip / blanking come from Ricoh2C02Chip's own
    // internal constants and the two burst codes ride the plain linear map, so
    // only these six need palette anchoring. Mirrors that table by hand.
    private static readonly int[] DacLumaLowCode = { 350, 518, 962, 1550 };
    private static readonly int[] DacLumaHighCode = { 1094, 1506, 1962, 1962 };

    // Chip DAC code (Ricoh2C02Chip.Video.cs "arbitrary units") -> composite byte.
    // A plain linear sync<->blanking map is the baseline - exact at the two
    // anchor codes (DacSyncLow 0 -> 0, DacSyncHigh 518 -> BlankingLevel 64),
    // sane everywhere else, and already right for the two burst codes
    // (DacBurstLow 196 -> ~24, DacBurstHigh 934 -> ~115): burst only has to
    // straddle blanking, the decoder self-references its amplitude.
    //
    // The six $xD / $x0 grey-tap codes are then overwritten with palette-
    // anchored levels (see AnchorGreyByte): NtscYiqDecoder's own luma recovery
    // run backwards, so a settled grey screen decodes straight back to its
    // palette Y with no free scalar - the same trick
    // Atari2600System.CompositeVideo.cs's LumaLevels uses. It is anchored
    // against the black level the decoder's NtscSyncSeparator actually settles
    // on (~53.9, see SeparatorClampedBlackLevel), not the emitted 64: the FIR's
    // sync-edge response, sampled by the level-triggered separator, clamps
    // black low, and compensating the grey taps is the only lever left (the
    // separator and the decimation ratio are both off-limits). A plain linear
    // sync<->blanking map sent NES white (DAC 1962) to byte ~242 while the
    // decoder only ever rebuilds whiteRef from the sync swing, so every settled
    // grey decoded far too bright and hues clipped.
    //
    // Coloured hues $x1..$xC alternate their luma code's _l / _h taps, so their
    // decoded luma falls out as the mean of the two anchored greys and their
    // saturation as half the difference, band-limited by the FIR - no separate
    // hue calibration, same as the Atari file. Only the ~10 anchored codes ever
    // reach this table; the rest of the 2048 entries keep the linear fallback
    // so an unexpected code still maps somewhere sane. Sized 2048 because the
    // largest code the chip emits is DacLumaHigh's 1962.
    private static readonly byte[] DacCodeToByte = BuildDacCodeToByte();

    private static byte[] BuildDacCodeToByte()
    {
        var table = new byte[2048];

        for (var code = 0; code < table.Length; code++)
        {
            var scaled = (int)Math.Round(
                code * (double)(BlankingLevel - SyncLevel)
                / (Ricoh2C02Chip.DacSyncHigh - Ricoh2C02Chip.DacSyncLow));
            table[code] = (byte)Math.Clamp(scaled, 0, 255);
        }

        for (var i = 0; i < 4; i++)
        {
            table[DacLumaHighCode[i]] = AnchorGreyByte(GreyLumaHighY[i]);
            table[DacLumaLowCode[i]] = AnchorGreyByte(GreyLumaLowY[i]);
        }

        // Sync tip and blanking are hard anchors the decoder reconstructs its
        // whole gain from, so they win over anything the loop above wrote.
        // DacLumaLowCode[1] (the $1D "constant _l at luma 1" tap) is the same
        // arbitrary-unit code as DacSyncHigh - $1D genuinely sits at the
        // blanking voltage on real hardware - so the loop's anchored value for
        // it has to be stamped back down to BlankingLevel here; $1D then
        // decodes as near-black (blanking sampled against the low black
        // estimate), which is exactly what the palette's pure-black $1D entry
        // wants anyway.
        table[Ricoh2C02Chip.DacSyncLow] = SyncLevel;
        table[Ricoh2C02Chip.DacSyncHigh] = BlankingLevel;

        return table;
    }

    // NtscSyncSeparator is level-triggered: once per line it re-clamps its
    // black-level estimate from the single decimated sample that lands
    // immediately after the HSYNC trailing edge. The band-limiting FIR below,
    // together with the fixed 3:1 decimation, spreads that sync -> breezeway
    // step across a few output samples; because the 2C02's sync edge is not
    // phase-locked to the 4x-f_SC output grid (2728 12x cells per line, not a
    // multiple of 3), one line in three has that post-sync sample land
    // mid-transition rather than on the settled 64 plateau. Averaged over the
    // 3-line beat the separator's black estimate converges to ~53.9 - a
    // stable steady state (identical across every palette code, flat from the
    // 8th settled frame on), not a transient - roughly ten units under the
    // emitted blanking level. The NES breezeway (dots 305-308, locked against
    // Flawless2C02) is too short to give the FIR a clean plateau to settle on
    // before colour burst, and NtscSyncSeparator / the decimation ratio are
    // both off-limits, so the grey/luma DAC taps compensate instead: emit
    // each grey byte as NtscYiqDecoder's own luma recovery
    //   Y = (sample - black) * 255 / (WhiteReferenceGainFromSyncSwing * (black - sync))
    // run backwards against that *measured* black (sync self-calibrates to
    // ~0), so a settled grey screen still decodes straight back to its
    // palette luma. The blanking tap itself is untouched - it stays at
    // BlankingLevel (64); only active-picture greys and the hue codes' _l/_h
    // taps are anchored this way.
    private const float SeparatorClampedBlackLevel = 53.9f;

    private static byte AnchorGreyByte(float greyY) => (byte)Math.Clamp(
        (int)Math.Round(
            SeparatorClampedBlackLevel
            + greyY * NtscYiqDecoder.WhiteReferenceGainFromSyncSwing
                * SeparatorClampedBlackLevel / 255f),
        0, 255);

    // Windowed-sinc low-pass decimating FIR on the 12x-f_SC cell stream,
    // replacing the box-of-3 the plan started with. The 2C02's colour burst is
    // a real f_SC *square* wave; a 3-cell box average leaves it as roughly
    // [115, 115, 24, 24] per cycle at 4x-f_SC - two consecutive samples below
    // NtscSyncSeparator's sync/blanking midpoint (byte ~32), so every burst
    // half-cycle was misclassified as HSync and the active picture never
    // decoded. This FIR band-limits the square wave to near its fundamental
    // before decimation, leaving at most a single isolated sub-threshold sample
    // per burst cycle, which the separator's 2-sample confirm ignores.
    //
    // Length 15, Hann window, cutoff 1.75 x f_SC (normalised 0.14583
    // cycles/cell, i.e. 0.292 of the 12x-f_SC Nyquist at 6 x f_SC): passes the
    // f_SC chroma/burst fundamental at |H(f_SC)| ~ 0.86, drops the 3 x f_SC
    // harmonic to ~0.015 (>35 dB) - that 3rd harmonic is exactly what aliased
    // the burst troughs down into the sync slice. Taps are normalised to sum to
    // 1 so DC gain is exactly unity and flat regions (sync tip, blanking, back
    // porch) pass straight through to the byte, keeping the decoder's
    // sync<->blanking gain anchor intact.
    //
    // This is a narrow operating point, not a free choice. A gentler cutoff
    // lets the decimated burst trough (byte ~34 here, against NtscSyncSeparator's
    // ~27 sync/blanking midpoint) drop back under the slice and the picture
    // stops locking; a sharper one widens the sync-edge response, pulling the
    // separator's clamped black estimate (see SeparatorClampedBlackLevel) below
    // ~53.5, at which point the blanking-level near-black codes ($0F/$1D) no
    // longer decode dark enough. 1.75 x f_SC threads both - and the ~14% f_SC
    // loss it does cost is common-mode between colour burst and active chroma
    // (both at f_SC through this same filter), so NtscColorBurstPll's
    // burst-referenced phase lock and the decoder's burst-anchored chroma
    // handling absorb it, leaving saturated hues a touch over-saturated rather
    // than dim (see NesSystemTelevisionTests' hue tolerance).
    //
    // Atari2600System.CompositeVideo.cs sidesteps this same square-wave-aliasing
    // problem by synthesising a sine instead of sampling TIA's real Col square
    // wave; this file keeps the 2C02's real square wave (it is what
    // Ricoh2C02Tests checks node-for-node against Flawless2C02) and band-limits
    // it here instead - the plan's sanctioned alternative.
    //
    // Constant group delay (N-1)/2 = 7 cells is a fixed sub-pixel shift the
    // Television's geometry lock takes up.
    private const int LowPassTapCount = 15;
    private static readonly float[] LowPassTaps = BuildLowPassTaps();

    private static float[] BuildLowPassTaps()
    {
        const double cutoff = 0.14583333; // cycles per 12x-f_SC cell == 1.75 x f_SC
        var taps = new float[LowPassTapCount];
        var m = LowPassTapCount - 1;
        double sum = 0;

        for (var n = 0; n < LowPassTapCount; n++)
        {
            var x = n - m / 2.0;
            var sinc = Math.Abs(x) < 1e-9
                ? 2.0 * cutoff
                : Math.Sin(2.0 * Math.PI * cutoff * x) / (Math.PI * x);
            var hann = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * n / m);
            var tap = sinc * hann;

            taps[n] = (float)tap;
            sum += tap;
        }

        // Normalise to unit DC gain (see remarks).
        for (var n = 0; n < LowPassTapCount; n++)
        {
            taps[n] = (float)(taps[n] / sum);
        }

        return taps;
    }

    // Delay line of the most recent LowPassTapCount mapped bytes, oldest at
    // _delayLineHead. Fed two cells per Tick(); every third cell the FIR
    // dot-product is taken and decoded (12x-f_SC -> 4x-f_SC). The line is never
    // reset on dot or line boundaries - cell boundaries do not align to dots
    // (8 cells/dot is not divisible by 3) - so the resample runs continuously.
    private readonly float[] _delayLine = new float[LowPassTapCount];
    private int _delayLineHead;
    private int _decimatePhase; // 0..2

    // Most recently emitted composite-video sample, for parity with the other
    // systems' Analog scope channels. One Tick() emits at most one sample (two
    // 12x cells per tick, one Television sample per three cells), so this lags
    // the FIR delay line between emissions.
    public byte CurrentCompositeVideoSample { get; private set; }

    private void TickCompositeVideo()
    {
        // One master tick is two 12x-f_SC cells (12 x f_SC = 2 x master clock).
        for (var i = 0; i < 2; i++)
        {
            _delayLine[_delayLineHead] = DacCodeToByte[Ppu.NextVideoCell()];
            _delayLineHead = _delayLineHead + 1 == LowPassTapCount ? 0 : _delayLineHead + 1;

            if (++_decimatePhase == 3)
            {
                _decimatePhase = 0;

                // Hann-windowed taps are symmetric, so the walk direction over
                // the ring does not matter; start at the oldest sample.
                double acc = 0;
                var idx = _delayLineHead;
                for (var k = 0; k < LowPassTapCount; k++)
                {
                    acc += LowPassTaps[k] * _delayLine[idx];
                    idx = idx + 1 == LowPassTapCount ? 0 : idx + 1;
                }

                var sample = (byte)Math.Clamp((int)Math.Round(acc), 0, 255);
                Television.Decode(sample);
                CurrentCompositeVideoSample = sample;
            }
        }
    }
}
