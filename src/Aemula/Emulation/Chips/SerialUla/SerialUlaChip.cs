namespace Aemula.Emulation.Chips.SerialUla;

/// <summary>
/// The BBC Micro's serial ULA (IC7, Ferranti "SERPROC"). No die-level
/// description of it exists, so this is behavioural, following the register
/// description on the BBC Micro wiki and the observed behaviour that
/// <c>hoglet67/SerialULA</c>'s FPGA re-implementation reproduces. It sits
/// between the 6850 ACIA and the two serial connectors: the cassette port and
/// RS423.
///
/// A write-only control register at &amp;FE10 chooses the ACIA's receive and
/// transmit clock rates (the 1.2288MHz-ish input <see cref="Clk"/> divided
/// down to 64 times the baud rate, for the ACIA's divide-by-64 mode; bits 0-2
/// for transmit, bits 3-5 for receive), whether
/// the ACIA is connected to the cassette port or to RS423, and the cassette
/// motor relay.
///
/// <para>
/// Cassette transmit: the ACIA's TxD is turned into a tone, 2400Hz for a 1 bit
/// and 1200Hz for a 0 bit, made from the same counter that makes the baud
/// clocks, so that the tone always changes where a cycle begins and every
/// cycle starts with its rising edge. It idles at
/// 2400Hz, which is the leader tone.
/// </para>
///
/// <para>
/// Cassette receive: the squared tape signal is timed between rising edges. An
/// edge that comes soon after the last one is a 2400Hz cycle and makes RxD
/// high. When no edge has come for most of a 1200Hz cycle RxD goes low, which
/// is how a 0 bit reaches the ACIA; and when none has come for longer than any
/// legitimate bit, the line is idle and RxD returns high. DCD is asserted
/// once 2400Hz has been present for about a tenth of a second, longer than
/// any run of 1 bits in a data byte; the real figure is not documented. When
/// the leader tone ends it is dropped for a moment, which interrupts the OS
/// (its receive interrupt waits for the carrier to be lost, then for the
/// sync byte to arrive with it present again); after that it stays asserted
/// through the data until the signal disappears.
/// </para>
/// </summary>
public sealed class SerialUlaChip
{
    private const byte ControlRs423 = 0x40;
    private const byte ControlMotor = 0x80;

    // The input clock is 16MHz / 13, so a 1200 baud bit (one 1200Hz cycle, or
    // two of 2400Hz) is 1024 clocks, give or take 0.2%.
    private const int CounterMask = 0x3FF;

    // The time since the last rising edge that separates a 2400Hz cycle from
    // a 1200Hz one (0.75 of a bit), and the time after which no signal is
    // present at all (1.5 bits).
    private const int LongCycleClocks = 770;
    private const int IdleClocks = 1540;

    private const int CarrierDetectClocks = 1 << 17;

    // How long carrier detect is dropped at the end of a leader tone: long
    // enough for the ACIA's interrupt to be taken and its handler to read the
    // status register with the line low again.
    private const int CarrierLostPulseClocks = 256;

    // The divisors that turn the input clock into 64 times each of the baud
    // rates the control register's three bits choose from: 19200, 1200, 4800,
    // 150, 9600, 300, 2400 and 75.
    private static readonly int[] ClockDivisors = [1, 16, 4, 128, 2, 64, 8, 256];

    /// <summary>
    /// The data bus (D0-D7), latched by a write while <see cref="CS"/> is low.
    /// </summary>
    public byte Data { private get; set; }

    /// <summary>Chip select (CS, pin 9), active low.</summary>
    public bool CS { private get; set; } = true;

    private bool _e;

    /// <summary>
    /// The 1MHz bus clock (1MHZE). A selected write latches
    /// <see cref="Data"/> as it falls.
    /// </summary>
    public bool E
    {
        set
        {
            var fallingEdge = _e && !value;
            _e = value;

            if (fallingEdge && !CS)
            {
                Control = Data;
            }
        }
    }

    /// <summary>The control register, as last written.</summary>
    public byte Control { get; private set; }

    private bool _clk;

    /// <summary>
    /// The input clock (pin 25), 16MHz divided by 13. Everything advances on
    /// its rising edge.
    /// </summary>
    public bool Clk
    {
        set
        {
            var risingEdge = value && !_clk;
            _clk = value;

            if (risingEdge)
            {
                Advance();
            }
            else if (_clockFollowsInput)
            {
                // 19200 baud is the input clock itself.
                UpdateClocks();
            }
        }
    }

    /// <summary>
    /// The squared cassette input. The board's front end (a comparator and
    /// filter) is reduced to this logic level.
    /// </summary>
    public bool CasIn { private get; set; }

    /// <summary>The ACIA's transmit data (pin 22 to the ACIA's TxD).</summary>
    public bool TxD { private get; set; } = true;

    /// <summary>The ACIA's request to send, active low (the RS423 RTS output).</summary>
    public bool RtsBar { private get; set; } = true;

    /// <summary>The RS423 receive data input.</summary>
    public bool Rs423In { private get; set; } = true;

    /// <summary>Receive data to the ACIA.</summary>
    public bool Rxd { get; private set; } = true;

    /// <summary>Receive clock to the ACIA.</summary>
    public bool Rxc { get; private set; }

    /// <summary>Transmit clock to the ACIA.</summary>
    public bool Txc { get; private set; }

    /// <summary>Clear to send to the ACIA, active low.</summary>
    public bool CtsBar { get; private set; }

    /// <summary>Data carrier detect to the ACIA, active low.</summary>
    public bool DcdBar { get; private set; } = true;

    /// <summary>The cassette motor relay's drive.</summary>
    public bool Motor => (Control & ControlMotor) != 0;

    /// <summary>The tone sent to the cassette recorder's input.</summary>
    public bool CasOut { get; private set; }

    /// <summary>The RS423 transmit data output.</summary>
    public bool Rs423Out => TxD;

    private int _counter;
    private bool _clockFollowsInput;
    private bool _toneHigh = true;
    private bool _previousCasIn;
    private int _sinceEdge;
    private int _carrierClocks;
    private bool _carrierDetected;
    private bool _carrierLostPulseArmed;
    private int _carrierLostPulseClocks;

    private bool Rs423Selected => (Control & ControlRs423) != 0;

    private void Advance()
    {
        _counter = (_counter + 1) & CounterMask;

        UpdateClocks();

        if (_counter == 0)
        {
            _toneHigh = TxD;
        }

        CasOut = !Rs423Selected && (_toneHigh ? (_counter & 0x100) == 0 : (_counter & 0x200) == 0);

        if (Rs423Selected)
        {
            Rxd = Rs423In;
            DcdBar = true;
            CtsBar = true;
            return;
        }

        CtsBar = false;
        Demodulate();
    }

    private void UpdateClocks()
    {
        // Bits 0-2 pick the transmit rate and bits 3-5 the receive rate. The
        // cassette port has one rate for both directions (the OS always writes
        // &85 for tape and chooses 1200 or 300 baud with the ACIA's divider),
        // so there the receive clock is the transmit clock.
        var transmitDivisor = ClockDivisors[Control & 7];
        var receiveDivisor = Rs423Selected ? ClockDivisors[(Control >> 3) & 7] : transmitDivisor;
        _clockFollowsInput = receiveDivisor == 1 || transmitDivisor == 1;

        Rxc = receiveDivisor == 1 ? _clk : (_counter & (receiveDivisor >> 1)) == 0;
        Txc = transmitDivisor == 1 ? _clk : (_counter & (transmitDivisor >> 1)) == 0;
    }

    private void Demodulate()
    {
        if (_sinceEdge < IdleClocks)
        {
            _sinceEdge++;
        }

        if (CasIn && !_previousCasIn)
        {
            if (_sinceEdge < LongCycleClocks)
            {
                Rxd = true;
                _carrierClocks = System.Math.Min(_carrierClocks + _sinceEdge, CarrierDetectClocks);
            }
            else
            {
                _carrierClocks = 0;
            }

            _sinceEdge = 0;
        }
        else if (_sinceEdge == LongCycleClocks)
        {
            Rxd = false;
            _carrierClocks = 0;

            if (_carrierLostPulseArmed)
            {
                _carrierLostPulseArmed = false;
                _carrierLostPulseClocks = CarrierLostPulseClocks;
            }
        }
        else if (_sinceEdge == IdleClocks)
        {
            Rxd = true;
            _carrierDetected = false;
        }

        if (_carrierClocks >= CarrierDetectClocks)
        {
            _carrierDetected = true;
            _carrierLostPulseArmed = true;
        }

        if (_carrierLostPulseClocks > 0)
        {
            _carrierLostPulseClocks--;
        }

        _previousCasIn = CasIn;
        DcdBar = !_carrierDetected || _carrierLostPulseClocks > 0;
    }
}
