# Third-Party Notices

Harness.WinUI incorporates the third-party components listed below. Each is used under the license shown;
license texts are reproduced or linked at the end of this file.

## Components

| Component | License | Source |
|---|---|---|
| .NET runtime and libraries (`System.*`, `Microsoft.Extensions.*`, `Microsoft.Data.Sqlite`, `Microsoft.ML.Tokenizers`) | MIT | https://github.com/dotnet |
| Microsoft Agent Framework (`Microsoft.Agents.AI`) | MIT | https://github.com/microsoft/agent-framework |
| Microsoft.Extensions.AI | MIT | https://github.com/dotnet/extensions |
| OpenAI .NET SDK (`OpenAI`) | MIT | https://github.com/openai/openai-dotnet |
| Model Context Protocol C# SDK (`ModelContextProtocol`) | Apache-2.0 | https://github.com/modelcontextprotocol/csharp-sdk |
| OpenTelemetry .NET (`OpenTelemetry`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`) | Apache-2.0 | https://github.com/open-telemetry/opentelemetry-dotnet |
| Open XML SDK (`DocumentFormat.OpenXml`) | MIT | https://github.com/dotnet/Open-XML-SDK |
| PdfPig | Apache-2.0 | https://github.com/UglyToad/PdfPig |
| SmartReader | Apache-2.0 | https://github.com/Strumenta/SmartReader |
| ReverseMarkdown | MIT | https://github.com/mysticmind/reversemarkdown-net |
| SQLitePCLRaw | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| SQLite | Public domain | https://sqlite.org/copyright.html |
| Markdig | BSD-2-Clause | https://github.com/xoofx/markdig |
| CommunityToolkit.Mvvm, CommunityToolkit.WinUI | MIT | https://github.com/CommunityToolkit |
| Google.Protobuf | BSD-3-Clause | https://github.com/protocolbuffers/protobuf |
| Microsoft Edge WebView2 SDK (`Microsoft.Web.WebView2`) | BSD-3-Clause | https://aka.ms/webview2 |
| Windows App SDK (incl. WinUI 3) | Microsoft Software License Terms | https://github.com/microsoft/WindowsAppSDK |
| highlight.js 11.9.0 (bundled, incl. GitHub themes) | BSD-3-Clause | https://github.com/highlightjs/highlight.js |
| Mermaid 11.12.2 (bundled) | MIT | https://github.com/mermaid-js/mermaid |

---

## MIT License

Applies to the components marked MIT above. Copyright holders include the .NET Foundation and
Contributors, Microsoft Corporation, OpenAI, Babu Annamalai (ReverseMarkdown) and Knut Sveidqvist (Mermaid).

```
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Apache License 2.0

Applies to the Model Context Protocol C# SDK, OpenTelemetry .NET (Copyright The OpenTelemetry
Authors), PdfPig (Copyright UglyToad / Eliot Jones), SmartReader (Copyright Strumenta) and SQLitePCLRaw
(Copyright Eric Sink). The full license text is at
https://www.apache.org/licenses/LICENSE-2.0.

## BSD 2-Clause License — Markdig

```
Copyright (c) 2018-2019, Alexandre Mutel
All rights reserved.

Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## BSD 3-Clause License

Applies to highlight.js (Copyright (c) 2006, Ivan Sagalaev), Google.Protobuf (Copyright 2008 Google
Inc.) and the Microsoft Edge WebView2 SDK (Copyright (C) Microsoft Corporation), each under its own
copyright line:

```
Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

3. Neither the name of the copyright holder nor the names of its
   contributors may be used to endorse or promote products derived from
   this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## Windows App SDK

The Windows App SDK runtime is redistributed with Harness.WinUI under the Microsoft Software License Terms
for the Microsoft Windows App SDK, included in the `Microsoft.WindowsAppSDK` NuGet package
(https://www.nuget.org/packages/Microsoft.WindowsAppSDK).
