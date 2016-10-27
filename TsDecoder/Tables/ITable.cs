﻿using System.Collections.Generic;
using TsDecoder.TransportStream;

namespace TsDecoder.Tables
{
    public interface ITable
    {
        List<Descriptor> Descriptors { get; set; }
        short Pid { get; set; }
        byte PointerField { get; set; }
        short SectionLength { get; set; }
        byte TableId { get; set; }
    }
}