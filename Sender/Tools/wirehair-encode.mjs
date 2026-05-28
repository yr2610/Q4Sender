import { readFile, writeFile } from "node:fs/promises";
import { WirehairEncoder } from "../../Scanner/libs/wirehair-wasm/dist/wirehair.mjs";

function bytesToBase64Url(bytes) {
  return Buffer.from(bytes)
    .toString("base64")
    .replace(/\+/g, "-")
    .replace(/\//g, "_")
    .replace(/=+$/g, "");
}

const [, , inputPath, outputPath, packetByteCountText, totalPacketsText] = process.argv;
if (!inputPath || !outputPath || !packetByteCountText || !totalPacketsText) {
  console.error("Usage: node wirehair-encode.mjs <input> <output> <packetByteCount> <totalPackets>");
  process.exit(2);
}

const packetByteCount = Number(packetByteCountText);
const totalPackets = Number(totalPacketsText);
if (!Number.isInteger(packetByteCount) || packetByteCount <= 8) {
  console.error("packetByteCount must be an integer greater than 8.");
  process.exit(2);
}
if (!Number.isInteger(totalPackets) || totalPackets <= 0 || totalPackets > 0xffff) {
  console.error("totalPackets must be 1..65535.");
  process.exit(2);
}

const message = new Uint8Array(await readFile(inputPath));
const encoder = await WirehairEncoder.create();
try {
  encoder.setMessage(message, packetByteCount);
  const lines = [];
  for (let packetId = 0; packetId < totalPackets; packetId++) {
    lines.push(bytesToBase64Url(encoder.encode()));
  }
  await writeFile(outputPath, `${lines.join("\n")}\n`, "utf8");
} finally {
  encoder.free();
}
