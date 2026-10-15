// Bink Audio decoder from binkadec (GPL-3.0), see NOTICE.
namespace FModel.Framework.Audio.Binka;

public enum BinkaMode { Bfc, Ueba }

public class BinkaDecoderCore
{
    private const int MaxDecoders = 8;
    private const int MaxFrameChannels = 2;
    private const int MaxChannels = 8;
    private const int MinChannels = 1;
    private const int MaxSampleRate = 96000;
    private const int MinSampleRate = 300;
    private const int MaxFrameSamples = 2048;
    private const int MaxFrameOverlap = 128;
    private const int MaxBands = 26;

    private const uint FlagsDct = 1u << 0;
    private const uint FlagsOutputPlanar = 1u << 1;
    private const uint FlagsBinka2 = 1u << 2;

    private class BinkaDecoder
    {
        public float[] Table;
        public int FrameSamples;
        public float Scale;
        public int OverlapSamples;
        public int OverlapBits;
        public int FrameChannels;
        public bool IsFirstFrame;
        public int BandCount;
        public int TransformSize;
        public uint Flags;
        public int[] BandThresholds = new int[MaxBands + 1];
    }

    private int _channels;
    private int _sampleRate;
    private int _outputSamples;
    private BinkaDecoder[] _decoders = new BinkaDecoder[MaxDecoders];
    private float[] _samples;
    private float[] _overlap;
    private int _decoderCount;

    public BinkaDecoderCore(int sampleRate, int channels, BinkaMode mode)
    {
        if (sampleRate < MinSampleRate || sampleRate > MaxSampleRate) return;
        if (channels < MinChannels || channels > MaxChannels) return;

        _channels = channels;
        _sampleRate = sampleRate;
        _decoderCount = (channels + 1) / MaxFrameChannels;

        if (sampleRate < 22050) _outputSamples = 480;
        else if (sampleRate < 44100) _outputSamples = 960;
        else _outputSamples = 1920;

        uint flags = 0;
        switch (mode)
        {
            case BinkaMode.Bfc:
                if (channels > 2) flags |= FlagsOutputPlanar;
                flags |= FlagsDct;
                break;
            case BinkaMode.Ueba:
                flags |= FlagsBinka2;
                flags |= FlagsDct;
                break;
        }

        int tempChannels = channels;
        for (int i = 0; i < _decoderCount; i++)
        {
            int frameChannels = tempChannels >= 2 ? 2 : 1;
            _decoders[i] = OpenDecoder(sampleRate, frameChannels, flags);
            tempChannels -= 2;
        }

        if (_decoders[0] == null) return;

        _samples = new float[MaxFrameSamples * channels];
        _overlap = new float[MaxFrameOverlap * channels];
    }

    public bool IsValid => _samples != null && _decoders[0] != null;

    private BinkaDecoder OpenDecoder(int sampleRate, int frameChannels, uint flags)
    {
        int frameSamples;
        if (sampleRate < 22050) frameSamples = 512;
        else if (sampleRate < 44100) frameSamples = 1024;
        else frameSamples = 2048;

        if ((flags & FlagsDct) == 0)
        {
            sampleRate *= frameChannels;
            frameSamples *= frameChannels;
            frameChannels = 1;
        }

        int frameSamplesHalf = frameSamples >> 1;
        int sampleRateHalf = (sampleRate + 1) >> 1;

        int bandCount = 0;
        while (bandCount < MaxBands - 1)
        {
            if (BinkaData.CutoffFrequency[bandCount] >= sampleRateHalf)
                break;
            bandCount++;
        }

        var decoder = new BinkaDecoder();
        if (frameChannels == 1) flags &= ~FlagsOutputPlanar;

        decoder.Table = BinkaData.Cosines;
        decoder.TransformSize = 2048;
        decoder.Flags = flags;
        decoder.OverlapSamples = frameSamples >> 4;
        decoder.FrameChannels = frameChannels;
        decoder.BandCount = bandCount;
        decoder.FrameSamples = frameSamples;

        decoder.OverlapBits = 0;
        switch (frameSamples >> 4)
        {
            case 32: decoder.OverlapBits = 5; break;
            case 64: decoder.OverlapBits = 6; break;
            case 128: decoder.OverlapBits = 7; break;
            case 256: decoder.OverlapBits = 8; break;
        }

        decoder.Scale = 2.0f / (float)System.Math.Sqrt(frameSamples);

        for (int i = 0; i < bandCount; i++)
        {
            int bandLimit = frameSamplesHalf * BinkaData.CutoffFrequency[i] / sampleRateHalf;
            if (bandLimit == 0) bandLimit = 1;
            decoder.BandThresholds[i] = bandLimit;
        }
        decoder.BandThresholds[bandCount] = frameSamplesHalf;
        decoder.IsFirstFrame = true;

        return decoder;
    }

    private void ApplyOverlap(float[] dst, int dstOffset, int overlapSamples, float[] overlap, int overlapOffset, int overlapBits)
    {
        if (overlapBits == 0) return;
        for (int i = 0; i < overlapSamples; i++)
        {
            float s1 = overlap[overlapOffset + i];
            float s2 = (i * (dst[dstOffset + i] - s1)) / overlapSamples;
            dst[dstOffset + i] = s1 + s2;
        }
    }

    private void ApplyScale(float[] coefs, int offset, float scale, int count)
    {
        for (int i = 0; i < count; i++)
            coefs[offset + i] *= scale;
    }

    private void OutputSamples(BinkaDecoder dec, float[] coefs, int coefsOffset, float[] overlap, int overlapOffset)
    {
        ApplyScale(coefs, coefsOffset, dec.Scale, dec.FrameSamples * dec.FrameChannels);

        int outputSamples = dec.FrameSamples - dec.OverlapSamples;
        int currentOverlapBits = dec.OverlapBits;
        if (dec.IsFirstFrame)
        {
            dec.IsFirstFrame = false;
            currentOverlapBits = 0;
        }

        int cOff = coefsOffset;
        int oOff = overlapOffset;
        for (int ch = 0; ch < dec.FrameChannels; ch++)
        {
            ApplyOverlap(coefs, cOff, dec.OverlapSamples, overlap, oOff, currentOverlapBits);
            System.Buffer.BlockCopy(coefs, (cOff + outputSamples) * 4, overlap, oOff * 4, dec.OverlapSamples * 4);
            cOff += dec.FrameSamples;
            oOff += dec.OverlapSamples;
        }
    }

    private float GetFloat29(Bitstream bs)
    {
        uint code = bs.Read(29);
        int power = (int)(code & 0x1F);
        int mantissa = (int)((code >> 5) & 0x77FFFFF);
        int sign = (int)((code >> 28) & 1);
        float value = mantissa * BinkaData.Float29Power[power];
        if (sign != 0) value = -value;
        return value;
    }

    private bool UnpackChannel(float[] coefs, int coefsOffset, int frameSamples, int bandCount, Bitstream bs, int[] bandThresholds, bool isBinka2)
    {
        coefs[coefsOffset] = GetFloat29(bs);
        coefs[coefsOffset + 1] = GetFloat29(bs);

        int iBits = isBinka2 ? 7 : 8;
        float[] scalefactors = new float[MaxBands];
        for (int i = 0; i < bandCount; i++)
        {
            int index = (int)bs.Read((uint)iBits);
            if (index > 95) index = 95;
            scalefactors[i] = BinkaData.Scalefactors[index];
        }

        float bandScalefactor = 0;
        int band = 0;
        int pos = 2;

        while (pos < frameSamples)
        {
            int rleFlag = (int)bs.Read(1);
            int end;
            if (rleFlag != 0)
            {
                int rleIndex = (int)bs.Read(4);
                end = pos + 8 * BinkaData.RleTable[rleIndex];
            }
            else
            {
                end = pos + 8;
            }
            if (end > frameSamples) end = frameSamples;

            int qBits = (int)bs.Read(4);
            if (qBits > 0)
            {
                if (isBinka2)
                {
                    for (int subpos = pos; subpos < end; subpos++)
                    {
                        int value = (int)bs.Read((uint)qBits);
                        coefs[coefsOffset + subpos] = value;
                    }
                    for (int subpos = pos; subpos < end; subpos++)
                    {
                        if (coefs[coefsOffset + subpos] != 0)
                        {
                            int negative = (int)bs.Read(1);
                            if (negative != 0)
                                coefs[coefsOffset + subpos] = -coefs[coefsOffset + subpos];
                        }
                    }
                    while (pos < end)
                    {
                        if (pos == bandThresholds[band] * 2)
                        {
                            bandScalefactor = scalefactors[band];
                            band++;
                        }
                        coefs[coefsOffset + pos] *= bandScalefactor;
                        pos++;
                    }
                }
                else
                {
                    while (pos < end)
                    {
                        if (pos == bandThresholds[band] * 2)
                        {
                            bandScalefactor = scalefactors[band];
                            band++;
                        }
                        int value = (int)bs.Read((uint)qBits);
                        if (value != 0)
                        {
                            int negative = (int)bs.Read(1);
                            float coef = value * bandScalefactor;
                            if (negative != 0) coef = -coef;
                            coefs[coefsOffset + pos] = coef;
                        }
                        else
                        {
                            coefs[coefsOffset + pos] = 0;
                        }
                        pos++;
                    }
                }
            }
            else
            {
                for (int i = pos; i < end; i++)
                    coefs[coefsOffset + i] = 0;
                pos = end;
                while (end > bandThresholds[band] * 2)
                {
                    bandScalefactor = scalefactors[band];
                    band++;
                }
            }
        }

        return !bs.Error;
    }

    private int DecodeFrameInternal(BinkaDecoder dec, byte[] src, int srcOffset, int srcSize, float[] coefs, int coefsOffset, float[] overlap, int overlapOffset)
    {
        var bs = new Bitstream();
        bs.Setup(src, srcOffset, (uint)srcSize);

        bool isDct = (dec.Flags & FlagsDct) != 0;
        bool isBinka2 = (dec.Flags & FlagsBinka2) != 0;

        if (isDct) bs.Skip(2);

        int cOff = coefsOffset;
        for (int ch = 0; ch < dec.FrameChannels; ch++)
        {
            bool ok = UnpackChannel(coefs, cOff, dec.FrameSamples, dec.BandCount, bs, dec.BandThresholds, isBinka2);
            if (ok)
            {
                if (isDct)
                    BinkaTransform.TransformDct(coefs, cOff, dec.FrameSamples, dec.TransformSize, dec.Table!);
                else
                    BinkaTransform.TransformRdft(coefs, cOff, dec.FrameSamples, dec.TransformSize, dec.Table!);
            }
            else
            {
                return -1;
            }
            cOff += dec.FrameSamples;
        }

        OutputSamples(dec, coefs, coefsOffset, overlap, overlapOffset);

        bs.Align(32);
        return (int)(bs.Pos / 8);
    }

    private void CopySamples(float[] dst, float[] src, int srcSamples, int dstSamples, int channels)
    {
        for (int ch = 0; ch < channels; ch++)
        {
            int dstI = ch;
            int srcI = srcSamples * ch;
            for (int s = 0; s < dstSamples; s++)
            {
                float val = src[srcI];
                if (val > 32767f) val = 32767f;
                if (val < -32768f) val = -32768f;
                dst[dstI] = val / 32768f;
                dstI += channels;
                srcI++;
            }
        }
    }

    public int Decode(byte[] src, int srcSize, float[] dst)
    {
        if (!IsValid) return -1;

        int samplesOff = 0, overlapOff = 0;
        int remaining = srcSize;
        int offset = 0;

        for (int i = 0; i < _decoderCount; i++)
        {
            BinkaDecoder decoder = _decoders[i];
            int bytesDone = DecodeFrameInternal(decoder, src, offset, remaining, _samples!, samplesOff, _overlap!, overlapOff);
            if (bytesDone < 0) return bytesDone;

            samplesOff += decoder.FrameSamples * decoder.FrameChannels;
            overlapOff += decoder.OverlapSamples * decoder.FrameChannels;
            offset += bytesDone;
            remaining -= bytesDone;
        }

        if (remaining > 4)
            return -remaining;

        CopySamples(dst, _samples!, _decoders[0].FrameSamples, _outputSamples, _channels);
        return _outputSamples;
    }

    public void Reset()
    {
        for (int i = 0; i < _decoderCount; i++)
            _decoders[i].IsFirstFrame = true;
    }

    public int FrameSamples => _outputSamples;
}