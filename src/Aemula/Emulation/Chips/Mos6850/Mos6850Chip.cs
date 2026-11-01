namespace Aemula.Emulation.Chips.Mos6850;

/// <summary>
/// 6850 ACIA (Asynchronous Communications Interface Adapter), the serial
/// interface chip of the 6800 family. It turns bytes written by the CPU into
/// asynchronous frames on <see cref="TxD"/> and frames arriving on
/// <see cref="Rxd"/> into bytes, with its bit rate taken from the external
/// <see cref="Txc"/> and <see cref="Rxc"/> clocks divided by 1, 16 or 64.
///
/// Bus timing: a selected read presents its data on <see cref="DB"/> when
/// <see cref="E"/> rises and its side effects (reading the data register
/// frees it for the next character) happen as E falls; a selected write
/// latches <see cref="DB"/> as E falls.
///
/// The transmitter's divider and the receiver's start-bit hunt free-run from
/// master reset. A character written to the transmit data register is moved
/// to the shift register at the next bit boundary of the transmit clock (the
/// start bit begins there), which frees the register for the next write
/// while the frame is still going out. The receiver notices a start bit on
/// a rising edge of the receive clock, checks that it is still low half a
/// bit later and samples every bit in its middle.
/// </summary>
public sealed class Mos6850Chip
{
    private const byte StatusRdrf = 0x01;
    private const byte StatusTdre = 0x02;
    private const byte StatusDcd = 0x04;
    private const byte StatusCts = 0x08;
    private const byte StatusFramingError = 0x10;
    private const byte StatusOverrun = 0x20;
    private const byte StatusParityError = 0x40;
    private const byte StatusIrq = 0x80;

    private const byte ControlDivideMask = 0x03;
    private const byte ControlMasterReset = 0x03;
    private const byte ControlTransmitMask = 0x60;
    private const byte ControlReceiveInterrupt = 0x80;

    /// <summary>Data bus pins (D0-D7).</summary>
    public byte DB { get; set; }

    /// <summary>
    /// Register select (RS): false for the status (read) and control (write)
    /// registers, true for the receive (read) and transmit (write) data
    /// registers.
    /// </summary>
    public bool RS { private get; set; }

    /// <summary>Read/write pin (input). Read = true, write = false.</summary>
    public bool RW { private get; set; }

    /// <summary>Chip select 0 (input), active high.</summary>
    public bool Cs0 { private get; set; }

    /// <summary>Chip select 1 (input), active high.</summary>
    public bool Cs1 { private get; set; }

    /// <summary>Chip select 2 (input), active low.</summary>
    public bool Cs2Bar { private get; set; } = true;

    private bool _e;
    private bool _accessSelected;
    private bool _accessRead;
    private bool _accessData;

    /// <summary>The enable clock (E, input); bus accesses happen on its edges.</summary>
    public bool E
    {
        set
        {
            if (value == _e)
            {
                return;
            }

            _e = value;

            if (value)
            {
                BeginAccess();
            }
            else
            {
                EndAccess();
            }
        }
    }

    /// <summary>
    /// Interrupt request (output), active low, open drain on real hardware.
    /// </summary>
    public bool Irq { get; private set; } = true;

    private bool _ctsBar;

    /// <summary>
    /// Clear to send (input), active low. While it is high the transmit data
    /// register empty flag is held low.
    /// </summary>
    public bool CtsBar
    {
        set
        {
            _ctsBar = value;
            UpdateIrq();
        }
    }

    private bool _dcdBar = true;

    /// <summary>
    /// Data carrier detect (input), active low. A rise (carrier lost) sets
    /// the status flag and, with receive interrupts enabled, interrupts.
    /// </summary>
    public bool DcdBar
    {
        set
        {
            if (value && !_dcdBar)
            {
                _dcdFlag = true;
                UpdateIrq();
            }

            _dcdBar = value;
        }
    }

    /// <summary>Receive data (input).</summary>
    public bool Rxd { private get; set; } = true;

    /// <summary>
    /// Request to send (output), active low. Controlled by bits 5-6 of the
    /// control register.
    /// </summary>
    public bool RtsBar { get; private set; } = true;

    /// <summary>Transmit data (output). Idles high.</summary>
    public bool TxD { get; private set; } = true;

    private bool _txc;

    /// <summary>
    /// The transmit clock (input). The transmitter advances on its falling
    /// edges.
    /// </summary>
    public bool Txc
    {
        set
        {
            var fallingEdge = _txc && !value;
            _txc = value;

            if (fallingEdge)
            {
                TransmitClock();
            }
        }
    }

    private bool _rxc;

    /// <summary>
    /// The receive clock (input). The receiver samples on its rising edges.
    /// </summary>
    public bool Rxc
    {
        set
        {
            var risingEdge = value && !_rxc;
            _rxc = value;

            if (risingEdge)
            {
                ReceiveClock();
            }
        }
    }

    private byte _control;
    private byte _statusFlags;
    private bool _dcdFlag;
    private bool _statusRead;

    private byte _receiveData;
    private byte _transmitData;
    private bool _transmitEmpty = true;

    // The transmitter: the frame being sent, least significant bit first,
    // and how far through it the shifter is. _txBitsLeft is zero while idle.
    private int _txFrame;
    private int _txBitsLeft;
    private int _txDivider;

    // The receiver: 0 while hunting for a start bit, otherwise the count of
    // receive clocks since the start bit was seen.
    private bool _rxReceiving;
    private int _rxClocks;
    private int _rxBitsTaken;
    private int _rxFrame;
    private int _rxNextSample;

    public Mos6850Chip()
    {
        MasterReset();
    }

    private int DivideRatio => (_control & ControlDivideMask) switch
    {
        0 => 1,
        1 => 16,
        _ => 64,
    };

    // Control register bits 2-4: data bits, parity (0 none, 1 even, 2 odd)
    // and stop bits.
    private (int DataBits, int Parity, int StopBits) WordFormat => ((_control >> 2) & 7) switch
    {
        0 => (7, 1, 2),
        1 => (7, 2, 2),
        2 => (7, 1, 1),
        3 => (7, 2, 1),
        4 => (8, 0, 2),
        5 => (8, 0, 1),
        6 => (8, 1, 1),
        _ => (8, 2, 1),
    };

    private bool TransmitInterruptEnabled => (_control & ControlTransmitMask) == 0x20;

    private bool TransmitEmptyStatus => _transmitEmpty && !_ctsBar;

    private void MasterReset()
    {
        _statusFlags = 0;
        _dcdFlag = false;
        _statusRead = false;
        _transmitEmpty = true;
        _txBitsLeft = 0;
        _txDivider = 0;
        _rxReceiving = false;
        _control &= unchecked((byte)~ControlDivideMask);
        _control |= ControlMasterReset;

        RtsBar = true;
        TxD = true;
        UpdateIrq();
    }

    private void BeginAccess()
    {
        _accessSelected = Cs0 && Cs1 && !Cs2Bar;
        _accessRead = RW;
        _accessData = RS;

        if (!_accessSelected || !_accessRead)
        {
            return;
        }

        DB = _accessData ? _receiveData : ReadStatus();
    }

    private byte ReadStatus()
    {
        var status = (byte)(_statusFlags & (StatusRdrf | StatusFramingError | StatusOverrun | StatusParityError));

        if (TransmitEmptyStatus)
        {
            status |= StatusTdre;
        }

        if (_dcdBar || _dcdFlag)
        {
            status |= StatusDcd;
        }

        if (_ctsBar)
        {
            status |= StatusCts;
        }

        if (!Irq)
        {
            status |= StatusIrq;
        }

        return status;
    }

    private void EndAccess()
    {
        if (!_accessSelected)
        {
            return;
        }

        _accessSelected = false;

        if (_accessRead)
        {
            if (!_accessData)
            {
                _statusRead = true;
                return;
            }

            // Reading the data register frees it. If the status was read
            // first, that also clears the overrun and, once the carrier is
            // back, the carrier-lost flag.
            _statusFlags &= unchecked((byte)~(StatusRdrf | StatusFramingError | StatusParityError));

            if (_statusRead)
            {
                _statusFlags &= unchecked((byte)~StatusOverrun);

                if (!_dcdBar)
                {
                    _dcdFlag = false;
                }
            }

            _statusRead = false;
            UpdateIrq();
            return;
        }

        if (_accessData)
        {
            _transmitData = DB;
            _transmitEmpty = false;
            UpdateIrq();
            return;
        }

        WriteControl(DB);
    }

    private void WriteControl(byte value)
    {
        _control = value;

        if ((value & ControlDivideMask) == ControlMasterReset)
        {
            MasterReset();
            return;
        }

        RtsBar = (value & ControlTransmitMask) == 0x40;
        UpdateIrq();
    }

    private void UpdateIrq()
    {
        var receive = (_control & ControlReceiveInterrupt) != 0
            && ((_statusFlags & (StatusRdrf | StatusOverrun)) != 0 || _dcdFlag);
        var transmit = TransmitInterruptEnabled && TransmitEmptyStatus;

        Irq = !(receive || transmit);
    }

    private void TransmitClock()
    {
        if (++_txDivider < DivideRatio)
        {
            return;
        }

        _txDivider = 0;

        if (_txBitsLeft == 0)
        {
            if (_transmitEmpty)
            {
                TxD = (_control & ControlTransmitMask) != 0x60;
                return;
            }

            StartFrame();
        }

        TxD = (_txFrame & 1) != 0;
        _txFrame >>= 1;
        _txBitsLeft--;

        if (_txBitsLeft == 0 && _transmitEmpty)
        {
            // The stop bit has gone out; the line stays high until the next
            // write.
            _txFrame = 1;
        }
    }

    private void StartFrame()
    {
        var (dataBits, parity, stopBits) = WordFormat;

        var data = _transmitData & ((1 << dataBits) - 1);
        var frame = data << 1;
        var length = 1 + dataBits;

        if (parity != 0)
        {
            var ones = System.Numerics.BitOperations.PopCount((uint)data);
            var parityBit = parity == 1 ? ones & 1 : (ones & 1) ^ 1;
            frame |= parityBit << length;
            length++;
        }

        for (var stop = 0; stop < stopBits; stop++)
        {
            frame |= 1 << length;
            length++;
        }

        _txFrame = frame;
        _txBitsLeft = length;
        _transmitEmpty = true;
        UpdateIrq();
    }

    private void ReceiveClock()
    {
        var ratio = DivideRatio;

        if (!_rxReceiving)
        {
            if (!Rxd)
            {
                _rxReceiving = true;
                _rxClocks = 0;
                _rxBitsTaken = 0;
                _rxFrame = 0;
                _rxNextSample = ratio / 2 > 0 ? ratio / 2 : 1;
            }

            return;
        }

        _rxClocks++;

        if (_rxClocks != _rxNextSample)
        {
            return;
        }

        var half = ratio / 2 > 0 ? ratio / 2 : 1;

        if (_rxBitsTaken == 0)
        {
            // Half a bit after the falling edge the start bit must still be
            // low, or it was a glitch.
            if (Rxd)
            {
                _rxReceiving = false;
                return;
            }

            _rxBitsTaken = 1;
            _rxNextSample = half + ratio;
            return;
        }

        _rxNextSample += ratio;

        var (dataBits, parity, _) = WordFormat;
        var bitIndex = _rxBitsTaken - 1;
        _rxBitsTaken++;

        if (bitIndex < dataBits + (parity != 0 ? 1 : 0))
        {
            _rxFrame |= (Rxd ? 1 : 0) << bitIndex;
            return;
        }

        // The first stop bit ends the character; the receiver is ready to
        // hunt for the next start bit straight away.
        _rxReceiving = false;
        FinishCharacter(Rxd);
    }

    private void FinishCharacter(bool stopBit)
    {
        var (dataBits, parity, _) = WordFormat;
        var data = _rxFrame & ((1 << dataBits) - 1);

        var parityError = false;

        if (parity != 0)
        {
            var ones = System.Numerics.BitOperations.PopCount((uint)data) + ((_rxFrame >> dataBits) & 1);
            parityError = parity == 1 ? (ones & 1) != 0 : (ones & 1) == 0;
        }

        if ((_statusFlags & StatusRdrf) != 0)
        {
            _statusFlags |= StatusOverrun;
        }
        else
        {
            _receiveData = (byte)data;
            _statusFlags |= StatusRdrf;
            _statusFlags = (byte)(stopBit ? _statusFlags & ~StatusFramingError : _statusFlags | StatusFramingError);
            _statusFlags = (byte)(parityError ? _statusFlags | StatusParityError : _statusFlags & ~StatusParityError);
        }

        UpdateIrq();
    }
}
