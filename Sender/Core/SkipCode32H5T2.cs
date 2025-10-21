using System;
using System.Collections.Generic;

namespace Q4Sender.Core
{
    public static class SkipCode32H5T2
    {
        private const string Alphabet = "0123456789abcdefghijkmnopqrstuvw";

        private static readonly Dictionary<char, int> DecodeMap = BuildDecodeMap();

        private static Dictionary<char, int> BuildDecodeMap()
        {
            var map = new Dictionary<char, int>(64);
            for (int i = 0; i < Alphabet.Length; i++)
            {
                map[Alphabet[i]] = i;
            }

            map['l'] = 1;
            map['L'] = 1;
            map['o'] = 0;
            map['O'] = 0;
            map['-'] = 31;
            map['.'] = 31;

            return map;
        }

        public static bool TryDecode(string? input, out uint mask)
        {
            mask = 0;
            if (string.IsNullOrWhiteSpace(input))
            {
                return false;
            }

            var trimmed = input.Trim().ToLowerInvariant();
            if (trimmed.Length == 7)
            {
                return TryDecodeClassic(trimmed, out mask);
            }

            if (trimmed.Length >= 1 && trimmed.Length <= 6)
            {
                return TryDecodeH5T2Partial(trimmed, out mask);
            }

            return false;
        }

        public static IEnumerable<(int start, int end)> Buckets(uint mask, int total)
        {
            if (total <= 0)
            {
                yield break;
            }

            long totalLong = total;
            for (int bucket = 0; bucket < 32; bucket++)
            {
                if (((mask >> (31 - bucket)) & 1) == 0)
                {
                    continue;
                }

                int start = (int)((bucket * totalLong) / 32);
                int end = (int)(((bucket + 1L) * totalLong) / 32) - 1;
                if (start <= end)
                {
                    yield return (start, end);
                }
            }
        }

        private static bool TryDecodeClassic(string input, out uint mask)
        {
            mask = 0;
            ulong value = 0;

            foreach (var ch in input)
            {
                if (!DecodeMap.TryGetValue(ch, out int digit))
                {
                    return false;
                }

                value = (value << 5) | (uint)digit;
            }

            mask = (uint)((value >> 3) & 0xFFFFFFFFUL);
            return true;
        }

        private static bool TryDecodeH5T2Partial(string input, out uint mask)
        {
            mask = 0;
            if (!DecodeMap.TryGetValue(input[0], out int header))
            {
                return false;
            }

            int[] symbols = new int[7];
            int pointer = 1;

            for (int i = 0; i < 5; i++)
            {
                bool required = ((header >> (4 - i)) & 1) != 0;
                if (!required)
                {
                    symbols[i] = 0;
                    continue;
                }

                if (pointer < input.Length)
                {
                    if (!DecodeMap.TryGetValue(input[pointer++], out int digit))
                    {
                        return false;
                    }

                    symbols[i] = digit;
                }
                else
                {
                    symbols[i] = 31;
                }
            }

            for (int i = 5; i <= 6; i++)
            {
                if (pointer < input.Length)
                {
                    if (!DecodeMap.TryGetValue(input[pointer++], out int digit))
                    {
                        return false;
                    }

                    symbols[i] = digit;
                }
                else
                {
                    symbols[i] = 0;
                }
            }

            if (pointer != input.Length)
            {
                return false;
            }

            ulong value = 0;
            for (int i = 0; i < symbols.Length; i++)
            {
                value = (value << 5) | (uint)symbols[i];
            }

            mask = (uint)((value >> 3) & 0xFFFFFFFFUL);
            return true;
        }
    }
}
