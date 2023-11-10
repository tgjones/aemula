﻿using System.IO;
using NUnit.Framework;

namespace Aemula.Output.Tests;

internal class TelevisionTests
{
    [Test]
    public void CanDecodePal()
    {
        var wfmFilePath = Path.GetFullPath("Assets/nes.wmf");
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