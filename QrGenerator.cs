using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

namespace KTLanDrop
{
    public static class QrGenerator
    {
        private static readonly int[] TotalCodewords = { 0, 26, 44, 70, 100, 134, 172 };
        private static readonly int[] DataCodewords  = { 0, 19, 34, 55,  80, 108, 136 };
        private static readonly int[] EccPerBlock    = { 0,  7, 10, 15,  20,  26,  18 };
        private static readonly int[] NumBlocks      = { 0,  1,  1,  1,   1,   1,   2 };
        private static readonly int[] AlignCoord     = { 0,  0, 18, 22,  26,  30,  34 };
        private static readonly int[] FormatInfoL    = { 0x77C4, 0x72F3, 0x7DAA, 0x789D, 0x662F, 0x6318, 0x6C41, 0x6976 };

        public static Bitmap Generate(string text, int pixelsPerModule = 6)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(text);
            int version = 0;
            for (int v = 1; v <= 6; v++)
            {
                // 4-bit mode + 8-bit length + data + 4-bit terminator <= DataCodewords[v] * 8
                if (4 + 8 + utf8.Length * 8 <= DataCodewords[v] * 8)
                {
                    version = v;
                    break;
                }
            }
            if (version == 0)
            {
                return new Bitmap(100, 100);
            }

            byte[] data = EncodeData(utf8, DataCodewords[version]);
            byte[] finalBytes = InterleaveWithEcc(data, NumBlocks[version], EccPerBlock[version]);

            int size = version * 4 + 17;
            bool[,] modules = new bool[size, size];
            bool[,] isFunction = new bool[size, size];

            DrawFunctionPatterns(modules, isFunction, version, size);
            DrawCodewords(modules, isFunction, finalBytes, size);

            int bestMask = 0;
            int bestPenalty = int.MaxValue;
            for (int mask = 0; mask < 8; mask++)
            {
                bool[,] trial = (bool[,])modules.Clone();
                ApplyMask(trial, isFunction, mask, size);
                DrawFormatBits(trial, mask, size);
                int penalty = CalculatePenalty(trial, size);
                if (penalty < bestPenalty)
                {
                    bestPenalty = penalty;
                    bestMask = mask;
                }
            }

            ApplyMask(modules, isFunction, bestMask, size);
            DrawFormatBits(modules, bestMask, size);

            int quiet = 4;
            int imgSize = (size + quiet * 2) * pixelsPerModule;
            Bitmap bmp = new Bitmap(imgSize, imgSize);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        if (modules[y, x])
                        {
                            g.FillRectangle(Brushes.Black, (x + quiet) * pixelsPerModule, (y + quiet) * pixelsPerModule, pixelsPerModule, pixelsPerModule);
                        }
                    }
                }
            }
            return bmp;
        }

        private static byte[] EncodeData(byte[] utf8, int capacityBytes)
        {
            List<int> bits = new List<int>();
            AddBits(bits, 0x4, 4); // Byte mode
            AddBits(bits, utf8.Length, 8);
            foreach (byte b in utf8) AddBits(bits, b, 8);

            int capacityBits = capacityBytes * 8;
            int termLen = Math.Min(4, capacityBits - bits.Count);
            AddBits(bits, 0, termLen);
            while (bits.Count % 8 != 0) bits.Add(0);

            byte[] result = new byte[capacityBytes];
            for (int i = 0; i < bits.Count / 8; i++)
            {
                int val = 0;
                for (int j = 0; j < 8; j++) val = (val << 1) | bits[i * 8 + j];
                result[i] = (byte)val;
            }
            bool toggle = true;
            for (int i = bits.Count / 8; i < capacityBytes; i++)
            {
                result[i] = toggle ? (byte)0xEC : (byte)0x11;
                toggle = !toggle;
            }
            return result;
        }

        private static void AddBits(List<int> bits, int val, int len)
        {
            for (int i = len - 1; i >= 0; i--) bits.Add((val >> i) & 1);
        }

        private static byte[] InterleaveWithEcc(byte[] data, int numBlocks, int eccLen)
        {
            int blockDataLen = data.Length / numBlocks;
            byte[][] dataBlocks = new byte[numBlocks][];
            byte[][] eccBlocks = new byte[numBlocks][];
            byte[] poly = ComputeReedSolomonDivisor(eccLen);

            for (int b = 0; b < numBlocks; b++)
            {
                dataBlocks[b] = new byte[blockDataLen];
                Array.Copy(data, b * blockDataLen, dataBlocks[b], 0, blockDataLen);
                eccBlocks[b] = ComputeReedSolomonRemainder(dataBlocks[b], poly);
            }

            List<byte> result = new List<byte>(data.Length + numBlocks * eccLen);
            for (int i = 0; i < blockDataLen; i++)
                for (int b = 0; b < numBlocks; b++)
                    result.Add(dataBlocks[b][i]);
            for (int i = 0; i < eccLen; i++)
                for (int b = 0; b < numBlocks; b++)
                    result.Add(eccBlocks[b][i]);
            return result.ToArray();
        }

        private static byte[] ComputeReedSolomonDivisor(int degree)
        {
            byte[] result = new byte[degree];
            result[degree - 1] = 1;
            int root = 1;
            for (int i = 0; i < degree; i++)
            {
                for (int j = 0; j < degree; j++)
                {
                    result[j] = MultiplyGf(result[j], (byte)root);
                    if (j + 1 < degree) result[j] ^= result[j + 1];
                }
                root = MultiplyGf((byte)root, 0x02);
            }
            return result;
        }

        private static byte[] ComputeReedSolomonRemainder(byte[] data, byte[] divisor)
        {
            byte[] result = new byte[divisor.Length];
            foreach (byte b in data)
            {
                byte factor = (byte)(b ^ result[0]);
                Array.Copy(result, 1, result, 0, result.Length - 1);
                result[result.Length - 1] = 0;
                for (int i = 0; i < divisor.Length; i++)
                    result[i] ^= MultiplyGf(divisor[i], factor);
            }
            return result;
        }

        private static byte MultiplyGf(byte x, byte y)
        {
            int z = 0;
            for (int i = 7; i >= 0; i--)
            {
                z = (z << 1) ^ ((z >> 7) * 0x11D);
                z ^= ((y >> i) & 1) * x;
            }
            return (byte)z;
        }

        private static void DrawFunctionPatterns(bool[,] m, bool[,] f, int version, int size)
        {
            for (int i = 0; i < size; i++)
            {
                SetFunc(m, f, 6, i, i % 2 == 0);
                SetFunc(m, f, i, 6, i % 2 == 0);
            }
            DrawFinder(m, f, 3, 3, size);
            DrawFinder(m, f, size - 4, 3, size);
            DrawFinder(m, f, 3, size - 4, size);

            if (version >= 2)
            {
                int pos = AlignCoord[version];
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                        SetFunc(m, f, pos + dx, pos + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
            }

            // Reserve format info
            for (int i = 0; i <= 8; i++)
            {
                if (i != 6) { f[8, i] = true; f[i, 8] = true; }
            }
            for (int i = 0; i < 8; i++)
            {
                f[8, size - 1 - i] = true;
                f[size - 1 - i, 8] = true;
            }
            SetFunc(m, f, 8, size - 8, true);
        }

        private static void DrawFinder(bool[,] m, bool[,] f, int cx, int cy, int size)
        {
            for (int dy = -4; dy <= 4; dy++)
            {
                for (int dx = -4; dx <= 4; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x >= 0 && x < size && y >= 0 && y < size)
                    {
                        int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                        SetFunc(m, f, x, y, dist != 2 && dist != 4);
                    }
                }
            }
        }

        private static void SetFunc(bool[,] m, bool[,] f, int x, int y, bool dark)
        {
            m[y, x] = dark;
            f[y, x] = true;
        }

        private static void DrawCodewords(bool[,] m, bool[,] f, byte[] data, int size)
        {
            int i = 0;
            for (int right = size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                for (int vert = 0; vert < size; vert++)
                {
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j;
                        bool upward = ((right + 1) & 2) == 0;
                        int y = upward ? size - 1 - vert : vert;
                        if (!f[y, x] && i < data.Length * 8)
                        {
                            m[y, x] = ((data[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                            i++;
                        }
                    }
                }
            }
        }

        private static void ApplyMask(bool[,] m, bool[,] f, int mask, int size)
        {
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    if (f[y, x]) continue;
                    bool invert = false;
                    switch (mask)
                    {
                        case 0: invert = (x + y) % 2 == 0; break;
                        case 1: invert = y % 2 == 0; break;
                        case 2: invert = x % 3 == 0; break;
                        case 3: invert = (x + y) % 3 == 0; break;
                        case 4: invert = (x / 3 + y / 2) % 2 == 0; break;
                        case 5: invert = x * y % 2 + x * y % 3 == 0; break;
                        case 6: invert = (x * y % 2 + x * y % 3) % 2 == 0; break;
                        case 7: invert = ((x + y) % 2 + x * y % 3) % 2 == 0; break;
                    }
                    if (invert) m[y, x] = !m[y, x];
                }
            }
        }

        private static void DrawFormatBits(bool[,] m, int mask, int size)
        {
            int data = FormatInfoL[mask];
            // Copy 1: top-left (MSB=14 at (8,0) .. LSB=0 at (0,8))
            for (int i = 0; i <= 5; i++) m[8, i] = ((data >> (14 - i)) & 1) != 0;
            m[8, 7] = ((data >> 8) & 1) != 0;
            m[8, 8] = ((data >> 7) & 1) != 0;
            m[7, 8] = ((data >> 6) & 1) != 0;
            for (int i = 0; i <= 5; i++) m[i, 8] = ((data >> i) & 1) != 0;

            // Copy 2: bottom-left (MSB=14 at (size-1,8) .. bit 8 at (size-7,8)) and top-right (bit 7 at (8,size-8) .. LSB=0 at (8,size-1))
            for (int i = 0; i < 7; i++) m[size - 1 - i, 8] = ((data >> (14 - i)) & 1) != 0;
            for (int i = 0; i < 8; i++) m[8, size - 8 + i] = ((data >> (7 - i)) & 1) != 0;
            m[size - 8, 8] = true;
        }

        private static int CalculatePenalty(bool[,] m, int size)
        {
            int penalty = 0;
            for (int y = 0; y < size; y++)
            {
                int runX = 0, runY = 0;
                bool lastX = false, lastY = false;
                for (int x = 0; x < size; x++)
                {
                    if (m[y, x] == lastX) { runX++; if (runX == 5) penalty += 3; else if (runX > 5) penalty++; }
                    else { lastX = m[y, x]; runX = 1; }
                    if (m[x, y] == lastY) { runY++; if (runY == 5) penalty += 3; else if (runY > 5) penalty++; }
                    else { lastY = m[x, y]; runY = 1; }
                }
            }
            for (int y = 0; y < size - 1; y++)
                for (int x = 0; x < size - 1; x++)
                    if (m[y, x] == m[y, x + 1] && m[y, x] == m[y + 1, x] && m[y, x] == m[y + 1, x + 1])
                        penalty += 3;
            return penalty;
        }
    }
}
