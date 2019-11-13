using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using RtspClientSharp.RawFrames;
using RtspClientSharp.RawFrames.Video;
using RtspClientSharp.Utils;

namespace RtspClientSharp.MediaParsers
{
    class H264Parser
    {
        private enum FrameType
        {
            Unknown,
            IntraFrame,
            PredictionFrame
        }

        public static readonly ArraySegment<byte> StartMarkerSegment = new ArraySegment<byte>(RawH264Frame.StartMarker);

        private readonly Func<DateTime> _frameTimestampProvider;
        private readonly BitStreamReader _bitStreamReader = new BitStreamReader();
        private readonly Dictionary<int, byte[]> _spsMap = new Dictionary<int, byte[]>();
        private readonly Dictionary<int, byte[]> _ppsMap = new Dictionary<int, byte[]>();
        private bool _waitForIFrame = true;
        private byte[] _spsPpsBytes = new byte[0];
        private bool _updateSpsPpsBytes;
        private int _sliceType = -1;

        private readonly MemoryStream _frameStream;

        public Action<RawFrame> FrameGenerated;

        public H264Parser(Func<DateTime> frameTimestampProvider)
        {
            _frameTimestampProvider = frameTimestampProvider ?? throw new ArgumentNullException(nameof(frameTimestampProvider));
            _frameStream = new MemoryStream(8 * 1024);
        }

        public void Parse(ArraySegment<byte> byteSegment, bool generateFrame)
        {
            Debug.Assert(byteSegment.Array != null, "byteSegment.Array != null");

            if (ArrayUtils.StartsWith(byteSegment.Array, byteSegment.Offset, byteSegment.Count,
                RawH264Frame.StartMarker))
                H264Slicer.Slice(byteSegment, SlicerOnNalUnitFound);
            else
                ProcessNalUnit(byteSegment, false, ref generateFrame);

            if (generateFrame) {
                TryGenerateFrame();
            } else {
#if DEBUG
                Console.WriteLine("[H264Parser] Marker bit not set.");
#endif
            }
        }

        public void TryGenerateFrame()
        {
#if DEBUG
            Console.WriteLine("[H264Parser] Trying to generate frame.");
#endif
            if (_frameStream.Position == 0) {
#if DEBUG
                Console.WriteLine("[H264Parser] No frame data.");
#endif
                return;
            }

            var frameBytes = new ArraySegment<byte>(_frameStream.GetBuffer(), 0, (int)_frameStream.Position);
            _frameStream.Position = 0;
            TryGenerateFrame(frameBytes);
        }

        private void TryGenerateFrame(ArraySegment<byte> frameBytes)
        {
            if (_updateSpsPpsBytes)
            {
                UpdateSpsPpsBytes();
                _updateSpsPpsBytes = false;
            }

            if (_sliceType == -1 || _spsPpsBytes.Length == 0) {
#if DEBUG
                if (_spsPpsBytes.Length == 0) {
                    Console.WriteLine("[H264Parser] No SPS/PPS data.");
                } else {
                    Console.WriteLine("[H264Parser] No slice type.");
                }
#endif

                return;
            }

            FrameType frameType = GetFrameType(_sliceType);
            _sliceType = -1;
            DateTime frameTimestamp;

            if (frameType == FrameType.PredictionFrame)
            {
                if (!_waitForIFrame)
                {
                    frameTimestamp = _frameTimestampProvider();
                    FrameGenerated?.Invoke(new RawH264PFrame(frameTimestamp, frameBytes));
                    return;
                }
                else
                {
                    Console.WriteLine("[H264Parser] Skipping PFrame because waiting for IFrame");
                }
            }

            if (frameType != FrameType.IntraFrame) {
#if DEBUG
                Console.WriteLine("[H264Parser] Not IFrame " + frameType);
#endif
                return;
            }

            _waitForIFrame = false;
            var byteSegment = new ArraySegment<byte>(_spsPpsBytes);

            frameTimestamp = _frameTimestampProvider();
            FrameGenerated?.Invoke(new RawH264IFrame(frameTimestamp, frameBytes, byteSegment));
        }

        public void ResetState()
        {
            _frameStream.Position = 0;
            _sliceType = -1;
            // _waitForIFrame = true;
        }

        private void SlicerOnNalUnitFound(ArraySegment<byte> byteSegment)
        {
#if DEBUG
            Console.WriteLine("[H264Parser] Sliced off the start marker");
#endif
            bool generateFrame = false;
            ProcessNalUnit(byteSegment, true, ref generateFrame);
        }

        private void ProcessNalUnit(ArraySegment<byte> byteSegment, bool hasStartMarker, ref bool generateFrame)
        {
            Debug.Assert(byteSegment.Array != null, "byteSegment.Array != null");

            int offset = byteSegment.Offset;

            if (hasStartMarker)
                offset += RawH264Frame.StartMarker.Length;

            int nalUnitType = byteSegment.Array[offset] & 0x1F;
            bool nri = ((byteSegment.Array[offset] >> 5) & 3) == 0;

            if (!(nalUnitType > 0 && nalUnitType < 24))
                throw new H264ParserException($"Invalid nal unit type: {nalUnitType}");

            if (nalUnitType == 7)
            {
#if DEBUG
                Console.WriteLine($"[H264Parser] Parse SPS");
#endif
                ParseSps(byteSegment, hasStartMarker);
                return;
            }

            if (nalUnitType == 8)
            {
#if DEBUG
                Console.WriteLine($"[H264Parser] Parse PPS");
#endif
                ParsePps(byteSegment, hasStartMarker);
                return;
            }

            if (_sliceType == -1 && (nalUnitType == 5 || nalUnitType == 1))
                _sliceType = GetSliceType(byteSegment, hasStartMarker);

            if (nri || nalUnitType == 6) {
#if DEBUG
                Console.WriteLine($"[H264Parser] NRI or nalUnitTYpe == 6 ({nri}, {nalUnitType} == 6)");
#endif
                return;
            }

            if (generateFrame && (hasStartMarker || byteSegment.Offset >= StartMarkerSegment.Count) && _frameStream.Position == 0)
            {
#if DEBUG
                Console.WriteLine($"[H264Parser] Generate frame flag was set. Has Start marker: {hasStartMarker}");
#endif
                if (!hasStartMarker)
                {
                    int newOffset = byteSegment.Offset - StartMarkerSegment.Count;

                    Buffer.BlockCopy(StartMarkerSegment.Array, StartMarkerSegment.Offset,
                        byteSegment.Array, newOffset, StartMarkerSegment.Count);

                    byteSegment = new ArraySegment<byte>(byteSegment.Array, newOffset, byteSegment.Count + StartMarkerSegment.Count);
                }

                generateFrame = false;
                TryGenerateFrame(byteSegment);
            }
            else
            {
#if DEBUG
                Console.WriteLine($"[H264Parser] Writing {byteSegment.Count} bytes to stream.");
#endif

                if (!hasStartMarker)
                    _frameStream.Write(StartMarkerSegment.Array, StartMarkerSegment.Offset, StartMarkerSegment.Count);

                _frameStream.Write(byteSegment.Array, byteSegment.Offset, byteSegment.Count);
            }
        }

        private void ParseSps(ArraySegment<byte> byteSegment, bool hasStartMarker)
        {
            const int spsMinSize = 5;

            if (byteSegment.Count < spsMinSize)
                return;

            ProcessSpsOrPps(byteSegment, hasStartMarker, spsMinSize - 1, _spsMap);
        }

        private void ParsePps(ArraySegment<byte> byteSegment, bool hasStartMarker)
        {
            const int ppsMinSize = 2;

            if (byteSegment.Count < ppsMinSize)
                return;

            ProcessSpsOrPps(byteSegment, hasStartMarker, ppsMinSize - 1, _ppsMap);
        }

        private void ProcessSpsOrPps(ArraySegment<byte> byteSegment, bool hasStartMarker, int offset,
            Dictionary<int, byte[]> idToBytesMap)
        {
            _bitStreamReader.ReInitialize(hasStartMarker
                ? byteSegment.SubSegment(RawH264Frame.StartMarker.Length + offset)
                : byteSegment.SubSegment(offset));

            int id = _bitStreamReader.ReadUe();

            if (id == -1)
                return;

            if (hasStartMarker)
                byteSegment = byteSegment.SubSegment(RawH264Frame.StartMarker.Length);

            if (TryUpdateSpsOrPps(byteSegment, id, idToBytesMap))
                _updateSpsPpsBytes = true;
        }

        private static bool TryUpdateSpsOrPps(ArraySegment<byte> byteSegment, int id,
            Dictionary<int, byte[]> idToBytesMap)
        {
            Debug.Assert(byteSegment.Array != null, "byteSegment.Array != null");

            if (!idToBytesMap.TryGetValue(id, out byte[] data))
            {
                data = new byte[byteSegment.Count];
                Buffer.BlockCopy(byteSegment.Array, byteSegment.Offset, data, 0, byteSegment.Count);
                idToBytesMap.Add(id, data);
                return true;
            }

            if (!ArrayUtils.IsBytesEquals(data, 0, data.Length, byteSegment.Array, byteSegment.Offset,
                byteSegment.Count))
            {
                if (data.Length != byteSegment.Count)
                    data = new byte[byteSegment.Count];

                Buffer.BlockCopy(byteSegment.Array, byteSegment.Offset, data, 0, byteSegment.Count);
                idToBytesMap[id] = data;
                return true;
            }

            return false;
        }

        private void UpdateSpsPpsBytes()
        {
            int totalSize = _spsMap.Values.Sum(sps => sps.Length) + _ppsMap.Values.Sum(pps => pps.Length) +
                            RawH264Frame.StartMarker.Length * (_spsMap.Count + _ppsMap.Count);

            if (_spsPpsBytes.Length != totalSize)
                _spsPpsBytes = new byte[totalSize];

            int offset = 0;

            foreach (byte[] sps in _spsMap.Values)
            {
                Buffer.BlockCopy(RawH264Frame.StartMarker, 0, _spsPpsBytes, offset, RawH264Frame.StartMarker.Length);
                offset += RawH264Frame.StartMarker.Length;
                Buffer.BlockCopy(sps, 0, _spsPpsBytes, offset, sps.Length);
                offset += sps.Length;
            }

            foreach (byte[] pps in _ppsMap.Values)
            {
                Buffer.BlockCopy(RawH264Frame.StartMarker, 0, _spsPpsBytes, offset, RawH264Frame.StartMarker.Length);
                offset += RawH264Frame.StartMarker.Length;
                Buffer.BlockCopy(pps, 0, _spsPpsBytes, offset, pps.Length);
                offset += pps.Length;
            }
        }

        private int GetSliceType(ArraySegment<byte> byteSegment, bool hasStartMarker)
        {
            int offset = 1;

            if (hasStartMarker)
                offset += RawH264Frame.StartMarker.Length;

            _bitStreamReader.ReInitialize(byteSegment.SubSegment(offset));

            int firstMbInSlice = _bitStreamReader.ReadUe();

            if (firstMbInSlice == -1)
                return firstMbInSlice;

            int nalSliceType = _bitStreamReader.ReadUe();
            return nalSliceType;
        }

        private static FrameType GetFrameType(int sliceType)
        {
            if (sliceType == 0 || sliceType == 5)
                return FrameType.PredictionFrame;
            if (sliceType == 2 || sliceType == 7)
                return FrameType.IntraFrame;

            return FrameType.Unknown;
        }
    }
}