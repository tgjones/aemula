using System;
using Aemula.Emulation.Output;

namespace Aemula.Emulation.Systems.Atari2600;

// Real 2600 hardware never outputs composite video at all (it only ever
// drives an RF modulator) - so turning TIA's digital outputs into one
// composite-video byte is a design choice modeled
// on how real composite mods and AppleIISystem.CompositeVideo.cs both do
// it ("weighted sum, landmark-calibrated"), not something read off a
// schematic. Color burst itself, though, *is* real TIA behavior (TiaChip's
// own DoVideo/ExecuteClockLogic generate it on the Col pin, the same
// way real hardware does - see TiaChip._colorBurst's remarks) - this file
// just samples it, the same as it samples picture chroma.
//
// Chroma itself is synthesized here as a sine at Col's phase, not sampled
// as the real square wave TIA's Col pin actually outputs - see
// TiaChip.Col's own doc comment for the full reasoning and the measurements
// behind it. Short version: Television.Decode only ever sees 4 samples per
// subcarrier cycle, and reducing a real square wave to 4 samples (by any
// method tried - fixed-point evaluation, averaging finer sub-samples)
// decodes with real, measured hue/saturation error, while a directly-
// synthesized sine decodes exactly - the mathematically exact answer for a
// bandlimited signal at this sample rate, not merely a smoother-looking
// approximation.
public sealed partial class Atari2600System
{
    // Same "fed one sample at a time, live, from the same tick that
    // produced it" reasoning as AppleIISystem.Television - see that field's
    // remarks.
    public Television Television { get; } = new();

    // Landmark levels on the shared composite-video byte scale that
    // Television.Decode expects: sync tip 0, blanking 64, reference white
    // 224 - 1.6 bytes/IRE with a 2.5x sync-to-white gain, the same scale
    // every producer in the repo emits (and NtscSyncSeparator seeds its
    // estimates to). Not measured voltages - there's nothing real to
    // measure (see the type-level remarks) - but keeping to the one scale
    // is what lets the decoder's sync-anchored gain reconstruct the same
    // reference white from any of them. White is 224 rather than full-scale
    // 255 so bright saturated hues can swing above 100 IRE once chroma
    // rides on luma without the comb filter clipping it flat.
    private const byte SyncLevel = 0;
    private const byte BlankingLevel = 64;
    private const byte WhiteLevel = 224;

    // TIA's three LUM lines are a binary code that a passive resistor ladder
    // on the board turns back into one analog luma level (LUM0/1/2 pads ->
    // motherboard R214/R215/R216 -> summing node -> RF modulator; the LUM
    // pins also carry weak ~3.3k pull-ups, and R211/R213 trims the ramp -
    // AtariAge schematic archive, RetroSix / TinkerDifferent composite-mod
    // teardowns, Atari 2600 Field Service Manual). The ladder is deliberately
    // not R-2R: the eight levels come out *compressive* - each step smaller
    // than the last toward white - not a straight line, which is why the old
    // lum/7f curve was wrong at every code but 0 and 7.
    //
    // The exact ladder resistances are not recoverable from any source
    // reachable here - the schematic survives only as page scans and the
    // AtariAge scope-measurement threads that would pin the LUM/COLOR pin
    // drive are login-walled - so, as a deliberate fallback, the curve's
    // *shape* is taken from this codebase's own hardware-derived reference:
    // Palette.NtscPalette row 0, the achromatic ramp, whose entries are by
    // definition each level's luma Y - 0, 64, 108, 144, 176, 200, 220, 236
    // for codes 0..7 (steps 64, 44, 36, 32, 24, 20, 16). That row is the grey
    // output of the real luma DAC; using it directly models the hardware more
    // faithfully than resistor values reverse-fitted to reproduce it.
    private static readonly float[] GreyDacLevels =
        { 0f, 64f, 108f, 144f, 176f, 200f, 220f, 236f };

    // GreyDacLevels are luma-Y values on NtscYiqDecoder's own 0..255 output
    // scale, and the decoder recovers Y as (byte - BlankingLevel) * 255 /
    // (WhiteLevel - BlankingLevel). Emitting a level is just that map run
    // backwards - byte = BlankingLevel + Y * (WhiteLevel - BlankingLevel) /
    // 255 - so a settled grey screen decodes straight back to its palette Y,
    // with no free scalar to calibrate. lum 7 grey then lands near byte 212,
    // ~92 IRE: real 2600 grey genuinely doesn't reach 100 IRE reference
    // white. The palette carries that - its achromatic ramp stops at 236
    // while its coloured rows reach ~252, i.e. luma+chroma swings above the
    // grey ceiling but grey alone doesn't - and keeping it is the same
    // choice AppleIISystem.CompositeVideo.cs makes emitting its measured-hot
    // white instead of forcing it onto reference white.
    private static readonly float LumaYToByteScale =
        (WhiteLevel - BlankingLevel) / 255f;

    // The eight active-video luma levels on the shared byte scale - the map
    // above applied to GreyDacLevels once at type load, so there is no
    // per-sample network solve. Emitted bytes are roughly 64, 104, 132,
    // 154, 174, 189, 202, 212, decoding straight back to palette row 0
    // (0, 64, 108, 144, 176, 200, 220, 236). The compressive DAC shape -
    // each step smaller than the last toward white - is what the old lum/7f
    // line got wrong at every code but 0. Grey tops out well under byte 255,
    // leaving headroom for chroma to ride above it (see ChromaAmplitude).
    private static readonly float[] LumaLevels = BuildLumaLevels();

    private static float[] BuildLumaLevels()
    {
        var levels = new float[8];
        for (var i = 0; i < levels.Length; i++)
        {
            levels[i] = BlankingLevel + GreyDacLevels[i] * LumaYToByteScale;
        }
        return levels;
    }

    // Coloured entries are not black at lum 0. TIA's luma DAC keeps
    // chroma-bearing hues on their own raised sub-range: Palette.NtscPalette's
    // hue-1 lum-0 entry ($10 = 0x444400, RGB 68/68/0) has luma Y ~= 60, not
    // 0 - "lum-0 colours (tree trunks) render black" is this sub-range being
    // dropped. Modeled as a floor under the luma curve for any non-grey entry
    // (Col != 0): the entry sits at max(level, floor). The palette puts that
    // coloured-black luma (~60) right at the grey DAC's own code-1 output
    // (GreyDacLevels[1] = 64), so the floor is simply LumaLevels[1] - it
    // decodes back to ~0x444400's Y with no separate calibration constant.
    // Only hue 1 is matched (it is the reported case and TIA's reference
    // phase); other lum-0 hues, whose palette Y runs ~15..57, are lifted to
    // the same floor too - a known simplification of the DAC's per-hue
    // coloured-black spread.
    private static readonly float ColourFloor = LumaLevels[1];

    // The COLOR pin's share of that same summing node, as a sine amplitude on
    // the byte scale (the real Col pin is a square wave - see the type-level
    // remarks for why this stage synthesizes a sine instead). Calibrated the
    // same way as the luma curve, against Palette.NtscPalette rather than a
    // resistor value - but then clamped by sync safety, so it lands below the
    // palette rather than on it.
    //
    // Target: NtscYiqDecoder recovers a chroma vector whose magnitude is this
    // amplitude times its decode scale, 255 / (whiteRef - black) = 255 / 160
    // = 1.594 (the sync-anchored gain is exactly nominal - black and sync
    // self-calibrate to the emitted 64 / 0). So amplitude 26 decodes to a
    // magnitude near 41, against a palette mean across all 15 hues at lum 3
    // of ~45 (taken as |0.492*(b-y), 0.877*(r-y)|) - a few percent shy of
    // the mean, and a touch over the palette's less-saturated hues.
    //
    // Sync safety is what stops it going higher. NtscSyncSeparator classifies
    // a sample as sync purely by level (closer to SyncLevel than
    // BlankingLevel, i.e. below their midpoint 32). Chroma rides its lowest
    // pedestal during color burst, where that pedestal is BlankingLevel (64)
    // - the coloured-black floor lifts active-video coloured pedestals to
    // ~104, so burst is the binding case - and the sine's one isolated low
    // sample per cycle reaches BlankingLevel - amplitude. 0.40625 *
    // (BlankingLevel - SyncLevel) = 26 holds that at 38, the same ~6-byte
    // clearance over the midpoint the previous hand-set amplitude (24 -> 40)
    // kept. TIA's real chroma:luma ratio is set by the output resistor
    // network (not recoverable here - see the luma-curve remarks above), so
    // this stays a palette calibration bounded by that sync-classification
    // margin.
    private const float ChromaAmplitude = (BlankingLevel - SyncLevel) * 0.40625f;

    // How far one hue code steps around the color wheel. Not 360/15 = 24:
    // TIA's hue generator is an analog delay line (see TiaChip.Col), and its
    // fifteen taps do *not* add up to a clean single turn - the delay line's
    // total is what the trim potentiometer on real 2600 boards adjusts, and
    // no two consoles are trimmed identically. That per-step spacing is the
    // one genuinely console-specific number in TIA color, and it's the
    // parameter emulators expose as such: Stella models it as
    // DEF_NTSC_SHIFT = 26.7 degrees, user-adjustable +/-4.5 degrees around
    // that (src/common/PaletteHandler.hxx, exposed as -pal.phase_ntsc), and
    // generates its whole NTSC palette from it. This project's own
    // (Gopher2600-sourced, hardware-derived) Palette.NtscPalette
    // independently averages 27.4 degrees per step, corroborating it.
    //
    // Worth being precise about what this is *not*: it is not an absolute
    // hue rotation, and no absolute rotation belongs anywhere in this
    // pipeline. Absolute phase is pinned by color burst, at both ends -
    // TIA transmits burst off the same delay-line tap as hue 1, and
    // NtscYiqDecoder rotates off recovered burst by the plain spec figure
    // with no calibration on top (see BurstToIAxisRotationRadians). That is
    // exactly why a period TV needed no re-tinting when swapping an Atari
    // 2600 for an Apple II: burst is what makes absolute phase a fixed
    // point rather than a per-source calibration. What the pot varies, and
    // all it varies, is how far apart the hues land - which is this
    // constant, and which no receiver-side tint control could correct
    // anyway.
    //
    // Using 24 here instead accumulated 14 steps' worth of ~2.7-degree
    // error, i.e. ~38 degrees of drift by hue 15 - measured, and previously
    // mistaken for irreducible nonlinearity in Palette.NtscPalette itself.
    private const float HueStepDegrees = 26.7f;

    // The most recently written composite-video sample - exposed as an
    // Analog scope channel alongside the TIA pins. One Tick() call produces
    // 4 sub-samples (see the loop below), so this is specifically the last
    // of those 4, matching the value Television itself decoded most
    // recently as of this tick.
    public byte CurrentCompositeVideoSample { get; private set; }

    // Fires once per composite-video sub-sample - 4x per Tick() - rather
    // than once per Tick() like Debugger.Ticked. This is what lets
    // LogicAnalyzerWindow record Composite Video (and every other channel,
    // held constant across the extra samples) at its true 4x-oversampled
    // rate instead of being capped at TIA's own 3.58MHz tick rate - see
    // Atari2600Debugger.CreateDebuggerWindows' SampleClock construction.
    internal event Action? CompositeVideoSampled;

    private void TickCompositeVideo()
    {
        // TIA's digital outputs don't resolve any finer than once per OSC
        // tick (one call to this method), so these are read once and held
        // constant across the 4 sub-samples below - matching
        // AppleIISystem.CompositeVideo.cs's VideoDataBit/SyncBit, which are
        // likewise held constant within their own faster-than-the-digital-
        // signal sample loop.
        var sync = _tia.Sync;
        var lum = _tia.Lum;
        var col = _tia.Col;

        float luma;
        if (sync)
        {
            luma = SyncLevel;
        }
        else if (_tia.Blk)
        {
            // Blanking, including the back porch that carries color burst:
            // TIA forces the LUM lines off here, so the level is flat
            // blanking whatever the lum code reads - and the coloured-black
            // floor below must not lift it, since burst rides on blanking.
            luma = BlankingLevel;
        }
        else
        {
            // Active video: the compressive luma DAC curve. Any non-grey
            // entry (Col != 0) also takes the coloured-black floor, so lum-0
            // hues keep TIA's raised coloured sub-range instead of collapsing
            // to blanking.
            luma = LumaLevels[lum];
            if (col != 0)
            {
                luma = MathF.Max(luma, ColourFloor);
            }
        }

        // Television.Decode needs samples at exactly 4x the NTSC color
        // subcarrier (~14.318MHz). TIA's OSC input is the subcarrier rate
        // itself (3.579545MHz), not 4x it - there's no faster clock anywhere on
        // real 2600 hardware, so unlike AppleIISystem (whose own master
        // clock genuinely is 4x its dot clock already), the 4x oversampling
        // Television needs has to be synthesized here, purely as part of
        // this not-real-hardware composite-summing stage: since one call to
        // this method already *is* exactly one subcarrier cycle, that's
        // just 4 sub-samples 90 degrees apart, no cross-tick phase state
        // needed (unlike AppleIISystem's free-running _masterTickCounter,
        // which exists because Apple II's video-data bit changes slower
        // than its own master clock - TIA's Sync/Blk/Lum/Col genuinely
        // don't).
        for (var subSample = 0; subSample < 4; subSample++)
        {
            var chroma = 0f;

            // Col == 0 is grayscale (no chroma - see TiaChip.Col's own doc
            // comment on the hue-index approximation this plan phase keeps
            // as-is). The !sync guard is a pragmatic safety net, not
            // something sourced from a schematic: TiaChip's _colorBurst
            // window is purely horizontal-counter-driven, so it can in
            // principle overlap a broad vertical-sync pulse's own extended
            // low period on a handful of lines per frame - genuine sync tip
            // shouldn't carry chroma regardless of how that edge case
            // really behaves on real silicon.
            if (col != 0 && !sync)
            {
                // One full subcarrier cycle per call (see above), so the 4
                // sub-samples are exactly 90 degrees apart; hue code 1 is
                // TIA's own reference phase (0 degrees, the same phase as
                // color burst - see TiaChip._colorBurst's remarks, and note
                // that this file needs no special case to make that true:
                // TiaChip drives Col = 1 for the burst window itself, so
                // burst is literally hue 1, exactly as the real delay line's
                // shared tap makes it), so hues 2-15 fall at
                // (Col-1)*HueStepDegrees from there.
                //
                // Negative, not positive: real TIA's hue generator is a
                // phase-*delay* line (see TiaChip.Col's own doc comment -
                // "a digital phase shifter... with fifteen phase angles"),
                // and delaying a sinusoid in time is a negative phase
                // shift, not a positive one - increasing hue code adds more
                // delay, so it should rotate the phase backward, not
                // forward. Corroborated independently by the reference
                // palette: converting Palette.NtscPalette's own entries back
                // to chroma phase walks the hue circle in exactly this
                // direction as the hue code rises (gold, orange, red,
                // purple, blue, cyan, green), which on a standard NTSC
                // vectorscope is decreasing phase.
                //
                // A sine, not the real square wave TIA's Col pin actually
                // outputs - see TiaChip.Col's own doc comment for why: this
                // decodes exactly (0 error against the mathematically exact
                // target), where reducing a real square wave to Television's
                // 4-samples-per-cycle contract by any method measured
                // (direct 4-point evaluation, or averaging finer
                // sub-samples down to 4) introduced real, measured hue and
                // saturation error instead.
                var phaseRadians = (subSample * 90f - (col - 1) * HueStepDegrees) * MathF.PI / 180f;
                chroma = ChromaAmplitude * MathF.Sin(phaseRadians);
            }

            var sample = (byte)Math.Clamp(MathF.Round(luma + chroma), 0, 255);

            Television.Decode(sample);

            CurrentCompositeVideoSample = sample;

            CompositeVideoSampled?.Invoke();
        }
    }
}
