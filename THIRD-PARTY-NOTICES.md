# Third-party notices

Imago ships with the following third-party components. Their license texts are copied next to
`Imago.exe` by `build.ps1` and installed with the app.

| Component | Use | License | Source |
| --- | --- | --- | --- |
| Lato (Regular, Bold) | App typeface, embedded in the exe | SIL Open Font License 1.1, see [`app/Fonts/OFL.txt`](app/Fonts/OFL.txt) | Łukasz Dziedzic, latofonts.com |
| ONNX Runtime 1.31.0 | Optional on-device image classifier | MIT, copied to `onnxruntime-LICENSE.txt` | NuGet `Microsoft.ML.OnnxRuntime` |
| .NET 10 runtime | Bundled only in `-SelfContained` builds | MIT | dotnet/runtime |

No AI model is included. Any `.onnx` model a user places in the models folder is under that model's own license.
