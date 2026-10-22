using EmulatorLauncher.Models;
using Microsoft.UI.Xaml.Controls;

namespace EmulatorLauncher.Views;

/// <summary>
/// 本体(BIOS)起動の共通処理。ゲーム選択画面とコンソール選択画面の両方から使う。
/// Launchers.jsonで"biosBoot": trueのコア(DC/PS2/X68000等)を使うコンソールだけが対象。
/// </summary>
internal static class BiosBoot
{
    /// <summary>ヒントバーに出す案内(Menuボタン)。</summary>
    public const string HintKey = "+";
    public const string HintLabel = "BIOS起動";

    public static bool IsAvailable(ConsoleInfo? console)
        => console is not null
           && !string.IsNullOrEmpty(console.Id)
           && SampleData.ResolveLauncher(console.Id, console.LauncherName)?.BiosBoot == true;

    /// <summary>ゲームを選ばずに本体(BIOS)だけを起動する。DBには保存しない擬似RomFileで起動する。</summary>
    public static void Start(Frame frame, ConsoleInfo console)
    {
        if (!IsAvailable(console)) return;
        var bios = new RomFile
        {
            No = -1,
            ConsoleId = console.Id,
            Title = "本体(BIOS)",
            IsBiosBoot = true,
        };
        frame.Navigate(typeof(GamePlayPage), bios);
    }
}
