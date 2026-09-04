namespace Aemula.Emulation.Chips;

/// <summary>
/// Dual retriggerable monostable multivibrator with clear. Each channel is a
/// one-shot: a valid trigger edge drives <see cref="Q1"/>/<see cref="Q2"/>
/// high for <see cref="PulseTicks1"/>/<see cref="PulseTicks2"/> ticks, then
/// low again. Retriggering while already high restarts the pulse from zero -
/// same modelling choice as <see cref="Ne555Chip"/>, and for the same
/// reason: the real device instead extends the pulse smoothly (it's timed by
/// an external RC network, not a digital counter), which isn't
/// representable at tick granularity anyway.
///
/// Per the datasheet's function table, a channel triggers on: a high-to-low
/// edge on <see cref="ABar1"/> while <see cref="B1"/> and <see cref="Clr1"/>
/// are both high, a low-to-high edge on <see cref="B1"/> while
/// <see cref="ABar1"/> is low and <see cref="Clr1"/> is high, or a
/// low-to-high edge on <see cref="Clr1"/> itself while <see cref="ABar1"/>
/// is low and <see cref="B1"/> is high - the last of these lets a channel
/// held cleared start timing the instant it's released. Holding
/// <see cref="Clr1"/> low forces <see cref="Q1"/> low immediately and
/// abandons any pulse in progress.
/// </summary>
public sealed class Ttl74123Chip
{
    private bool _aBar1 = true;
    public bool ABar1
    {
        set
        {
            var fallingEdge = _aBar1 && !value;
            _aBar1 = value;

            if (fallingEdge && _b1 && _clr1)
            {
                Trigger1();
            }
        }
    }

    private bool _b1;
    public bool B1
    {
        set
        {
            var risingEdge = !_b1 && value;
            _b1 = value;

            if (risingEdge && !_aBar1 && _clr1)
            {
                Trigger1();
            }
        }
    }

    private bool _clr1 = true;
    public bool Clr1
    {
        set
        {
            var risingEdge = !_clr1 && value;
            _clr1 = value;

            if (!value)
            {
                _elapsedTicks1 = 0;
                Q1 = false;
                Qn1 = true;
                return;
            }

            if (risingEdge && !_aBar1 && _b1)
            {
                Trigger1();
            }
        }
    }

    /// <summary>
    /// The output on-time for channel 1, in <see cref="Tick"/> calls.
    /// </summary>
    public uint PulseTicks1 { private get; set; }

    public bool Q1 { get; private set; }
    public bool Qn1 { get; private set; } = true;

    private uint _elapsedTicks1;

    private void Trigger1()
    {
        _elapsedTicks1 = 0;
        Q1 = true;
        Qn1 = false;
    }

    private bool _aBar2 = true;
    public bool ABar2
    {
        set
        {
            var fallingEdge = _aBar2 && !value;
            _aBar2 = value;

            if (fallingEdge && _b2 && _clr2)
            {
                Trigger2();
            }
        }
    }

    private bool _b2;
    public bool B2
    {
        set
        {
            var risingEdge = !_b2 && value;
            _b2 = value;

            if (risingEdge && !_aBar2 && _clr2)
            {
                Trigger2();
            }
        }
    }

    private bool _clr2 = true;
    public bool Clr2
    {
        set
        {
            var risingEdge = !_clr2 && value;
            _clr2 = value;

            if (!value)
            {
                _elapsedTicks2 = 0;
                Q2 = false;
                Qn2 = true;
                return;
            }

            if (risingEdge && !_aBar2 && _b2)
            {
                Trigger2();
            }
        }
    }

    /// <summary>
    /// The output on-time for channel 2, in <see cref="Tick"/> calls.
    /// </summary>
    public uint PulseTicks2 { private get; set; }

    public bool Q2 { get; private set; }
    public bool Qn2 { get; private set; } = true;

    private uint _elapsedTicks2;

    private void Trigger2()
    {
        _elapsedTicks2 = 0;
        Q2 = true;
        Qn2 = false;
    }

    /// <summary>
    /// Advances both one-shots by one time step. No effect on a channel
    /// unless its <c>Q</c> output is currently high.
    /// </summary>
    public void Tick()
    {
        if (Q1)
        {
            _elapsedTicks1++;

            if (_elapsedTicks1 >= PulseTicks1)
            {
                Q1 = false;
                Qn1 = true;
            }
        }

        if (Q2)
        {
            _elapsedTicks2++;

            if (_elapsedTicks2 >= PulseTicks2)
            {
                Q2 = false;
                Qn2 = true;
            }
        }
    }
}
