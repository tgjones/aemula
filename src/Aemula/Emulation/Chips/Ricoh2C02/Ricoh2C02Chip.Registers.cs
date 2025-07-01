namespace Aemula.Emulation.Chips.Ricoh2C02;

partial class Ricoh2C02Chip
{
    internal struct PpuCtrlRegister
    {
        public PackedByte Data;

        public byte NameTableX
        {
            get => Data.Get(0, 1);
            set => Data.Set(0, 1, value);
        }

        public byte NameTableY
        {
            get => Data.Get(1, 1);
            set => Data.Set(1, 1, value);
        }

        public VRamAddressIncrementMode VRamAddressIncrementMode
        {
            get => (VRamAddressIncrementMode)Data.Get(2, 1);
            set => Data.Set(2, 1, (byte)value);
        }

        public byte SpritePatternTableAddress
        {
            get => Data.Get(3, 1);
            set => Data.Set(3, 1, value);
        }

        public byte BackgroundPatternTableAddress
        {
            get => Data.Get(4, 1);
            set => Data.Set(4, 1, value);
        }

        public SpriteSize SpriteSize
        {
            get => (SpriteSize)Data.Get(5, 1);
            set => Data.Set(5, 1, (byte)value);
        }

        public bool SlaveMode
        {
            get => Data.GetBit(6);
            set => Data.SetBit(6, value);
        }

        public bool EnableNmi
        {
            get => Data.GetBit(7);
            set => Data.SetBit(7, value);
        }
    }

    internal enum VRamAddressIncrementMode
    {
        Add1,
        Add32,
    }

    internal enum SpriteSize
    {
        Size8x8,
        Size8x16,
    }

    internal struct PpuMaskRegister
    {
        public PackedByte Data;

        public bool Grayscale
        {
            get => Data.GetBit(0);
            set => Data.SetBit(0, value);
        }

        public bool RenderBackgroundLeft
        {
            get => Data.GetBit(1);
            set => Data.SetBit(1, value);
        }

        public bool RenderSpritesLeft
        {
            get => Data.GetBit(2);
            set => Data.SetBit(2, value);
        }

        public bool RenderBackground
        {
            get => Data.GetBit(3);
            set => Data.SetBit(3, value);
        }

        public bool RenderSprites
        {
            get => Data.GetBit(4);
            set => Data.SetBit(4, value);
        }

        public bool EmphasizeRed
        {
            get => Data.GetBit(5);
            set => Data.SetBit(5, value);
        }

        public bool EmphasizeGreen
        {
            get => Data.GetBit(6);
            set => Data.SetBit(6, value);
        }

        public bool EmphasizeBlue
        {
            get => Data.GetBit(7);
            set => Data.SetBit(7, value);
        }
    }

    internal struct PpuStatusRegister
    {
        public PackedByte Data;

        public byte Unused
        {
            set => Data.Set(0, 5, value);
        }

        public bool SpriteOverflow
        {
            get => Data.GetBit(5);
            set => Data.SetBit(5, value);
        }

        public bool Sprite0Hit
        {
            get => Data.GetBit(6);
            set => Data.SetBit(6, value);
        }

        public bool VBlankStarted
        {
            get => Data.GetBit(7);
            set => Data.SetBit(7, value);
        }
    }
}
