namespace EmulatorLauncher.Models;

/// <summary>Controls.CycleSelector.Choices、および ControllerMappingRowItem.Choices が使う、「実際に保存する値」と「表示ラベル」の組。</summary>
public class CycleChoice
{
    /// <summary>実際に保存する値。</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>画面表示用のラベル。</summary>
    public string Label { get; set; } = string.Empty;
}
