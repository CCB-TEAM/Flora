// Bink Audio decoder from binkadec (GPL-3.0), see NOTICE.
using System;
using System.Collections.Generic;

namespace FModel.Framework.Audio.Binka;

public class Bcf1Decoder
{
    private byte[] _pktBuf;
    private uint _pktBufSize;
    private uint _pktFilled;
    private int _pktSamples;
    private BinkaDecoderCore _core;
    private int _channels;
    private int _frameSamples;
    private uint _lastBytesConsumed;

    public Bcf1Decoder(int sampleRate, int channels)
    {
        _channels = channels;
        _core = new BinkaDecoderCore(sampleRate, channels, BinkaMode.Bfc);
        if (!_core.IsValid) return;
        _frameSamples = _core.FrameSamples;
        _pktBufSize = 16384;
        _pktBuf = new byte[_pktBufSize];
    }

    public bool IsValid => _core != null && _core.IsValid;

    public uint LastBytesConsumed => _lastBytesConsumed;

    public static int ParseHeader(byte[] data, out int version, out int channels, out int sampleRate, out int numSamples, out uint dataOffset)
    {
        version = 0; channels = 0; sampleRate = 0; numSamples = 0; dataOffset = 0;
        if (data[0] != 0x31 || data[1] != 0x46 || data[2] != 0x43 || data[3] != 0x42)
            return -1;
        version = data[4];
        channels = data[5];
        sampleRate = System.BitConverter.ToUInt16(data, 6);
        numSamples = System.BitConverter.ToInt32(data, 8);
        int seekEntries = System.BitConverter.ToUInt16(data, 20);
        dataOffset = (uint)(24 + seekEntries * 2);
        return 0;
    }

    private bool ReadFrame(byte[] data, ref uint offset, uint size)
    {
        if (offset + 4 > size) return false;
        uint header = System.BitConverter.ToUInt32(data, (int)offset);
        ushort sync = (ushort)(header & 0xFFFF);
        ushort packetSize = (ushort)((header >> 16) & 0xFFFF);
        offset += 4;
        if (sync != 0x9999) return false;
        if (packetSize == 0xFFFF)
        {
            if (offset + 4 > size) return false;
            uint limitHeader = System.BitConverter.ToUInt32(data, (int)offset);
            packetSize = (ushort)(limitHeader & 0xFFFF);
            _pktSamples = (ushort)((limitHeader >> 16) & 0xFFFF);
            offset += 4;
        }
        else _pktSamples = 0;
        if (packetSize > _pktBufSize) return false;
        if (offset + packetSize > size) return false;
        System.Buffer.BlockCopy(data, (int)offset, _pktBuf!, 0, packetSize);
        _pktFilled = packetSize;
        offset += packetSize;
        return true;
    }

    public int DecodeFrame(byte[] input, uint inputOffset, uint inputSize, float[] output)
    {
        if (!IsValid || input == null || inputSize == 0) return 0;
        uint offset = inputOffset;
        uint startOffset = offset;
        if (!ReadFrame(input, ref offset, inputOffset + inputSize)) return -1;
        _lastBytesConsumed = offset - startOffset;
        if (_pktFilled == 0) return 0;
        float[] tempOut = new float[_frameSamples * _channels];
        int samples = _core.Decode(_pktBuf!, (int)_pktFilled, tempOut);
        if (samples < 0) return -1;
        if (_pktSamples > 0 && samples > _pktSamples)
            samples = _pktSamples;
        System.Buffer.BlockCopy(tempOut, 0, output, 0, samples * _channels * 4);
        return samples;
    }

    public int DecodeFrame(byte[] input, uint inputSize, float[] output)
    {
        return DecodeFrame(input, 0, inputSize, output);
    }

    /// <summary>
    /// Decodes every packet from <paramref name="startOffset"/> on into interleaved samples, stopping at
    /// <paramref name="maxSamples"/> frames. Returns null when nothing could be decoded.
    /// </summary>
    public float[] DecodeAll(byte[] input, uint startOffset, int maxSamples)
    {
        if (!IsValid || input == null) return null;
        var output = new List<float>(Math.Max(0, maxSamples) * _channels);
        var frame = new float[_frameSamples * _channels];
        var offset = startOffset;
        var totalSamples = 0;
        while (offset < input.Length && totalSamples < maxSamples)
        {
            int samples = DecodeFrame(input, offset, (uint)input.Length - offset, frame);
            if (samples < 0) break; // truncated tail or padding
            if (_lastBytesConsumed == 0) break;
            offset += _lastBytesConsumed;
            samples = Math.Min(samples, maxSamples - totalSamples);
            for (var i = 0; i < samples * _channels; i++)
                output.Add(frame[i]);
            totalSamples += samples;
        }
        return totalSamples > 0 ? output.ToArray() : null;
    }

    public void Reset() => _core?.Reset();
    public int FrameSamples => _frameSamples;
}