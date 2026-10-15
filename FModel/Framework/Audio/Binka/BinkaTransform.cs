// Bink Audio decoder from binkadec (GPL-3.0), see NOTICE.
namespace FModel.Framework.Audio.Binka;

public static class BinkaTransform
{
    public static void TransformDct(float[] coefs, int coefsOffset, int frameSamples, int transformSize, float[] table)
    {
        RadDdctf(frameSamples, coefs, coefsOffset, transformSize, table);
    }

    public static void TransformRdft(float[] coefs, int coefsOffset, int frameSamples, int transformSize, float[] table)
    {
        RadRdft(frameSamples, -1, coefs, coefsOffset, transformSize, table);
    }

    private static void Swap2(float[] coefs, int from, int to)
    {
        float v = coefs[from];
        coefs[from] = coefs[to];
        coefs[to] = v;
    }

    private static void Transform4Dct(float[] coefs)
    {
        Swap2(coefs, 2, 8);
        Swap2(coefs, 3, 9);
        Swap2(coefs, 7, 13);
        Swap2(coefs, 6, 12);
    }

    private static void Transform4(float[] coefs)
    {
        float v0 = coefs[0], v1 = coefs[1], v2 = coefs[2], v3 = coefs[3];
        coefs[0] = v0 + v2;
        coefs[1] = v1 + v3;
        coefs[2] = v0 - v2;
        coefs[3] = v1 - v3;
    }

    private static void Transform8Dct(float[] coefs)
    {
        float v0 = coefs[0] + coefs[4], v1 = coefs[0] - coefs[4];
        float v2 = coefs[1] + coefs[5], v3 = coefs[1] - coefs[5];
        float v4 = coefs[2] + coefs[6], v5 = coefs[2] - coefs[6];
        float v6 = coefs[3] + coefs[7], v7 = coefs[3] - coefs[7];
        coefs[0] = v0 + v4; coefs[1] = v2 + v6;
        coefs[2] = v1 - v7; coefs[3] = v3 + v5;
        coefs[4] = v0 - v4; coefs[5] = v2 - v6;
        coefs[6] = v1 + v7; coefs[7] = v3 - v5;
    }

    private static void Transform8Dft(float[] coefs)
    {
        float v0 = coefs[0] + coefs[4], v1 = coefs[0] - coefs[4];
        float v2 = coefs[1] + coefs[5], v3 = coefs[1] - coefs[5];
        float v4 = coefs[2] + coefs[6], v5 = coefs[2] - coefs[6];
        float v6 = coefs[3] + coefs[7], v7 = coefs[3] - coefs[7];
        coefs[0] = v0 + v4; coefs[1] = v2 + v6;
        coefs[2] = v1 + v7; coefs[3] = v3 - v5;
        coefs[4] = v0 - v4; coefs[5] = v2 - v6;
        coefs[6] = v1 - v7; coefs[7] = v3 + v5;
    }

    private static void Transform16Dft(float[] coefs)
    {
        float c2 = coefs[2], c3 = coefs[3], c4 = coefs[4], c5 = coefs[5];
        float c6 = coefs[6], c7 = coefs[7], c8 = coefs[8], c9 = coefs[9];
        float c10 = coefs[10], c11 = coefs[11], c12 = coefs[12], c13 = coefs[13];
        float c14 = coefs[14], c15 = coefs[15];
        coefs[2] = c14; coefs[3] = c15; coefs[4] = c6; coefs[5] = c7;
        coefs[6] = c10; coefs[7] = c11; coefs[8] = c2; coefs[9] = c3;
        coefs[10] = c12; coefs[11] = c13; coefs[12] = c4; coefs[13] = c5;
        coefs[14] = c8; coefs[15] = c9;
    }

    private static void Transform32Swap(float[] coefs)
    {
        Swap2(coefs, 2, 16); Swap2(coefs, 3, 17);
        Swap2(coefs, 4, 8); Swap2(coefs, 5, 9);
        Swap2(coefs, 6, 24); Swap2(coefs, 7, 25);
        Swap2(coefs, 10, 20); Swap2(coefs, 11, 21);
        Swap2(coefs, 14, 28); Swap2(coefs, 15, 29);
        Swap2(coefs, 22, 26); Swap2(coefs, 23, 27);
    }

    private static void Transform32Dft(float[] coefs)
    {
        float c2 = coefs[2], c3 = coefs[3], c4 = coefs[4], c5 = coefs[5];
        float c6 = coefs[6], c7 = coefs[7], c8 = coefs[8], c9 = coefs[9];
        float c10 = coefs[10], c11 = coefs[11], c12 = coefs[12], c13 = coefs[13];
        float c14 = coefs[14], c15 = coefs[15], c16 = coefs[16], c17 = coefs[17];
        float c18 = coefs[18], c19 = coefs[19], c20 = coefs[20], c21 = coefs[21];
        float c22 = coefs[22], c23 = coefs[23], c24 = coefs[24], c25 = coefs[25];
        float c26 = coefs[26], c27 = coefs[27], c28 = coefs[28], c29 = coefs[29];
        float c30 = coefs[30], c31 = coefs[31];
        coefs[2] = c30; coefs[3] = c31; coefs[4] = c14; coefs[5] = c15;
        coefs[6] = c22; coefs[7] = c23; coefs[8] = c6; coefs[9] = c7;
        coefs[10] = c26; coefs[11] = c27; coefs[12] = c10; coefs[13] = c11;
        coefs[14] = c18; coefs[15] = c19; coefs[16] = c2; coefs[17] = c3;
        coefs[18] = c28; coefs[19] = c29; coefs[20] = c12; coefs[21] = c13;
        coefs[22] = c20; coefs[23] = c21; coefs[24] = c4; coefs[25] = c5;
        coefs[26] = c24; coefs[27] = c25; coefs[28] = c8; coefs[29] = c9;
        coefs[30] = c16; coefs[31] = c17;
    }

    private static void Rotation32A(float[] coefs, float[] table, int tableOffset)
    {
        float t1 = table[tableOffset + 1];
        float t2 = table[tableOffset + 2] * t1;
        float t3 = table[tableOffset + 2] + t2;

        float a0 = coefs[0] - coefs[16], a1 = coefs[0] + coefs[16];
        float a2 = coefs[1] + coefs[17], a3 = coefs[1] - coefs[17];
        float a4 = coefs[8] - coefs[24], a5 = coefs[8] + coefs[24];
        float a6 = coefs[9] - coefs[25], a7 = coefs[9] + coefs[25];
        float a8 = coefs[10] + coefs[26], a9 = coefs[10] - coefs[26];

        float b1 = a5 + a1, b0 = a7 + a2;
        a1 = a1 - a5; a2 = a2 - a7;
        float b2 = a4 + a3; a3 = a3 - a4;
        a7 = a0 - a6; a6 = a6 + a0;

        float b3x = coefs[2] + coefs[18]; a0 = coefs[2] - coefs[18];
        a4 = coefs[11] + coefs[27]; float b5x = coefs[11] - coefs[27];
        float b6x = coefs[3] + coefs[19]; float b7x = coefs[3] - coefs[19];

        float b4x = a8 + b3x; a5 = a4 + b6x;
        b3x = b3x - a8; b6x = b6x - a4;
        a4 = a0 - b5x; b5x = b5x + a0;
        a8 = a9 + b7x; b7x = b7x - a9;
        float c0 = a4 * t3 - a8 * t2;
        float c1 = a8 * t3 + a4 * t2;
        float c2 = b5x * t3 + b7x * t2;
        float c3 = b5x * t2 - b7x * t3;

        b7x = coefs[4] - coefs[20]; float c4 = coefs[4] + coefs[20];
        float c5 = coefs[5] + coefs[21], c6 = coefs[5] - coefs[21];
        a8 = coefs[12] + coefs[28]; a9 = coefs[13] + coefs[29];
        a0 = coefs[12] - coefs[28]; b5x = coefs[13] - coefs[29];

        a4 = a8 + c4; float c7 = a0 + c6;
        c6 = c6 - a0; c4 = c4 - a8;
        a8 = a9 + c5; c5 = c5 - a9;
        a9 = b7x - b5x; b5x = b5x + b7x;
        float d3 = (a9 - c7) * t1;
        b7x = (c6 - b5x) * t1;
        float d4 = (c6 + b5x) * t1;
        float d5 = (c7 + a9) * t1;

        float d6 = coefs[6] + coefs[22], d7 = coefs[6] - coefs[22];
        float d2 = coefs[7] + coefs[23], d1 = coefs[7] - coefs[23];
        float d0 = coefs[14] + coefs[30]; a0 = coefs[14] - coefs[30];
        c6 = coefs[15] + coefs[31]; c7 = coefs[15] - coefs[31];

        a9 = d0 + d6; b5x = a0 + d1;
        float e0 = d7 - c7; c7 = c7 + d7;
        d1 = d1 - a0; a0 = c6 + d2;
        d2 = d2 - c6; d6 = d6 - d0;
        float e1 = e0 * t2 - b5x * t3;
        e0 = e0 * t3 + b5x * t2;
        d7 = c7 * t3 - d1 * t2;
        t3 = d1 * t3 + c7 * t2;

        c7 = a3 - b7x; b7x = b7x + a3;
        d0 = a6 - d4; d4 = d4 + a6;
        b5x = c3 - d7; c6 = c2 - t3;
        t3 = t3 + c2; d7 = d7 + c3;

        coefs[24] = b5x + d0; coefs[25] = c6 + c7;
        coefs[26] = d0 - b5x; coefs[27] = c7 - c6;
        coefs[28] = d4 - t3; coefs[29] = d7 + b7x;
        coefs[30] = t3 + d4; coefs[31] = b7x - d7;

        d0 = d3 + a7; c7 = d5 + b2;
        a7 = a7 - d3; b2 = b2 - d5;
        b5x = e1 + c0; c6 = e0 + c1;
        c1 = c1 - e0; c0 = c0 - e1;

        coefs[16] = b5x + d0; coefs[17] = c6 + c7;
        coefs[18] = d0 - b5x; coefs[19] = c7 - c6;
        coefs[20] = a7 - c1; coefs[21] = c0 + b2;
        coefs[22] = c1 + a7; coefs[23] = b2 - c0;

        b7x = b3x - d2; d2 = d2 + b3x;
        d0 = d6 + b6x; b6x = b6x - d6;
        b5x = (d2 - b6x) * t1; c7 = (d0 + b7x) * t1;
        c6 = (b7x - d0) * t1; t1 = (b6x + d2) * t1;
        a7 = a1 - c5; c5 = c5 + a1;
        b7x = c4 + a2; a2 = a2 - c4;
        b2 = a4 + b1;

        coefs[8] = c6 + a7; coefs[9] = c7 + b7x;
        coefs[10] = a7 - c6; coefs[11] = b7x - c7;
        coefs[12] = c5 - t1; coefs[13] = b5x + a2;
        coefs[14] = t1 + c5; coefs[15] = a2 - b5x;

        b1 = b1 - a4; a4 = a8 + b0;
        b0 = b0 - a8; a7 = a0 + a5;
        a5 = a5 - a0; t1 = a9 + b4x;
        b4x = b4x - a9;

        coefs[0] = t1 + b2; coefs[1] = a7 + a4;
        coefs[2] = b2 - t1; coefs[3] = a4 - a7;
        coefs[4] = b1 - a5; coefs[5] = b4x + b0;
        coefs[6] = a5 + b1; coefs[7] = b0 - b4x;
    }

    private static void Rotation32B(float[] coefs, float[] table, int tableOffset)
    {
        float tc1 = table[tableOffset + 1];
        float tc4 = table[tableOffset + 4], tc5 = table[tableOffset + 5];
        float tc6 = table[tableOffset + 6], tc7 = table[tableOffset + 7];
        float tc8 = table[tableOffset + 8], tc9 = table[tableOffset + 9];

        float a0 = coefs[0] - coefs[17], a1 = coefs[0] + coefs[17];
        float a2 = coefs[1] + coefs[16], a3 = coefs[1] - coefs[16];
        float a4 = coefs[8] + coefs[25], a5 = coefs[8] - coefs[25];
        float a6 = coefs[9] + coefs[24], a7 = coefs[9] - coefs[24];

        float b0 = (a5 - a6) * tc1; a6 = (a6 + a5) * tc1;
        a5 = b0 + a0; a0 = a0 - b0;
        b0 = a6 + a2; a2 = a2 - a6;
        a6 = (a4 - a7) * tc1; float b1 = (a7 + a4) * tc1;
        float b2 = a6 + a3; float b3 = a1 - b1;
        a3 = a3 - a6; b1 = b1 + a1;

        a7 = coefs[2] - coefs[19]; a6 = coefs[3] + coefs[18];
        float c0 = coefs[3] - coefs[18];

        float c1 = a7 * tc4 - a6 * tc5;
        a4 = a7 * tc5 + a6 * tc4;
        float c2 = coefs[19] + coefs[2];
        a1 = coefs[11] + coefs[26]; a6 = coefs[10] - coefs[27];
        float c3 = coefs[27] + coefs[10];
        a7 = a6 * tc7 - a1 * tc6; a1 = a1 * tc7 + a6 * tc6;

        a6 = a7 + c1; c1 = c1 - a7;
        a7 = a1 + a4; a4 = a4 - a1;
        a1 = coefs[11] - coefs[26];

        float c4 = c2 * tc7 + c0 * tc6;
        float c5 = c2 * tc6 - c0 * tc7;
        float c7 = a1 * tc5 + c3 * tc4;
        float d0 = a1 * tc4 - c3 * tc5;
        float c6 = c5 - c7; c7 = c7 + c5;
        c5 = c4 - d0;

        c3 = coefs[4] - coefs[21]; d0 = d0 + c4;
        float d1 = coefs[5] + coefs[20];
        a1 = c3 * tc9 + d1 * tc8;
        float d2 = c3 * tc8 - d1 * tc9;
        float d3 = coefs[5] - coefs[20]; c0 = coefs[13] + coefs[28];
        float d4 = coefs[13] - coefs[28]; c3 = coefs[12] - coefs[29];
        c4 = coefs[21] + coefs[4];

        c2 = c0 * tc9 + c3 * tc8; c3 = c3 * tc9 - c0 * tc8;
        float d5 = c2 + a1; a1 = a1 - c2;
        c0 = coefs[29] + coefs[12]; float d6 = c3 + d2;
        d2 = d2 - c3; c3 = c4 * tc9 - d3 * tc8;
        c2 = d3 * tc9 + c4 * tc8; c4 = c0 * tc8 - d4 * tc9;
        d3 = c0 * tc9 + d4 * tc8; float d7 = c3 - c4;
        c4 = c4 + c3; c0 = coefs[7] + coefs[22];

        float e0 = c2 - d3; d3 = d3 + c2;
        d4 = coefs[15] + coefs[30]; c2 = coefs[6] - coefs[23];
        c3 = c2 * table[tableOffset + 7] + c0 * tc6;
        float e1 = c2 * tc6 - c0 * table[tableOffset + 7];
        c2 = coefs[14] - coefs[31]; float e2 = coefs[15] - coefs[30];
        float e3 = coefs[31] + coefs[14]; float e4 = coefs[23] + coefs[6];

        float e5 = d5 + b0; c0 = c2 * table[tableOffset + 5] - d4 * table[tableOffset + 4];
        c2 = d4 * table[tableOffset + 5] + c2 * table[tableOffset + 4];
        float e6 = c2 + c3; c3 = c3 - c2;
        c2 = coefs[7] - coefs[22]; float e7 = c0 + e1;
        e1 = e1 - c0; c0 = e4 * table[tableOffset + 5] + c2 * table[tableOffset + 4];
        d4 = c2 * table[tableOffset + 5] - e4 * table[tableOffset + 4];
        e4 = d6 + a5; c2 = e3 * table[tableOffset + 7] - e2 * tc6;
        d1 = e2 * table[tableOffset + 7] + e3 * tc6;

        e3 = e6 + a7; e2 = d1 + d4; d4 = d4 - d1;
        a7 = a7 - e6; d1 = e7 + a6; a6 = a6 - e7;
        e7 = c2 + c0; c0 = c0 - c2;
        a5 = a5 - d6; b0 = b0 - d5;

        coefs[0] = d1 + e4; coefs[1] = e3 + e5;
        coefs[2] = e4 - d1; coefs[3] = e5 - e3;
        coefs[4] = a5 - a7; coefs[5] = a6 + b0;
        coefs[6] = a7 + a5; coefs[7] = b0 - a6;

        a7 = d2 + a2; d1 = c1 - c3; b0 = e1 + a4;
        a4 = a4 - e1; a6 = a0 - a1;
        a5 = (d1 - b0) * tc1; b0 = (b0 + d1) * tc1;
        coefs[8] = a5 + a6; c3 = c3 + c1; a1 = a1 + a0;
        d1 = (c3 - a4) * tc1; a4 = (a4 + c3) * tc1;
        a2 = a2 - d2; coefs[9] = b0 + a7;
        coefs[12] = a1 - a4; coefs[10] = a6 - a5;
        a5 = c6 - e7; coefs[11] = a7 - b0;
        a6 = d7 + b3; coefs[15] = a2 - d1;
        b0 = c5 - e2; coefs[14] = a4 + a1;
        coefs[13] = d1 + a2; coefs[16] = a5 + a6;
        d1 = e0 + b2; b3 = b3 - d7; e2 = e2 + c5;
        coefs[19] = d1 - b0; coefs[17] = b0 + d1;
        coefs[18] = a6 - a5; b2 = b2 - e0;
        e7 = e7 + c6; d7 = d0 - c0;
        coefs[20] = b3 - e2; coefs[21] = e7 + b2;
        coefs[22] = e2 + b3; coefs[23] = b2 - e7;
        d1 = d4 + c7; c0 = c0 + d0;
        c7 = c7 - d4; c6 = (d1 - d7) * tc1;
        d1 = (d7 + d1) * tc1; d7 = b1 - d3; d3 = d3 + b1;
        c5 = c4 + a3; e0 = (c0 + c7) * tc1;
        float e8 = (c7 - c0) * tc1; a3 = a3 - c4;

        coefs[24] = c6 + d7; coefs[25] = d1 + c5;
        coefs[26] = d7 - c6; coefs[27] = c5 - d1;
        coefs[28] = d3 - e0; coefs[29] = e8 + a3;
        coefs[30] = e0 + d3; coefs[31] = a3 - e8;
    }

    private static void Rotation16A(float[] coefs, float[] table, int tableOffset)
    {
        float t1 = table[tableOffset + 1];
        float a0 = coefs[0] + coefs[8], b0 = coefs[0] - coefs[8];
        float a1 = coefs[1] + coefs[9], b1 = coefs[1] - coefs[9];
        float a2 = coefs[2] + coefs[10], b2 = coefs[2] - coefs[10];
        float a3 = coefs[3] + coefs[11], b3 = coefs[3] - coefs[11];
        float a4 = coefs[4] + coefs[12], b4 = coefs[4] - coefs[12];
        float a5 = coefs[5] + coefs[13], b5 = coefs[5] - coefs[13];
        float a6 = coefs[6] + coefs[14], b6 = coefs[6] - coefs[14];
        float a7 = coefs[7] + coefs[15], b7 = coefs[7] - coefs[15];

        float c0 = a0 + a4, d0 = a0 - a4;
        float c1 = a1 + a5, d1 = a1 - a5;
        float c2 = b1 + b4; b1 = b1 - b4;
        a4 = b2 - b7; b7 = b7 + b2;
        float e0 = b0 - b5; b5 = b0 + b5;
        float c3 = a3 + a7; b0 = b6 + b3;
        a3 = a3 - a7; b3 = b3 - b6;
        b2 = a6 + a2; a2 = a2 - a6;
        b6 = (b0 + a4) * t1; b4 = (b7 - b3) * t1;
        a4 = (a4 - b0) * t1; a5 = (b3 + b7) * t1;

        coefs[0] = b2 + c0; coefs[1] = c3 + c1;
        coefs[2] = c0 - b2; coefs[3] = c1 - c3;
        coefs[4] = d0 - a3; coefs[5] = a2 + d1;
        coefs[6] = a3 + d0; coefs[7] = d1 - a2;
        coefs[8] = a4 + e0; coefs[9] = b6 + c2;
        coefs[10] = e0 - a4; coefs[12] = b5 - a5;
        coefs[15] = b1 - b4; coefs[13] = b4 + b1;
        coefs[11] = c2 - b6; coefs[14] = a5 + b5;
    }

    private static void Rotation16B(float[] coefs, float[] table, int tableOffset)
    {
        float t1 = table[tableOffset + 1];
        float t4 = table[tableOffset + 4], t5 = table[tableOffset + 5];

        float a0 = coefs[0] + coefs[9], a1 = coefs[0] - coefs[9];
        float a2 = coefs[1] + coefs[8], a3 = coefs[1] - coefs[8];
        float a4 = coefs[4] + coefs[13], a5 = coefs[4] - coefs[13];
        float a6 = coefs[5] + coefs[12], a7 = coefs[5] - coefs[12];
        float b0 = (a5 - a6) * t1, b1 = (a6 + a5) * t1;
        float b2 = (a7 + a4) * t1; t1 = (a4 - a7) * t1;
        a5 = coefs[3] + coefs[10]; a7 = coefs[3] - coefs[10];
        a6 = coefs[2] - coefs[11]; a4 = coefs[11] + coefs[2];
        float b3 = a6 * t4 - a5 * t5;
        float b4 = a6 * t5 + a5 * t4;
        a5 = coefs[6] - coefs[15];
        float b5 = a7 * t5 + a4 * t4;
        float b6 = a4 * t5 - a7 * t4;
        a4 = coefs[7] + coefs[14]; a7 = coefs[7] - coefs[14];
        float b7 = coefs[15] + coefs[6];
        float c0 = a5 * t5 - a4 * t4;
        float c1 = a4 * t5 + a5 * t4;
        a5 = c0 + b3; float c2 = b7 * t5 + a7 * t4;
        a4 = c1 + b4; a6 = b0 + a1;
        a7 = b7 * t4 - a7 * t5; t4 = b1 + a2;
        a1 = a1 - b0;
        coefs[0] = a5 + a6; b3 = b3 - c0; b4 = b4 - c1;
        coefs[1] = a4 + t4; coefs[4] = a1 - b4;
        coefs[2] = a6 - a5; t5 = b6 - a7;
        coefs[3] = t4 - a4; t4 = b5 - c2;
        a5 = t1 + a3; a2 = a2 - b1;
        coefs[6] = b4 + a1; coefs[5] = b3 + a2;
        c2 = c2 + b5; a1 = a0 - b2; b2 = b2 + a0;
        a3 = a3 - t1; a7 = a7 + b6;
        coefs[8] = t5 + a1; coefs[7] = a2 - b3;
        coefs[9] = t4 + a5; coefs[10] = a1 - t5;
        coefs[11] = a5 - t4; coefs[12] = b2 - c2;
        coefs[14] = c2 + b2; coefs[15] = a3 - a7;
        coefs[13] = a7 + a3;
    }

    private static void Swap4A(float[] coefsA, int offsetA, float[] coefsB, int offsetB)
    {
        float a0 = coefsA[offsetA], a1 = coefsA[offsetA + 1];
        float b0 = coefsB[offsetB], b1 = coefsB[offsetB + 1];
        coefsB[offsetB] = a0; coefsB[offsetB + 1] = a1;
        coefsA[offsetA] = b0; coefsA[offsetA + 1] = b1;
    }

    private static void Swap4B(float[] coefsA, int offsetA, float[] coefsB, int offsetB)
    {
        float a0 = coefsA[offsetA], a1 = coefsA[offsetA + 1];
        float b0 = coefsB[offsetB], b1 = coefsB[offsetB + 1];
        coefsB[offsetB] = a0; coefsB[offsetB + 1] = -a1;
        coefsA[offsetA] = b0; coefsA[offsetA + 1] = -b1;
    }

    private static void TransformDctPost(int samples, float[] coefs, int coefsOffset = 0)
    {
        int[] indexes = new int[64];
        int limit = 1;
        indexes[0] = 0;
        int s = samples;
        for (int k = 8; k < s; k *= 2)
        {
            s >>= 1;
            for (int i = 0; i < limit; i++)
                indexes[limit + i] = s + indexes[i];
            limit *= 2;
        }

        if (s == limit * 8)
        {
            for (int i = 0, j = 0; i < limit; i++, j += 2)
            {
                for (int k = 0; k < i; k++)
                {
                    int i1 = coefsOffset + indexes[i] + k * 2;
                    int i0 = coefsOffset + indexes[k] + j;
                    Swap4A(coefs, i0, coefs, i1);
                    i1 += limit * 2; i0 += limit * 4;
                    Swap4A(coefs, i0, coefs, i1);
                    i1 += limit * 2; i0 += limit * -2;
                    Swap4A(coefs, i0, coefs, i1);
                    i1 += limit * 2; i0 += limit * 4;
                    Swap4A(coefs, i0, coefs, i1);
                }
                int ii1 = coefsOffset + indexes[i] + j + limit * 2;
                int ii0 = ii1 + limit * 2;
                Swap4A(coefs, ii0, coefs, ii1);
            }
        }
        else
        {
            for (int i = 1, j = 2; i < limit; i++, j += 2)
            {
                for (int k = 0; k < i; k++)
                {
                    int i0 = coefsOffset + indexes[k] + j;
                    int i1 = coefsOffset + indexes[i] + k * 2;
                    Swap4A(coefs, i0, coefs, i1);
                    i0 += limit * 2; i1 += limit * 2;
                    Swap4A(coefs, i0, coefs, i1);
                }
            }
        }
    }

    private static void TransformDftPost(int samples, float[] coefs)
    {
        int[] indexes = new int[64];
        int limit = 1;
        indexes[0] = 0;
        int s = samples;
        for (int k = 8; k < s; k *= 2)
        {
            s >>= 1;
            for (int i = 0; i < limit; i++)
                indexes[limit + i] = s + indexes[i];
            limit *= 2;
        }

        if (s == limit * 8)
        {
            for (int i = 0, j = 0; i < limit; i++, j += 2)
            {
                for (int k = 0; k < i; k++)
                {
                    int i0 = indexes[k] + j;
                    int i1 = indexes[i] + k * 2;
                    Swap4B(coefs, i0, coefs, i1);
                    i0 += limit * 4; i1 += limit * 2;
                    Swap4B(coefs, i0, coefs, i1);
                    i0 += limit * -2; i1 += limit * 2;
                    Swap4B(coefs, i0, coefs, i1);
                    i0 += limit * 4; i1 += limit * 2;
                    Swap4B(coefs, i0, coefs, i1);
                }
                int i0x = indexes[i] + j;
                coefs[i0x + 1] = -coefs[i0x + 1];
                i0x += limit * 2;
                int i1x = i0x + limit * 2;
                Swap4B(coefs, i0x, coefs, i1x);
                i1x += limit * 2;
                coefs[i1x + 1] = -coefs[i1x + 1];
            }
        }
        else
        {
            int i0x = 0;
            int i1x = i0x + limit * 2;
            coefs[i0x + 1] = -coefs[i0x + 1];
            coefs[i1x + 1] = -coefs[i1x + 1];

            for (int i = 1, j = 2; i < limit; i++, j += 2)
            {
                for (int k = 0; k < i; k++)
                {
                    i0x = indexes[k] + j;
                    i1x = indexes[i] + k * 2;
                    Swap4B(coefs, i0x, coefs, i1x);
                    i0x += limit * 2; i1x += limit * 2;
                    Swap4B(coefs, i0x, coefs, i1x);
                }
                i0x = indexes[i] + j;
                i1x = i0x + limit * 2;
                coefs[i0x + 1] = -coefs[i0x + 1];
                coefs[i1x + 1] = -coefs[i1x + 1];
            }
        }
    }

    private static void TransformDctPre(int samples, float[] coefs, float[] table, int tableOffset)
    {
        int samplesOct = samples >> 3;
        int i2 = samplesOct * 2, i4 = samplesOct * 4, i6 = samplesOct * 6;

        float a0 = coefs[0] + coefs[i4], a1 = coefs[1] + coefs[i4 + 1];
        float a2 = coefs[0] - coefs[i4], a3 = coefs[1] - coefs[i4 + 1];
        float a4 = coefs[i2] + coefs[i6], a5 = coefs[i2] - coefs[i6];
        float a6 = coefs[i2 + 1] + coefs[i6 + 1], a7 = coefs[i2 + 1] - coefs[i6 + 1];

        coefs[0] = a4 + a0; coefs[1] = a6 + a1;
        coefs[i2 + 1] = a1 - a6; coefs[i2] = a0 - a4;
        coefs[i4] = a2 - a7; coefs[i4 + 1] = a5 + a3;
        coefs[i6] = a7 + a2; coefs[i6 + 1] = a3 - a5;

        a6 = table[tableOffset + 1];
        a4 = table[tableOffset + 2];
        a7 = table[tableOffset + 3];

        float bv0 = 0f, bv1 = 0f, bv2 = 1f, bv3 = 1f;

        int[] coefsOffsets0a = new int[1], coefsOffsets1a = new int[1], coefsOffsets1b = new int[1];
        int[] coefsOffsets2a = new int[1], coefsOffsets2b = new int[1];
        int[] coefsOffsets3a = new int[1], coefsOffsets3b = new int[1], coefsOffsets4b = new int[1];

        int ttOff = tableOffset + 2;
        int c0a = 4, c1a = i2 + 4, c1b = i2 - 4;
        int c2a = i2 * 2 + 4, c2b = i2 * 2 - 4;
        int c3a = i2 * 3 + 4, c3b = i2 * 3 - 4;
        int c4b = i2 * 4 - 4;

        a5 = bv0; a1 = bv2; a0 = bv3;

        int loopCount = ((samplesOct - 5) >> 2) + 1;
        for (int li = 0; li < loopCount; li++)
        {
            bv2 = table[ttOff + 2]; bv0 = table[ttOff + 3]; bv3 = table[ttOff + 4];

            a3 = (bv2 + a1) * a4;
            a2 = (bv0 + a5) * a4;
            a0 = (bv3 + a0) * a7;
            float c1v = bv1 - table[ttOff + 5];
            bv1 = -table[ttOff + 5];
            c1v = c1v * a7;

            float cv0 = coefs[c0a - 2] + coefs[c2a - 2];
            float cv2 = coefs[c0a - 2] - coefs[c2a - 2];
            float cv4 = coefs[c0a - 1] + coefs[c2a - 1];
            a5 = coefs[c0a - 1] - coefs[c2a - 1];
            float cv3 = coefs[c0a] + coefs[c2a];
            float cv5 = coefs[c0a] - coefs[c2a];
            float cv6 = coefs[c1a - 2] + coefs[c3a - 2];
            float cv7 = coefs[c1a - 2] - coefs[c3a - 2];
            float d1v = coefs[c1a - 1] + coefs[c3a - 1];
            float d2v = coefs[c1a - 1] - coefs[c3a - 1];
            float d0v = coefs[c0a + 1] + coefs[c2a + 1];
            a1 = coefs[c0a + 1] - coefs[c2a + 1];
            float d3v = coefs[c3a + 1] + coefs[c1a + 1];
            float d4v = coefs[c1a + 1] - coefs[c3a + 1];
            float d5v = coefs[c3a] + coefs[c1a];
            float d6v = coefs[c1a] - coefs[c3a];

            coefs[c0a - 2] = cv6 + cv0; coefs[c0a - 1] = d1v + cv4;
            coefs[c0a] = d5v + cv3; coefs[c0a + 1] = d3v + d0v;
            coefs[c1a - 2] = cv0 - cv6; coefs[c1a + 1] = d0v - d3v;
            coefs[c1a] = cv3 - d5v; coefs[c1a - 1] = cv4 - d1v;

            float dv3 = cv7 + a5, dv5 = cv2 - d2v;
            d2v = d2v + cv2; a5 = a5 - cv7;
            coefs[c2a - 2] = a3 * dv5 - a2 * dv3;
            coefs[c2a - 1] = a3 * dv3 + a2 * dv5;

            dv3 = d6v + a1; dv5 = cv5 - d4v;
            d4v = d4v + cv5; a1 = a1 - d6v;
            coefs[c2a] = bv2 * dv5 - bv0 * dv3;
            coefs[c2a + 1] = bv2 * dv3 + bv0 * dv5;
            coefs[c3a - 2] = c1v * a5 + a0 * d2v;
            coefs[c3a - 1] = a0 * a5 - c1v * d2v;
            coefs[c3a] = bv1 * a1 + bv3 * d4v;
            coefs[c3a + 1] = bv3 * a1 - bv1 * d4v;

            float dd3 = coefs[c1b + 3] - coefs[c3b + 3];
            float dd2 = coefs[c1b + 2] + coefs[c3b + 2];
            float dd6 = coefs[c1b + 3] + coefs[c3b + 3];
            float dd7 = coefs[c1b + 2] - coefs[c3b + 2];
            a5 = coefs[c4b];
            float dd5 = coefs[c1b + 1] - coefs[c3b + 1];
            float cc4 = coefs[c2b + 2] + coefs[c4b + 2];
            float cc0 = coefs[c1b + 1] + coefs[c3b + 1];
            float dd4 = coefs[c2b + 2] - coefs[c4b + 2];
            a1 = coefs[c2b];
            float cc7 = coefs[c1b] + coefs[c3b];
            float cc3 = coefs[c2b + 3] + coefs[c4b + 3];
            float cc2 = coefs[c2b + 3] - coefs[c4b + 3];
            float dd8 = coefs[c1b] - coefs[c3b];
            float d1x = coefs[c2b + 1] + coefs[c4b + 1];
            float cc5 = coefs[c2b + 1] - coefs[c4b + 1];
            float cc6 = a5 + a1;
            a1 = a1 - a5;
            coefs[c1b + 2] = cc4 + dd2;
            coefs[c1b] = cc6 + cc7;
            coefs[c1b + 3] = cc3 + dd6;
            float d0x = dd7 - cc2;
            a5 = dd4 + dd3;
            coefs[c2b] = cc7 - cc6;
            coefs[c1b + 1] = d1x + cc0;
            coefs[c2b + 1] = cc0 - d1x;
            coefs[c2b + 2] = dd2 - cc4;
            coefs[c3b + 2] = a2 * d0x - a3 * a5;
            coefs[c2b + 3] = dd6 - cc3;
            coefs[c3b + 3] = a2 * a5 + a3 * d0x;

            a3 = dd8 - cc5; a5 = a1 + dd5;
            float cc2x = cc2 + dd7;
            float dv3x = dd3 - dd4;
            dv5 = dd5 - a1;
            cc5 = cc5 + dd8;
            coefs[c3b] = bv0 * a3 - bv2 * a5;
            coefs[c3b + 1] = bv0 * a5 + bv2 * a3;
            coefs[c4b] = bv3 * dv5 + bv1 * cc5;
            coefs[c4b + 1] = bv1 * dv5 - bv3 * cc5;
            coefs[c4b + 2] = a0 * dv3x + c1v * cc2x;
            coefs[c4b + 3] = c1v * dv3x - a0 * cc2x;

            a5 = bv0; a1 = bv2; a0 = bv3;

            c0a += 4; c1a += 4; c1b -= 4;
            c2a += 4; c2b -= 4; c3a += 4; c3b -= 4; c4b -= 4;
            ttOff += 4;
        }

        bv0 = a5; bv2 = a1; bv3 = a0;

        int i3 = samplesOct * 3, i5 = samplesOct * 5, i7 = samplesOct * 7;

        float fd1 = (bv3 - a6) * a7;
        a7 = (bv1 - a6) * a7;
        float fd5 = (bv2 + a6) * a4;
        a5 = coefs[samplesOct - 1] + coefs[i5 - 1];
        a2 = coefs[samplesOct - 2] - coefs[i5 - 2];
        float fd3 = coefs[samplesOct - 1] - coefs[i5 - 1];
        a1 = coefs[samplesOct - 2] + coefs[i5 - 2];
        float fb2 = coefs[i3 - 1] + coefs[i7 - 1];
        float fb3 = coefs[i3 - 2] + coefs[i7 - 2];
        a0 = coefs[i3 - 2] - coefs[i7 - 2];
        a3 = coefs[i3 - 1] - coefs[i7 - 1];
        coefs[samplesOct - 2] = fb3 + a1;
        float fb1 = a0 + fd3;
        fd3 = fd3 - a0;
        a4 = (bv0 + a6) * a4;
        coefs[samplesOct - 1] = fb2 + a5;
        coefs[i3 - 1] = a5 - fb2;
        coefs[i3 - 2] = a1 - fb3;
        float fb0 = a2 - a3;
        a3 = a3 + a2;
        coefs[i5 - 2] = fd5 * fb0 - a4 * fb1;
        coefs[i5 - 1] = fd5 * fb1 + a4 * fb0;
        coefs[i7 - 2] = a7 * fd3 + fd1 * a3;
        coefs[i7 - 1] = fd1 * fd3 - a7 * a3;
        fb1 = coefs[samplesOct] + coefs[i5];
        fb2 = coefs[samplesOct + 1] + coefs[i5 + 1];
        a0 = coefs[samplesOct] - coefs[i5];
        a3 = coefs[samplesOct + 1] - coefs[i5 + 1];
        fb3 = coefs[i3] + coefs[i7];
        a5 = coefs[i3] - coefs[i7];
        fb0 = coefs[i3 + 1] + coefs[i7 + 1];
        a1 = coefs[i3 + 1] - coefs[i7 + 1];
        coefs[samplesOct] = fb3 + fb1;
        coefs[samplesOct + 1] = fb0 + fb2;
        coefs[i3 + 1] = fb2 - fb0;
        fb2 = a5 + a3;
        fb0 = a0 - a1;
        a3 = a3 - a5;
        a1 = a1 + a0;
        coefs[i3] = fb1 - fb3;
        coefs[i5] = (fb0 - fb2) * a6;
        coefs[i5 + 1] = (fb2 + fb0) * a6;
        coefs[i7] = (a3 + a1) * -a6;
        coefs[i7 + 1] = (a3 - a1) * -a6;
        fb2 = coefs[i3 + 2] + coefs[i7 + 2];
        a1 = coefs[i3 + 2] - coefs[i7 + 2];
        a5 = coefs[samplesOct + 2] + coefs[i5 + 2];
        a6 = coefs[i3 + 3] + coefs[i7 + 3];
        a0 = coefs[i3 + 3] - coefs[i7 + 3];
        fb1 = coefs[samplesOct + 3] + coefs[i5 + 3];
        a3 = coefs[samplesOct + 2] - coefs[i5 + 2];
        a2 = coefs[samplesOct + 3] - coefs[i5 + 3];

        fb3 = a3 - a0; fb0 = a1 + a2;
        a2 = a2 - a1; a0 = a0 + a3;

        coefs[samplesOct + 3] = a6 + fb1;
        coefs[samplesOct + 2] = fb2 + a5;
        coefs[i3 + 2] = a5 - fb2;
        coefs[i3 + 3] = fb1 - a6;
        coefs[i5 + 2] = a4 * fb3 - fd5 * fb0;
        coefs[i5 + 3] = a4 * fb0 + fd5 * fb3;
        coefs[i7 + 3] = a7 * a2 - fd1 * a0;
        coefs[i7 + 2] = fd1 * a2 + a7 * a0;
    }

    private static void TransformDftPre(int samples, float[] coefs, float[] table, int tableOffset)
    {
        int samplesOct = samples >> 3;
        int i2 = samplesOct * 2, i4 = samplesOct * 4, i6 = samplesOct * 6;

        float a0 = coefs[0] + coefs[i4], a1 = coefs[0] - coefs[i4];
        float a2 = -coefs[1] - coefs[i4 + 1], a3 = coefs[i4 + 1] - coefs[1];
        float a4 = coefs[i2] + coefs[i6], a5 = coefs[i2] - coefs[i6];
        float a6 = coefs[i2 + 1] + coefs[i6 + 1], a7 = coefs[i2 + 1] - coefs[i6 + 1];

        coefs[0] = a4 + a0; coefs[1] = a2 - a6;
        coefs[i2] = a0 - a4; coefs[i2 + 1] = a6 + a2;
        coefs[i4] = a7 + a1; coefs[i4 + 1] = a5 + a3;
        coefs[i6] = a1 - a7; coefs[i6 + 1] = a3 - a5;

        a6 = table[tableOffset + 1];
        a4 = table[tableOffset + 2];
        a7 = table[tableOffset + 3];

        float bv0 = 0f, bv1 = 0f, bv2 = 1f, bv3 = 1f;

        int ttOff = tableOffset + 2;
        int c0a = 4, c1a = i2 + 4, c1b = i2 - 4;
        int c2a = i2 * 2 + 4, c2b = i2 * 2 - 4;
        int c3a = i2 * 3 + 4, c3b = i2 * 3 - 4;
        int c4b = i2 * 4 - 4;

        a5 = bv0; a2 = bv2; a0 = bv3;

        int loopCount = ((samplesOct - 5) >> 2) + 1;
        for (int li = 0; li < loopCount; li++)
        {
            bv2 = table[ttOff + 2]; bv0 = table[ttOff + 3]; bv3 = table[ttOff + 4];

            a2 = (bv2 + a2) * a4;
            a3 = (bv0 + a5) * a4;
            a0 = (bv3 + a0) * a7;
            float c1v = bv1 - table[ttOff + 5];
            bv1 = -table[ttOff + 5];
            c1v = c1v * a7;

            float cv0 = coefs[c0a - 2] + coefs[c2a - 2];
            a5 = coefs[c0a - 2] - coefs[c2a - 2];
            float cv2 = -coefs[c0a - 1] - coefs[c2a - 1];
            a1 = coefs[c0a] - coefs[c2a];
            float cv3 = -coefs[c0a + 1] - coefs[c2a + 1];
            float cv4 = coefs[c1a] + coefs[c3a];
            float cv5 = coefs[c1a] - coefs[c3a];
            float cv6 = coefs[c1a - 1] - coefs[c3a - 1];
            float cv7 = coefs[c1a + 1] + coefs[c3a + 1];
            float d0v = coefs[c1a + 1] - coefs[c3a + 1];
            float d1v = coefs[c1a - 2] - coefs[c3a - 2];
            float d2v = coefs[c2a] + coefs[c0a];
            float d3v = coefs[c2a - 1] - coefs[c0a - 1];
            float d4v = coefs[c2a + 1] - coefs[c0a + 1];
            float d5v = coefs[c3a - 1] + coefs[c1a - 1];
            float d6v = coefs[c3a - 2] + coefs[c1a - 2];

            coefs[c0a - 1] = cv2 - d5v;
            coefs[c0a - 2] = d6v + cv0;
            coefs[c0a + 1] = cv3 - cv7;
            coefs[c0a] = cv4 + d2v;
            coefs[c1a + 1] = cv7 + cv3;
            coefs[c1a] = d2v - cv4;
            float c7v = d1v + d3v;
            coefs[c1a - 1] = d5v + cv2;
            coefs[c1a - 2] = cv0 - d6v;
            float c4v = cv5 + d4v;
            float d5x = cv6 + a5;
            float d3x = d3v - d1v;
            a5 = a5 - cv6;
            coefs[c2a - 2] = a2 * d5x - a3 * c7v;
            coefs[c2a - 1] = a2 * c7v + a3 * d5x;
            c7v = d0v + a1;
            a1 = a1 - d0v;
            d4v = d4v - cv5;
            coefs[c2a] = bv2 * c7v - bv0 * c4v;
            coefs[c2a + 1] = bv2 * c4v + bv0 * c7v;
            coefs[c3a - 2] = c1v * d3x + a0 * a5;
            coefs[c3a - 1] = a0 * d3x - c1v * a5;
            coefs[c3a] = bv1 * d4v + bv3 * a1;
            coefs[c3a + 1] = bv3 * d4v - bv1 * a1;

            float cc6 = coefs[c1b + 2] + coefs[c3b + 2];
            a1 = coefs[c1b + 2] - coefs[c3b + 2];
            float cc3 = coefs[c2b + 2] + coefs[c4b + 2];
            float cc5 = coefs[c2b + 2] - coefs[c4b + 2];
            float dd6 = coefs[c2b + 3] + coefs[c4b + 3];
            float dd0 = coefs[c2b + 3] - coefs[c4b + 3];
            a5 = coefs[c3b + 1];
            float cc0 = -coefs[c1b + 3] - coefs[c3b + 3];
            float dd4 = coefs[c3b + 3] - coefs[c1b + 3];
            float cc7 = coefs[c1b] - coefs[c3b];
            float cc2 = coefs[c1b] + coefs[c3b];
            float dd5 = coefs[c4b] + coefs[c2b];
            float dd3 = coefs[c2b] - coefs[c4b];
            float cc4 = coefs[c2b + 1] + coefs[c4b + 1];
            float dd1 = coefs[c2b + 1] - coefs[c4b + 1];
            coefs[c1b + 3] = cc0 - dd6;
            float dd2 = -coefs[c1b + 1] - a5;
            a5 = a5 - coefs[c1b + 1];
            coefs[c1b + 1] = dd2 - cc4;
            coefs[c1b + 2] = cc3 + cc6;
            coefs[c1b] = dd5 + cc2;
            coefs[c2b + 3] = dd6 + cc0;
            coefs[c2b + 1] = cc4 + dd2;
            dd6 = dd0 + a1;
            coefs[c2b] = cc2 - dd5;
            c4v = cc5 + dd4;
            coefs[c2b + 2] = cc6 - cc3;
            coefs[c3b + 3] = a3 * c4v + a2 * dd6;
            coefs[c3b + 2] = a3 * dd6 - a2 * c4v;
            a3 = dd1 + cc7;
            a2 = dd3 + a5;
            coefs[c3b] = bv0 * a3 - bv2 * a2;
            a5 = a5 - dd3;
            a1 = a1 - dd0;
            coefs[c3b + 1] = bv0 * a2 + bv2 * a3;
            dd4 = dd4 - cc5;
            cc7 = cc7 - dd1;
            coefs[c4b + 2] = a0 * dd4 + c1v * a1;
            coefs[c4b + 3] = c1v * dd4 - a0 * a1;
            coefs[c4b] = bv3 * a5 + bv1 * cc7;
            coefs[c4b + 1] = bv1 * a5 - bv3 * cc7;

            a5 = bv0; a2 = bv2; a0 = bv3;

            c0a += 4; c1a += 4; c2a += 4; c3a += 4;
            c1b -= 4; c2b -= 4; c3b -= 4; c4b -= 4;
            ttOff += 4;
        }

        bv0 = a5; bv2 = a2; bv3 = a0;

        int i3 = samplesOct * 3, i5 = samplesOct * 5, i7 = samplesOct * 7;

        float fc4 = (bv3 - a6) * a7;
        a7 = (bv1 - a6) * a7;
        float fc7 = (bv2 + a6) * a4;
        a2 = coefs[samplesOct - 2] + coefs[i5 - 2];
        a1 = coefs[samplesOct - 2] - coefs[i5 - 2];
        float fb2 = coefs[i3 - 1] + coefs[i7 - 1];
        a5 = coefs[i3 - 1] - coefs[i7 - 1];
        float fb3 = coefs[i3 - 2] + coefs[i7 - 2];
        a0 = coefs[i3 - 2] - coefs[i7 - 2];
        float fb1 = -coefs[samplesOct - 1] - coefs[i5 - 1];
        a3 = coefs[i5 - 1] - coefs[samplesOct - 1];
        a4 = (bv0 + a6) * a4;
        coefs[samplesOct - 2] = fb3 + a2;
        coefs[samplesOct - 1] = fb1 - fb2;
        coefs[i3 - 1] = fb2 + fb1;
        fb2 = a5 + a1; a1 = a1 - a5;
        coefs[i3 - 2] = a2 - fb3;
        float fb0 = a0 + a3; a3 = a3 - a0;
        coefs[i5 - 2] = fc7 * fb2 - a4 * fb0;
        coefs[i5 - 1] = fc7 * fb0 + a4 * fb2;
        coefs[i7 - 2] = a7 * a3 + fc4 * a1;
        coefs[i7 - 1] = fc4 * a3 - a7 * a1;
        a5 = coefs[samplesOct] + coefs[i5];
        fb2 = -coefs[samplesOct + 1] - coefs[i5 + 1];
        a3 = coefs[samplesOct] - coefs[i5];
        fb0 = coefs[i3 + 1] + coefs[i7 + 1];
        fb3 = coefs[i3] + coefs[i7];
        a2 = coefs[i3] - coefs[i7];
        fb1 = coefs[i3 + 1] - coefs[i7 + 1];
        a0 = coefs[i5 + 1] - coefs[samplesOct + 1];
        coefs[samplesOct + 1] = fb2 - fb0;
        coefs[samplesOct] = fb3 + a5;
        coefs[i3 + 1] = fb0 + fb2;
        fb0 = fb1 + a3; fb2 = a2 + a0;
        a3 = a3 - fb1;
        coefs[i3] = a5 - fb3;
        coefs[i5] = (fb0 - fb2) * a6;
        coefs[i5 + 1] = (fb2 + fb0) * a6;
        a0 = a0 - a2;
        coefs[i7] = (a0 + a3) * -a6;
        coefs[i7 + 1] = (a0 - a3) * -a6;
        fb0 = coefs[i3 + 2] + coefs[i7 + 2];
        a6 = coefs[i3 + 3] + coefs[i7 + 3];
        fb1 = coefs[samplesOct + 2] + coefs[i5 + 2];
        a5 = coefs[i3 + 2] - coefs[i7 + 2];
        fb3 = coefs[i3 + 3] - coefs[i7 + 3];
        a0 = coefs[samplesOct + 2] - coefs[i5 + 2];
        fb2 = -coefs[samplesOct + 3] - coefs[i5 + 3];
        a2 = coefs[i5 + 3] - coefs[samplesOct + 3];

        coefs[samplesOct + 3] = fb2 - a6;
        coefs[samplesOct + 2] = fb0 + fb1;
        coefs[i3 + 3] = a6 + fb2;

        fb2 = fb3 + a0; a6 = a5 + a2;
        a2 = a2 - a5; a0 = a0 - fb3;

        coefs[i3 + 2] = fb1 - fb0;
        coefs[i5 + 2] = a4 * fb2 - fc7 * a6;
        coefs[i5 + 3] = a4 * a6 + fc7 * fb2;
        coefs[i7 + 3] = a7 * a2 - fc4 * a0;
        coefs[i7 + 2] = fc4 * a2 + a7 * a0;
    }

    private static void RotationMainA(int samples, float[] coefs, int coefsOffset, float[] table, int tableOffset)
    {
        int samplesOct = samples >> 3;
        {
            int i0 = 0, i2 = samplesOct * 2, i4 = samplesOct * 4, i6 = samplesOct * 6;
            float a0 = coefs[i0] + coefs[i4], a1 = coefs[i0] - coefs[i4];
            float a2 = coefs[i0 + 1] + coefs[i4 + 1], a3 = coefs[i0 + 1] - coefs[i4 + 1];
            float a4 = coefs[i2] + coefs[i6], a5 = coefs[i2] - coefs[i6];
            float a6 = coefs[i2 + 1] + coefs[i6 + 1], a7 = coefs[i2 + 1] - coefs[i6 + 1];
            coefs[i0] = a4 + a0; coefs[i0 + 1] = a6 + a2;
            coefs[i2] = a0 - a4; coefs[i2 + 1] = a2 - a6;
            coefs[i4] = a1 - a7; coefs[i4 + 1] = a5 + a3;
            coefs[i6] = a7 + a1; coefs[i6 + 1] = a3 - a5;
        }
        {
            for (int i = 2, j = 4; i < samplesOct; i += 2, j += 4)
            {
                float t0 = table[tableOffset + j], t1 = table[tableOffset + j + 1];
                float t2 = table[tableOffset + j + 2], t3 = -table[tableOffset + j + 3];
                float a0 = coefs[i] + coefs[samplesOct * 4 + i];
                float a1 = coefs[i] - coefs[samplesOct * 4 + i];
                float a2 = coefs[i + 1] + coefs[samplesOct * 4 + i + 1];
                float a3 = coefs[i + 1] - coefs[samplesOct * 4 + i + 1];
                float a4 = coefs[samplesOct * 2 + i] - coefs[samplesOct * 6 + i];
                float a5 = coefs[samplesOct * 2 + i] + coefs[samplesOct * 6 + i];
                float a6 = coefs[samplesOct * 2 + i + 1] + coefs[samplesOct * 6 + i + 1];
                float a7 = coefs[samplesOct * 2 + i + 1] - coefs[samplesOct * 6 + i + 1];
                coefs[i] = a5 + a0; coefs[i + 1] = a6 + a2;
                coefs[samplesOct * 2 + i] = a0 - a5;
                coefs[samplesOct * 2 + i + 1] = a2 - a6;
                float b0 = a1 + a7, b1 = a1 - a7;
                float b2 = a3 + a4, b3 = a3 - a4;
                coefs[samplesOct * 4 + i] = b1 * t0 - b2 * t1;
                coefs[samplesOct * 4 + i + 1] = b2 * t0 + b1 * t1;
                coefs[samplesOct * 6 + i] = b3 * t3 + b0 * t2;
                coefs[samplesOct * 6 + i + 1] = b3 * t2 - b0 * t3;
            }
            for (int i = 2, j = 4; i < samplesOct; i += 2, j += 4)
            {
                float t0 = table[tableOffset + j], t1 = table[tableOffset + j + 1];
                float t2 = table[tableOffset + j + 2], t3 = -table[tableOffset + j + 3];
                float a0 = coefs[samplesOct * 6 - i] + coefs[samplesOct * 2 - i];
                float a1 = coefs[samplesOct * 2 - i] - coefs[samplesOct * 6 - i];
                float a2 = coefs[samplesOct * 6 + 1 - i] + coefs[samplesOct * 2 + 1 - i];
                float a3 = coefs[samplesOct * 2 + 1 - i] - coefs[samplesOct * 6 + 1 - i];
                float a5 = coefs[samplesOct * 8 - i] + coefs[samplesOct * 4 - i];
                float a6 = coefs[samplesOct * 4 + 1 - i] + coefs[samplesOct * 8 + 1 - i];
                float a7 = coefs[samplesOct * 4 + 1 - i] - coefs[samplesOct * 8 + 1 - i];
                float a4 = coefs[samplesOct * 4 - i] - coefs[samplesOct * 8 - i];
                coefs[samplesOct * 2 - i] = a5 + a0;
                coefs[samplesOct * 2 + 1 - i] = a6 + a2;
                coefs[samplesOct * 4 - i] = a0 - a5;
                coefs[samplesOct * 4 + 1 - i] = a2 - a6;
                float b0 = a1 + a7, b1 = a1 - a7;
                float b2 = a3 + a4, b3 = a3 - a4;
                coefs[samplesOct * 6 - i] = b1 * t1 - b2 * t0;
                coefs[samplesOct * 6 + 1 - i] = b2 * t1 + b1 * t0;
                coefs[samplesOct * 8 - i] = b3 * t2 + b0 * t3;
                coefs[samplesOct * 8 + 1 - i] = b3 * t3 - b0 * t2;
            }
        }
        {
            int i1 = samplesOct, i3 = samplesOct * 3, i5 = samplesOct * 5, i7 = samplesOct * 7;
            float t1 = table[tableOffset + 1];
            float a0 = coefs[i1] + coefs[i5], a1 = coefs[i1] - coefs[i5];
            float a2 = coefs[i1 + 1] + coefs[i5 + 1], a3 = coefs[i1 + 1] - coefs[i5 + 1];
            float a4 = coefs[i3] + coefs[i7], a5 = coefs[i3] - coefs[i7];
            float a6 = coefs[i3 + 1] + coefs[i7 + 1], a7 = coefs[i3 + 1] - coefs[i7 + 1];
            float b0 = a5 + a3, b1 = a3 - a5;
            float b2 = a1 - a7, b3 = a7 + a1;
            coefs[i1] = a4 + a0; coefs[i1 + 1] = a6 + a2;
            coefs[i3] = a0 - a4; coefs[i3 + 1] = a2 - a6;
            coefs[i5] = (b2 - b0) * t1; coefs[i5 + 1] = (b0 + b2) * t1;
            coefs[i7 + 1] = (b1 - b3) * -t1; coefs[i7] = (b1 + b3) * -t1;
        }
    }

    private static void RotationMainB(int samples, float[] coefs, int coefsOffset, float[] table, int tableOffset)
    {
        int samplesOct = samples >> 3;
        {
            float t1 = table[tableOffset + 1];
            int i0 = 0, i2 = samplesOct * 2, i4 = samplesOct * 4, i6 = samplesOct * 6;
            float a0 = coefs[i0] + coefs[i4 + 1], a1 = coefs[i0] - coefs[i4 + 1];
            float a2 = coefs[i0 + 1] + coefs[i4], a3 = coefs[i0 + 1] - coefs[i4];
            float a4 = coefs[i2] + coefs[i6 + 1], a5 = coefs[i2] - coefs[i6 + 1];
            float a6 = coefs[i2 + 1] + coefs[i6], a7 = coefs[i2 + 1] - coefs[i6];
            float b0 = (a7 + a4) * t1;
            float b1 = (a4 - a7) * t1;
            float b2 = (a6 + a5) * t1;
            float b3 = (a5 - a6) * t1;
            coefs[i0] = b3 + a1; coefs[i0 + 1] = b2 + a2;
            coefs[i2] = a1 - b3; coefs[i2 + 1] = a2 - b2;
            coefs[i4] = a0 - b0; coefs[i4 + 1] = b1 + a3;
            coefs[i6] = b0 + a0; coefs[i6 + 1] = a3 - b1;
        }
        {
            int i4off = samplesOct * 4;
            for (int i = 2, j = 4; i < samplesOct; i += 2, j += 4)
            {
                float t0 = table[tableOffset + j], t1 = table[tableOffset + j + 1];
                float t2 = table[tableOffset + j + 2], t3 = -table[tableOffset + j + 3];
                float t4 = table[tableOffset + i4off - j], t5 = table[tableOffset + i4off - j + 1];
                float t6 = table[tableOffset + i4off - j + 2], t7 = -table[tableOffset + i4off - j + 3];
                float a0 = coefs[i] - coefs[samplesOct * 4 + i + 1];
                float a1 = coefs[i + 1] + coefs[samplesOct * 4 + i];
                float a2 = coefs[i + 1] - coefs[samplesOct * 4 + i];
                float a3 = coefs[samplesOct * 2 + i] + coefs[samplesOct * 6 + i + 1];
                float a4 = coefs[samplesOct * 2 + i] - coefs[samplesOct * 6 + i + 1];
                float a5 = coefs[samplesOct * 2 + i + 1] + coefs[samplesOct * 6 + i];
                float a6 = coefs[samplesOct * 2 + i + 1] - coefs[samplesOct * 6 + i];
                float a7 = coefs[samplesOct * 4 + i + 1] + coefs[i];
                float bv0 = a0 * t1 + a1 * t0;
                float bv1 = a0 * t0 - a1 * t1;
                float bv2 = a4 * t4 + a5 * t5;
                float bv3 = a4 * t5 - a5 * t4;
                coefs[i] = bv3 + bv1; coefs[i + 1] = bv2 + bv0;
                coefs[samplesOct * 2 + i] = bv1 - bv3;
                coefs[samplesOct * 2 + i + 1] = bv0 - bv2;
                bv0 = a2 * t3 + a7 * t2;
                a5 = a2 * t2 - a7 * t3;
                a0 = a6 * t6 + a3 * t7;
                a1 = a6 * t7 - a3 * t6;
                coefs[samplesOct * 4 + i] = a0 + bv0;
                coefs[samplesOct * 4 + i + 1] = a1 + a5;
                coefs[samplesOct * 6 + i + 1] = a5 - a1;
                coefs[samplesOct * 6 + i] = bv0 - a0;
            }
            for (int i = 2, j = 4; i < samplesOct; i += 2, j += 4)
            {
                float t0 = table[tableOffset + j], t1 = table[tableOffset + j + 1];
                float t2 = table[tableOffset + j + 2], t3 = -table[tableOffset + j + 3];
                float t4 = table[tableOffset + i4off - j], t5 = table[tableOffset + i4off - j + 1];
                float t6 = table[tableOffset + i4off - j + 2], t7 = -table[tableOffset + i4off - j + 3];
                float bv0 = coefs[samplesOct * 4 - i] - coefs[samplesOct * 8 + 1 - i];
                float a2 = coefs[samplesOct * 8 + 1 - i] + coefs[samplesOct * 4 - i];
                float a1 = coefs[samplesOct * 2 + 1 - i] + coefs[samplesOct * 6 - i];
                float a0 = coefs[samplesOct * 4 + 1 - i] + coefs[samplesOct * 8 - i];
                float a6 = coefs[samplesOct * 2 + 1 - i] - coefs[samplesOct * 6 - i];
                float a4 = coefs[samplesOct * 4 + 1 - i] - coefs[samplesOct * 8 - i];
                float bv1 = coefs[samplesOct * 2 - i] - coefs[samplesOct * 6 + 1 - i];
                float a3 = coefs[samplesOct * 6 + 1 - i] + coefs[samplesOct * 2 - i];
                float c0 = t4 * bv1 - t5 * a1;
                float c1 = t4 * a1 + t5 * bv1;
                float c2 = t1 * bv0 - t0 * a0;
                float c3 = t1 * a0 + t0 * bv0;
                float c4 = a6 * t6 - a3 * t7;
                float c5 = a6 * t7 + a3 * t6;
                float c6 = a4 * t3 - a2 * t2;
                float c7 = a4 * t2 + a2 * t3;
                coefs[samplesOct * 2 - i] = c2 + c0;
                coefs[samplesOct * 2 + 1 - i] = c3 + c1;
                coefs[samplesOct * 4 - i] = c0 - c2;
                coefs[samplesOct * 4 + 1 - i] = c1 - c3;
                coefs[samplesOct * 6 - i] = c7 + c5;
                coefs[samplesOct * 6 + 1 - i] = c6 + c4;
                coefs[samplesOct * 8 - i] = c5 - c7;
                coefs[samplesOct * 8 + 1 - i] = c4 - c6;
            }
        }
        {
            int i1 = samplesOct, i3 = samplesOct * 3, i5 = samplesOct * 5, i7 = samplesOct * 7;
            int i2x = samplesOct * 2;
            float t0 = table[tableOffset + i2x], t1 = table[tableOffset + i2x + 1];
            float a0 = coefs[i1] + coefs[i5 + 1], a1 = coefs[i1] - coefs[i5 + 1];
            float a2 = coefs[i1 + 1] + coefs[i5], a3 = coefs[i1 + 1] - coefs[i5];
            float a4 = coefs[i3] + coefs[i7 + 1], a5 = coefs[i3] - coefs[i7 + 1];
            float a6 = coefs[i3 + 1] + coefs[i7], a7 = coefs[i3 + 1] - coefs[i7];
            float bv0 = a1 * t0 - a2 * t1;
            float bv1 = a2 * t0 + a1 * t1;
            float bv2 = a5 * t1 - t0 * a6;
            float bv3 = a6 * t1 + t0 * a5;
            float c0 = a3 * t1 + a0 * t0;
            float c1 = a0 * t1 - a3 * t0;
            float c2 = a7 * t0 + a4 * t1;
            float c3 = a4 * t0 - a7 * t1;
            coefs[i1] = bv2 + bv0; coefs[i1 + 1] = bv3 + bv1;
            coefs[i3] = bv0 - bv2; coefs[i3 + 1] = bv1 - bv3;
            coefs[i5] = c1 - c3; coefs[i5 + 1] = c0 - c2;
            coefs[i7] = c3 + c1; coefs[i7 + 1] = c2 + c0;
        }
    }

    private static void ApplyRot32(System.Action<float[], float[], int> fn, float[] coefs, int co, float[] table, int tOff)
    {
        float[] t = new float[32];
        System.Array.Copy(coefs, co, t, 0, 32);
        fn(t, table, tOff);
        System.Array.Copy(t, 0, coefs, co, 32);
    }

    private static void ApplyRot16(System.Action<float[], float[], int> fn, float[] coefs, int co, float[] table, int tOff)
    {
        float[] t = new float[16];
        System.Array.Copy(coefs, co, t, 0, 16);
        fn(t, table, tOff);
        System.Array.Copy(t, 0, coefs, co, 16);
    }

    private static void Transform128A(int samples, float[] coefs, int coefsOffset, int points, float[] table)
    {
        if (samples == 128)
        {
            ApplyRot32(Rotation32A, coefs, coefsOffset + 0, table, points - 8);
            ApplyRot32(Rotation32B, coefs, coefsOffset + 32, table, points - 32);
            ApplyRot32(Rotation32A, coefs, coefsOffset + 64, table, points - 8);
            ApplyRot32(Rotation32A, coefs, coefsOffset + 96, table, points - 8);
        }
        else
        {
            ApplyRot16(Rotation16A, coefs, coefsOffset + 0, table, points - 16);
            ApplyRot16(Rotation16B, coefs, coefsOffset + 16, table, points - 16);
            ApplyRot16(Rotation16A, coefs, coefsOffset + 32, table, points - 16);
            ApplyRot16(Rotation16A, coefs, coefsOffset + 48, table, points - 16);
        }
    }

    private static void Transform128B(int samples, float[] coefs, int coefsOffset, int points, float[] table)
    {
        if (samples == 128)
        {
            ApplyRot32(Rotation32A, coefs, coefsOffset + 0, table, points - 8);
            ApplyRot32(Rotation32B, coefs, coefsOffset + 32, table, points - 32);
            ApplyRot32(Rotation32A, coefs, coefsOffset + 64, table, points - 8);
            ApplyRot32(Rotation32B, coefs, coefsOffset + 96, table, points - 32);
        }
        else
        {
            ApplyRot16(Rotation16A, coefs, coefsOffset + 0, table, points - 16);
            ApplyRot16(Rotation16B, coefs, coefsOffset + 16, table, points - 16);
            ApplyRot16(Rotation16A, coefs, coefsOffset + 32, table, points - 16);
            ApplyRot16(Rotation16B, coefs, coefsOffset + 48, table, points - 16);
        }
    }


    private static void RotationMainAOffset(int samples, float[] coefs, int coefsOffset, float[] table, int tableOffset)
    {
        if (coefsOffset == 0) { RotationMainA(samples, coefs, 0, table, tableOffset); return; }
        int len = samples;
        if (coefsOffset + len > coefs.Length)
        {
            System.Console.WriteLine($"RotationMainAOffset OOB: coefsOffset={coefsOffset}, len={len}, coefsLen={coefs.Length}");
            return;
        }
        float[] temp = new float[len];
        System.Array.Copy(coefs, coefsOffset, temp, 0, len);
        RotationMainA(samples, temp, 0, table, tableOffset);
        System.Array.Copy(temp, 0, coefs, coefsOffset, len);
    }

    private static void RotationMainBOffset(int samples, float[] coefs, int coefsOffset, float[] table, int tableOffset)
    {
        if (coefsOffset == 0) { RotationMainB(samples, coefs, 0, table, tableOffset); return; }
        float[] temp = new float[samples + (samples >> 2)];
        System.Array.Copy(coefs, coefsOffset, temp, 0, samples);
        RotationMainB(samples, temp, 0, table, tableOffset);
        System.Array.Copy(temp, 0, coefs, coefsOffset, samples);
    }

    private static void Transform128AOffset(int samples, float[] coefs, int coefsOffset, int points, float[] table)
    {
        if (coefsOffset == 0) { Transform128A(samples, coefs, 0, points, table); return; }
        float[] temp = new float[128];
        System.Array.Copy(coefs, coefsOffset, temp, 0, 128);
        Transform128A(samples, temp, 0, points, table);
        System.Array.Copy(temp, 0, coefs, coefsOffset, 128);
    }

    private static void Transform128BOffset(int samples, float[] coefs, int coefsOffset, int points, float[] table)
    {
        if (coefsOffset == 0) { Transform128B(samples, coefs, 0, points, table); return; }
        float[] temp = new float[128];
        System.Array.Copy(coefs, coefsOffset, temp, 0, 128);
        Transform128B(samples, temp, 0, points, table);
        System.Array.Copy(temp, 0, coefs, coefsOffset, 128);
    }

    private static void Transform256A(int samples, float[] coefs, int coefsOffset, int points, float[] table)
    {
        int step, substep, samplesTmp;
        for (samplesTmp = samples >> 2; ; samplesTmp >>= 2)
        {
            step = samplesTmp;
            substep = samplesTmp >> 1;
            if (128 >= samplesTmp) break;
            for (; step < samples; step *= 4)
            {
                for (int i = step - samplesTmp; i < samples; i += step * 4)
                {
                    RotationMainAOffset(samplesTmp, coefs, coefsOffset + i, table, points - substep);
                    RotationMainBOffset(samplesTmp, coefs, coefsOffset + i + step, table, points - samplesTmp);
                    RotationMainAOffset(samplesTmp, coefs, coefsOffset + i + step * 2, table, points - substep);
                }
            }
            RotationMainAOffset(samplesTmp, coefs, coefsOffset + samples - samplesTmp, table, points - substep);
        }
        for (; step < samples; step *= 4)
        {
            for (int i = step - samplesTmp; i < samples; i += step * 4)
            {
                RotationMainAOffset(samplesTmp, coefs, coefsOffset + i, table, points - substep);
                Transform128AOffset(samplesTmp, coefs, coefsOffset + i, points, table);
                RotationMainBOffset(samplesTmp, coefs, coefsOffset + i + step, table, points - samplesTmp);
                Transform128BOffset(samplesTmp, coefs, coefsOffset + i + step, points, table);
                RotationMainAOffset(samplesTmp, coefs, coefsOffset + i + step * 2, table, points - substep);
                Transform128AOffset(samplesTmp, coefs, coefsOffset + i + step * 2, points, table);
            }
        }
        RotationMainAOffset(samplesTmp, coefs, coefsOffset + samples - samplesTmp, table, points - substep);
        Transform128AOffset(samplesTmp, coefs, coefsOffset + samples - samplesTmp, points, table);
    }

    private static void Transform256B(int samples, float[] coefs, int coefsOffset, int points, float[] table)
    {
        int samplesHalf = samples >> 1;
        int step, samplesTmp;
        for (samplesTmp = samples >> 2; ; samplesTmp >>= 2)
        {
            step = samplesTmp;
            if (128 >= samplesTmp) break;
            for (; step < samplesHalf; step *= 4)
            {
                for (int i = step - samplesTmp; i < samplesHalf; i += step * 2)
                {
                    RotationMainAOffset(samplesTmp, coefs, coefsOffset + i, table, points - (samplesTmp >> 1));
                    RotationMainAOffset(samplesTmp, coefs, coefsOffset + i + samplesHalf, table, points - (samplesTmp >> 1));
                }
                for (int i = step * 2 - samplesTmp; i < samplesHalf; i += step * 4)
                {
                    RotationMainBOffset(samplesTmp, coefs, coefsOffset + i, table, points - samplesTmp);
                    RotationMainBOffset(samplesTmp, coefs, coefsOffset + i + samplesHalf, table, points - samplesTmp);
                }
            }
        }
        for (; step < samplesHalf; step *= 4)
        {
            for (int i = step - samplesTmp; i < samplesHalf; i += step * 2)
            {
                RotationMainAOffset(samplesTmp, coefs, coefsOffset + i, table, points - (samplesTmp >> 1));
                Transform128AOffset(samplesTmp, coefs, coefsOffset + i, points, table);
                RotationMainAOffset(samplesTmp, coefs, coefsOffset + i + samplesHalf, table, points - (samplesTmp >> 1));
                Transform128AOffset(samplesTmp, coefs, coefsOffset + i + samplesHalf, points, table);
            }
            for (int i = step * 2 - samplesTmp; i < samplesHalf; i += step * 4)
            {
                RotationMainBOffset(samplesTmp, coefs, coefsOffset + i, table, points - samplesTmp);
                Transform128BOffset(samplesTmp, coefs, coefsOffset + i, points, table);
                RotationMainBOffset(samplesTmp, coefs, coefsOffset + i + samplesHalf, table, points - samplesTmp);
                Transform128BOffset(samplesTmp, coefs, coefsOffset + i + samplesHalf, points, table);
            }
        }
    }

    private static void Transform512A(int samples, float[] coefs, int coefsOffset, int txPoints, float[] table)
    {
        RotationMainAOffset(samples, coefs, coefsOffset, table, txPoints + (samples >> 2) * -2);
        int co = coefsOffset;
        while (samples > 512)
        {
            samples >>= 2;
            Transform512A(samples, coefs, co, txPoints, table);
            Transform512B(samples, coefs, co + samples, txPoints, table);
            Transform512A(samples, coefs, co + samples * 2, txPoints, table);
            co += samples * 3;
            RotationMainAOffset(samples, coefs, co, table, txPoints + (samples >> 2) * -2);
        }
        Transform256A(samples, coefs, co, txPoints, table);
    }

    private static void Transform512B(int samples, float[] coefs, int coefsOffset, int txPoints, float[] table)
    {
        RotationMainBOffset(samples, coefs, coefsOffset, table, txPoints - samples);
        int co = coefsOffset;
        while (samples > 512)
        {
            samples >>= 2;
            Transform512A(samples, coefs, co, txPoints, table);
            Transform512B(samples, coefs, co + samples, txPoints, table);
            Transform512A(samples, coefs, co + samples * 2, txPoints, table);
            co += samples * 3;
            RotationMainBOffset(samples, coefs, co, table, txPoints - samples);
        }
        Transform256B(samples, coefs, co, txPoints, table);
    }

    private static void TransformDctMain(int samples, float[] coefs, int coefsOffset, int points, float[] table)
    {
        int samplesQrt = samples >> 2;
        if (samples > 32)
        {
            if (coefsOffset == 0)
            {
                TransformDctPre(samples, coefs, table, points - samplesQrt);
                if (samples > 512)
                {
                    Transform512A(samplesQrt, coefs, 0 + samplesQrt * 0, points, table);
                    Transform512B(samplesQrt, coefs, 0 + samplesQrt * 1, points, table);
                    Transform512A(samplesQrt, coefs, 0 + samplesQrt * 2, points, table);
                    Transform512A(samplesQrt, coefs, 0 + samplesQrt * 3, points, table);
                }
                else if (samples > 128)
                    Transform256A(samples, coefs, 0, points, table);
                else
                    Transform128A(samples, coefs, 0, points, table);
                TransformDctPost(samples, coefs, 0);
            }
            else
            {
                float[] temp = new float[samples + (samples >> 2)];
                System.Array.Copy(coefs, coefsOffset, temp, 0, samples);
                TransformDctPre(samples, temp, table, points - samplesQrt);
                if (samples > 512)
                {
                    Transform512A(samplesQrt, temp, 0 + samplesQrt * 0, points, table);
                    Transform512B(samplesQrt, temp, 0 + samplesQrt * 1, points, table);
                    Transform512A(samplesQrt, temp, 0 + samplesQrt * 2, points, table);
                    Transform512A(samplesQrt, temp, 0 + samplesQrt * 3, points, table);
                }
                else if (samples > 128)
                    Transform256A(samples, temp, 0, points, table);
                else
                    Transform128A(samples, temp, 0, points, table);
                TransformDctPost(samples, temp, 0);
                System.Array.Copy(temp, 0, coefs, coefsOffset, samples);
            }
        }
        else
        {
            if (coefsOffset == 0)
            {
                if (samples <= 8)
                {
                    if (samples == 8) Transform8Dct(coefs);
                    else if (samples == 4) Transform4(coefs);
                }
                else if (samples == 32)
                {
                    Rotation32A(coefs, table, points - 8);
                    Transform32Swap(coefs);
                }
                else
                {
                    Rotation16A(coefs, table, 0);
                    Transform4Dct(coefs);
                }
            }
            else
            {
                float[] temp = new float[samples + (samples >> 2)];
                System.Array.Copy(coefs, coefsOffset, temp, 0, samples);
                if (samples <= 8)
                {
                    if (samples == 8) Transform8Dct(temp);
                    else if (samples == 4) Transform4(temp);
                }
                else if (samples == 32)
                {
                    Rotation32A(temp, table, points - 8);
                    Transform32Swap(temp);
                }
                else
                {
                    Rotation16A(temp, table, 0);
                    Transform4Dct(temp);
                }
                System.Array.Copy(temp, 0, coefs, coefsOffset, samples);
            }
        }
    }

    private static void TransformDftMain(int samples, float[] coefs, int coefsOffset, int points, float[] table)
    {
        int samplesQrt = samples >> 2;
        if (samples > 32)
        {
            if (coefsOffset == 0)
            {
                TransformDftPre(samples, coefs, table, points - samplesQrt);
                if (samples > 512)
                {
                    Transform512A(samplesQrt, coefs, 0 + samplesQrt * 0, points, table);
                    Transform512B(samplesQrt, coefs, 0 + samplesQrt * 1, points, table);
                    Transform512A(samplesQrt, coefs, 0 + samplesQrt * 2, points, table);
                    Transform512A(samplesQrt, coefs, 0 + samplesQrt * 3, points, table);
                }
                else if (samples > 128)
                    Transform256A(samples, coefs, 0, points, table);
                else
                    Transform128A(samples, coefs, 0, points, table);
                TransformDftPost(samples, coefs);
            }
            else
            {
                float[] temp = new float[samples + (samples >> 2)];
                System.Array.Copy(coefs, coefsOffset, temp, 0, samples);
                TransformDftPre(samples, temp, table, points - samplesQrt);
                if (samples > 512)
                {
                    Transform512A(samplesQrt, temp, 0 + samplesQrt * 0, points, table);
                    Transform512B(samplesQrt, temp, 0 + samplesQrt * 1, points, table);
                    Transform512A(samplesQrt, temp, 0 + samplesQrt * 2, points, table);
                    Transform512A(samplesQrt, temp, 0 + samplesQrt * 3, points, table);
                }
                else if (samples > 128)
                    Transform256A(samples, temp, 0, points, table);
                else
                    Transform128A(samples, temp, 0, points, table);
                TransformDftPost(samples, temp);
                System.Array.Copy(temp, 0, coefs, coefsOffset, samples);
            }
        }
        else
        {
            if (coefsOffset == 0)
            {
                if (samples <= 8)
                {
                    if (samples == 8) Transform8Dft(coefs);
                    else if (samples == 4) Transform4(coefs);
                }
                else if (samples == 32)
                {
                    Rotation32A(coefs, table, points - 8);
                    Transform32Dft(coefs);
                }
                else
                {
                    Rotation16A(coefs, table, 0);
                    Transform16Dft(coefs);
                }
            }
            else
            {
                float[] temp = new float[samples + (samples >> 2)];
                System.Array.Copy(coefs, coefsOffset, temp, 0, samples);
                if (samples <= 8)
                {
                    if (samples == 8) Transform8Dft(temp);
                    else if (samples == 4) Transform4(temp);
                }
                else if (samples == 32)
                {
                    Rotation32A(temp, table, points - 8);
                    Transform32Dft(temp);
                }
                else
                {
                    Rotation16A(temp, table, 0);
                    Transform16Dft(temp);
                }
                System.Array.Copy(temp, 0, coefs, coefsOffset, samples);
            }
        }
    }

    private static void PreDct(int samples, float[] coefs, int points, float[] table, int tableOffset)
    {
        int samplesHalf = samples >> 1;
        int step = points / samples;
        int tableLoOff = tableOffset;
        int tableHiOff = tableOffset + points;

        for (int i = 1, j = samples - 1; i < samplesHalf; i++, j--)
        {
            tableLoOff += step;
            tableHiOff -= step;
            float v0 = coefs[i], v1 = coefs[j];
            float tmp0 = table[tableLoOff] - table[tableHiOff];
            float tmp1 = table[tableHiOff] + table[tableLoOff];
            coefs[i] = v1 * tmp1 + v0 * tmp0;
            coefs[j] = v0 * tmp1 - v1 * tmp0;
        }
        coefs[samplesHalf] = coefs[samplesHalf] * table[tableOffset];
    }

    private static void PostDct(int samples, float[] coefs)
    {
        float v0 = coefs[0], v1 = coefs[1];
        coefs[0] = v0 + v1;
        for (int i = 2; i < samples; i += 2)
        {
            float vi = coefs[i];
            coefs[i] = coefs[i + 1] + vi;
            coefs[i - 1] = vi - coefs[i + 1];
        }
        coefs[samples - 1] = v0 - v1;
    }

    private static void ScrambleDct(int samples, float[] coefs, int points, float[] table, int tableOffset)
    {
        int samplesHalf = samples >> 1;
        int step = (points * 2) / samplesHalf;
        int tableLoOff = tableOffset;
        int tableHiOff = tableOffset + points;

        for (int i = 2, j = samples - 2; i < samplesHalf; i += 2, j -= 2)
        {
            tableLoOff += step;
            tableHiOff -= step;
            float v0 = coefs[i] - coefs[j];
            float v1 = coefs[i + 1] + coefs[j + 1];
            float tmp0 = v0 * (0.5f - table[tableHiOff]) - v1 * table[tableLoOff];
            float tmp1 = v0 * table[tableLoOff] + v1 * (0.5f - table[tableHiOff]);
            coefs[i] = coefs[i] - tmp0;
            coefs[i + 1] = coefs[i + 1] - tmp1;
            coefs[j] = tmp0 + coefs[j];
            coefs[j + 1] = coefs[j + 1] - tmp1;
        }
    }

    private static void ScrambleDft(int samples, float[] coefs, int points, float[] table, int tableOffset)
    {
        int samplesHalf = samples >> 1;
        int step = (points * 2) / samplesHalf;
        int tableLoOff = tableOffset;
        int tableHiOff = tableOffset + points;

        for (int i = 2, j = samples - 2; i < samplesHalf; i += 2, j -= 2)
        {
            tableLoOff += step;
            tableHiOff -= step;
            float v0 = coefs[i] - coefs[j];
            float v1 = coefs[i + 1] + coefs[j + 1];
            float tmp0 = v1 * table[tableLoOff] + v0 * (0.5f - table[tableHiOff]);
            float tmp1 = v1 * (0.5f - table[tableHiOff]) - v0 * table[tableLoOff];
            coefs[i] = coefs[i] - tmp0;
            coefs[i + 1] = coefs[i + 1] - tmp1;
            coefs[j] = tmp0 + coefs[j];
            coefs[j + 1] = coefs[j + 1] - tmp1;
        }
    }

    private static void RadDdctfInner(int samples, float[] coefs, int points, float[] table)
    {
        int pointsQrt = points >> 2;
        PreDct(samples, coefs, points, table, pointsQrt);
        if (samples > 4)
        {
            TransformDctMain(samples, coefs, 0, pointsQrt, table);
            ScrambleDct(samples, coefs, points, table, pointsQrt);
        }
        else if (samples == 4)
        {
            TransformDctMain(samples, coefs, 0, pointsQrt, table);
        }
        PostDct(samples, coefs);
    }

    private static void RadDdctf(int samples, float[] coefs, int coefsOffset, int points, float[] table)
    {
        if (coefsOffset == 0) { RadDdctfInner(samples, coefs, points, table); return; }
        float[] temp = new float[samples + (samples >> 2)];
        System.Array.Copy(coefs, coefsOffset, temp, 0, samples);
        RadDdctfInner(samples, temp, points, table);
        System.Array.Copy(temp, 0, coefs, coefsOffset, samples);
    }

    private static void RadRdftInner(int samples, int flags, float[] coefs, int points, float[] table)
    {
        int pointsQrt = points >> 2;
        float v = (coefs[0] - coefs[1]) * 0.5f;
        coefs[1] = v;
        coefs[0] = coefs[0] - v;
        if (samples > 4)
        {
            ScrambleDft(samples, coefs, points, table, pointsQrt);
            TransformDftMain(samples, coefs, 0, pointsQrt, table);
        }
        else if (samples == 4)
        {
            TransformDftMain(4, coefs, 0, pointsQrt, table);
        }
    }

    private static void RadRdft(int samples, int flags, float[] coefs, int coefsOffset, int points, float[] table)
    {
        if (coefsOffset == 0) { RadRdftInner(samples, flags, coefs, points, table); return; }
        float[] temp = new float[samples + (samples >> 2)];
        System.Array.Copy(coefs, coefsOffset, temp, 0, samples);
        RadRdftInner(samples, flags, temp, points, table);
        System.Array.Copy(temp, 0, coefs, coefsOffset, samples);
    }
}
