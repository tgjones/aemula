namespace Aemula.Emulation.Chips.Tia;

/// <summary>
/// One of the TIA's two identical audio channels (AUDC/AUDF/AUDV -> AUDx pin).
///
/// This is a direct port of the state machine in Stella's
/// <c>src/emucore/tia/AudioChannel.cxx</c>, which is itself a clean-room model
/// of the real gate-level circuit reverse-engineered by Eric Ball / Andrew
/// Towers (see the "Audio" section of Towers' <c>TIA_HW_Notes.txt</c>). Ron
/// Fries' classic <c>TIASound.c</c> AUDC mode table was used to cross-check
/// the results: AUDC 0 and 11 hold the output constant (silent), AUDC 4 and 5
/// are a pure divide-by-two tone, and the three polynomial counters have
/// periods 15 (4-bit), 31 (5-bit) and 511 (9-bit "white noise").
///
/// The circuit is naturally two half-cycles - Stella's <c>phase0</c> (sample
/// the counters, decide the feedback bits, advance the frequency divider) and
/// <c>phase1</c> (shift the polynomial counters). They are kept as two private
/// methods and run back-to-back from <see cref="Tick"/>; the channels are
/// independent, so running one channel's phase0+phase1 as a pair matches
/// Stella running phase0 for both channels and then phase1 for both.
///
/// A mutable value type with no allocation: <see cref="TiaChip"/> holds two of
/// these as fields and ticks them twice per scanline off the horizontal
/// counter.
/// </summary>
internal struct TiaAudioChannel
{
    /// <summary>AUDC (0x15/0x16) D0-D3 - waveform / polynomial-tap select.</summary>
    public byte Audc;

    /// <summary>AUDF (0x17/0x18) D0-D4 - frequency divisor; the divider
    /// reloads on reaching this value, so the channel clocks once every
    /// <c>Audf + 1</c> audio ticks.</summary>
    public byte Audf;

    /// <summary>AUDV (0x19/0x1A) D0-D3 - 4-bit output volume.</summary>
    public byte Audv;

    // Frequency divider. Counts up each audio tick and is zeroed when it
    // reaches Audf (or 0x1f as a safety net if Audf was lowered under it).
    private byte _divCounter;

    // 4-bit pulse counter - the shared shift register that produces the pure
    // tone, the 4-bit poly and, combined with the noise counter, every other
    // waveform. Its low bit is the channel output.
    private byte _pulseCounter;

    // 5-bit noise shift register - the 4-bit and 5-bit polys, and (with the
    // extra feedback term in AUDC 8) the 9-bit "white noise" sequence.
    private byte _noiseCounter;

    // Set in phase0 when the frequency divider underflows this tick; gates the
    // shift-register updates in phase1. Named to match Stella.
    private bool _clockEnable;

    private bool _noiseFeedback;
    private bool _noiseCounterBit4;
    private bool _pulseCounterHold;

    /// <summary>
    /// The 1-bit waveform output - the low bit of the pulse counter - which
    /// drives the AUDx pin.
    /// </summary>
    public readonly bool Output => (_pulseCounter & 0x01) != 0;

    /// <summary>
    /// The volume-scaled numeric sample (<see cref="Audv"/> when the waveform
    /// bit is set, otherwise 0), for a future mixer that sums both channels.
    /// The pin itself stays the 1-bit <see cref="Output"/> signal.
    /// </summary>
    public readonly byte Sample => Output ? Audv : (byte)0;

    /// <summary>
    /// One audio-clock tick: the two circuit half-cycles run back-to-back.
    /// Called twice per scanline (~31.4 kHz on NTSC).
    /// </summary>
    public void Tick()
    {
        Phase0();
        Phase1();
    }

    // Sample the counters, work out the feedback bits for this tick, then
    // advance the frequency divider. The counter sampling is guarded by the
    // PREVIOUS tick's _clockEnable; _clockEnable is then recomputed for phase1.
    private void Phase0()
    {
        if (_clockEnable)
        {
            _noiseCounterBit4 = (_noiseCounter & 0x01) != 0;

            switch (Audc & 0x03)
            {
                case 0x00:
                case 0x01:
                    _pulseCounterHold = false;
                    break;

                case 0x02:
                    _pulseCounterHold = (_noiseCounter & 0x1e) != 0x02;
                    break;

                case 0x03:
                    _pulseCounterHold = !_noiseCounterBit4;
                    break;
            }

            if ((Audc & 0x03) == 0x00)
            {
                // AUDC low bits 00: the noise register is fed a mix of the two
                // counters, plus a term that forces a constant stream when the
                // high AUDC bits are also 0 - this is what makes AUDC 0 silent.
                _noiseFeedback =
                    ((_pulseCounter ^ _noiseCounter) & 0x01) != 0 ||
                    (_noiseCounter == 0 && _pulseCounter == 0x0a) ||
                    (Audc & 0x0c) == 0;
            }
            else
            {
                // Otherwise the noise register is a plain LFSR: tap bit 2 xor
                // bit 0, self-correcting out of the all-zero lock-up state.
                _noiseFeedback =
                    (((_noiseCounter & 0x04) != 0 ? 1 : 0) ^ (_noiseCounter & 0x01)) != 0 ||
                    _noiseCounter == 0;
            }
        }

        _clockEnable = _divCounter == Audf;

        if (_divCounter == Audf || _divCounter == 0x1f)
        {
            _divCounter = 0;
        }
        else
        {
            _divCounter++;
        }
    }

    // Shift the polynomial counters, when the divider underflowed this tick.
    private void Phase1()
    {
        if (!_clockEnable)
        {
            return;
        }

        // AUDC high bits pick how the pulse counter is fed back: 00 = 4-bit
        // poly (gated off entirely when AUDC low bits are 0, i.e. pure "set to
        // 1"), 01 = divide-by-two pure tone, 10 = follow the noise counter,
        // 11 = divide-by-six (5-bit-style tap on the pulse counter itself).
        var pulseFeedback = false;
        switch (Audc >> 2)
        {
            case 0x00:
                pulseFeedback =
                    (((_pulseCounter & 0x02) != 0 ? 1 : 0) ^ (_pulseCounter & 0x01)) != 0 &&
                    _pulseCounter != 0x0a &&
                    (Audc & 0x03) != 0;
                break;

            case 0x01:
                pulseFeedback = (_pulseCounter & 0x08) == 0;
                break;

            case 0x02:
                pulseFeedback = !_noiseCounterBit4;
                break;

            case 0x03:
                pulseFeedback = !((_pulseCounter & 0x02) != 0 || (_pulseCounter & 0x0e) == 0);
                break;
        }

        _noiseCounter >>= 1;
        if (_noiseFeedback)
        {
            _noiseCounter |= 0x10;
        }

        if (!_pulseCounterHold)
        {
            _pulseCounter = (byte)(~(_pulseCounter >> 1) & 0x07);
            if (pulseFeedback)
            {
                _pulseCounter |= 0x08;
            }
        }
    }
}
