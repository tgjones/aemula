using System;

namespace Aemula.Emulation.Chips.Mos6522;

public sealed class Mos6522Chip
{
    private enum Register
    {
        ORB,
        ORA,
        DDRB,
        DDRA,
        T1CL,
        T1CH,
        T1LL,
        T1LH,
        T2CL,
        T2CH,
        SR,
        ACR,
        PCR,
        IFR,
        IER,
        ORANoHandshake
    }

    public byte ORB;
    public byte ORA;
    public byte IRB;
    public byte IRA;
    public byte DDRB;
    public byte DDRA;
    public byte T1C_L;
    public byte T1C_H;
    public byte T1L_L;
    public byte T1L_H;
    public byte T2C_L;
    public byte T2C_H;
    public byte SR;
    public byte ACR;
    public byte PCR;
    public byte IFR;
    public byte IER;

    public byte Read(ushort address)
    {
        switch ((Register)address)
        {
            case Register.ORB:
                return IRB;

            case Register.ORA:
                return IRA;

            case Register.DDRB:
                return DDRB;

            case Register.DDRA:
                return DDRA;

            case Register.IER:
                return (byte)(IER | 0x80);

            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    public void Write(ushort address, byte data)
    {
        switch ((Register)address)
        {
            case Register.ORB:
                ORB = data;
                // TODO
                break;

            case Register.ORA:
                // TODO
                break;

            case Register.DDRB:
                DDRB = data;
                // TODO
                break;
        }
    }
}
