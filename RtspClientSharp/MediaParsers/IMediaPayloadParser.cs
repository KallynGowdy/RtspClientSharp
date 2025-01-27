﻿using System;
using RtspClientSharp.RawFrames;

namespace RtspClientSharp.MediaParsers
{
    public interface IMediaPayloadParser
    {
        Action<RawFrame> FrameGenerated { get; set; }

        void Parse(TimeSpan timeOffset, ArraySegment<byte> byteSegment, bool markerBit);

        void ResetState();
    }
}