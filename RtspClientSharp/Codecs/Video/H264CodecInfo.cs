using System;

namespace RtspClientSharp.Codecs.Video
{
    public class H264CodecInfo : VideoCodecInfo
    {
        public byte[] SpsPpsBytes { get; set; } = Array.Empty<byte>();
    }
}