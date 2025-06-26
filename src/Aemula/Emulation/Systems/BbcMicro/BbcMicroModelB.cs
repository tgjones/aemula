//using System.IO;
//using Aemula.Bus;
//using Aemula.Chips.Mos6502;
//using Aemula.Chips.Mos6522;
//using Aemula.Chips.Motorola6845;
//using Aemula.Memory;

//namespace Aemula.Systems.BbcMicro
//{
//    public sealed class BbcMicroModelB : EmulatedSystem
//    {
//        public readonly Bus<ushort, byte> Bus;
//        public readonly Mos6502 Cpu;
//        public readonly Motorola6845 Crtc;
//        public readonly Mos6522 SystemVia;

//        public BbcMicroModelB()
//        {
//            Bus = new Bus<ushort, byte>();

//            Cpu = new Mos6502(Bus);

//            Crtc = new Motorola6845();
//            SystemVia = new Mos6522();

//            Bus.Map(0x0000, 0x7FFF, new Ram<ushort, byte>(0x8000));
//            Bus.Map(0xC000, 0xFFFF, new Rom<ushort, byte>(File.ReadAllBytes(@"Roms\os.rom")));
//            Bus.Map(0xFE00, 0xFF00, new Sheila(Crtc, SystemVia));
//        }

//        public override void Reset()
//        {
//            Cpu.Reset();
//        }
//    }

//    public sealed class Sheila : MemoryProxy<ushort, byte>
//    {
//        public Sheila(Motorola6845 crtc, Mos6522 systemVia)
//            : base(0x100)
//        {
//            // CRTC
//            Map(0x00,
//                0x1,
//                a => crtc.AddressRegister,
//                (a, d) => crtc.AddressRegister = d);
//            Map(0x01,
//                0x1,
//                a => crtc.Read(),
//                (a, d) => crtc.Write(d));

//            // System VIA
//            Map(0x40,
//                0xF,
//                a => systemVia.Read((ushort)(a - 0x40)),
//                (a, d) => systemVia.Write((ushort)(a - 0x40), d));
//        }
//    }
//}
