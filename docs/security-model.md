# Security model

This document states what Imago protects, against whom, how, and where the protection ends. It also records
the findings of the pre-release review.

## Assets

| Asset | Where it lives | Protection |
| --- | --- | --- |
| ES256 signing key | TPM (Microsoft Platform Crypto Provider), else the software KSP | Created non-exportable. On TPM machines it cannot leave the chip. |
| ML-DSA-65 signing key | `%LOCALAPPDATA%\Imago\mldsa65.key` | 32-byte seed encrypted with DPAPI (CurrentUser) plus app entropy. Seed zeroed after use. |
| Unedited capture pixels | Process memory only | Never written to disk. Only the edited, signed image is saved. |
| Locked captures (`.snapseal`) | Wherever the user saves them | AES-256-GCM, PBKDF2-SHA256 at 600,000 rounds, authenticated header. |
| Ledger of signed manifests | `%LOCALAPPDATA%\Imago\ledger\` | Manifests are signed; the ledger itself is a cache, not a trust root. |

## Adversaries and what each one can do

| Adversary | Goal | Outcome |
| --- | --- | --- |
| Recipient who edits a stamped PNG | Pass off an edit as original | Any pixel or manifest change fails the signature: **Tampered**, with the changed tiles shown. |
| Recipient who strips the metadata | Remove the evidence | File reads as **Unsigned**, never as valid. The watermark still identifies the stamp ID. |
| Someone who recompresses or rescales | Launder through chat apps | Watermark survives JPEG 85 and common DPI scales. Review reports a stamped copy and, with the ledger, where it changed. |
| Forger who copies the watermark | Make a fake look stamped | Gets an ID, not a signature. The watermark identifies; only signatures authenticate. |
| Attacker with a future quantum computer | Forge ES256 | ML-DSA-65 (FIPS 204, level 3) must also verify. Removing it is detected. |
| Offline password guesser on a `.snapseal` | Recover the image | 600,000 PBKDF2 rounds per guess, minimum 8 characters, NFC-normalised. |
| Attacker who edits a `.snapseal` | Learn about the password or plaintext | Any edit to the ciphertext, tag or an in-range header gives the same error as a wrong password. Structurally invalid headers (bad magic, iteration count outside 100k to 10M) are rejected before key derivation. |
| Malicious PNG, JPEG or `.snapseal` opened in the viewer | Code execution or crash | Strict decoder: CRC on every chunk, 64 MB chunk cap, 32768 px per side, 1 GB pixel cap, exact inflate length, no trailing data. Seal files capped at 1 GB. |
| DLL planting next to the exe or on PATH | Hijack native code | `snapcore.dll` and ONNX Runtime load by absolute path from the app folder, never by search order. |

## Out of scope

- **A compromised Windows account.** Malware running as the user can ask the TPM to sign. Hardware backing
  stops key theft, not key misuse.
- **Truth of what was on screen.** Imago proves which pixels it captured, not that the page behind them was honest.
- **Identity.** The display name is typed by the user. Signatures are not yet chained to a certificate authority.
- **Location.** It is what Windows reports and can be spoofed.

## Cryptography

| Purpose | Algorithm | Notes |
| --- | --- | --- |
| Capture signature | ES256 (ECDSA P-256, SHA-256) | Key size checked on verify. Key ID bound into the manifest. |
| Post-quantum signature | ML-DSA-65 | Context string bound to Imago. Key ID bound into the manifest. Used when the OS supports it. |
| Pixel binding | SHA-256 over a fixed label, width, height and RGB | Independent of PNG encoding and alpha. Compared in fixed time. |
| Trusted time | RFC 3161 (default DigiCert) | Optional and off by default. Only the signature hash is sent. Nonce checked, reply capped at 64 KB, 8 s timeout. |
| Lock with password | AES-256-GCM, PBKDF2-SHA256 600k | Fresh 16-byte salt and 12-byte nonce per file. Header is the GCM associated data. Read back and verified before an optional delete of the original. |

The RFC 3161 certificate chain is validated without online revocation checks. That keeps verification
offline and deterministic; the trade-off is that a revoked TSA certificate would still verify.

## Platform hardening

Checked on every build by `build.ps1`, which fails if any flag is missing:

| Binary | Flags |
| --- | --- |
| `Imago.exe` | High-entropy ASLR, DYNAMICBASE, NX, Control Flow Guard, CET shadow stack compatible |
| `snapcore.dll` | Same, plus `-fstack-protector-strong`, `_FORTIFY_SOURCE=2`, static CRT, `-Werror` |
| ONNX Runtime DLLs | High-entropy ASLR, DYNAMICBASE, NX, Control Flow Guard |

At start the process also applies runtime mitigations (`sc_harden_process`): DLL search limited to system
folders, extension points such as AppInit_DLLs disabled, no images from network shares or low-integrity
locations, and non-system fonts blocked. The installer
is per-user and never asks for administrator rights. ONNX Runtime telemetry is disabled.

## Pre-release review (October 2026)

Scope: every file under `app/` and `native/`, the build script, the installer script and the dependency graph.

| Area | Result |
| --- | --- |
| Dependencies | `dotnet list package --vulnerable --include-transitive`: no known vulnerable packages. |
| Secrets | No keys, tokens or personal paths in the tree. Keys are generated per machine at first use. |
| Native core | All buffer writes bounds-checked against caller-supplied lengths; window title and path buffers always terminated. |
| PNG and iTXt parsing | Bounded as listed above. Compressed iTXt inflates into a fixed 4 MB buffer. |
| Seal | Authenticated header, uniform error, bounded iteration count, unsealed file name sanitised before writing. |
| CLI | Passwords accepted only on stdin (`--password-stdin`), never as arguments. |
| Process launch | The shell opens only files Imago wrote, Explorer, and one fixed Settings page. No user-supplied commands. |

Hardening notes, not vulnerabilities:

1. **Blur is visual, not cryptographic.** It pixelates in blocks of at least 6 px, then blurs and adds grain.
   That defeats casual reading but not a determined reconstruction of short, known-font text. Use **Redact**
   (solid box) for secrets; the editor's auto-redact uses solid boxes.
2. **Memory ceiling on hostile images.** A small PNG can legitimately declare up to 1 GB of pixels, so opening
   one can allocate about 2 GB before it is rejected or shown. This is a local denial of service at most.
3. **Decrypted images are not zeroed** after display. They are ordinary managed arrays.

## Reporting

See [SECURITY.md](../SECURITY.md).
