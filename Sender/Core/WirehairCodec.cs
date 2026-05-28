using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Q4Sender.Core
{
    public static class WirehairCodec
    {
        public const int Version = 1;
        private const int WirehairPacketHeaderBytes = 8;
        private const double RepairRatio = 0.25;
        private const int MinRepairPackets = 16;

        public static FountainPackage PackFileToQ4WLines(string filePath, int packetByteCount, string? sid = null)
        {
            if (packetByteCount <= WirehairPacketHeaderBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(packetByteCount));
            }

            sid ??= MakeSid(3);
            var source = ZipFileToBytes(filePath);
            var sourceLength = source.Length;
            var sourcePacketCount = Math.Max(1, (int)Math.Ceiling(sourceLength / (double)(packetByteCount - WirehairPacketHeaderBytes)));
            if (sourcePacketCount > 0xFFFF)
            {
                throw new InvalidOperationException("Wirehair source packet count exceeds 65,535.");
            }

            var repairPackets = Math.Max(MinRepairPackets, (int)Math.Ceiling(sourcePacketCount * RepairRatio));
            var totalPackets = Math.Min(0xFFFF, sourcePacketCount + repairPackets);
            var crc = Crc32(source);
            var payloads = EncodePacketsWithNode(source, packetByteCount, totalPackets);
            if (payloads.Length != totalPackets)
            {
                throw new InvalidOperationException("Wirehair helper returned an unexpected packet count.");
            }

            var lines = payloads.Select((payload, packetId) =>
                $"Q4W|{Version:X}|{(packetId + 1).ToString("X")}/{totalPackets.ToString("X")}|{sid}|{sourcePacketCount.ToString("X")}|{packetByteCount.ToString("X")}|{sourceLength.ToString("X")}|{crc:X8}|{payload}")
                .ToArray();

            return new FountainPackage
            {
                Lines = lines,
                Sid = sid,
                SourceSymbolCount = sourcePacketCount,
                SymbolSize = packetByteCount,
                SourceLength = sourceLength,
                Crc32 = crc
            };
        }

        private static string[] EncodePacketsWithNode(byte[] source, int packetByteCount, int totalPackets)
        {
            var helperPath = FindHelperPath();
            var tempInput = Path.Combine(Path.GetTempPath(), $"q4sender-wirehair-{Guid.NewGuid():N}.bin");
            var tempOutput = Path.Combine(Path.GetTempPath(), $"q4sender-wirehair-{Guid.NewGuid():N}.txt");

            try
            {
                File.WriteAllBytes(tempInput, source);

                var startInfo = new ProcessStartInfo
                {
                    FileName = "node",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };
                startInfo.ArgumentList.Add(helperPath);
                startInfo.ArgumentList.Add(tempInput);
                startInfo.ArgumentList.Add(tempOutput);
                startInfo.ArgumentList.Add(packetByteCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
                startInfo.ArgumentList.Add(totalPackets.ToString(System.Globalization.CultureInfo.InvariantCulture));

                using var process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Failed to start Node.js for Wirehair encoding.");
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"Wirehair encoder helper failed with exit code {process.ExitCode}: {stderr}{stdout}");
                }

                return File.ReadAllLines(tempOutput, Encoding.UTF8)
                    .Select(static line => line.Trim())
                    .Where(static line => line.Length > 0)
                    .ToArray();
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new InvalidOperationException("Wirehair mode requires Node.js to run the bundled WASM encoder helper.", ex);
            }
            finally
            {
                TryDelete(tempInput);
                TryDelete(tempOutput);
            }
        }

        private static string FindHelperPath()
        {
            var relativePath = Path.Combine("Sender", "Tools", "wirehair-encode.mjs");
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, relativePath),
                Path.Combine(Environment.CurrentDirectory, relativePath),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", relativePath)),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", relativePath)),
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new FileNotFoundException("Wirehair encoder helper was not found.", relativePath);
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

        private static uint Crc32(byte[] data)
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

        private static string MakeSid(int len)
        {
            const string chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            var bytes = new byte[len];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(bytes);
            return new string(bytes.Select(b => chars[b % chars.Length]).ToArray());
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Temporary-file cleanup failure should not mask the real result.
            }
        }
    }
}
