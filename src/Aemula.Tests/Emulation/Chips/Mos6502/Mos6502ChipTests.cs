using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Aemula.Tests.Emulation.Chips.Mos6502;

public class Mos6502ChipTests
{
    private static readonly string AssetsPath = Path.Combine("Emulation", "Chips", "Mos6502", "Assets");

    [Test]
    public async Task AllSuiteA()
    {
        var rom = File.ReadAllBytes(Path.Combine(AssetsPath, "AllSuiteA.bin"));
        var ram = new byte[0x4000];

        var testHelper = new Mos6502ChipTestHelper(
            address => address switch
            {
                _ when address <= 0x3FFF => ram[address],
                _ => rom[address - 0x4000]
            },
            (address, data) =>
            {
                if (address <= 0x3FFF)
                {
                    ram[address] = data;
                }
            },
            doReferenceComparison: true);

        await testHelper.Startup();

        while (testHelper.PC != 0x45C2)
        {
            await testHelper.Tick();
        }

        await Assert.That(ram[0x0210]).IsEqualTo((byte)0xFF);
    }

    [Test]
    public async Task DormannFunctionalTest()
    {
        var ram = File.ReadAllBytes(Path.Combine(AssetsPath, "6502_functional_test.bin"));
        await Assert.That(ram.Length).IsEqualTo(0x10000);

        // Patch the test start address into the RESET vector.
        ram[0xFFFC] = 0x00;
        ram[0xFFFD] = 0x04;

        var testHelper = new Mos6502ChipTestHelper(
            address => ram[address],
            (address, data) => ram[address] = data,
            // This test does pass the reference comparison, but...
            // it takes 8.5 hours to complete on my machine!
            doReferenceComparison: false);

        await testHelper.Startup();

        while (testHelper.PC != 0x3399 && testHelper.PC != 0xD0FE)
        {
            await testHelper.Tick();
        }

        await Assert.That(testHelper.PC).IsEqualTo((ushort)0x3399);
    }

    [Test]
    [Arguments(" start")]
    [Arguments("ldab")]
    [Arguments("ldaz")]
    [Arguments("ldazx")]
    [Arguments("ldaa")]
    [Arguments("ldaax")]
    [Arguments("ldaay")]
    [Arguments("ldaix")]
    [Arguments("ldaiy")]
    [Arguments("staz")]
    [Arguments("stazx")]
    [Arguments("staa")]
    [Arguments("staax")]
    [Arguments("staay")]
    [Arguments("staix")]
    [Arguments("staiy")]
    [Arguments("ldxb")]
    [Arguments("ldxz")]
    [Arguments("ldxzy")]
    [Arguments("ldxa")]
    [Arguments("ldxay")]
    [Arguments("stxz")]
    [Arguments("stxzy")]
    [Arguments("stxa")]
    [Arguments("ldyb")]
    [Arguments("ldyz")]
    [Arguments("ldyzx")]
    [Arguments("ldya")]
    [Arguments("ldyax")]
    [Arguments("styz")]
    [Arguments("styzx")]
    [Arguments("stya")]
    [Arguments("taxn")]
    [Arguments("tayn")]
    [Arguments("txan")]
    [Arguments("tyan")]
    [Arguments("tsxn")]
    [Arguments("txsn")]
    [Arguments("phan")]
    [Arguments("plan")]
    [Arguments("phpn")]
    [Arguments("plpn")]
    [Arguments("inxn")]
    [Arguments("inyn")]
    [Arguments("dexn")]
    [Arguments("deyn")]
    [Arguments("incz")]
    [Arguments("inczx")]
    [Arguments("inca")]
    [Arguments("incax")]
    [Arguments("decz")]
    [Arguments("deczx")]
    [Arguments("deca")]
    [Arguments("decax")]
    [Arguments("asln")]
    [Arguments("aslz")]
    [Arguments("aslzx")]
    [Arguments("asla")]
    [Arguments("aslax")]
    [Arguments("lsrn")]
    [Arguments("lsrz")]
    [Arguments("lsrzx")]
    [Arguments("lsra")]
    [Arguments("lsrax")]
    [Arguments("roln")]
    [Arguments("rolz")]
    [Arguments("rolzx")]
    [Arguments("rola")]
    [Arguments("rolax")]
    [Arguments("rorn")]
    [Arguments("rorz")]
    [Arguments("rorzx")]
    [Arguments("rora")]
    [Arguments("rorax")]
    [Arguments("andb")]
    [Arguments("andz")]
    [Arguments("andzx")]
    [Arguments("anda")]
    [Arguments("andax")]
    [Arguments("anday")]
    [Arguments("andix")]
    [Arguments("andiy")]
    [Arguments("orab")]
    [Arguments("oraz")]
    [Arguments("orazx")]
    [Arguments("oraa")]
    [Arguments("oraax")]
    [Arguments("oraay")]
    [Arguments("oraix")]
    [Arguments("oraiy")]
    [Arguments("eorb")]
    [Arguments("eorz")]
    [Arguments("eorzx")]
    [Arguments("eora")]
    [Arguments("eorax")]
    [Arguments("eoray")]
    [Arguments("eorix")]
    [Arguments("eoriy")]
    [Arguments("clcn")]
    [Arguments("secn")]
    [Arguments("cldn")]
    [Arguments("sedn")]
    [Arguments("clin")]
    [Arguments("sein")]
    [Arguments("clvn")]
    [Arguments("adcb")]
    [Arguments("adcz")]
    [Arguments("adczx")]
    [Arguments("adca")]
    [Arguments("adcax")]
    [Arguments("adcay")]
    [Arguments("adcix")]
    [Arguments("adciy")]
    [Arguments("sbcb")]
    [Arguments("sbcz")]
    [Arguments("sbczx")]
    [Arguments("sbca")]
    [Arguments("sbcax")]
    [Arguments("sbcay")]
    [Arguments("sbcix")]
    [Arguments("sbciy")]
    [Arguments("cmpb")]
    [Arguments("cmpz")]
    [Arguments("cmpzx")]
    [Arguments("cmpa")]
    [Arguments("cmpax")]
    [Arguments("cmpay")]
    [Arguments("cmpix")]
    [Arguments("cmpiy")]
    [Arguments("cpxb")]
    [Arguments("cpxz")]
    [Arguments("cpxa")]
    [Arguments("cpyb")]
    [Arguments("cpyz")]
    [Arguments("cpya")]
    [Arguments("bitz")]
    [Arguments("bita")]
    [Arguments("brkn")]
    [Arguments("rtin")]
    [Arguments("jsrw")]
    [Arguments("rtsn")]
    [Arguments("jmpw")]
    [Arguments("jmpi")]
    [Arguments("beqr")]
    [Arguments("bner")]
    [Arguments("bmir")]
    [Arguments("bplr")]
    [Arguments("bcsr")]
    [Arguments("bccr")]
    [Arguments("bvsr")]
    [Arguments("bvcr")]
    [Arguments("nopn")]
    [Arguments("nopb")]
    [Arguments("nopz")]
    [Arguments("nopzx")]
    [Arguments("nopa")]
    [Arguments("nopax")]
    [Arguments("asoz")]
    [Arguments("asozx")]
    [Arguments("asoa")]
    [Arguments("asoax")]
    [Arguments("asoay")]
    [Arguments("asoix")]
    [Arguments("asoiy")]
    [Arguments("rlaz")]
    [Arguments("rlazx")]
    [Arguments("rlaa")]
    [Arguments("rlaax")]
    [Arguments("rlaay")]
    [Arguments("rlaix")]
    [Arguments("rlaiy")]
    [Arguments("lsez")]
    [Arguments("lsezx")]
    [Arguments("lsea")]
    [Arguments("lseax")]
    [Arguments("lseay")]
    [Arguments("lseix")]
    [Arguments("lseiy")]
    [Arguments("rraz")]
    [Arguments("rrazx")]
    [Arguments("rraa")]
    [Arguments("rraax")]
    [Arguments("rraay")]
    [Arguments("rraix")]
    [Arguments("rraiy")]
    [Arguments("dcmz")]
    [Arguments("dcmzx")]
    [Arguments("dcma")]
    [Arguments("dcmax")]
    [Arguments("dcmay")]
    [Arguments("dcmix")]
    [Arguments("dcmiy")]
    [Arguments("insz")]
    [Arguments("inszx")]
    [Arguments("insa")]
    [Arguments("insax")]
    [Arguments("insay")]
    [Arguments("insix")]
    [Arguments("insiy")]
    [Arguments("laxz")]
    [Arguments("laxzy")]
    [Arguments("laxa")]
    [Arguments("laxay")]
    [Arguments("laxix")]
    [Arguments("laxiy")]
    [Arguments("axsz")]
    [Arguments("axszy")]
    [Arguments("axsa")]
    [Arguments("axsix")]
    [Arguments("alrb")]
    [Arguments("arrb")]
    [Arguments("aneb")]
    [Arguments("lxab")]
    [Arguments("sbxb")]
    [Arguments("shaay")]
    [Arguments("shaiy")]
    [Arguments("shxay")]
    [Arguments("shyax")]
    [Arguments("shsay")]
    [Arguments("ancb")]
    [Arguments("lasay")]
    [Arguments("sbcb(eb)")]
    [Arguments("trap1")]
    [Arguments("trap2")]
    [Arguments("trap3")]
    [Arguments("trap4")]
    [Arguments("trap5")]
    [Arguments("trap6")]
    [Arguments("trap7")]
    [Arguments("trap8")]
    [Arguments("trap9")]
    [Arguments("trap10")]
    [Arguments("trap11")]
    [Arguments("trap12")]
    [Arguments("trap13")]
    [Arguments("trap14")]
    [Arguments("trap15")]
    [Arguments("trap16")]
    public async Task C64Suite(string fileName)
    {
        static string PetsciiToAscii(byte character) => character switch
        {
            147 => "\n------------\n", // Clear
            14 => "", // Toggle lowercase/uppercase character set
            _ when character >= 0x41 && character <= 0x5A => ((char)(character - 0x41 + 97)).ToString(),
            _ when character >= 0xC1 && character <= 0xDA => ((char)(character - 0xC1 + 65)).ToString(),
            _ => ((char)character).ToString()
        };

        // This test suite is traditionally run by loading " start",
        // which then loads and runs other test files sequentially.
        // But to speed things up, we just run each test in parallel.

        // The list of test files ends before "trap17", which is C64-specific.

        var ram = new byte[0x10000];

        // Initialize some memory locations.
        ram[0x0002] = 0x00;
        ram[0xA002] = 0x00;
        ram[0xA003] = 0x80;
        ram[0xFFFE] = 0x48;
        ram[0xFFFF] = 0xFF;
        ram[0x01FE] = 0xFF;
        ram[0x01FF] = 0x7F;

        // Install KERNAL "IRQ handler".
        ReadOnlySpan<byte> irqRoutine =
        [
            0x48,             // PHA
            0x8A,             // TXA
            0x48,             // PHA
            0x98,             // TYA
            0x48,             // PHA
            0xBA,             // TSX
            0xBD,
            0x04,
            0x01, // LDA $0104,X
            0x29,
            0x10,       // AND #$10
            0xF0,
            0x03,       // BEQ $FF58
            0x6C,
            0x16,
            0x03, // JMP ($0316)
            0x6C,
            0x14,
            0x03, // JMP ($0314)
        ];
        for (var i = 0; i < irqRoutine.Length; i++)
        {
            ram[0xFF48 + i] = irqRoutine[i];
        }

        // Stub CHROUT routine.
        ram[0xFFD2] = 0x60; // RTS

        // Stub load routine.
        ram[0xE16F] = 0xEA; // NOP

        // Stub GETIN routine.
        ram[0xFFE4] = 0xA9; // LDA #3
        ram[0xFFE5] = 0x03;
        ram[0xFFE6] = 0x60; // RTS

        // Initialize RESET vector.
        ram[0xFFFC] = 0x01;
        ram[0xFFFD] = 0x08;

        // Load test data.
        // First two bytes contain starting address.
        var path = Path.Combine(AssetsPath, "C64TestSuite", "bin", fileName);
        var testData = File.ReadAllBytes(path);
        var startAddress = testData[0] | testData[1] << 8;
        for (var i = 2; i < testData.Length; i++)
        {
            ram[startAddress + i - 2] = testData[i];
        }

        var log = new StringBuilder();

        var continueTest = true;

        Mos6502MemoryAccessResult HandleMemoryAccess(ushort address, Mos6502RegisterState registerState)
        {
            var resultKind = Mos6502MemoryAccessResultKind.Continue;

            switch (address)
            {
                case 0xFFD2: // Print character
                    if (registerState.A == 13)
                    {
                        Console.WriteLine(log.ToString());
                        log.Clear();
                    }
                    else
                    {
                        log.Append(PetsciiToAscii(registerState.A));
                    }
                    ram[0x030C] = 0x00;
                    break;

                case 0xE16F: // Load
                    var fileNameAddress = ram[0xBB] | ram[0xBC] << 8;
                    var fileNameLength = ram[0xB7];
                    var testFileName = string.Empty;
                    for (var i = 0; i < fileNameLength; i++)
                    {
                        testFileName += PetsciiToAscii(ram[fileNameAddress + i]);
                    }
                    Console.WriteLine($"Next program: {testFileName}");
                    resultKind = Mos6502MemoryAccessResultKind.StopTest;
                    continueTest = false;
                    break;

                case 0x8000: // Exit
                case 0xA474:
                    return new Mos6502MemoryAccessResult(
                        Mos6502MemoryAccessResultKind.FailTest,
                        log.ToString());
            }

            return new Mos6502MemoryAccessResult(resultKind, null);
        }

        var testHelper = new Mos6502ChipTestHelper(
            address => ram[address],
            (address, data) => ram[address] = data,
            doReferenceComparison: false,
            HandleMemoryAccess);

        await testHelper.Startup();

        while (continueTest)
        {
            await testHelper.Tick();
        }
    }
}
