namespace Aemula;


public readonly struct RgbaByte(byte r, byte g, byte b, byte a)
{
    public const int SizeInBytes = 4;

    public readonly byte R = r;
    public readonly byte G = g;
    public readonly byte B = b;
    public readonly byte A = a;
}

