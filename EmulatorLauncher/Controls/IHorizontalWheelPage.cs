namespace EmulatorLauncher.Controls;

/// <summary>
/// 一覧が横一列(横スクロール)の画面が実装する。trueのあいだ、マウスのホイールの上下を「左右」の移動として扱う
/// (上へ回す=左、下へ回す=右)。上下に動かす先が無い画面では、ホイール上下のままだと使いにくいため。
/// </summary>
public interface IHorizontalWheelPage
{
    bool WheelIsHorizontal { get; }
}
