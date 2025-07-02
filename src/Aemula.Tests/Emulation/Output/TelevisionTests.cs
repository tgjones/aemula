using System.IO;

namespace Aemula.Tests.Emulation.Output;

internal class TelevisionTests
{
    [Test]
    public void CanDecodePal()
    {
        var wfmFilePath = Path.GetFullPath(Path.Combine("Emulation", "Output", "Assets", "nes.wmf"));
        var wmfFile = WfmFile.FromFile(wfmFilePath);
    }
}

internal class WfmFile
{
    public static WfmFile FromFile(string filePath)
    {
        using var fileStream = File.OpenRead(filePath);
        using var binaryReader = new BinaryReader(fileStream);

        return new WfmFile();
    }
}
