namespace Aemula.Emulation.Chips;

/// <summary>
/// 4-bit binary counter, built from two independent sections that aren't
/// connected internally: a divide-by-2 (clock <see cref="A"/>, output
/// <see cref="QA"/>) and a divide-by-8 ripple counter (clock
/// <see cref="B"/>, outputs <see cref="QB"/>-<see cref="QD"/>, each stage
/// toggling on the previous stage's falling edge). A full divide-by-16
/// count needs <see cref="QA"/> wired back to <see cref="B"/> externally,
/// same as on a real board. Both sections toggle on their clock's falling
/// edge, and share one AND-gated asynchronous master reset.
/// </summary>
public sealed class Ttl7493Chip
{
    private bool _r01;
    public bool R01
    {
        private get => _r01;
        set
        {
            _r01 = value;
            UpdateReset();
        }
    }

    private bool _r02;
    public bool R02
    {
        private get => _r02;
        set
        {
            _r02 = value;
            UpdateReset();
        }
    }

    private void UpdateReset()
    {
        if (_r01 && _r02)
        {
            _qa = false;
            _qb = false;
            _qc = false;
            _qd = false;
        }
    }

    private bool _a;
    public bool A
    {
        set
        {
            var fallingEdge = !value && _a;
            _a = value;

            if (fallingEdge && !(_r01 && _r02))
            {
                _qa = !_qa;
            }
        }
    }

    private bool _b;
    public bool B
    {
        set
        {
            var fallingEdge = !value && _b;
            _b = value;

            if (fallingEdge && !(_r01 && _r02))
            {
                ToggleQb();
            }
        }
    }

    private void ToggleQb()
    {
        _qb = !_qb;
        if (_qb)
        {
            return;
        }

        // QB's own falling edge ripples into QC, and QC's into QD.
        _qc = !_qc;
        if (_qc)
        {
            return;
        }

        _qd = !_qd;
    }

    private bool _qa;
    private bool _qb;
    private bool _qc;
    private bool _qd;

    public bool QA => _qa;
    public bool QB => _qb;
    public bool QC => _qc;
    public bool QD => _qd;
}
