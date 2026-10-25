# Stamped captures

Stamped replaces Verified. A Stamped capture looks untouched: there is no visible mark. Inside it carry
three things that let anyone with Imago review it later:

1. **A signed manifest** (ES256 + post-quantum ML-DSA-65) over the exact pixels, as before.
2. **An invisible watermark** woven into the image itself, which survives screenshots of screenshots,
   JPEG recompression, resizing and chat apps that strip metadata.
3. **A signed edit log** of everything done in Imago's editor (blur, redact, arrows, text, crop).

"Seal" is now **Lock with password**, an option on any Stamped capture. The encryption is unchanged:
AES-256-GCM, PBKDF2-SHA256 at 600,000 rounds, at least 8 characters, and the same error for a wrong
password as for a damaged file.

## Use cases

| Who | What they need | What Stamped gives them |
| --- | --- | --- |
| Legal, HR, compliance | Evidence that a chat, email or dashboard looked like this at this time | Signature over exact pixels, trusted timestamp, device key, edit log. A reviewer sees "Unchanged since capture" or exactly where it changed. |
| Journalists, researchers | Share a redacted screenshot and still prove the rest is genuine | Redactions are logged as Imago edits with their boxes. Review shows "Redacted in Imago at capture" for those areas and "Unchanged" everywhere else. |
| Support and QA teams | Bug reports that weren't staged | Watermark ties a pasted or forwarded image back to the original capture ID, even after Slack or Teams recompresses it. |
| Anyone | "Is this screenshot real or was it edited / made with AI?" | Review gives a verdict, a heatmap of changed areas, the kind of change, and an AI-likelihood reading with its limits stated. |

## What a review answers

| Question | How |
| --- | --- |
| Changed or not? | Original PNG: signature over pixels holds or fails, bit-exact. Recompressed copy: watermark found, then compared to the ledger. |
| Where? | Per-tile comparison against the signed tile grid (one value per 16 x 16 tile). Shown as a heatmap over the image. |
| What kind? | Changes inside boxes in the signed edit log are **Imago edits** (blue). Changes outside are **altered after capture** (red). Uniform small differences everywhere are **recompression** (ignored). |
| AI involved? | On-device analysis of the altered tiles: screen-likeness (flat palettes, hard edges, pixel-aligned text) against the soft gradients and noise that generative inpainting leaves. Reported as a likelihood, never as proof. |

## How it works

### At capture

1. The capture opens in the editor (optional, on by default). The person blurs, redacts and annotates.
   The unedited pixels stay in memory only and are never written to disk.
2. Imago builds the manifest: stamp ID (`IMG-XXXX-XXXX`), time, name, place, device keys, size, the
   SHA-256 of the final pixels, the edit log (`tool` + bounding box per operation), and the tile grid.
3. The watermark is embedded into the final pixels (below). The pixel hash is taken after embedding.
4. The manifest is signed with both keys and optionally time-stamped by a public TSA (hash only).
5. The PNG is written with the signature in an `iTXt` chunk, and a copy of the manifest goes to the
   local ledger (`%LOCALAPPDATA%\Imago\ledger\<stamp>.json`).

### The watermark

- Luma channel, split into 8 x 8 blocks, each transformed with a DCT.
- The payload is 64 bits: the 40-bit stamp code, a 16-bit key tag and an 8-bit CRC.
- Each bit is spread over many blocks with a keyed pseudo-random +/- pattern on four mid-frequency
  coefficients (spread-spectrum). Mid frequencies survive JPEG quantisation and downscaling but are
  below what the eye notices on flat UI colours. Strength adapts to local texture, so flat white areas
  get almost nothing and busy areas carry more.
- Detection correlates every block against the keyed pattern, sums the votes per bit, and accepts only
  when the CRC matches. A random image matches by chance about once in 256 x 2^16.
- It also tries the common rescales (100%, 125%, 150%, 200%, 50%, 75%, 80%) so a copy taken on a
  different DPI is still found.
- The key is built into Imago, so any copy of Imago can read it. The watermark **identifies**; the
  signatures **authenticate**. Forging a watermark gets you an ID, not a valid signature.

### At review

```
open image
  -> has iTXt "imago" signature?
       yes -> verify signatures and pixel hash
                holds  -> "Unchanged since capture" (+ edit log summary)
                fails  -> compare tiles with manifest grid -> heatmap
       no  -> look for watermark
                found  -> stamp ID -> ledger entry on this PC?
                            yes -> verify the ledger manifest, compare tiles -> heatmap
                            no  -> "Stamped by Imago as IMG-..., original not on this PC"
                not found -> "No Imago stamp"
  -> for tiles marked altered: AI likelihood
```

### AI likelihood

**Built in, no model:** for each altered tile Imago measures

- palette concentration (screens use few exact colours; generated pixels use many near ones),
- edge sharpness (UI edges are one pixel; diffusion edges are soft),
- noise residual (screen pixels are noise-free; generative fills carry fine noise),
- text alignment (OCR boxes on the pixel grid versus warped glyphs).

These combine into Low / Medium / High with the reasons listed.

**Optional small language model:** if a vision SLM in ONNX format (for example Florence-2 or
Phi-3.5-vision) is placed in `%LOCALAPPDATA%\Imago\models`, Imago runs it on the altered region only, on
this PC, to describe in words what changed ("a number in the second row was replaced"). Nothing is
sent anywhere. Without the model, review still works with the heuristics above.

## What it does not prove

- That what was on screen was true. It proves the pixels Imago captured, not the website behind them.
- AI likelihood is a statistical reading. A careful human edit can look like a screen; a screen showing
  an AI image will look generated.
- A heavily cropped, rotated or re-photographed copy may lose the watermark.
- Without the original PNG or this PC's ledger, a review of a recompressed copy can say who stamped it
  and when, but not where it changed.
- Locked files hide content, they do not hide that a file exists.
