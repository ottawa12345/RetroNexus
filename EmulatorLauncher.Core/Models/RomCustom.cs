using System.Text.Json.Nodes;

namespace EmulatorLauncher.Models;

/// <summary>RomFile.Custom(ゲーム個別の設定をまとめて持つJSON文字列)を扱う小さなヘルパー。</summary>
public static class RomCustom
{
    public const string LauncherKey = "launcher";

    /// <summary>JSON文字列を読み込む。</summary>
    public static JsonObject Parse(string? json)
    {
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                if (JsonNode.Parse(json) is JsonObject obj) return obj;
            }
            catch (Exception)
            {
                // 壊れたJSONは差分なしとして扱う(ゲームの起動を妨げない)。
            }
        }

        return new JsonObject();
    }

    /// <summary>保存用の文字列にする。</summary>
    public static string Serialize(JsonObject obj) => obj.Count == 0 ? string.Empty : obj.ToJsonString();

    /// <summary>トップレベルの文字列値を取得する(無い・文字列でないならnull)。</summary>
    public static string? GetString(string? json, string key)
        => GetString(Parse(json), key);

    public static string? GetString(JsonObject obj, string key)
        => obj[key] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    // ゲーム別のプレイ設定(すべて「標準との差分」だけを持つ。標準=コア毎の既定値)。
    public const string VideoKey = "video";
    public const string OptionsKey = "options";

    /// <summary>セクション内の文字列値を取得する(無ければnull)。</summary>
    public static string? GetSectionString(JsonObject obj, string section, string key)
        => obj[section] is JsonObject sec ? GetString(sec, key) : null;

    /// <summary>セクション内の文字列値をすべて取得する(無ければ空)。</summary>
    public static Dictionary<string, string> GetSectionStrings(JsonObject obj, string section)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (obj[section] is JsonObject sec)
        {
            foreach (var pair in sec)
            {
                if (pair.Value is JsonValue v && v.TryGetValue(out string? text) && text is not null)
                    result[pair.Key] = text;
            }
        }
        return result;
    }

    /// <summary>値が標準と同じならキーを削除、違えば保存する(差分だけを持つ)。</summary>
    public static void SetSectionDiff(JsonObject obj, string section, string key, string value, string baseValue)
    {
        JsonObject? sec = obj[section] as JsonObject;
        if (string.Equals(value, baseValue, StringComparison.Ordinal))
        {
            sec?.Remove(key);
            if (sec is not null && sec.Count == 0) obj.Remove(section);
            return;
        }

        if (sec is null)
        {
            sec = new JsonObject();
            obj[section] = sec;
        }
        sec[key] = value;
    }

    /// <summary>トップレベルの文字列値を、標準と同じなら削除、違えば保存する。</summary>
    public static void SetDiff(JsonObject obj, string key, string value, string baseValue)
    {
        if (string.Equals(value, baseValue, StringComparison.Ordinal)) obj.Remove(key);
        else obj[key] = value;
    }

    /// <summary>ゲーム別のランチャー名(未指定なら空文字)。</summary>
    public static string GetLauncher(string? json) => GetString(json, LauncherKey) ?? string.Empty;

    /// <summary>ゲーム別のランチャー名を設定する(空文字ならキーごと削除=コンソールの既定に戻す)。</summary>
    public static void SetLauncher(JsonObject obj, string launcher)
    {
        if (string.IsNullOrEmpty(launcher)) obj.Remove(LauncherKey);
        else obj[LauncherKey] = launcher;
    }
}
