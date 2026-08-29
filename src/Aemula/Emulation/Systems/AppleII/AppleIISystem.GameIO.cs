using System;
using Aemula.Emulation.Chips;

namespace Aemula.Emulation.Systems.AppleII;

// The $C030 speaker toggle, the $C058-$C05F annunciators, and the
// $C061-$C067 game-connector reads - the three pushbuttons and the four
// paddle one-shots retriggered by $C070. Modelled from Jim Sather's
// "Understanding the Apple II" chapter 7 ("Address Decoding and Input/
// Output", Table 7.1 and the "Annunciator Outputs" / "Game I/O Connector"
// sections) and "The Apple II Circuit Description".
public sealed partial class AppleIISystem
{
    // $C030: the speaker toggle. A 74LS74 flip-flop with D wired to its own
    // Q', so every access to the $C03X range complements it; Q drives the
    // speaker through a transistor.
    private readonly Ttl7474Chip _speakerFlipFlop;

    // $C061-$C067 read: the 74LS251 (H14) muxing the game connector's three
    // pushbuttons and four paddle one-shot outputs (plus the unmodelled
    // $C060 cassette input) onto data bit 7, addressed by CPU A0-A2.
    private readonly Ttl74251Chip _gameInputMux;

    // $C064-$C067: the four paddle one-shots. On the real board these are a
    // single NE558 quad timer; the plan's chip inventory abstracts the
    // board's timers as 555s, so they're four independent Ne555 one-shots
    // here. Every $C070 strobe retriggers all four at once.
    private readonly Ne555Chip[] _paddleTimers;

    // Position of each paddle, 0-255, standing in for the pot wiper's
    // resistance. Defaults to centre - a game-controller axis that's
    // present but idle.
    private readonly byte[] _paddlePositions;

    // The three game-connector pushbuttons (PB0-PB2 at $C061-$C063);
    // true = pressed = data bit 7 high.
    private readonly bool[] _pushButtons;

    // PREAD (Autostart Monitor, $FB1E) reads a paddle by strobing $C070 and
    // then polling PADDLn in an 11-cycle loop until the one-shot output
    // clears, returning the loop count (0-255). One count is therefore
    // ~11 CPU cycles, and a CPU cycle averages 912/65 ~= 14.03 master ticks
    // (64 short cycles of 14 plus one long cycle of 16 per scan line), so
    // ~154 master ticks. The real 558's RC network (0.022uF, 100ohm + a
    // 0-150kohm pot) runs a little slower than that per count near full
    // scale, which is why period hardware shipped trim-pots; we scale the
    // one-shot straight to PREAD's loop instead, so SetPaddlePosition(n)
    // reads back as n across the whole 0-255 range.
    private const uint PaddleOneShotTicksPerCount = 154;

    // The pot's fixed 100ohm series resistor still gives a short non-zero
    // on-time at position 0 (t = 1.1 * 100 * 0.022uF ~= 2.4us ~= 34 ticks).
    private const uint PaddleOneShotFloorTicks = 34;

    // Q of the speaker flip-flop, flipped on every $C03X access. A host
    // audio backend would band-limit and resample this; no audio path is
    // wired up yet (as with Television before it), so for now it's an
    // observable signal only.
    public bool SpeakerBit => _speakerFlipFlop.Q1;

    // The four annunciator outputs (AN0-AN3), latched by $C058-$C05F in the
    // same 74LS259 as the screen-mode switches - see AppleIISystem.Video.cs.
    public bool Annunciator0 => _modeSwitchLatch.Q4;
    public bool Annunciator1 => _modeSwitchLatch.Q5;
    public bool Annunciator2 => _modeSwitchLatch.Q6;
    public bool Annunciator3 => _modeSwitchLatch.Q7;

    /// <summary>
    /// Sets the position of one of the four paddles (index 0-3), 0-255,
    /// where 0 is the fully counter-clockwise stop.
    /// </summary>
    public void SetPaddlePosition(int index, byte position)
    {
        if ((uint)index >= (uint)_paddlePositions.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "Apple II game I/O has four paddle inputs (0-3).");
        }

        _paddlePositions[index] = position;
    }

    /// <summary>
    /// Sets whether one of the three game-connector pushbuttons (index 0-2)
    /// is held down.
    /// </summary>
    public void SetPushButton(int index, bool pressed)
    {
        if ((uint)index >= (uint)_pushButtons.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "Apple II game I/O has three pushbuttons (0-2).");
        }

        _pushButtons[index] = pressed;
    }

    private void TickGameIo()
    {
        foreach (var timer in _paddleTimers)
        {
            timer.Tick();
        }
    }

    private void ToggleSpeaker()
    {
        _speakerFlipFlop.D1 = _speakerFlipFlop.Qn1;
        _speakerFlipFlop.Clk1 = false;
        _speakerFlipFlop.Clk1 = true;
    }

    private void TriggerPaddleTimers()
    {
        // The RC time constant is fixed at the instant the one-shot fires,
        // so PulseTicks is (re)computed from the current paddle position on
        // every strobe.
        for (var i = 0; i < _paddleTimers.Length; i++)
        {
            _paddleTimers[i].PulseTicks =
                PaddleOneShotFloorTicks + (uint)_paddlePositions[i] * PaddleOneShotTicksPerCount;
            _paddleTimers[i].TriggerBar = true;
            _paddleTimers[i].TriggerBar = false;
        }
    }

    private byte ReadGameInputMux(ushort address)
    {
        _gameInputMux.D0 = false;                 // $C060 cassette in - not modelled
        _gameInputMux.D1 = _pushButtons[0];       // $C061 PB0
        _gameInputMux.D2 = _pushButtons[1];       // $C062 PB1
        _gameInputMux.D3 = _pushButtons[2];       // $C063 PB2
        _gameInputMux.D4 = _paddleTimers[0].Out;  // $C064 PADDL0
        _gameInputMux.D5 = _paddleTimers[1].Out;  // $C065 PADDL1
        _gameInputMux.D6 = _paddleTimers[2].Out;  // $C066 PADDL2
        _gameInputMux.D7 = _paddleTimers[3].Out;  // $C067 PADDL3
        _gameInputMux.A = (address & 0x1) != 0;
        _gameInputMux.B = (address & 0x2) != 0;
        _gameInputMux.C = (address & 0x4) != 0;
        _gameInputMux.S = _ioControlDecoder.Y6;   // active-low strobe

        // Bit 7 is the selected input; bits 0-6 have no bus driver for this
        // read, returned as open-bus ones to match the rest of this file.
        return (byte)((_gameInputMux.Y == true ? 0x80 : 0x00) | 0x7F);
    }
}
