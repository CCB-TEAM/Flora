// Bink Audio decoder from binkadec (GPL-3.0), see NOTICE.
namespace FModel.Framework.Audio.Binka;

public class Bitstream
{
    private static readonly uint[] MaskTableLsb = [
        0x00000000, 0x00000001, 0x00000003, 0x00000007, 0x0000000f, 0x0000001f, 0x0000003f, 0x0000007f, 0x000000ff,
        0x000001ff, 0x000003ff, 0x000007ff, 0x00000fff, 0x00001fff, 0x00003fff, 0x00007fff, 0x0000ffff, 0x0001ffff,
        0x0003ffff, 0x0007ffff, 0x000fffff, 0x001fffff, 0x003fffff, 0x007fffff, 0x00ffffff, 0x01ffffff, 0x03ffffff,
        0x07ffffff, 0x0fffffff, 0x1fffffff, 0x3fffffff, 0x7fffffff, 0xffffffff
    ];

    private byte[] _buf;
    private uint _bufSize;
    private uint _bMax;
    private uint _bOff;
    public bool Error;

    public void Setup(byte[] buf, uint bufSize)
    {
        _buf = buf;
        _bufSize = bufSize;
        _bMax = bufSize * 8;
        _bOff = 0;
        Error = false;
    }

    public void Setup(byte[] buf, int offset, uint bufSize)
    {
        _buf = buf;
        _bufSize = bufSize;
        _bMax = ((uint)offset + bufSize) * 8;
        _bOff = (uint)(offset * 8);
        Error = false;
    }

    public bool Skip(uint bits)
    {
        if (_bOff + bits > _bMax)
        {
            Error = true;
            return false;
        }
        _bOff += bits;
        return true;
    }

    public void Align(uint bits)
    {
        if (bits == 0) return;
        int left = (int)(_bOff % bits);
        if (left == 0) return;
        Skip((uint)(bits - left));
    }

    public int Pos => (int)_bOff;

    public bool Get(uint bits, out uint value)
    {
        if (bits > 32 || _bOff + bits > _bMax)
        {
            value = 0;
            Error = true;
            return false;
        }

        uint pos = _bOff / 8;
        uint shift = _bOff % 8;
        uint mask = MaskTableLsb[bits];

        uint val = (uint)(_buf![pos + 0] >> (int)shift);
        if (bits + shift > 8)
        {
            val |= (uint)(_buf![pos + 1] << (int)(8u - shift));
            if (bits + shift > 16)
            {
                val |= (uint)(_buf![pos + 2] << (int)(16u - shift));
                if (bits + shift > 24)
                {
                    val |= (uint)(_buf![pos + 3] << (int)(24u - shift));
                    if (bits + shift > 32)
                    {
                        val |= (uint)(_buf![pos + 4] << (int)(32u - shift));
                    }
                }
            }
        }

        value = val & mask;
        _bOff += bits;
        return true;
    }

    public uint Read(uint bits)
    {
        if (!Get(bits, out uint value))
            return 0;
        return value;
    }

    public bool Put(uint bits, uint value)
    {
        if (bits > 32 || _bOff + bits > _bMax)
        {
            Error = true;
            return false;
        }

        uint pos = _bOff / 8;
        uint shift = _bOff % 8;
        uint mask = (1u << (int)shift) - 1;

        _buf![pos + 0] = (byte)((value << (int)shift) | (_buf![pos + 0] & mask));
        if (bits + shift > 8)
        {
            _buf![pos + 1] = (byte)(value >> (int)(8 - shift));
            if (bits + shift > 16)
            {
                _buf![pos + 2] = (byte)(value >> (int)(16 - shift));
                if (bits + shift > 24)
                {
                    _buf![pos + 3] = (byte)(value >> (int)(24 - shift));
                    if (bits + shift > 32)
                    {
                        _buf![pos + 4] = (byte)(value >> (int)(32 - shift));
                    }
                }
            }
        }

        _bOff += bits;
        return true;
    }
}