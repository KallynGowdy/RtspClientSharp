
using RtspClientSharp.Utils;

namespace RtspClientSharp.Rtp
{
    public interface IRtpSequenceAssembler
    {
        RefAction<RtpPacket> PacketPassed { get; set; }

        void ProcessPacket(ref RtpPacket rtpPacket);
    }
}