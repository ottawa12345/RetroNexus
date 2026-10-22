namespace EmulatorLauncher.Services;

/// <summary>
/// 設定画面「アプリ情報」「ライセンス」に表示する文言。
/// 内容はリポジトリ直下のLICENSE / THIRD_PARTY_NOTICES.txt と同じ。どちらかを変える場合は
/// もう一方も合わせること(配布物に同梱するテキストと、アプリ内表示を一致させるため)。
///
/// ThirdPartyの構成: 先頭がCHDSharp以外の利用ライブラリ(MessagePack/Microsoft.Data.Sqlite/
/// System.Drawing.Common/CommunityToolkit=MIT、Dapper/SQLitePCLRaw=Apache-2.0、SQLite=パブリックドメイン、
/// SDL3=zlib、Windows App SDK、libretro/各コア、libretro-database=CC-BY-SA 4.0)、
/// 続いてCHDSharp。CHDSharpのライセンスはMITだが、「about dialog / credits page 等の目に見える場所へ
/// 作者名(Original developers)を表示する」という帰属表示の条件が付いている。
/// このアプリ内表示(設定→ライセンス)がその「visible location」を兼ねている。
/// 同梱コーデック(zlib / LZMA / FLAC(LGPL-2.1) / ZSTD)の表記もCHDSharp側のLICENSE.txtに従って載せている。
/// NuGetパッケージを追加/更新したら、ここにも反映すること。
/// </summary>
public static class LicenseTexts
{
    /// <summary>アプリ本体の著作権者表記(LICENSEファイルも同様)。</summary>
    public const string AppCopyright = "Copyright (c) 2026 Ottawa123";

    /// <summary>アプリ本体のライセンス(PolyForm Noncommercial 1.0.0。リポジトリ直下のLICENSEと同じ内容)。</summary>
    public const string AppLicense = @"Required Notice: Copyright (c) 2026 Ottawa123

PolyForm Noncommercial License 1.0.0

https://polyformproject.org/licenses/noncommercial/1.0.0

■ Acceptance

In order to get any license under these terms, you must agree to them as both strict obligations and conditions to all your licenses.

■ Copyright License

The licensor grants you a copyright license for the software to do everything you might do with the software that would otherwise infringe the licensor's copyright in it for any permitted purpose.  However, you may only distribute the software according to Distribution License and make changes or new works based on the software according to Changes and New Works License.

■ Distribution License

The licensor grants you an additional copyright license to distribute copies of the software.  Your license to distribute covers distributing the software with changes and new works permitted by Changes and New Works License.

■ Notices

You must ensure that anyone who gets a copy of any part of the software from you also gets a copy of these terms or the URL for them above, as well as copies of any plain-text lines beginning with Required Notice: that the licensor provided with the software.  For example:

  Required Notice: Copyright Yoyodyne, Inc. (http://example.com)

■ Changes and New Works License

The licensor grants you an additional copyright license to make changes and new works based on the software for any permitted purpose.

■ Patent License

The licensor grants you a patent license for the software that covers patent claims the licensor can license, or becomes able to license, that you would infringe by using the software.

■ Noncommercial Purposes

Any noncommercial purpose is a permitted purpose.

■ Personal Uses

Personal use for research, experiment, and testing for the benefit of public knowledge, personal study, private entertainment, hobby projects, amateur pursuits, or religious observance, without any anticipated commercial application, is use for a permitted purpose.

■ Noncommercial Organizations

Use by any charitable organization, educational institution, public research organization, public safety or health organization, environmental protection organization, or government institution is use for a permitted purpose regardless of the source of funding or obligations resulting from the funding.

■ Fair Use

You may have ""fair use"" rights for the software under the law. These terms do not limit them.

■ No Other Rights

These terms do not allow you to sublicense or transfer any of your licenses to anyone else, or prevent the licensor from granting licenses to anyone else.  These terms do not imply any other licenses.

■ Patent Defense

If you make any written claim that the software infringes or contributes to infringement of any patent, your patent license for the software granted under these terms ends immediately. If your company makes such a claim, your patent license ends immediately for work on behalf of your company.

■ Violations

The first time you are notified in writing that you have violated any of these terms, or done anything with the software not covered by your licenses, your licenses can nonetheless continue if you come into full compliance with these terms, and take practical steps to correct past violations, within 32 days of receiving notice.  Otherwise, all your licenses end immediately.

■ No Liability

As far as the law allows, the software comes as is, without any warranty or condition, and the licensor will not be liable to you for any damages arising out of these terms or the use or nature of the software, under any kind of legal claim.

■ Definitions

The licensor is the individual or entity offering these terms, and the software is the software the licensor makes available under these terms.

You refers to the individual or entity agreeing to these terms.

Your company is any legal entity, sole proprietorship, or other kind of organization that you work for, plus all organizations that have control over, are under the control of, or are under common control with that organization.  Control means ownership of substantially all the assets of an entity, or the power to direct its management and policies by vote, contract, or otherwise.  Control can be direct or indirect.

Your licenses are all the licenses granted to you for the software under these terms.

Use means anything you do with the software requiring one of your licenses.";

    /// <summary>使用しているサードパーティ製ソフトウェアの表記(その他のライブラリ+CHDSharp)。</summary>
    public const string ThirdParty = @"Other libraries used by RetroNexus

--- MIT License components ---

The following components are licensed under the MIT License. Each is used under the MIT terms below, with its own copyright notice.

- MessagePack for C# (NuGet ""MessagePack"" 3.1.10) - https://github.com/MessagePack-CSharp/MessagePack-CSharp
  Copyright (c) 2017 Yoshifumi Kawai and contributors
  (MessagePack also contains lz4net, BSD 2-Clause, Copyright (c) 2013-2017, Milosz Krajewski, and BufferWriter.cs, Apache License 2.0, Copyright 2019 .NET Foundation.)
- Microsoft.Data.Sqlite (NuGet ""Microsoft.Data.Sqlite"" 10.0.12) - https://github.com/dotnet/efcore
  Copyright (c) .NET Foundation and Contributors
- System.Drawing.Common (NuGet ""System.Drawing.Common"" 10.0.x) - https://github.com/dotnet/winforms
  Copyright (c) .NET Foundation and Contributors
- Windows Community Toolkit (NuGet ""CommunityToolkit.WinUI.Controls.Primitives"" 8.2.251219) - https://github.com/CommunityToolkit/Windows
  Copyright (c) .NET Foundation and Contributors
- SharpCompress (NuGet ""SharpCompress"" 0.50.0) - https://github.com/adamhathcock/sharpcompress
  Copyright (c) 2014 Adam Hathcock

MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the ""Software""), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED ""AS IS"", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

--- Apache License 2.0 components ---

- Dapper (NuGet ""Dapper"" 2.1.89) - https://github.com/DapperLib/Dapper
  Licensed under the Apache License, Version 2.0. Copyright: the Dapper authors and contributors.
- SQLitePCLRaw (dependency of Microsoft.Data.Sqlite) - https://github.com/ericsink/SQLitePCL.raw
  Licensed under the Apache License, Version 2.0. Copyright: Eric Sink and contributors.
- Material Icons ""sports_esports"" glyph (console card placeholder) - https://github.com/google/material-design-icons
  Licensed under the Apache License, Version 2.0. Copyright: Google LLC.

The full text of the Apache License, Version 2.0 is included in the file licenses\Apache-2.0.txt distributed with this application, and is also available at:
https://www.apache.org/licenses/LICENSE-2.0
Unless required by applicable law or agreed to in writing, software distributed under the License is distributed on an ""AS IS"" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.

--- SQLite ---

SQLite (bundled through Microsoft.Data.Sqlite / SQLitePCLRaw) is in the public domain. https://www.sqlite.org/copyright.html

--- Simple DirectMedia Layer (SDL3) ---

RetroNexus uses SDL3 (SDL3.dll) for video, audio and gamepad input. https://www.libsdl.org/

Copyright (C) 1997-2026 Sam Lantinga <slouken@libsdl.org>

This software is provided 'as-is', without any express or implied warranty. In no event will the authors be held liable for any damages arising from the use of this software.

Permission is granted to anyone to use this software for any purpose, including commercial applications, and to alter it and redistribute it freely, subject to the following restrictions:

1. The origin of this software must not be misrepresented; you must not claim that you wrote the original software. If you use this software in a product, an acknowledgment in the product documentation would be appreciated but is not required.
2. Altered source versions must be plainly marked as such, and must not be misrepresented as being the original software.
3. This notice may not be removed or altered from any source distribution.

--- Microsoft Windows App SDK ---

The user interface is built with the Windows App SDK (WinUI 3) - NuGet ""Microsoft.WindowsAppSDK"" 1.8.260416003. Copyright (c) Microsoft Corporation. Provided under the Microsoft software license terms that accompany the package: https://github.com/microsoft/WindowsAppSDK

--- libretro API and emulation cores ---

RetroNexus is a frontend that hosts emulation cores through the libretro API (https://www.libretro.com/). The libretro API definitions are provided by the libretro/RetroArch project under the MIT License.

The emulation cores (*_libretro.dll, e.g. FCEUmm, Nestopia, Snes9x, bsnes, mGBA, Mednafen) are separate programs developed by their respective authors. They are not part of RetroNexus; each core is distributed under its own license (for example GPL, LGPL, MPL or a non-commercial license), and the license of each core applies to that core. Please check the license of every core you install.

--- libretro-database ---

Game identification uses RDB data from the libretro-database project (https://github.com/libretro/libretro-database), licensed under CC-BY-SA 4.0 (https://creativecommons.org/licenses/by-sa/4.0/). Box art images are provided by the libretro thumbnails project (https://thumbnails.libretro.com/); the rights to game titles and images belong to their respective owners.

============================================================

CHDSharp (NuGet package ""CHDSharp"" 1.4.3) - https://github.com/purelogiccode/CHDSharp

RetroNexus uses CHDSharp, unmodified, to read .chd disc images. CHDSharp is distributed as a separate library (CHDSharp.dll) together with this application.

Original developers: Peterson Fernandes (@purelogiccode) and Gordon Jefferyes (@gjefferyes)

MIT License

Copyright (c) 2026 Peterson Fernandes and Gordon Jefferyes

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the ""Software""), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

ATTRIBUTION REQUIREMENT:
Any use of this software, whether in commercial or open source projects, must include the following attribution in a visible location (such as documentation, about dialog, or credits page):
- Original developers: Peterson Fernandes (@purelogiccode) and Gordon Jefferyes (@gjefferyes)

THE SOFTWARE IS PROVIDED ""AS IS"", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

--- Third-party components included in CHDSharp ---

CHDSharp is a combined work. The project code is distributed under the MIT License. Several compression codecs are vendored in the library (pure C#, no native dependencies) and retain their own licenses:

VendoredZLib - zlib license
Pure C# zlib (deflate/inflate) implementation.
- Managed C#/.NET code: Copyright (c) 2022-2024 Magnus Montin.
- Original zlib: Copyright (c) 1995-2024 Jean-loup Gailly and Mark Adler.
- Source: https://github.com/dotnet/runtime (ported from https://zlib.net/)
This software is provided 'as-is', without any express or implied warranty. The origin must not be misrepresented; altered versions must be marked as such; and this notice may not be removed or altered from any source distribution.

VendoredLZMA - Public Domain
C# port of the LZMA SDK.
- Copyright (c) 1999-2023 Igor Pavlov. Released into the public domain.
- Source: https://www.7-zip.org/sdk.html

VendoredFlac - GNU Lesser General Public License v2.1 (LGPL-2.1)
Pure C# FLAC encoder/decoder, derived from CUETools.Flake and libFLAC.
- CUETools.Flake: Copyright (c) 2009-2023 Grigory Chudov.
- Based on Flake encoder: Copyright (c) 2006-2009 Justin Ruggles.
- libFLAC encoder port: based on libFLAC 1.4.3 (BSD/GPL dual license, used under BSD terms).
The FLAC part of CHDSharp is licensed under the LGPL-2.1.
- The full text of the GNU Lesser General Public License, version 2.1 is included in the file licenses\LGPL-2.1.txt distributed with this application, and is also available at:
  https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html
- RetroNexus uses CHDSharp as an unmodified, separate library file (CHDSharp.dll). You may replace CHDSharp.dll with a modified version of the library, and you may modify RetroNexus for your own use to that end and reverse engineer it for debugging such modifications.
- The complete source code of CHDSharp (including the FLAC part) is available at:
  https://github.com/purelogiccode/CHDSharp
  If that address becomes unavailable, contact the distributor of RetroNexus (Ottawa123) and a copy of the corresponding source code will be provided.

VendoredZSTD - MIT License (based on Facebook zstd, BSD-3-Clause)
Pure C# port of zstd 1.5.5, carried as ZstdSharp 0.7.6 source tree.
- ZstdSharp (C# port): Copyright (c) 2021 Oleg Stepanischev. MIT License.
- Source: https://github.com/oleg-st/ZstdSharp
- Underlying Facebook zstd: Copyright (c) Meta Platforms, Inc. and affiliates. BSD-3-Clause.
- Source: https://github.com/facebook/zstd

--- Trademarks ---

All product, console and company names mentioned in this application (for example PlayStation, Nintendo, Famicom, Super Famicom, Wii, GameCube, Sega, Xbox) are trademarks or registered trademarks of their respective owners. They are used only to identify the hardware that the emulation cores support. RetroNexus is an independent project and is not affiliated with, endorsed by or sponsored by any of them. The controller diagrams and button symbols shown in the application are original drawings.

Acknowledgments
- MAME (https://www.mamedev.org/) - CHD format specification and reference implementation
- libchdr 0.3.0 (Romain Tisseraud, BSD-3-Clause) - C reference library
- RomVault/CHDSharp (Gordon Jefferyes) - original C# CHDSharp implementation";

    /// <summary>ライセンス画面に表示する全文。</summary>
    public static string Full =>
        "RetroNexus\n\n" + AppLicense
        + "\n\n============================================================\n\n"
        + "Third-party software\n\n" + ThirdParty;
}
