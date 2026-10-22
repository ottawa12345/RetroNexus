using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmulatorLauncher.Models;

/// <summary>cores/libretro.jsonの1エントリ。</summary>
public class LibretroCoreInfo
{
    /// <summary>一覧表示用の名前(例: "Nintendo - Nintendo 64 (Mupen64Plus-Next)")。</summary>
    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>対応するコンソールのId("fc"、"n64"等。ConsoleInfo.Id/LauncherInfo.Idと同じ)。</summary>
    [JsonPropertyName("consoleId")]
    public string ConsoleId { get; set; } = string.Empty;

    /// <summary>coresフォルダに配置するコアのファイル名(例: "mupen64plus_next_libretro.dll")。</summary>
    [JsonPropertyName("core_name")]
    public string CoreName { get; set; } = string.Empty;

    /// <summary>coresフォルダに実際に配置済みかどうか。</summary>
    [JsonPropertyName("installed")]
    [JsonConverter(typeof(YesNoJsonConverter))]
    public bool Installed { get; set; }

    /// <summary>このコア固有の設定項目(コアオプション)一覧。</summary>
    [JsonPropertyName("options")]
    public List<LibretroCoreOptionInfo> Options { get; set; } = new();

    /// <summary>このコアが必要とするsystemデータ(BIOS以外の同梱データ等。例: DolphinのSys)のzipのURL(任意)。</summary>
    [JsonPropertyName("system_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SystemUrl { get; set; }
}

/// <summary>bool値をJSON上は"yes"/"no"の文字列として読み書きするコンバータ。</summary>
public class YesNoJsonConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return string.Equals(reader.GetString(), "yes", StringComparison.OrdinalIgnoreCase);
        }

        if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
        {
            return reader.GetBoolean();
        }

        return false;
    }

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
        => writer.WriteStringValue(value ? "yes" : "no");
}
