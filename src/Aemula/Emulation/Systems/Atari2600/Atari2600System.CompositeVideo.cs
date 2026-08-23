using System;

namespace Aemula.Emulation.Systems.Atari2600;

// This namespace nests under the root Aemula namespace, where the older,
// unrelated Aemula.Television already lives (see docs/television-plan.md's
// "Naming collision, explicitly out of scope" note) - aliased here for the
// same reason TelevisionWindow.cs and AppleIISystem.CompositeVideo.cs
// already are.
using Television = Aemula.Emulation.Output.Television;

// Phase 4 of docs/atari2600-television-plan.md: unlike every earlier phase,
// real 2600 hardware never outputs composite video at all (it only ever
// drives an RF modulator) - so, per that phase's checkpoint, this is a
// design choice modeled on how real composite mods and AppleIISystem.
// CompositeVideo.cs both do it ("weighted sum, landmark-calibrated"), not
// something read off a schematic.
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

    // Not sourced from a schematic (see the type-level remarks). Chosen so
    // the chroma swing this adds on top of luma is clearly visible without
    // clipping even at the top of the luma ramp (Lum == 7 already reaches
    // WhiteLevel on its own) - the final Math.Clamp below is what actually
    // protects against clipping either way.
    private const float ChromaAmplitude = (WhiteLevel - BlankingLevel) * 0.25f;

    // Free-running master-tick counter for the chroma sine's phase - same
    // idea as AppleIISystem.CompositeVideo.cs's own _masterTickCounter
    // (never reset per-line/per-frame, matching TIA's free-running OSC
    // input), but doing double duty here: TIA's OSC edge and
    // Television.Decode below both advance exactly once per master tick
    // (the plan doc's "no resampling needed" note), so this counter and
    // Television's own internal per-sample phase counters always stay in
    // lockstep starting from tick 0. That's *why* no synthesized color
    // burst is needed for chroma to decode correctly: with no real burst
    // ever crossing NtscColorBurstPll's detection threshold, its phase-
    // offset correction never fires and stays at its default of zero - and
    // because the two counters are already aligned by construction, zero
    // offset is already the *correct* answer, not just a harmless
    // fallback.
    private uint _masterTickCounter;

    private void TickCompositeVideo()
    {
        byte sample;

        if (_tia.Sync)
        {
            sample = SyncLevel;
        }
        else if (_tia.Blk)
        {
            sample = BlankingLevel;
        }
        else
        {
            var luma = BlankingLevel + (WhiteLevel - BlankingLevel) * (_tia.Lum / 7f);

            // A chroma sine, not just a per-sample offset: it has to
            // actually oscillate at the subcarrier rate (the
            // subcarrierPhase term, advancing every tick the same way
            // AppleIISystem.CompositeVideo.cs's burst sine does) for
            // Television's comb filter to recognize it as chroma at all -
            // a constant addition would just get absorbed into luma
            // instead. Col selects a fixed phase offset on top of that
            // (Col * 24 degrees - TiaChip.Col's own doc comment on the
            // 15-phase hue approximation this plan phase keeps as-is),
            // with zero amplitude at Col == 0 (grayscale).
            var subcarrierPhase = 2f * MathF.PI * (_masterTickCounter % 4) / 4f;
            var huePhase = _tia.Col * 24f * MathF.PI / 180f;
            var chroma = _tia.Col == 0 ? 0f : ChromaAmplitude * MathF.Sin(subcarrierPhase + huePhase);

            sample = (byte)Math.Clamp(MathF.Round(luma + chroma), 0, 255);
        }

        Television.Decode(sample);

        _masterTickCounter++;
    }
}
