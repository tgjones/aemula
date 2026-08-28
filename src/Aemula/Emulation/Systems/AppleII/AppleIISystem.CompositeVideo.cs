using System;
using Aemula.Emulation.Output;

namespace Aemula.Emulation.Systems.AppleII;

// The analog composite video summing stage. Real hardware: Q3, an NPN
// emitter follower with three weighted resistor inputs (R7=1.5K VIDEO DATA,
// R8=2K SYNC, R6=2.7K COLOR BURST). Modelled as a weighted-sum-then-clamp
// formula rather than transistor-level simulation, calibrated directly
// against Gayler's measured output levels.
public sealed partial class AppleIISystem
{
    // Fed one sample at a time from TickCompositeVideo below, live, the
    // same tick the analog summing stage produces it - the same way every
    // other signal in this emulator propagates through the chips/systems
    // that consume it, rather than a UI window pulling a backlog from a
    // ring buffer once per frame.
    public Television Television { get; } = new();

    // Real resistor values (kΩ) from the video-summing circuit.
    private const double R7Video = 1.5;
    private const double R8Sync = 2.0;
    private const double R6Burst = 2.7; // always part of the divider, even when idle

    private const double GVideo = 1.0 / R7Video;
    private const double GSync = 1.0 / R8Sync;
    private const double GBurst = 1.0 / R6Burst;
    private const double GSum = GVideo + GSync + GBurst;

    private const double WVideo = GVideo / GSum;
    private const double WSync = GSync / GSum;

    private const double BlackVoltage = 0.5;
    private const double WhiteVoltage = 2.0;

    // The volts->byte map is anchored on Gayler's two measured low
    // landmarks - sync 0V -> byte 0, blanking 0.5V -> byte 64 - i.e.
    // byte = volts * (BlankingByte / BlackVoltage) = volts * 128. Those two
    // points are shared, to the byte, with every other producer's scale
    // (sync 0, blanking 64), so Apple II sync and blanking stay
    // spec-conformant with no fudge.
    //
    // The Apple II genuinely drives white hot: its measured 2.0V white is a
    // 1:3 sync-to-black vs black-to-white split, not spec's 1:2.5, so it
    // lands at byte 4*64 = 256 -> clamps to 255 (~120 IRE, ~20% over
    // reference white 224). That is faithful - a period TV would not have
    // hard-clipped it either (its AGC keys off sync, not white, so the
    // excursion passed at full amplitude); what compressed it was soft -
    // beam-current limiting, CRT saturation, the viewer's contrast knob.
    // NtscYiqDecoder's luma clamp downstream is a crude stand-in for that
    // soft top-end compression. For 1-bit white text this is invisible;
    // only bright artifact-colour pixels whose luma+chroma exceeds 224
    // actually shift.
    private const double BlankingByte = 64;

    // Solved directly from Gayler's two measured non-burst levels, not
    // assumed component tolerances: v_base(black) - Vbe = BlackVoltage and
    // v_base(white) - Vbe = WhiteVoltage, where v_base = EffectiveLogicHigh
    // * weight for whichever input is driven high. Comes out to a
    // physically sensible ~3.46V logic-high and ~0.625V Vbe - an
    // independent sanity check the model is right, not a curve-fit.
    private const double EffectiveLogicHigh = (WhiteVoltage - BlackVoltage) / WVideo;
    private const double TransistorVbe = EffectiveLogicHigh * WSync - BlackVoltage;

    // Targets the measured 0.7Vpp burst window (Gayler Fig. 4-4), added on
    // top of the digital-only baseline - which already lands exactly on
    // BlackVoltage during the burst window, since VIDEO DATA is blanked
    // and SYNC is high there. In the same volts units as everything else
    // here, so it rides through the volts->byte map unchanged: +/-0.35V
    // still lands ~0.7Vpp, i.e. +/-45 bytes about blanking.
    private const double BurstAmplitudeVolts = 0.35;

    // One byte sample per master tick; a fixed-capacity ring buffer sized to
    // one frame's worth of samples. Every line is 912 ticks, not just some
    // of them - verification found every line carries one long
    // (16-tick) PHASE0 cycle among its 65 (64*14+16=912), not just one line
    // in 65 as originally assumed; see "Sample rate" below.
    private const int CompositeVideoCapacity = 262 * 912;

    public readonly byte[] CompositeVideo = new byte[CompositeVideoCapacity];

    public int CompositeVideoWriteIndex { get; private set; }

    internal uint GetMasterTickCounterForTests() => _masterTickCounter;

    // Ticks elapsed since the last PHASE0 rising edge, i.e. since
    // TickVideo() last computed a fresh 14-tick cell (_videoDataBits' own
    // per-master-tick indexing - see that field's remarks). The once-per-
    // line "long cycle" stretch (one 16-tick cell among the line's 65)
    // adds 2 extra ticks that VideoDataBit below simply holds the last
    // tick's value through, rather than modelling exactly which tick
    // really gets stretched on real hardware - an accepted approximation.
    private int _ticksSincePhase0Edge;

    // Free-running master-tick counter for the burst sine's phase - never
    // reset per-scanline or per-frame, matching real hardware where the
    // subcarrier is just a fixed division of the one free-running crystal.
    // uint wraps every 2^32 ticks, itself a multiple of 4, so wraparound
    // doesn't disturb the phase sequence.
    private uint _masterTickCounter;

    // The digital VIDEO DATA bit for whichever master tick the clock is
    // currently within - the same per-tick array TickCompositeVideo uses
    // below to build vOut, exposed as a scope channel.
    public bool VideoDataBit => _videoDataBits[Math.Min(_ticksSincePhase0Edge, 13)];

    // The most recently written composite-video sample, i.e. this tick's
    // value - exposed as an Analog scope channel alongside the digital
    // sync/blanking rows.
    public byte CurrentCompositeVideoSample =>
        CompositeVideo[(CompositeVideoWriteIndex + CompositeVideoCapacity - 1) % CompositeVideoCapacity];

    private void TickCompositeVideo(bool phase0RisingEdge)
    {
        if (phase0RisingEdge)
        {
            _ticksSincePhase0Edge = 0;
        }
        else
        {
            _ticksSincePhase0Edge++;
        }

        var videoBit = VideoDataBit ? 1.0 : 0.0;
        var syncBit = SyncBit ? 1.0 : 0.0;

        var vBase = EffectiveLogicHigh * (WVideo * videoBit + WSync * syncBit);
        var vOut = Math.Max(0.0, vBase - TransistorVbe);

        if (ColorBurstGate)
        {
            // Only 4 samples/cycle are achievable at this sample rate (the
            // subcarrier is exactly master/4). That lands every sample
            // exactly on a zero-crossing or a peak (0, +1, 0, -1), not a
            // smooth curve - still a real sine's *shape*, and a genuine step up from a
            // flat square wave, which is what this replaces.
            //
            // The +2 (half a subcarrier cycle) is a real, load-bearing part
            // of the encoding, not cosmetic. Burst's job is to tell the
            // receiver where zero phase is, so which of the four master
            // ticks this sine starts on is what fixes every decoded hue on
            // screen - and _masterTickCounter is only a free-running
            // counter from power-on (see its own remarks), with no
            // hardware-derived alignment of its own to the VIDEO DATA
            // shift-register phase that actually carries the picture's
            // chroma. So the offset between the two has to be calibrated
            // against a known-correct landmark, exactly the way this file's
            // EffectiveLogicHigh/TransistorVbe are solved from Gayler's
            // measured levels rather than assumed. The landmark used is
            // Sather's worked example (Understanding the Apple II, p.8-15,
            // quoted in full in AppleIISystemTelevisionTests): $2A at even
            // addresses / $55 at odd addresses "produces a short green
            // line", and swapping the two produces violet. Without this
            // offset those two decode to each other's colors - the picture
            // stays perfectly self-consistent, just half a turn around the
            // hue circle from real hardware, which is precisely the failure
            // a burst phase is defined to prevent.
            var phase = 2.0 * Math.PI * ((_masterTickCounter + 2) % 4) / 4.0;
            vOut += BurstAmplitudeVolts * Math.Sin(phase);
        }

        // Anchored on Gayler's measured sync/blanking landmarks:
        // byte = volts * (BlankingByte / BlackVoltage) = volts * 128. White
        // legitimately overshoots byte 255 and clamps - see BlankingByte's
        // remarks.
        var sample = (byte)Math.Clamp(Math.Round(vOut * (BlankingByte / BlackVoltage)), 0, 255);

        CompositeVideo[CompositeVideoWriteIndex] = sample;
        CompositeVideoWriteIndex = (CompositeVideoWriteIndex + 1) % CompositeVideoCapacity;

        Television.Decode(sample);

        _masterTickCounter++;
    }
}
