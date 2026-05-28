# Q4W Wirehair Fountain Protocol

Q4W is the Wirehair-based fountain QR text format used by Q4Sender and
QRScanner. Legacy `Q4|...` frames remain supported as the non-fountain mode.

The earlier `Q4F|...` prototype was a custom LT-like experiment and is kept only
as historical context. New fountain transfers should use `Q4W|...`.

## Frame Format

```text
Q4W|<versionHex>|<packetHex>/<totalHex>|<sid>|<sourceCountHex>|<packetBytesHex>|<sourceLengthHex>|<crc32Hex>|<payload>
```

Example:

```text
Q4W|1|2A/8C|7Z2|64|2F4|1A20|89ABCDEF|Base64UrlPayload
```

Fields:

- `versionHex`: currently `1`.
- `packetHex`: 1-based packet id for the finite sender cycle.
- `totalHex`: number of packets generated for that sender cycle.
- `sid`: 3-4 character session id.
- `sourceCountHex`: estimated number of original Wirehair source packets.
- `packetBytesHex`: requested Wirehair packet size, including Wirehair's 8-byte packet header.
- `sourceLengthHex`: zip archive byte length before fountain encoding.
- `crc32Hex`: CRC-32 of the zip archive bytes.
- `payload`: Base64URL without padding, containing the full Wirehair packet including its 8-byte header.

## Encoding

Q4Sender keeps the existing payload preparation:

```text
file -> zip archive bytes -> Wirehair packets -> Q4W frames
```

The sender uses `wirehair-wasm` to generate systematic and repair packets. The
scanner passes the packet payloads to the same Wirehair decoder and accepts the
result only when the recovered zip archive CRC-32 matches the frame metadata.

The sender currently generates a finite cycle:

```text
source packets + repair packets
```

The UI then loops that cycle. This keeps the displayed frame count bounded while
still avoiding the legacy mode requirement that every exact chunk id must be
seen.

## Decoding Progress

Wirehair does not expose decoder rank through the JavaScript wrapper, so
QRScanner reports approximate progress as:

```text
unique received packets / estimated source packet count+
```

The `+` is intentional: recovery may require a few more packets than the source
count, and completion is authoritative only when Wirehair recovers bytes and the
CRC-32 check passes.

## Compatibility

- Sender default: Fountain (`Q4W`).
- Sender optional mode: Legacy Q4.
- Scanner default: Fountain (`Q4W`).
- Scanner optional mode: Legacy Q4.
- SkipCode applies only to Legacy Q4.
