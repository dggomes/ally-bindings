# Third-party notices

## HidSharp 2.6.4

Ally Bindings uses [HidSharp](https://software.seekye.com/hidsharp) for access to the existing ASUS HID feature-report interface.

- Copyright: James F. Bellinger and contributors
- License: Apache License 2.0
- Package: <https://www.nuget.org/packages/HidSharp/2.6.4>
- Full bundled license: [`LICENSES/HidSharp-Apache-2.0.txt`](LICENSES/HidSharp-Apache-2.0.txt)

The self-contained package includes HidSharp's compiled assembly. No G-Helper, Handheld Companion, or ROGueENEMY source code is included; those projects were used as independent protocol-research references listed in `docs/HARDWARE-SPIKE.md`.

## Microsoft.Diagnostics.Tracing.TraceEvent 3.2.5

Ally Bindings uses [TraceEvent](https://www.nuget.org/packages/Microsoft.Diagnostics.Tracing.TraceEvent/3.2.5) to consume Windows' built-in USB ETW providers in real time without installing a capture driver.

- Copyright: .NET Foundation and contributors
- License: MIT
- Source: <https://github.com/microsoft/perfview>
- Full bundled license: [`LICENSES/TraceEvent-MIT.txt`](LICENSES/TraceEvent-MIT.txt)

TraceEvent's Microsoft .NET transitive dependencies are also distributed under MIT-compatible .NET Foundation licences. They provide ETW/event decoding, JSON, immutable collections, pipes, logging abstractions and Windows security APIs; Ally Bindings does not use them for telemetry or upload capture data.

## MinHook 1.3.4

Ally Bindings uses [MinHook](https://github.com/TsudaKageyu/minhook) for inline API hooking inside the temporary, capture-only Armoury tap DLL.

- Copyright: TsudaKageyu
- License: BSD 2-Clause
- Source: <https://github.com/TsudaKageyu/minhook/tree/v1.3.4>
- Full bundled license: [`LICENSES/MinHook-BSD-2-Clause.txt`](LICENSES/MinHook-BSD-2-Clause.txt)

MinHook source is vendored under `native/ArmouryTap/third_party/minhook/` and compiled into `AllyBindings.ArmouryTap.dll`, which is embedded as a resource inside `AllyBindings.exe` and extracted only during an explicit capture session.
