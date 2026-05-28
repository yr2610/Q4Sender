using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Q4Sender.Core
{
    public sealed class FountainPackage
    {
        public required string[] Lines { get; init; }
        public required string Sid { get; init; }
        public required int SourceSymbolCount { get; init; }
        public required int SymbolSize { get; init; }
        public required int SourceLength { get; init; }
        public required uint Crc32 { get; init; }
    }

    public static class FountainCodec
    {
        public const int Version = 1;
        private const double RepairRatio = 0.75;
        private const int MinRepairSymbols = 64;

        public static FountainPackage PackFileToQ4FLines(string filePath, int symbolSize, string? sid = null)
        {
            if (symbolSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(symbolSize));
            }

            sid ??= MakeSid(3);
            var source = ZipFileToBytes(filePath);
            var sourceLength = source.Length;
            var sourceSymbolCount = Math.Max(1, (int)Math.Ceiling(sourceLength / (double)symbolSize));
            if (sourceSymbolCount > 0xFFFF)
            {
                throw new InvalidOperationException("Fountain source symbol count exceeds 65,535.");
            }

            var replicaCount = sourceSymbolCount > 1 ? sourceSymbolCount : 0;
            var repairCount = Math.Max(MinRepairSymbols, (int)Math.Ceiling(sourceSymbolCount * RepairRatio));
            var totalSymbols = Math.Min(0xFFFF, sourceSymbolCount + replicaCount + repairCount);
            var chunks = BuildSourceChunks(source, sourceSymbolCount, symbolSize);
            var crc = Crc32(source);
            var lines = new string[totalSymbols];

            for (int symbolId = 0; symbolId < totalSymbols; symbolId++)
            {
                var payload = EncodeSymbol(chunks, symbolId);
                var payloadText = Base64UrlNoPad(payload);
                lines[symbolId] =
                    $"Q4F|{Version:X}|{(symbolId + 1).ToString("X")}/{totalSymbols.ToString("X")}|{sid}|{sourceSymbolCount.ToString("X")}|{symbolSize.ToString("X")}|{sourceLength.ToString("X")}|{crc:X8}|{payloadText}";
            }

            return new FountainPackage
            {
                Lines = lines,
                Sid = sid,
                SourceSymbolCount = sourceSymbolCount,
                SymbolSize = symbolSize,
                SourceLength = sourceLength,
                Crc32 = crc
            };
        }

        public static int[] GetCoefficientIndices(int symbolId, int sourceSymbolCount)
        {
            if (sourceSymbolCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sourceSymbolCount));
            }

            if (symbolId >= 0 && symbolId < sourceSymbolCount)
            {
                return new[] { symbolId };
            }

            if (sourceSymbolCount > 1 && symbolId < sourceSymbolCount * 2)
            {
                return new[] { sourceSymbolCount - 1 - (symbolId - sourceSymbolCount) };
            }

            var rng = new XorShift32(SeedFor(symbolId, sourceSymbolCount));
            var degree = ChooseDegree(ref rng, sourceSymbolCount);
            var set = new HashSet<int>();
            while (set.Count < degree)
            {
                set.Add((int)(rng.NextUInt32() % (uint)sourceSymbolCount));
            }

            return set.OrderBy(static x => x).ToArray();
        }

        public static uint Crc32(byte[] data)
        {
            uint crc = 0xFFFFFFFFu;
            foreach (var b in data)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                {
                    var mask = unchecked((uint)-(int)(crc & 1u));
                    crc = (crc >> 1) ^ (0xEDB88320u & mask);
                }
            }

            return ~crc;
        }

        private static byte[] ZipFileToBytes(string filePath)
        {
            var raw = File.ReadAllBytes(filePath);
            using var msOut = new MemoryStream();
            using (var zip = new ZipArchive(msOut, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entryName = Path.GetFileName(filePath);
                if (string.IsNullOrEmpty(entryName))
                {
                    entryName = "payload";
                }

                var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                entryStream.Write(raw, 0, raw.Length);
            }

            return msOut.ToArray();
        }

        private static byte[][] BuildSourceChunks(byte[] source, int sourceSymbolCount, int symbolSize)
        {
            var chunks = new byte[sourceSymbolCount][];
            for (int i = 0; i < sourceSymbolCount; i++)
            {
                var chunk = new byte[symbolSize];
                var offset = i * symbolSize;
                var count = Math.Min(symbolSize, Math.Max(0, source.Length - offset));
                if (count > 0)
                {
                    Buffer.BlockCopy(source, offset, chunk, 0, count);
                }

                chunks[i] = chunk;
            }

            return chunks;
        }

        private static byte[] EncodeSymbol(byte[][] chunks, int symbolId)
        {
            var symbolSize = chunks[0].Length;
            var output = new byte[symbolSize];
            foreach (var sourceIndex in GetCoefficientIndices(symbolId, chunks.Length))
            {
                XorInto(output, chunks[sourceIndex]);
            }

            return output;
        }

        private static void XorInto(byte[] target, byte[] source)
        {
            for (int i = 0; i < target.Length; i++)
            {
                target[i] ^= source[i];
            }
        }

        private static int ChooseDegree(ref XorShift32 rng, int sourceSymbolCount)
        {
            if (sourceSymbolCount <= 1)
            {
                return 1;
            }

            var sample = rng.NextUInt32() % 100u;
            var preferred = sample switch
            {
                < 15u => 2,
                < 30u => 3,
                < 50u => 5,
                < 70u => 8,
                < 85u => 13,
                < 95u => 21,
                _ => 34
            };

            var denseCap = Math.Min(64, sourceSymbolCount);
            return Math.Min(preferred, denseCap);
        }

        private static uint SeedFor(int symbolId, int sourceSymbolCount)
        {
            unchecked
            {
                var x = ((uint)(symbolId + 1) * 0x9E3779B1u)
                    ^ ((uint)sourceSymbolCount * 0x85EBCA77u)
                    ^ 0xA5A5A5A5u;
                x = Mix32(x);
                return x == 0 ? 0x6D2B79F5u : x;
            }
        }

        private static uint Mix32(uint x)
        {
            unchecked
            {
                x ^= x >> 16;
                x *= 0x7FEB352Du;
                x ^= x >> 15;
                x *= 0x846CA68Bu;
                x ^= x >> 16;
                return x;
            }
        }

        private static string Base64UrlNoPad(byte[] bytes)
        {
            return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }

        private static string MakeSid(int len)
        {
            const string chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            var bytes = new byte[len];
            using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
            rng.GetBytes(bytes);
            return new string(bytes.Select(b => chars[b % chars.Length]).ToArray());
        }

        private struct XorShift32
        {
            private uint _state;

            public XorShift32(uint seed)
            {
                _state = seed == 0 ? 0x6D2B79F5u : seed;
            }

            public uint NextUInt32()
            {
                unchecked
                {
                    var x = _state;
                    x ^= x << 13;
                    x ^= x >> 17;
                    x ^= x << 5;
                    _state = x;
                    return x;
                }
            }
        }
    }
}
