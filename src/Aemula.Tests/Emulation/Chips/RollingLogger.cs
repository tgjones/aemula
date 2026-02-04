using System;
using System.Collections.Generic;

namespace Aemula.Tests.Emulation.Chips;

internal sealed class RollingLogger
{
    private readonly Queue<ILoggable> _log = new();

    public void Add(ILoggable loggable)
    {
        _log.Enqueue(loggable);

        while (_log.Count > 50)
        {
            _log.Dequeue();
        }
    }

    public void DumpToConsole()
    {
        Console.WriteLine("---- LOG START ----");
        foreach (var logEntry in _log)
        {
            Console.WriteLine(logEntry.ToLogEntry());
        }
        Console.WriteLine("---- LOG END ----");
    }
}

internal interface ILoggable
{
    string ToLogEntry();
}

internal sealed record MemoryLog(ushort Address, byte Data, bool IsRead)
        : ILoggable
{
    public string ToLogEntry()
    {
        return IsRead
            ? $"Read      ${Address:X4} => ${Data:X2}"
            : $"Write     ${Address:X4} <= ${Data:X2}";
    }
}
