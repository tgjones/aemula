using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Aemula.Systems.Nes.Tests.Visual2C02;

internal class Macros
{
    private readonly ChipSim _sim;
    private int _cycle;

    private readonly List<string> _checksums = new();
    private readonly byte[] _memory = new byte[512];

    public Macros()
    {
        _sim = new ChipSim();

        //_sim.SetState(Configuration.ResetStatePreRenderEven);
        _sim.SetStartupState();
    }

    // simulate a single clock phase, updating trace and highlighting layout
    public void Step()
    {
        //var s = GetState();
        //var m = _memory;
        ////var m = GetMem();
        //var checksum = Adler32(s + string.Join(',', m[..511].Select(x => x.ToString("x"))));
        //_checksums.Add(checksum);
        //Console.WriteLine(checksum);

        HalfStep();
        _cycle++;
        //ChipStatus();
        //UpdateScope();
        //UpdateVideo();

        //Console.WriteLine($"Halfcyc: {_cycle}");
        //Console.WriteLine($"Clk: {ReadBit(NodeName.clk0)}");
        //Console.WriteLine($"Scanline: {ReadVPos()}");
        //Console.WriteLine($"Pixel: {ReadHPos()}");
    }

    private static string Adler32(string x)
    {
        var a = 1;
        var b = 0;
        for (var i = 0; i < x.Length; i++)
        {
            a = (a + x[i]) % 65521;
            b = (b + a) % 65521;
        }
        return (0x100000000 + (b << 16) + a).ToString("x")[^8..];
    }

    // simulate a single clock phase with no update to graphics or trace
    private void HalfStep()
    {
        var clk = _sim.IsNodeHigh(NodeName.clk0);

        _sim.SetNode(NodeName.clk0, !clk);

        // Handle memory reads and writes.
        HandleChrBus();
    }

    private struct ChrStatus
    {
        public bool Rd;
        public bool Wr;
        public bool Ale;
    }

    private ChrStatus _chrStatus = new ChrStatus
    {
        Rd = true,
        Wr = true,
        Ale = false,
    };

    private ushort _chrAddress;

    private void HandleChrBus()
    {
        var newStatus = new ChrStatus
        {
            Rd = _sim.IsNodeHigh(NodeName.rd),
            Wr = _sim.IsNodeHigh(NodeName.wr),
            Ale = _sim.IsNodeHigh(NodeName.ale),
        };

        // rising edge of ALE
        if (!_chrStatus.Ale && newStatus.Ale)
        {
            _chrAddress = ReadAddressBus();
        }
        // falling edge of /RD - put bits on bus
        if (_chrStatus.Rd && !newStatus.Rd)
        {
            var a = _chrAddress;
            //var d = eval(readTriggers[a]);
            //if(d == undefined)
            var d = MemoryRead(a);
            WriteBits(NodeGroups.db, d);
        }
        // rising edge of /RD - float the data bus
        if (!_chrStatus.Rd && newStatus.Rd)
        {
            this.FloatBits(NodeGroups.db);
        }
        // rising edge of /WR - store data in RAM
        if (!_chrStatus.Wr && newStatus.Wr)
        {
            var a = _chrAddress;
            var d = ReadDataBus();
            //eval(writeTriggers[a]);
            MemoryWrite(a, d);
        }

        _chrStatus = newStatus;
    }

    private ushort _lastAddress;
    private byte _lastData;

    private ushort ReadAddressBus()
    {
        if (_sim.IsNodeHigh(NodeName.ale))
        {
            _lastAddress = ReadBits<ushort>(NodeGroups.ab);
        }
        return _lastAddress;
    }

    private byte ReadDataBus()
    {
        if (!_sim.IsNodeHigh(NodeName.rd) || !_sim.IsNodeHigh(NodeName.wr))
        {
            _lastData = ReadBits<byte>(NodeGroups.db);
        }
        return _lastData;
    }

    // TODO
    private byte MemoryRead(ushort address) => 0;
    private void MemoryWrite(ushort address, byte value) {}

    public int ReadBit(NodeName name)
    {
        return _sim.IsNodeHigh(name) ? 1 : 0;
    }

    public T ReadBits<T>(Span<NodeName> names)
        where T : INumber<T>, IShiftOperators<T, int, T>
    {
        var res = T.Zero;
        for (var i = 0; i < names.Length; i++)
        {
            res += (_sim.IsNodeHigh(names[i]) ? T.One : T.Zero) << i;
        }
        return res;
    }

    public void WriteBits<T>(Span<NodeName> names, T value)
        where T : IUnsignedNumber<T>, IModulusOperators<T, T, T>
    {
        Span<bool> values = stackalloc bool[names.Length];

        var two = T.CreateChecked(2);

        for (var i = 0; i < names.Length; i++)
        {
            values[i] = (value % two) != T.Zero;
            value /= two;
        }

        _sim.SetNodes(names, values);
    }

    public void SetNode(NodeName name, bool value) => _sim.SetNode(name, value);

    public void FloatBits(Span<NodeName> names) => _sim.SetNodesFloating(names);

    public string GetState() => _sim.GetState();
}
