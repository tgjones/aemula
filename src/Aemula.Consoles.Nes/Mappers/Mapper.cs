﻿using System;

namespace Aemula.Consoles.Nes.Mappers
{
    public abstract class Mapper
    {
        public static Mapper Create(ushort mapperNumber)
        {
            switch (mapperNumber)
            {
                case 0:
                    return new Mapper000();

                default:
                    throw new NotSupportedException();
            }
        }
    }
}
