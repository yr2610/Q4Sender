# Q4F Fountain Protocol

Q4F is the fountain-mode QR text format used by Q4Sender and QRScanner.
Legacy `Q4|...` frames remain supported.

## Frame Format

```text
Q4F|<versionHex>|<symbolHex>/<totalHex>|<sid>|<sourceCountHex>|<symbolSizeHex>|<sourceLengthHex>|<crc32Hex>|<payload>
```

Example:

```text
Q4F|1|2A/8C|7Z2|64|2F4|1A20|89ABCDEF|Base64UrlPayload
```

Fields:

- `versionHex`: currently `1`.
- `symbolHex`: 1-based encoded symbol id.
- `totalHex`: number of encoded frames generated for the sender cycle.
- `sid`: 3-4 character session id.
- `sourceCountHex`: number of original source symbols, also the decoder rank required for recovery.
- `symbolSizeHex`: fixed byte size of each fountain symbol.
- `sourceLengthHex`: original encoded byte length before zero padding.
- `crc32Hex`: CRC-32 of the encoded byte stream.
- `payload`: Base64URL without padding, containing one encoded symbol.

## Encoding

Q4Sender currently keeps the existing payload preparation:

```text
file -> zip archive bytes -> Q4F fountain symbols
```

The first `sourceCount` symbols are systematic symbols, so a perfect scan can
recover exactly like plain chunking. Q4Sender then emits a reversed systematic
replica pass before the XOR repair symbols. This gives the scanner a direct way
to recover a late missing source symbol and avoids the "last one never arrives"
failure mode that a tiny finite LT repair set can have. Later symbols are
deterministic XOR repair symbols. The coefficient set is derived from `symbolId`
and `sourceCount`, so the scanner does not need the coefficient list in the QR
text.

## Decoding Progress

QRScanner reports progress as decoder rank:

```text
rank / sourceCount
```

This is better than raw frame count because duplicated or dependent frames do not
move the user-facing progress. Recovery is attempted when `rank == sourceCount`;
the recovered bytes are accepted only if CRC-32 matches.

## Compatibility

- Sender default: Fountain.
- Sender optional mode: Legacy Q4.
- Scanner default: Fountain.
- Scanner optional mode: Legacy Q4.
- SkipCode applies only to Legacy Q4.
