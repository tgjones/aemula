using System;
using System.Collections.Generic;
using Aemula.Emulation.Chips;
using Aemula.Emulation.Peripherals;
using Aemula.Emulation.Peripherals.Cassette;

namespace Aemula.Emulation.Systems.ZX80;

// The board's two audio jacks, EAR (tape playback in) and MIC (tape-out),
// wired straight to the motherboard - unlike the Apple I's optional ACI
// card, there's no expansion connector in between, so PeripheralRequests
// declares the deck directly (see EmulatedSystem's own doc on that
// property: "motherboard ports... declared here directly").
//
// IC10 (74LS365): five of its six buffers mediate the keyboard matrix's
// columns onto D0-D4, which ZX80System.Keyboard.cs already models
// behaviorally to the same net effect - see that file for why. The sixth is
// EAR: A1 (IC10.2) picks up EAR, AC-coupled through C12/R29 and DC-biased
// low by R1; Y1 (IC10.3) drives it onto the real D7 line whenever IC10 is
// enabled. There's no comparator chip on this board the way the Apple I's
// ACI has an LM311 - the AC coupling plus the 74LS365 input's own TTL
// threshold is the entire "squaring" circuit, so thresholding the incoming
// analog level at zero is a faithful model, not a shortcut.
public sealed partial class ZX80System
{
    private readonly Ttl74365Chip _cassetteBuffer;

    /// <summary>
    /// The board's EAR jack: the interpolated tape level for this tick.
    /// Set by the rig assembler to a connected deck's playback lead;
    /// unpatched, returns silence.
    /// </summary>
    public Func<float> CassetteInput { get; set; } = static () => 0f;

    /// <summary>
    /// The board's MIC jack: this tick's tape-out level, taken straight off
    /// the SYNC node the way R35/C14 tap it in real hardware (see
    /// TickCassette) - the low-pass filtering that real RC network (and the
    /// R34/C13 load beyond it) does is this model's analog boundary, the
    /// same convention TickCompositeVideo draws around VIDEO/SYNC on their
    /// way into Television.Decode.
    /// </summary>
    public Action<float> CassetteOutput { get; set; } = static _ => { };

    public override IReadOnlyList<PeripheralRequest> PeripheralRequests { get; }

    private IReadOnlyList<PeripheralRequest> BuildCassettePeripheralRequests() =>
    [
        PeripheralRequest.For<CassetteDeck>(
            context => new CassetteDeck(OscillatorEdgesPerSecond),
            deck =>
            {
                CassetteInput = deck.ReadPlayback;
                CassetteOutput = deck.WriteCapture;
            }),
    ];

    private void TickCassette()
    {
        // EAR runs continuously off the tape input, the same way the Apple
        // I's LM311 does - sampled every tick regardless of whether this
        // instant is actually a keyboard-style read, so the buffer's A1 is
        // already settled by the time DoCpuMemoryAccess's ReadKeyboardMatrix
        // call reads Y1 back out. G1/G2 (tied together on the real board)
        // are /KBD, i.e. exactly GetKbdSignal()'s own level. D11 (the NTSC
        // strap diode) also lands on this same enable net, but isn't wired
        // in here - that's the NTSC 50/60Hz strap's own concern, separate
        // from the cassette jacks.
        var kbd = GetKbdSignal();
        _cassetteBuffer.G1 = kbd;
        _cassetteBuffer.G2 = kbd;
        _cassetteBuffer.A1 = CassetteInput() > 0f;

        CassetteOutput(_ic19.Q1 ? 1f : 0f);
    }
}
