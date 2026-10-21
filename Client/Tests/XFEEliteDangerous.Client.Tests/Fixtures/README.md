# Raw LZ4 reference fixtures

`lz4-v1.10.0.json` contains 25 deterministic synthetic payloads compressed with the official [LZ4 v1.10.0 reference implementation](https://github.com/lz4/lz4/tree/v1.10.0/lib), using `LZ4_compress_default`. Each fixture was independently checked with `LZ4_decompress_safe` before saving the compressed bytes and the original payload's SHA256. The source URLs and SHA256 values are recorded in the JSON file.

The C# decoder is checked against these fixed compressed bytes and expected output hashes; this is not a roundtrip using its own compressor. The test runtime has no native-library or compression-package dependency. The reference DLL and generator used to produce the fixtures are development artifacts under `artifacts/protocol-lz4-reference`, excluded from the product and Git.

The primary [LZ4 block specification](https://github.com/lz4/lz4/blob/v1.10.0/doc/lz4_Block_format.md) defines the token nibbles, length extensions, little-endian offsets, expanding overlap copies and final literal restrictions. Separate C# cases exercise these boundaries and malformed data.

These files establish raw-block conformance on public test bytes. They do not establish Elite Dangerous native response-envelope compatibility, authentication success or playable integration.

`response-envelope-reference.json` adds 33 fixed encrypted-response cases. Twenty-four use the official reference-compressed blocks above; nine use literal-only blocks independently accepted by `LZ4_decompress_safe`. The ciphertext comes from Python `cryptography` 50.0.2, whose ChaCha20 state layout was first checked against RFC 8439 section 2.3.2. The [primary cipher API documentation](https://cryptography.io/en/latest/hazmat/primitives/symmetric-encryption/#cryptography.hazmat.primitives.ciphers.algorithms.ChaCha20) describes the 4-byte little-endian counter followed by 12 nonce bytes. All cases use a public synthetic key, counter zero and twelve raw ASCII nonce characters. They include arbitrary opaque EDDE prefix bytes; no meaning is inferred for them. The generating script is a development artifact, and no Python or native dependency is added to the product or test runtime.
