# Apple Developer ID intermediate certificates

These are public intermediate certificates from [Apple PKI](https://www.apple.com/certificateauthority/).
They contain no private keys. The distribution packager checks their SHA-256 hashes and imports them into its temporary signing keychain.
macOS validates them against its existing trusted roots. The packager does not add trust overrides or install root certificates.

| File | Apple source | SHA-256 |
| --- | --- | --- |
| DeveloperIDCA.cer | [Developer ID G1](https://www.apple.com/certificateauthority/DeveloperIDCA.cer) | `7AFC9D01A62F03A2DE9637936D4AFE68090D2DE18D03F29C88CFB0B1BA63587F` |
| DeveloperIDG2CA.cer | [Developer ID G2](https://www.apple.com/certificateauthority/DeveloperIDG2CA.cer) | `F16CD3C54C7F83CEA4BF1A3E6A0819C8AAA8E4A1528FD144715F350643D2DF3A` |

Both intermediates were downloaded from Apple and verified against Apple's root certificate on 25 October 2026.
When replacing an intermediate, verify its source and chain, update its pinned hash in `Publish-Distribution.ps1`, and run `Test-Distribution.ps1`.
