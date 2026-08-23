using System.Diagnostics.CodeAnalysis;

namespace Aemula;

public sealed class DisplayBuffer
{
    public uint Width { get; private set; }
    public uint Height { get; private set; }

    public RgbaByte[] Data { get; private set; }

    public DisplayBuffer(uint width, uint height)
    {
        Resize(width, height);
    }

    [MemberNotNull(nameof(Data))]
    public void Resize(uint width, uint height)
    {
        Width = width;
        Height = height;

        Data = new RgbaByte[width * height];

        for (var i = 0; i < Data.Length; i++)
        {
            Data[i] = new RgbaByte(0, 0, 0, 255);
        }
    }
}
