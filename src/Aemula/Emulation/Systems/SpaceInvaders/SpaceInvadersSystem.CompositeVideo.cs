using System;
using Aemula.Emulation.Chips;
using Aemula.Emulation.Output;

namespace Aemula.Emulation.Systems.SpaceInvaders;

// The composite blanking gate, the analog summing stage, and the fractional
// resampler feeding Television. The latter two are C# rather than chip
// classes - short version: this board's real
// composite summing network (the schematic's "6B"/"COMP BLANKING" -> a
// junction with VIDEO OUT, both through 1K resistors, then a 10uF cap out to
// the "Composite Video Output" edge connector pin - confirmed against the
// AA017742A schematic this session, including with the repo owner pointing
// at the exact grid cell (B8) once this session's own trace of it went
// astray into an unrelated READY test-point RC filter nearby) only has two
// equal-weight legs, which can't by itself explain a genuine sync-tip level
// below black the way Television's decoder needs (see below) - and this
// board's 19.968MHz master clock has no fixed ratio to Television's assumed
// 4*fsc sample rate the way Apple II's and Atari 2600's master clocks do.
public sealed partial class SpaceInvadersSystem
{
    // The composite blanking gate ("6B" on the schematic, confirmed this
    // session as a 74LS55). Wired the
    // simplest way that reproduces "COMP BLANKING active whenever Hblank or
    // Vblank is" from an AOI gate's inherent NOR-of-ANDs shape: one real
    // signal per term (Hblank, Vblank), the other three inputs of each term
    // tied high (this schematic's own "P = Pull Up to 5V" convention, used
    // throughout the sheet for unused TTL inputs) so they don't affect their
    // term's AND. That makes Y = !(Hblank || Vblank) - active LOW while
    // blanked - which is why every read of it below is inverted.
    private readonly Ttl7455Chip _compositeBlankingGate = new();

    // Fed one sample at a time from TickCompositeVideo below, live, the same
    // tick the summing stage produces it - see AppleIISystem.CompositeVideo.cs's
    // Television field for the same "no ring-buffer pull" reasoning.
    public Television Television { get; } = new();

    // The real board's blanking window (Hblank/Vblank, 64 H-states/38
    // V-lines - see SpaceInvadersSystem.VideoTiming.cs) is far wider than a
    // real NTSC sync pulse (~4.7us of a 63.5us line, ~7.4%): the schematic's
    // own 6B "COMP BLANKING" net only ever reads as one flat level - whether
    // VSYNC is serrated wasn't legible on the schematic scan this was traced
    // from - and the two equal-weight 1K legs found at the B8 summing
    // junction can't produce a third, lower-than-black sync-tip level from
    // Ohm's law alone. Real hardware likely resolves this either through
    // pulse-width/timing-based sync separation downstream (not the
    // amplitude-threshold scheme Television's NtscSyncSeparator uses) or
    // through circuitry past the 10uF cap this session didn't trace - either
    // way, not something reproducible as a literal resistor-divider formula
    // from what was confirmed this session.
    //
    // So, calibrating against known-good sync/black/white levels the same
    // way Apple II did, and following Atari2600System.CompositeVideo.cs's own
    // precedent even more closely (direct landmark byte levels selected by
    // case, not a weighted sum - the closer analogy here, since Space
    // Invaders is likewise a flat monochrome signal with no real per-
    // component resistor fidelity to preserve beyond "two equal legs exist"):
    // sync gets its own narrow, synthesized window (proportioned to real
    // NTSC's front-porch/sync/back-porch split within the 64-state HBLANK
    // interval: ~9/28/27 states), and black/white are selected directly by
    // case rather than literally summed. SyncLevel/BlankingLevel/WhiteLevel
    // below reuse Atari2600System.CompositeVideo.cs's own byte-scale values -
    // not measured, and not meant to be more precise than that file's, since
    // neither system has real voltages to calibrate against. WhiteLevel is
    // 224 (140 IRE reference white on the shared scale, sync 0 / blanking
    // 64), not the full-scale 255: the signal is 1-bit, so white still
    // decodes to pure white regardless, but keeping every producer on one
    // scale also keeps the Channel.Analog scope range honest.
    private const byte SyncLevel = 0;
    private const byte BlankingLevel = 64;
    private const byte WhiteLevel = 224;

    // Narrow HSYNC window within HBLANK (H=192..255): real NTSC spends
    // ~1.5us/4.7us/4.7us of its 10.9us blanking interval on front porch/
    // sync/back porch (13.8%/43.1%/43.1%) - applied to this board's 64-state
    // HBLANK gives 9/28/27, i.e. sync from H=201 (192+9) through H=228
    // inclusive. Not schematic-derived (see the type-level remarks) - a
    // proportion carried over from real NTSC spec, the same kind of
    // landmark AppleIISystem.CompositeVideo.cs's BurstAmplitudeVolts remarks
    // describe as "a real, load-bearing part of the encoding" even where the
    // exact source value isn't this board's own.
    private const int HsyncStartH = 201;
    private const int HsyncEndH = 229; // Exclusive.

    // Unserrated VSYNC (no equalizing pulses): one continuous low run for
    // the first few lines of VBLANK (V=0xDA..), rather than reproducing real
    // NTSC's per-line serration - a deliberate simplification: an unserrated
    // VSYNC causes Television's horizontal raster oscillator to lose lock
    // for a few lines once per field, which is cosmetic and self-recovers
    // via the same reacquisition path a real Atari 2600 capture needed
    // (see NtscRasterOscillators.cs). 3 lines is a reasonable estimate for
    // that window.
    private const int VsyncLines = 3;

    // Space Invaders' 19.968MHz master clock has no fixed-integer relationship
    // to Television's assumed 4*fsc (14.318180MHz) sample rate the way Apple
    // II's (exactly 4*fsc) and Atari 2600's (exactly fsc) master clocks do.
    // 13125/18304 is the exact
    // (lowest-terms) ratio of samples-per-master-tick: 19968000 * 13125 /
    // 18304 = 14318181.8(1) recurring, matching 315/88 MHz (real NTSC fsc)
    // *4 to within floating-point rounding. A free-running phase accumulator
    // - never reset per-line/per-frame, matching how AppleIISystem's own
    // _masterTickCounter free-runs for the same "real hardware has no
    // per-frame reset of a free-running clock division" reason - emits one
    // Decode() call each time it crosses 1.0.
    private const double CompositeVideoSamplesPerMasterTick = 13125.0 / 18304.0;

    private double _compositeVideoPhase;

    // The most recently *decoded* composite-video sample - unlike
    // Atari2600System's own CurrentCompositeVideoSample (updated every
    // sub-sample, since TIA's own clock is coarser than Television's sample
    // rate there), this only updates on ticks that actually cross the phase
    // accumulator's 1.0 threshold above, since most master ticks here don't
    // produce a sample at all.
    public byte CurrentCompositeVideoSample { get; private set; }

    // Fires once per composite-video sample actually decoded (i.e. once per
    // phase-accumulator crossing above, not once per master tick) - see
    // Atari2600System.CompositeVideoSampled's own remarks for why this
    // exists (LogicAnalyzerWindow recording the Composite Video channel at
    // its true sample rate rather than being capped at Debugger.Ticked's).
    internal event Action? CompositeVideoSampled;

    private void TickCompositeVideo()
    {
        var (h, v) = GetVideoScannerState();

        _compositeBlankingGate.A1 = Hblank;
        _compositeBlankingGate.B1 = true;
        _compositeBlankingGate.C1 = true;
        _compositeBlankingGate.D1 = true;
        _compositeBlankingGate.A2 = Vblank;
        _compositeBlankingGate.B2 = true;
        _compositeBlankingGate.C2 = true;
        _compositeBlankingGate.D2 = true;

        var blanked = !_compositeBlankingGate.Y;

        var hsyncActive = Hblank && h >= HsyncStartH && h < HsyncEndH;
        var vsyncActive = Vblank && (v - 0xDA) < VsyncLines;
        var syncActive = hsyncActive || vsyncActive;

        var sample = syncActive
            ? SyncLevel
            : !blanked && _videoShiftRegister.Qh ? WhiteLevel : BlankingLevel;

        _compositeVideoPhase += CompositeVideoSamplesPerMasterTick;

        if (_compositeVideoPhase >= 1.0)
        {
            _compositeVideoPhase -= 1.0;

            CurrentCompositeVideoSample = sample;

            Television.Decode(sample);

            CompositeVideoSampled?.Invoke();
        }
    }
}
