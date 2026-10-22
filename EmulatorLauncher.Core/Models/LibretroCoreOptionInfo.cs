using System.Text.Json.Serialization;

namespace EmulatorLauncher.Models;

/// <summary>cores/libretro.jsonの各コアエントリが持つ、コア固有設定(libretroコアオプション) 1項目分の定義。</summary>
public class LibretroCoreOptionInfo
{
    /// <summary>そのコアが実際に参照するオプションキー(libretroコアがRETRO_ENVIRONMENT_GET_VARIABLE等で参照する変数名。例: "mupen64plus-EnableHiRes")。</summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    /// <summary>コア毎の設定画面に表示するラベル(例: "内部解像度")。</summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    /// <summary>選択可能な値の一覧。</summary>
    [JsonPropertyName("choices")]
    public List<LibretroCoreOptionChoice> Choices { get; set; } = new();

    /// <summary>既定値(Choices中のいずれかのValueと一致する想定)。</summary>
    [JsonPropertyName("default")]
    public string Default { get; set; } = string.Empty;

    /// <summary>現在ユーザーが選択している値。</summary>
    [JsonPropertyName("value")]
    public string? Value { get; set; }

    /// <summary>trueの場合、この項目を設定画面(SettingsPage「コア毎の設定」タブ、およびゲーム中の中断メニュー「設定」パネルのどちらも)には表示しない。</summary>
    [JsonPropertyName("hide")]
    public bool Hide { get; set; }

    /// <summary>このオプションの値が変わった直後に、フロントエンド側で強制的に実行したい処理を名前で指定する(0個以上)。</summary>
    [JsonPropertyName("actions")]
    public List<string> Actions { get; set; } = new();

    /// <summary>trueの場合、この項目はコアを起動し直さないと反映されない(例: N64の内部解像度)。</summary>
    [JsonPropertyName("restartRequired")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool RestartRequired { get; set; }
}

/// <summary>LibretroCoreOptionInfo.Choicesの1件(選択肢の実値と表示ラベル)。</summary>
public class LibretroCoreOptionChoice
{
    /// <summary>実際にコアへ渡す値(例: "1")。</summary>
    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    /// <summary>画面表示用のラベル(例: "640x480")。</summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;
}