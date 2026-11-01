using System;
using System.Collections.Generic;
using Aemula.Emulation.Peripherals;
using Aemula.Emulation.Peripherals.Cassette;

namespace Aemula.Emulation.Systems.BbcMicro;

// The 6850 ACIA (IC4) and the serial ULA (IC7) that connects it to the
// cassette port (SK5, a 7-pin DIN) or to RS423. IC42, a 74LS163 reloaded with
// 3 whenever it carries out, divides the 16MHz clock by 13 into the ULA's
// clock; IC50's fourth gate, an inverter, makes its load line from the carry.
// The cassette recorder's relay is driven from the ULA's motor bit.
public sealed partial class BbcMicroSystem
{
    // The tape's audio leads are sampled once every 16 ULA clocks, which is
    // 76.9kHz: 32 samples to a cycle of the 2400Hz tone.
    private const int SerialClocksPerTapeSample = 16;
    private const double TapeSampleRate = 16_000_000.0 / 13 / SerialClocksPerTapeSample;

    // The comparator that squares up the tape's audio switches with a little
    // hysteresis, so noise on a quiet recording does not make edges.
    private const float TapeHysteresis = 0.05f;

    private bool _serialClock;
    private int _tapeSampleCounter;
    private bool _tapeLevel;

    /// <summary>
    /// The cassette port's input lead: the tape's level for this sample. Set by
    /// the rig assembler to a connected deck's playback lead; unpatched,
    /// silence.
    /// </summary>
    public Func<float> CassetteInput { get; set; } = static () => 0f;

    /// <summary>
    /// The cassette port's output lead: the tone the serial ULA sends, as +1 or
    /// -1. Set by the rig assembler to a connected deck's record lead.
    /// </summary>
    public Action<float> CassetteOutput { get; set; } = static _ => { };

    /// <summary>
    /// Whether the cassette recorder's motor relay is closed. The relay is
    /// driven by bit 7 of the serial ULA's control register.
    /// </summary>
    public bool CassetteMotor => SerialUla.Motor;

    public override IReadOnlyList<PeripheralRequest> PeripheralRequests { get; }

    private IReadOnlyList<PeripheralRequest> BuildCassettePeripheralRequests() =>
    [
        PeripheralRequest.For<CassetteDeck>(
            context => new CassetteDeck(TapeSampleRate),
            deck =>
            {
                CassetteInput = deck.ReadPlayback;
                CassetteOutput = deck.WriteCapture;
                deck.MotorRelay = () => CassetteMotor;
            }),
    ];

    private void TickSerial()
    {
        // The load line is the carry through an inverter.
        _ic42.Load = !_ic42.Rco;
        _ic42.Clk = true;
        _ic42.Clk = false;

        var clock = _ic42.Qd;

        if (clock == _serialClock)
        {
            return;
        }

        _serialClock = clock;

        if (clock && ++_tapeSampleCounter == SerialClocksPerTapeSample)
        {
            _tapeSampleCounter = 0;
            SampleTape();
        }

        SerialUla.Clk = clock;
        PropagateSerial();
    }

    private void SampleTape()
    {
        var level = CassetteInput();

        if (level > TapeHysteresis)
        {
            _tapeLevel = true;
        }
        else if (level < -TapeHysteresis)
        {
            _tapeLevel = false;
        }

        SerialUla.CasIn = _tapeLevel;
        CassetteOutput(SerialUla.CasOut ? 1f : -1f);
    }

    // The nets between the two chips: the ULA's receive data, clocks and
    // modem lines go to the ACIA, and the ACIA's transmit data and RTS go back.
    private void PropagateSerial()
    {
        Acia.Rxd = SerialUla.Rxd;
        Acia.CtsBar = SerialUla.CtsBar;
        Acia.DcdBar = SerialUla.DcdBar;
        Acia.Rxc = SerialUla.Rxc;
        Acia.Txc = SerialUla.Txc;

        SerialUla.TxD = Acia.TxD;
        SerialUla.RtsBar = Acia.RtsBar;
    }
}
