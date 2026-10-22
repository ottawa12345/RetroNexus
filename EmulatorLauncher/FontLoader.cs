using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace EmulatorLauncher;

/// <summary>
/// 同梱フォント(M PLUS Rounded 1c)をOSへ登録する。
/// Unpackaged(MSIXなし)のWinUI3では ms-appx:///...ttf#Family による同梱フォント指定が
/// 効かず、黙って既定フォントへフォールバックする。そのため「インストール済みフォントを
/// ファミリ名で指定する」経路に乗せる。
///  1) 現在のユーザー用フォントとして %LOCALAPPDATA%\Microsoft\Windows\Fonts へコピーし
///     HKCU のフォント一覧へ登録(管理者権限不要、Windows 10 1809以降)。
///  2) 念のためプロセス内にも AddFontResourceEx(FR_PRIVATE) で登録。
/// 失敗しても例外は出さず、既定フォントで動き続ける。
/// </summary>
internal static class FontLoader
{
    public const string FamilyName = "Rounded Mplus 1c";
    private const uint FR_PRIVATE = 0x10;
    private const uint WM_FONTCHANGE = 0x001D;

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int AddFontResourceExW(string name, uint fl, IntPtr res);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendNotifyMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>登録の結果(ログ用)。AppLogは登録より後に開くため、ここへ貯めておいて、App側が起動後にまとめて書く。</summary>
    public static readonly System.Collections.Generic.List<string> Diagnostics = new();

    /// <summary>
    /// 今回の起動で初めてフォントを登録(コピー/レジストリ書き込み)したか。
    /// 起動中に登録したフォントは、描画(DirectWrite)側にはまだ見えず、次に起動するまで既定のフォントで表示されることがある。
    /// </summary>
    public static bool InstalledThisRun { get; private set; }

    // ---- DirectWrite(XAMLが書体を探す仕組み)に、登録したフォントが見えているかの確認 ----
    [ComImport, Guid("b859ee5a-d838-4b5b-a2e8-1adc7d93db48"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteFactory
    {
        [PreserveSig]
        int GetSystemFontCollection(out IDWriteFontCollection fontCollection, [MarshalAs(UnmanagedType.Bool)] bool checkForUpdates);
    }

    [ComImport, Guid("a84cee02-3eea-4eee-a827-87c1a02a0fcc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteFontCollection
    {
        [PreserveSig] uint GetFontFamilyCount();
        [PreserveSig] int GetFontFamily(uint index, out IntPtr fontFamily);
        [PreserveSig] int FindFamilyName([MarshalAs(UnmanagedType.LPWStr)] string familyName, out uint index, [MarshalAs(UnmanagedType.Bool)] out bool exists);
    }

    [DllImport("dwrite.dll")]
    private static extern int DWriteCreateFactory(int factoryType, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object factory);

    /// <summary>
    /// DirectWriteのシステムフォント一覧に同梱フォントのファミリ名が「無い」ことが確認できたか。
    /// trueのときだけ、App側が別の読み込み方(ms-appxのフォントフォルダ指定)へ切り替える。確認できなかった場合はfalse(何も変えない)。
    /// </summary>
    public static bool SystemCollectionMissing { get; private set; }

    /// <summary>登録したフォントを、DirectWriteが見つけられるか確認してDiagnosticsへ書く(失敗しても例外は出さない)。</summary>
    public static void CheckDirectWrite()
    {
        try
        {
            Guid iid = new("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");   // IDWriteFactory
            int hr = DWriteCreateFactory(0 /* DWRITE_FACTORY_TYPE_SHARED */, ref iid, out object obj);
            if (hr < 0 || obj is not IDWriteFactory factory)
            {
                Diagnostics.Add($"DirectWrite: ファクトリを作れませんでした(HRESULT=0x{hr:X8})");
                return;
            }

            hr = factory.GetSystemFontCollection(out IDWriteFontCollection collection, true);
            if (hr < 0)
            {
                Diagnostics.Add($"DirectWrite: システムフォント一覧を取得できませんでした(HRESULT=0x{hr:X8})");
                return;
            }

            hr = collection.FindFamilyName(FamilyName, out uint index, out bool exists);
            if (hr < 0)
            {
                Diagnostics.Add($"DirectWrite: ファミリ名の検索に失敗しました(HRESULT=0x{hr:X8})");
                return;
            }

            Diagnostics.Add($"DirectWrite: システムフォント{collection.GetFontFamilyCount()}ファミリ中、「{FamilyName}」は{(exists ? "見つかりました" : "見つかりません")}");
            SystemCollectionMissing = !exists;
        }
        catch (Exception ex)
        {
            Diagnostics.Add($"DirectWrite: 確認で例外: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static void Register()
    {
        // Bold側はWindows用の名前表のファミリ名を Regular と同じ「Rounded Mplus 1c」へ揃えてある
        // (配布元のままだと別ファミリ扱いになり、FontWeight=Boldで使われない)。
        RegisterOne("MPLUSRounded1c-Regular.ttf", "Rounded Mplus 1c (TrueType)");
        RegisterOne("MPLUSRounded1c-Bold.ttf", "Rounded Mplus 1c Bold (TrueType)");
    }

    private static void RegisterOne(string FileName, string RegistryValueName)
    {
        try
        {
            string src = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", FileName);
            if (!File.Exists(src))
            {
                Diagnostics.Add($"フォントファイルが見つかりません: {src}");
                return;
            }

            bool installedNow = false;
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "Windows", "Fonts");
                Directory.CreateDirectory(dir);
                string dst = Path.Combine(dir, FileName);

                if (!File.Exists(dst) || new FileInfo(dst).Length != new FileInfo(src).Length)
                {
                    File.Copy(src, dst, true);
                    installedNow = true;
                }

                using var key = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows NT\CurrentVersion\Fonts");
                if (key != null && !string.Equals(key.GetValue(RegistryValueName) as string, dst, StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue(RegistryValueName, dst, RegistryValueKind.String);
                    installedNow = true;
                }

                AddFontResourceExW(dst, 0, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                // 登録できない環境でも、下のプロセス内登録だけは試す。
                Diagnostics.Add($"{FileName}: ユーザーフォントへの登録に失敗しました: {ex.GetType().Name}: {ex.Message}");
            }

            int privateCount = AddFontResourceExW(src, FR_PRIVATE, IntPtr.Zero);
            Diagnostics.Add($"{FileName}: 今回新規に登録={installedNow}, プロセス内登録={privateCount}個");

            if (installedNow)
            {
                InstalledThisRun = true;
                SendNotifyMessageW((IntPtr)0xFFFF, WM_FONTCHANGE, IntPtr.Zero, IntPtr.Zero);
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Add($"{FileName}: フォント登録で例外: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
