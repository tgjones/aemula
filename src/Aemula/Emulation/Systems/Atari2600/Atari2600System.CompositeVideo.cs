using System;
using Aemula.Emulation.Output;

namespace Aemula.Emulation.Systems.Atari2600;

// Real 2600 hardware never outputs composite video at all (it only ever
// drives an RF modulator) - so turning TIA's digital outputs into one
// composite-video byte is a design choice modeled
// on how real composite mods and AppleIISystem.CompositeVideo.cs both do
// it ("weighted sum, landmark-calibrated"), not something read off a
// schematic. Color burst itself, though, *is* real TIA behavior (TiaChip's
// own DoPlayfield/ExecuteClockLogic generate it on the Col pin, the same
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
    public readonly Television Television = new();

    // Landmark levels on the composite-video byte scale (0-255) that
    // Television.Decode expects. Not measured voltages (there's nothing
    // real to measure - see the type-level remarks above) - chosen to match
    // NtscSyncSeparator's own seeded defaults (sync tip = byte 0, black =
    // byte 64, white = byte 255) so decoding is sane from sample 1, the
    // same reasoning as AppleIISystem.CompositeVideo.cs's BlackVoltage/
    // WhiteVoltage constants.
    private const byte SyncLevel = 0;
    private const byte BlankingLevel = 64;
    private const byte WhiteLevel = 255;

    // Not sourced from a schematic (see the type-level remarks) - the real
    // signal this stands in for has been through analog filtering by this
    // point (see the type-level remarks' summary of TiaChip.Col's reasoning),
    // so there's no real amplitude to measure here either. Used for both
    // color burst and picture chroma, since real TIA generates both the
    // same way, off the same pin (see TiaChip._colorBurst's remarks) -
    // unlike AppleIISystem, which has physically separate burst/video
    // resistor weights.
    //
    // Kept well under half the sync-to-blanking swing (BlankingLevel -
    // SyncLevel): NtscSyncSeparator classifies a sample as sync purely by
    // level (closer to SyncLevel than BlankingLevel), and a sine's one
    // isolated low sample per cycle (immediately bounded by non-dipping
    // neighbors either side - see NtscSyncSeparator's own remarks on Apple
    // II's burst, the same shape) is only safely tolerated there while it
    // doesn't actually cross that midpoint. This value was originally tuned
    // against a square wave's low HALF-cycle (two consecutive low samples,
    // not sine's one) misclassifying as extra HSYNC pulses - a stricter
    // requirement than sine actually needs, but there's no reason to loosen
    // it now that chroma is a sine: the margin stays valid, just with room
    // to spare.
    private const float ChromaAmplitude = (BlankingLevel - SyncLevel) * 0.375f;

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

        var luma = sync
            ? SyncLevel
            : BlankingLevel + (WhiteLevel - BlankingLevel) * (lum / 7f);

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
        }
    }
}
