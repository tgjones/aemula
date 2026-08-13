namespace Aemula.Emulation.Chips;

public sealed class Ttl7400Chip
{
    private bool _a1;
    public bool A1 { set => _a1 = value; }

    private bool _b1;
    public bool B1 { set => _b1 = value; }

    public bool Y1 => !(_a1 && _b1);

    private bool _a2;
    public bool A2 { set => _a2 = value; }

    private bool _b2;
    public bool B2 { set => _b2 = value; }

    public bool Y2 => !(_a2 && _b2);

    private bool _a3;
    public bool A3 { set => _a3 = value; }

    private bool _b3;
    public bool B3 { set => _b3 = value; }

    public bool Y3 => !(_a3 && _b3);

    private bool _a4;
    public bool A4 { set => _a4 = value; }

    private bool _b4;
    public bool B4 { set => _b4 = value; }

    public bool Y4 => !(_a4 && _b4);
}
