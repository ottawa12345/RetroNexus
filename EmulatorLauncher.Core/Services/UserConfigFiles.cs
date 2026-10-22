using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EmulatorLauncher.Services;

/// <summary>
/// 利用者が画面から書き換える設定ファイル(Launchers.json / Consoles.json / libretro.json)の置き場所。
///
/// 配布物の実行フォルダに入っているものは「初期値」で、実際に読み書きするのは data\ の中のコピー。
/// こうしておくと、アプリを丸ごと上書き更新しても、利用者の変更(ランチャーの割り当て、コアの設定値)が消えない。
///
/// 初回(data\ にまだ無い)は、実行フォルダの初期版をそのままコピーする。
/// 2回目以降は、初期版にだけある項目(新しいバージョンで増えたコンソール/ランチャー/コア/コアの設定項目)を、
/// data\ 側へ「足す」だけにする。data\ にある項目は、変更も削除もしない。
/// (初期版で名前や値が変わった既存の項目は、data\ 側には反映されない。)
/// 処理はプロセスごとに1回だけ。失敗しても、あるものをそのまま使い、アプリは止めない。
/// </summary>
public static class UserConfigFiles
{
    private static readonly ConcurrentDictionary<string, bool> Done = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    private static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string DataFolder => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>Launchers.json の読み書きに使うパス(data\Launchers.json)。</summary>
    public static string LaunchersPath => Ensure("Launchers.json", Path.Combine(AppContext.BaseDirectory, "Launchers.json"),
        e => Key(e, "id") + "\u0001" + Key(e, "name"), null);

    /// <summary>Consoles.json の読み書きに使うパス(data\Consoles.json)。</summary>
    public static string ConsolesPath => Ensure("Consoles.json", Path.Combine(AppContext.BaseDirectory, "Consoles.json"),
        e => Key(e, "id"), null);

    /// <summary>libretro.json(コアの一覧と設定値)の読み書きに使うパス(data\libretro.json)。</summary>
    public static string CoreCatalogPath => Ensure("libretro.json", Path.Combine(AppContext.BaseDirectory, "cores", "libretro.json"),
        // 同じコアを複数のコンソールが共用する(blueMSX: MSX/MSX2、FBNeo: アーケード/ネオジオ等)ため、
        // コア名だけでなくコンソールIdも含めて同じ項目かを判断する。
        e => Key(e, "core_name") + "\u0001" + Key(e, "consoleId"), MergeCoreOptions);

    private static string Key(JsonObject e, string name)
        => e[name] is JsonValue v && v.TryGetValue(out string? s) ? (s ?? string.Empty).Trim().ToLowerInvariant() : string.Empty;

    /// <summary>既存のコアの項目に、初期版にだけある設定項目(options の key)を足す。利用者が選んだ value は触らない。</summary>
    private static bool MergeCoreOptions(JsonObject user, JsonObject shipped)
    {
        if (shipped["options"] is not JsonArray shippedOptions) return false;
        if (user["options"] is not JsonArray userOptions)
        {
            user["options"] = shippedOptions.DeepClone();
            return true;
        }

        var have = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonNode? o in userOptions)
            if (o is JsonObject oo && oo["key"] is JsonValue kv && kv.TryGetValue(out string? k) && k is not null) have.Add(k);

        bool changed = false;
        foreach (JsonNode? o in shippedOptions)
        {
            if (o is JsonObject oo && oo["key"] is JsonValue kv && kv.TryGetValue(out string? k) && k is not null && have.Add(k))
            {
                userOptions.Add(oo.DeepClone());
                changed = true;
            }
        }
        return changed;
    }

    private static string Ensure(string fileName, string shippedPath, Func<JsonObject, string> keyOf, Func<JsonObject, JsonObject, bool>? mergeEntry)
    {
        string userPath = Path.Combine(DataFolder, fileName);
        if (Done.ContainsKey(fileName)) return userPath;

        lock (Gate)
        {
            if (Done.ContainsKey(fileName)) return userPath;
            try
            {
                Directory.CreateDirectory(DataFolder);
                if (!File.Exists(shippedPath))
                {
                    // 初期版が無い(開発中など)。data\ にあるものをそのまま使う。
                }
                else if (!File.Exists(userPath))
                {
                    File.Copy(shippedPath, userPath);
                    AppLog.Info("Data", $"{fileName} を data\\ へコピーしました(初回)");
                }
                else
                {
                    MergeMissing(fileName, shippedPath, userPath, keyOf, mergeEntry);
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Data", $"{fileName} の準備に失敗しました(あるものをそのまま使います)", ex);
            }
            finally
            {
                Done[fileName] = true;
            }
        }
        return userPath;
    }

    private static void MergeMissing(string fileName, string shippedPath, string userPath,
        Func<JsonObject, string> keyOf, Func<JsonObject, JsonObject, bool>? mergeEntry)
    {
        JsonNode? userNode, shippedNode;
        try
        {
            userNode = JsonNode.Parse(File.ReadAllText(userPath), documentOptions: ReadOptions);
            shippedNode = JsonNode.Parse(File.ReadAllText(shippedPath), documentOptions: ReadOptions);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Data", $"{fileName} が読めないため、初期版との統合を行いません", ex);
            return;
        }
        if (userNode is not JsonArray userArray || shippedNode is not JsonArray shippedArray) return;

        var index = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (JsonNode? n in userArray)
            if (n is JsonObject o) index.TryAdd(keyOf(o), o);

        int added = 0;
        bool changed = false;
        foreach (JsonNode? n in shippedArray)
        {
            if (n is not JsonObject s) continue;
            string key = keyOf(s);
            if (key.Length == 0) continue;

            if (index.TryGetValue(key, out JsonObject? existing))
            {
                if (mergeEntry is not null && mergeEntry(existing, s)) changed = true;
            }
            else
            {
                JsonObject copy = (JsonObject)s.DeepClone();
                userArray.Add(copy);
                index[key] = copy;
                added++;
                changed = true;
            }
        }
        if (!changed) return;

        // 途中で失敗しても元のファイルが壊れないよう、一時ファイルへ書いてから置き換える。
        string temp = userPath + ".tmp";
        File.WriteAllText(temp, userArray.ToJsonString(WriteOptions), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, userPath, overwrite: true);
        AppLog.Info("Data", $"{fileName}: 新しい項目を {added} 件、初期版から追加しました");
    }
}
