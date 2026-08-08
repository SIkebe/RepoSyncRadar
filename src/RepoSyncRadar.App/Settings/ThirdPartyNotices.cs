namespace RepoSyncRadar.App.Settings;

public sealed record ThirdPartyNotice(
    string PackageId,
    string Version,
    string License,
    string Copyright,
    string ProjectUrl,
    string LicenseUrl,
    string LicenseText);

public static class ThirdPartyNotices
{
    public static IReadOnlyList<ThirdPartyNotice> All { get; } =
    [
        Mit("GitHub.Copilot.SDK", "1.0.10-preview.0", "Copyright (c) Microsoft Corporation. All rights reserved.", "https://github.com/github/copilot-sdk"),
        new(
            "Markdig",
            "1.2.0",
            "BSD-2-Clause",
            "Copyright (c) Alexandre Mutel",
            "https://xoofx.github.io/markdig",
            "https://licenses.nuget.org/BSD-2-Clause",
            Bsd2ClauseLicense("Copyright (c) Alexandre Mutel")),
        MicrosoftMit("Microsoft.AspNetCore.Components.Web", "11.0.0-preview.5.26302.115", "https://asp.net/"),
        MicrosoftMit("Microsoft.AspNetCore.Components.WebView", "11.0.0-preview.5.26302.115", "https://github.com/dotnet/aspnetcore"),
        MicrosoftMit("Microsoft.AspNetCore.Components.WebView.Wpf", "11.0.0-preview.5.26304.4", "https://github.com/dotnet/maui"),
        MicrosoftMit("Microsoft.EntityFrameworkCore.Design", "11.0.0-preview.6.26359.118", "https://docs.microsoft.com/ef/core/"),
        MicrosoftMit("Microsoft.EntityFrameworkCore.Sqlite", "11.0.0-preview.6.26359.118", "https://docs.microsoft.com/ef/core/"),
        MicrosoftMit("Microsoft.Extensions.AI", "10.6.0", "https://dot.net/"),
        MicrosoftMit("Microsoft.Extensions.Configuration.Json", "11.0.0-preview.5.26302.115", "https://dot.net/"),
        MicrosoftMit("Microsoft.Extensions.Hosting", "11.0.0-preview.5.26302.115", "https://dot.net/"),
        MicrosoftMit("Microsoft.Extensions.Hosting.Abstractions", "11.0.0-preview.5.26302.115", "https://dot.net/"),
        MicrosoftMit("Microsoft.Extensions.Http", "11.0.0-preview.5.26302.115", "https://dot.net/"),
        MicrosoftMit("Microsoft.Extensions.Localization", "11.0.0-preview.5.26302.115", "https://dot.net/"),
        MicrosoftMit("Microsoft.Extensions.Logging", "11.0.0-preview.6.26359.118", "https://dot.net/"),
        MicrosoftMit("Microsoft.Extensions.Logging.Debug", "11.0.0-preview.5.26302.115", "https://dot.net/"),
        MicrosoftMit("Microsoft.Extensions.Options", "11.0.0-preview.5.26302.115", "https://dot.net/"),
        MicrosoftMit("Microsoft.Extensions.Options.ConfigurationExtensions", "11.0.0-preview.5.26302.115", "https://dot.net/"),
        MicrosoftMit("Microsoft.Extensions.Options.DataAnnotations", "11.0.0-preview.5.26302.115", "https://dot.net/"),
        new(
            "Microsoft.Web.WebView2",
            "1.0.3967.48",
            "Microsoft WebView2 SDK license",
            "Copyright (C) Microsoft Corporation. All rights reserved.",
            "https://aka.ms/webview",
            "https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.3967.48",
            _webView2LicenseText),
        Mit("MudBlazor", "9.4.0", "Copyright 2026 MudBlazor", "https://mudblazor.com/"),
        Mit("Octokit", "14.0.0", "Copyright GitHub 2017", "https://github.com/octokit/octokit.net"),
        new(
            "Onigwrap",
            "1.0.11",
            "MIT with bundled third-party notices",
            "Copyright (c) 2024 Aikawa Yataro",
            "https://github.com/aikawayataro/Onigwrap",
            "https://github.com/aikawayataro/Onigwrap/blob/dd8a1cbbe3190c18033b96f2922b95c1761ae793/THIRD-PARTY-NOTICES.TXT",
            _onigwrapLicenseText),
        Mit("TextMateSharp", "2.0.4", "Copyright (c) 2021 Daniel Peñalba", "https://github.com/danipen/TextMateSharp"),
        Mit("TextMateSharp.Grammars", "2.0.4", "Copyright (c) 2021 Daniel Peñalba", "https://github.com/danipen/TextMateSharp"),
        Mit("Velopack", "1.1.1", "Copyright © Velopack Ltd. All rights reserved.", "https://github.com/velopack/velopack"),
        Mit("YamlDotNet", "17.1.0", "Copyright (c) Antoine Aubry and contributors", "https://github.com/aaubry/YamlDotNet/wiki"),
    ];

    private static ThirdPartyNotice MicrosoftMit(string packageId, string version, string projectUrl)
        => Mit(packageId, version, "© Microsoft Corporation. All rights reserved.", projectUrl);

    private static ThirdPartyNotice Mit(string packageId, string version, string copyright, string projectUrl)
        => new(
            packageId,
            version,
            "MIT",
            copyright,
            projectUrl,
            "https://licenses.nuget.org/MIT",
            MitLicense(copyright));

    private static string MitLicense(string copyright)
        => $$"""
{{copyright}}

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
""";

    private static string Bsd2ClauseLicense(string copyright)
        => $$"""
{{copyright}}

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

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
""";

    private const string _onigwrapLicenseText = """
Copyright (c) 2024 Aikawa Yataro

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

License notice for oniguruma
-------------------------------

Oniguruma LICENSE
-----------------

Copyright (c) 2002-2021  K.Kosako  <kkosako0@gmail.com>
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:
1. Redistributions of source code must retain the above copyright
   notice, this list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright
   notice, this list of conditions and the following disclaimer in the
   documentation and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE AUTHOR AND CONTRIBUTORS ``AS IS'' AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
ARE DISCLAIMED.  IN NO EVENT SHALL THE AUTHOR OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS
OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT
LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY
OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF
SUCH DAMAGE.

License notice for fluentCODE/onigwrap
-------------------------------

The MIT License (MIT)

Copyright (c) 2015 Fluent Solutions

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

License notice for TextMateSharp
-------------------------------

MIT License

Copyright (c) 2021 Daniel Peñalba

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

License notice for mono/SkiaSharp
-------------------------------

Copyright (c) 2015-2016 Xamarin, Inc.
Copyright (c) 2017-2018 Microsoft Corporation.

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
""";

    private const string _webView2LicenseText = """
Copyright (C) Microsoft Corporation. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

   * Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.
   * Redistributions in binary form must reproduce the above
copyright notice, this list of conditions and the following disclaimer
in the documentation and/or other materials provided with the
distribution.
   * The name of Microsoft Corporation, or the names of its contributors
may not be used to endorse or promote products derived from this
software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
""";
}