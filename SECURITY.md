# Security policy

## Reporting a vulnerability

Please report privately through GitHub: **Security > Report a vulnerability** on this repository.
Do not open a public issue for a security problem.

Include what you found, the steps or file that reproduce it, and the Imago version (`Imago.exe --version`).
You can expect an acknowledgement within 5 working days and a fix or a plan within 30 days.

## Supported versions

| Version | Supported |
| --- | --- |
| 1.0.x | Yes |

## Scope

In scope: signature or tamper-check bypasses, anything that makes a modified image verify as unchanged,
weaknesses in Lock with password, memory-safety bugs in the native core or decoders, and DLL loading issues.

The threat model, cryptography and known limits are in [docs/security-model.md](docs/security-model.md).
Issues listed there as out of scope (for example, malware already running as the user) are known trade-offs.
