# Contributing

Thanks for helping. A few rules keep Imago trustworthy.

## Before you open a pull request

1. `pwsh -NoProfile -File build.ps1` passes end to end: native core, publish, hardening check and self-test.
2. Any change to signing, verification, the PNG codec, the watermark or Lock adds a check to
   [`app/SelfTest.cs`](app/SelfTest.cs) that fails without your change.
3. No new warnings. The C# and C builds both treat warnings as errors.
4. No new dependencies without a reason in the PR. Each one widens the trust surface.

## Things that must not change silently

- Lock with password: at least 8 characters, PBKDF2-SHA256 at 600,000 rounds, and the same error for a wrong
  password and a modified file.
- The canonical pixel hash and manifest format. Old captures must keep verifying; bump the format version instead.
- Nothing is uploaded. Network use is limited to the opt-in RFC 3161 timestamp.

## Style

Match the surrounding code: small files by responsibility, plain names, comments only where the reason is not
obvious. UI text is short, calm and specific.

## Security issues

Report privately, see [SECURITY.md](SECURITY.md).
