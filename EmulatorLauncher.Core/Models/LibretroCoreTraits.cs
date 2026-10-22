namespace EmulatorLauncher.Models;

/// <summary>
/// libretroコアの性質のうち、設定画面(ゲームを起動する前)で知っておきたいもの。
/// 実際にHWレンダリングかどうかはコアの起動後(SET_HW_RENDER)にしか分からないため、
/// ここでは「HWレンダリングを使うコア」を名前で持つ。ゲーム中の判定(EmulationHost.IsHwRendered)が最終的な正解で、
/// これは設定画面で「画質」(2D専用)を出すかどうかの目安にだけ使う。
/// </summary>
public static class LibretroCoreTraits
{
    private static readonly HashSet<string> HardwareRenderedCores = new(StringComparer.OrdinalIgnoreCase)
    {
        "flycast_libretro.dll",
        "swanstation_libretro.dll",
        "mednafen_psx_hw_libretro.dll",
        "ppsspp_libretro.dll",
        "mupen64plus_next_libretro.dll",
        "dolphin_libretro.dll",
        "citra_libretro.dll",
        "pcsx2_libretro.dll",
        "supermodel_libretro.dll",
        "kronos_libretro.dll",
    };

    /// <summary>HWレンダリングを使うコアか(2D専用の「画質」フィルターが使えない)。</summary>
    public static bool IsHardwareRendered(string coreFileName)
        => HardwareRenderedCores.Contains(coreFileName ?? string.Empty);
}
