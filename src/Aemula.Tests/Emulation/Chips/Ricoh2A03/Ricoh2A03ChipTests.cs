using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Aemula.Tests.Emulation.Chips.Ricoh2A03;

public class Ricoh2A03ChipTests
{
    private static readonly string AssetsPath = Path.Combine("Emulation", "Chips", "Ricoh2A03", "Assets");

    [Test]
    public async Task NesTest()
    {
        byte[] rom;
        using (var reader = new BinaryReader(File.OpenRead(Path.Combine(AssetsPath, "nestest.nes"))))
        {
            reader.BaseStream.Seek(16, SeekOrigin.Current);
            rom = reader.ReadBytes(16384);
        }

        // Patch the test start address into the RESET vector.
        rom[0x3FFC] = 0x00;
        rom[0x3FFD] = 0xC0;

        var ram = new byte[0x0800];

        // APU and I/O registers - for the purposes of this test, treat them as RAM.
        var apu = new byte[0x18];

        var testHelper = new Ricoh2A03ChipTestHelper(
            address => address switch
            {
                _ when address <= 0x1FFF => ram[address & 0x07FF],
                _ when address >= 0x4000 && address <= 0x4017 => apu[address - 0x4000],
                _ when address >= 0x8000 && address <= 0xFFFF => rom[address - 0x8000 & 0x3FFF],
                _ => rom[address - 0x4000]
            },
            (address, data) =>
            {
                switch (address)
                {
                    case var _ when address <= 0x1FFF:
                        ram[address & 0x07FF] = data;
                        break;

                    case var _ when address >= 0x4000 && address <= 0x4017:
                        apu[address - 0x4000] = data;
                        break;
                }
            });

        await testHelper.Startup();

        while (true)
        {
            await testHelper.Tick();

            // End of the official-opcode run - a JMP to itself at $C66E.
            if (testHelper.CpuCoreSync && testHelper.Address == 0xC66E)
            {
                break;
            }
        }

        await Assert.That(ram[0x0002]).IsEqualTo((byte)0x000);
        await Assert.That(ram[0x0003]).IsEqualTo((byte)0x000);
    }

    // A $4014 write halts the core through RDY and the on-chip DMA unit copies a
    // page to $2004 with alternating get / put cycles. The get / put divider
    // free-runs, so a request that lands on a put cycle waits one more cycle
    // before it can start - the 513 vs 514 cycle difference. The padding
    // instruction below moves the write by an odd number of cycles to cover
    // both alignments.
    //
    // The test helper validates every pin against the transistor-level
    // reference chip on each master clock edge, so the halt, the alignment
    // cycle, the 512 transfer cycles and the resume are all checked for free;
    // the assertions here only cover what came out the other end.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OamDma(bool padWrite)
    {
        var ram = new byte[0x0800];
        for (var i = 0; i < 0x100; i++)
        {
            ram[0x300 + i] = (byte)(0x40 + i);
        }

        var rom = new byte[0x4000]; // $C000-$FFFF
        void WriteRom(ushort address, params byte[] bytes) =>
            bytes.CopyTo(rom.AsSpan(address - 0xC000));

        // LDA $00 takes 3 cycles, so it flips which phase the $4014 write lands on.
        ushort offset = 0;
        if (padWrite)
        {
            WriteRom(0xC000, 0xA5, 0x00);       // LDA $00
            offset = 2;
        }

        WriteRom((ushort)(0xC000 + offset), 0xA9, 0x03);         // LDA #$03
        WriteRom((ushort)(0xC002 + offset), 0x8D, 0x14, 0x40);   // STA $4014
        WriteRom((ushort)(0xC005 + offset), 0xEA);               // NOP
        WriteRom((ushort)(0xC006 + offset), 0xEA);               // NOP
        WriteRom((ushort)(0xC007 + offset), 0xEA);               // NOP
        var spinAddress = (ushort)(0xC008 + offset);
        WriteRom(spinAddress, 0x4C, (byte)spinAddress, 0xC0);    // JMP <spin>
        WriteRom(0xFFFC, 0x00, 0xC0);                            // RESET vector

        var oam = new List<byte>();

        var testHelper = new Ricoh2A03ChipTestHelper(
            address => address switch
            {
                _ when address <= 0x1FFF => ram[address & 0x07FF],
                _ when address >= 0xC000 => rom[address - 0xC000],
                _ => 0
            },
            (address, data) =>
            {
                switch (address)
                {
                    case var _ when address <= 0x1FFF:
                        ram[address & 0x07FF] = data;
                        break;

                    case 0x2004:
                        oam.Add(data);
                        break;
                }
            });

        await testHelper.Startup();

        // Long enough for the whole transfer (513 or 514 cycles depending on
        // which get / put phase the write landed on) plus the code around it.
        for (var i = 0; i < 600; i++)
        {
            await testHelper.Tick();
        }

        await Assert.That(oam.Count).IsEqualTo(256);
        await Assert.That(oam).IsEquivalentTo(ram.AsSpan(0x300, 0x100).ToArray());

        // The core picked up where it left off rather than executing DMA bytes:
        // it ran the three NOPs and is now going round the JMP.
        await Assert.That(testHelper.Address).IsGreaterThanOrEqualTo(spinAddress);
    }
}
