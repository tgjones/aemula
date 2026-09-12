namespace Aemula.Emulation.Chips;

/// <summary>
/// Octal transparent latch with tri-state outputs. While <see cref="Le"/>
/// is high, each output follows its <c>D</c> input directly; the latch
/// freezes whatever value was present the instant <see cref="Le"/> goes
/// low, and re-synchronizes to the live <c>D</c> inputs the instant it goes
/// high again.
/// </summary>
public sealed class Ttl74373Chip
{
    /// <summary>
    /// Output enable, active low. High makes every output high-impedance.
    /// </summary>
    public bool Oe { private get; set; }

    private bool _le;
    public bool Le
    {
        private get => _le;
        set
        {
            var risingEdge = value && !_le;
            _le = value;

            if (risingEdge)
            {
                _q0 = _d0;
                _q1 = _d1;
                _q2 = _d2;
                _q3 = _d3;
                _q4 = _d4;
                _q5 = _d5;
                _q6 = _d6;
                _q7 = _d7;
            }
        }
    }

    private bool _d0;
    public bool D0 { private get => _d0; set { _d0 = value; if (_le) _q0 = value; } }

    private bool _d1;
    public bool D1 { private get => _d1; set { _d1 = value; if (_le) _q1 = value; } }

    private bool _d2;
    public bool D2 { private get => _d2; set { _d2 = value; if (_le) _q2 = value; } }

    private bool _d3;
    public bool D3 { private get => _d3; set { _d3 = value; if (_le) _q3 = value; } }

    private bool _d4;
    public bool D4 { private get => _d4; set { _d4 = value; if (_le) _q4 = value; } }

    private bool _d5;
    public bool D5 { private get => _d5; set { _d5 = value; if (_le) _q5 = value; } }

    private bool _d6;
    public bool D6 { private get => _d6; set { _d6 = value; if (_le) _q6 = value; } }

    private bool _d7;
    public bool D7 { private get => _d7; set { _d7 = value; if (_le) _q7 = value; } }

    private bool _q0;
    public bool? Q0 => Oe ? null : _q0;

    private bool _q1;
    public bool? Q1 => Oe ? null : _q1;

    private bool _q2;
    public bool? Q2 => Oe ? null : _q2;

    private bool _q3;
    public bool? Q3 => Oe ? null : _q3;

    private bool _q4;
    public bool? Q4 => Oe ? null : _q4;

    private bool _q5;
    public bool? Q5 => Oe ? null : _q5;

    private bool _q6;
    public bool? Q6 => Oe ? null : _q6;

    private bool _q7;
    public bool? Q7 => Oe ? null : _q7;
}
