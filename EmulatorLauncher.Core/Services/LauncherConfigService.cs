using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmulatorLauncher.Models;

namespace EmulatorLauncher.Services;

/// <summary>
/// Launchers.json(ランチャー=libretroコア/外部コマンドの一覧)と、それに紐づく
/// Consoles.json(コンソール一覧。使用中ランチャー名 launcherName を持つ)の
/// 追加・編集・削除を行うサービス。設定画面の「ランチャー管理」から使う。
///
/// 両ファイルとも手書きで編集される前提のファイルなので、ここでは型付きのシリアライズではなく
/// JsonNodeで「該当エントリのキーだけ」を書き換え、知らないキーは丸ごと残す。
/// 書き出し形式も、既存ファイルと同じ「1エントリ1行」に揃える。
///
/// 読み込む側(SampleData.LoadLaunchers/LoadConsoles)は毎回ファイルを読み直すため、
/// 保存すれば次の読み込みからそのまま反映される。
/// 検証エラー等は <see cref="InvalidOperationException"/>(メッセージは画面表示用の日本語)で通知する。
/// </summary>
public static class LauncherConfigService
{
    private const string LaunchersFileName = "Launchers.json";
    private const string ConsolesFileName = "Consoles.json";

    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        // 日本語をそのまま(\uXXXXにせず)書き出す。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly object FileLock = new();

    // 読み書きするのは data\ のコピー(UserConfigFiles が、初回に初期版をコピーし、以後は新しい項目だけ足す)。
    private static string PathOf(string fileName)
        => string.Equals(fileName, LaunchersFileName, StringComparison.OrdinalIgnoreCase) ? UserConfigFiles.LaunchersPath
         : string.Equals(fileName, ConsolesFileName, StringComparison.OrdinalIgnoreCase) ? UserConfigFiles.ConsolesPath
         : Path.Combine(AppContext.BaseDirectory, fileName);

    /// <summary>
    /// ランチャーを追加(<paramref name="original"/> が null)または更新する。
    /// </summary>
    /// <param name="original">編集前のランチャー(新規追加なら null)。Id+Nameで元のエントリを特定する。</param>
    /// <param name="edited">保存したい内容。IsExternal かどうかは ExePath の有無で決まる。</param>
    /// <param name="useForConsole">このコンソールで使うランチャーとして Consoles.json の launcherName にも設定する。</param>
    /// <param name="newConsoleName">Idに一致するコンソールがまだ無い場合に作る、コンソールの表示名。</param>
    /// <param name="newConsoleExtensions">同、ROM拡張子(".rpx .wua" のような区切り文字列)。</param>
    public static void Save(LauncherInfo? original, LauncherInfo edited, bool useForConsole,
        string? newConsoleName = null, string? newConsoleExtensions = null)
    {
        string id = edited.Id.Trim();
        string name = edited.Name.Trim();
        ValidateId(id);
        if (name.Length == 0) throw new InvalidOperationException("ランチャー名を入力してください。");

        lock (FileLock)
        {
            JsonArray launchers = LoadArray(LaunchersFileName);

            int index = -1;
            if (original is not null)
            {
                index = FindIndex(launchers, original.Id, original.Name);
                if (index < 0) throw new InvalidOperationException("編集元のランチャーが見つかりませんでした(ファイルが外部で変更された可能性があります)。");
            }

            for (int i = 0; i < launchers.Count; i++)
            {
                if (i == index) continue;
                if (launchers[i] is JsonObject other && GetString(other, "id") == id && GetString(other, "name") == name)
                {
                    throw new InvalidOperationException($"同じコンソールに「{name}」という名前のランチャーが既にあります。");
                }
            }

            JsonObject obj = index >= 0 ? (JsonObject)launchers[index]! : new JsonObject();
            obj["id"] = id;
            obj["name"] = name;

            if (edited.IsExternal)
            {
                obj.Remove("corePath");
                obj["exePath"] = edited.ExePath.Trim();
                obj["args"] = edited.Args ?? string.Empty;
                obj["workingPath"] = edited.WorkingPath?.Trim() ?? string.Empty;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(edited.CorePath)) throw new InvalidOperationException("コアを選択してください。");
                obj.Remove("exePath");
                obj.Remove("args");
                obj.Remove("workingPath");
                obj["corePath"] = edited.CorePath.Trim();
            }

            obj["defaultAspectRatio"] = string.IsNullOrWhiteSpace(edited.DefaultAspectRatio) ? "Auto" : edited.DefaultAspectRatio.Trim();

            if (index < 0) launchers.Add(obj);

            // ---- Consoles.json ----
            JsonArray consoles = LoadArray(ConsolesFileName);
            JsonObject? console = FindConsole(consoles, id);
            bool consoleChanged = false;

            if (console is null)
            {
                string consoleName = string.IsNullOrWhiteSpace(newConsoleName) ? id : newConsoleName.Trim();
                console = new JsonObject
                {
                    ["id"] = id,
                    ["name"] = consoleName,
                    ["maker"] = string.Empty,
                    ["releaseYear"] = 0,
                    ["iconPath"] = string.Empty,
                    ["logoPath"] = string.Empty,
                    ["launcherName"] = name,
                    ["aspectRatio"] = 1.0,
                };

                List<string> extensions = ParseExtensions(newConsoleExtensions);
                if (extensions.Count > 0)
                {
                    var array = new JsonArray();
                    foreach (string extension in extensions) array.Add(extension);
                    console["romExtensions"] = array;
                }

                consoles.Add(console);
                consoleChanged = true;
            }
            else
            {
                string current = GetString(console, "launcherName");
                string originalName = original?.Name ?? string.Empty;

                if (useForConsole || current.Length == 0)
                {
                    if (current != name) { console["launcherName"] = name; consoleChanged = true; }
                }
                else if (original is not null && current == originalName)
                {
                    // 今までこのランチャーが使われていた。名前を変えたなら追従し(参照切れ防止)、
                    // 名前が同じまま「使用しない」にしたなら同じコンソールの別ランチャーへ切り替える。
                    if (originalName != name)
                    {
                        console["launcherName"] = name;
                        consoleChanged = true;
                    }
                    else
                    {
                        string? alternative = FirstOtherLauncherName(launchers, id, index);
                        if (alternative is not null) { console["launcherName"] = alternative; consoleChanged = true; }
                    }
                }
            }

            SaveArray(LaunchersFileName, launchers);
            if (consoleChanged) SaveArray(ConsolesFileName, consoles);
        }
    }

    /// <summary>
    /// ランチャーを削除する。削除したランチャーが Consoles.json で使用中になっていた場合は、
    /// 同じコンソールの残りのランチャーの先頭へ切り替える(残りが無ければ launcherName を空にする。
    /// コンソール自体は残るので、後から別のランチャーを追加すればまた起動できる)。
    /// </summary>
    public static void Delete(LauncherInfo launcher)
    {
        lock (FileLock)
        {
            JsonArray launchers = LoadArray(LaunchersFileName);
            int index = FindIndex(launchers, launcher.Id, launcher.Name);
            if (index < 0) throw new InvalidOperationException("削除するランチャーが見つかりませんでした。");

            launchers.RemoveAt(index);

            JsonArray consoles = LoadArray(ConsolesFileName);
            JsonObject? console = FindConsole(consoles, launcher.Id);
            bool consoleChanged = false;
            if (console is not null && GetString(console, "launcherName") == launcher.Name)
            {
                console["launcherName"] = FirstOtherLauncherName(launchers, launcher.Id, -1) ?? string.Empty;
                consoleChanged = true;
            }

            SaveArray(LaunchersFileName, launchers);
            if (consoleChanged) SaveArray(ConsolesFileName, consoles);
        }
    }

    /// <summary>
    /// Consoles.json の指定コンソールの、アイコン(iconPath)と使用ランチャー(launcherName)を更新する。
    /// 引数が null の項目は変更しない。コンソール編集オーバーレイ(ConsoleEditOverlay)から使う。
    /// </summary>
    public static void UpdateConsole(string consoleId, string? iconPath, string? launcherName)
    {
        lock (FileLock)
        {
            JsonArray consoles = LoadArray(ConsolesFileName);
            JsonObject console = FindConsole(consoles, consoleId)
                                 ?? throw new InvalidOperationException("コンソールが見つかりませんでした。");

            if (iconPath is not null) console["iconPath"] = ToConfigImagePath(iconPath);
            if (launcherName is not null) console["launcherName"] = launcherName;

            SaveArray(ConsolesFileName, consoles);
        }
    }

    /// <summary>
    /// Consoles.json のコンソールの並びを、指定したId順に並べ替えて保存する(設定画面の「コンソール並び替え」用)。
    /// 一覧に含まれないコンソール(外部で追加された等)は、元の相対順のまま末尾に残す。
    /// </summary>
    public static void ReorderConsoles(IReadOnlyList<string> orderedIds)
    {
        lock (FileLock)
        {
            JsonArray consoles = LoadArray(ConsolesFileName);
            var remaining = new List<JsonNode?>();
            foreach (JsonNode? node in consoles) remaining.Add(node);
            consoles.Clear();

            foreach (string id in orderedIds)
            {
                int found = remaining.FindIndex(n =>
                    n is JsonObject o && string.Equals(GetString(o, "id"), id, StringComparison.OrdinalIgnoreCase));
                if (found < 0) continue;
                JsonNode? node = remaining[found];
                remaining.RemoveAt(found);
                consoles.Add(node);
            }

            foreach (JsonNode? node in remaining) consoles.Add(node);

            SaveArray(ConsolesFileName, consoles);
        }
    }

    /// <summary>
    /// 画像パスをConsoles.jsonへ書く形にする。実行フォルダ配下の画像は
    /// "./Images/Console/xx.png"のような相対パス(区切りは/)にし、それ以外はそのまま。
    /// </summary>
    private static string ToConfigImagePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains("://", StringComparison.Ordinal) || !Path.IsPathRooted(path))
        {
            return path;
        }

        string baseDir = Path.GetFullPath(AppContext.BaseDirectory);
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        return "./" + Path.GetRelativePath(baseDir, full).Replace('\\', '/');
    }

    /// <summary>
    /// ".rpx .wua"、"rpx,wua"、".RPX;.WUA" のような文字列を [".rpx", ".wua"] に正規化する
    /// (区切りは空白/カンマ/セミコロン、先頭のドットは無ければ補い、小文字にし、重複は除く)。
    /// </summary>
    public static List<string> ParseExtensions(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        foreach (string token in text.Split(new[] { ' ', ',', ';', '\t', '、', '　' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string extension = token.Trim().TrimStart('*').ToLowerInvariant();
            if (extension.Length == 0 || extension == ".") continue;
            if (!extension.StartsWith('.')) extension = "." + extension;
            if (!result.Contains(extension)) result.Add(extension);
        }

        return result;
    }

    /// <summary>コンソールIDとして使える文字列か検証する(英数字・ハイフン・アンダースコアのみ)。</summary>
    public static void ValidateId(string id)
    {
        if (id.Length == 0) throw new InvalidOperationException("コンソールIDを入力してください。");
        foreach (char c in id)
        {
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_'))
            {
                throw new InvalidOperationException("コンソールIDには英数字・ハイフン・アンダースコアだけが使えます。");
            }
        }
    }

    // ------------------------------------------------------------------

    private static string GetString(JsonObject obj, string key)
    {
        try
        {
            return (string?)obj[key] ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static int FindIndex(JsonArray launchers, string id, string name)
    {
        for (int i = 0; i < launchers.Count; i++)
        {
            if (launchers[i] is JsonObject o && GetString(o, "id") == id && GetString(o, "name") == name) return i;
        }

        return -1;
    }

    private static JsonObject? FindConsole(JsonArray consoles, string id)
    {
        foreach (JsonNode? node in consoles)
        {
            if (node is JsonObject o && string.Equals(GetString(o, "id"), id, StringComparison.OrdinalIgnoreCase)) return o;
        }

        return null;
    }

    private static string? FirstOtherLauncherName(JsonArray launchers, string id, int excludeIndex)
    {
        for (int i = 0; i < launchers.Count; i++)
        {
            if (i == excludeIndex) continue;
            if (launchers[i] is JsonObject o && GetString(o, "id") == id)
            {
                string name = GetString(o, "name");
                if (name.Length > 0) return name;
            }
        }

        return null;
    }

    private static JsonArray LoadArray(string fileName)
    {
        string path = PathOf(fileName);
        if (!File.Exists(path)) return new JsonArray();

        JsonNode? node = JsonNode.Parse(
            File.ReadAllText(path),
            documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        return node as JsonArray
               ?? throw new InvalidOperationException($"{fileName} の形式が正しくありません(トップレベルが配列ではありません)。");
    }

    private static void SaveArray(string fileName, JsonArray array)
    {
        var lines = new List<string>();
        foreach (JsonNode? node in array)
        {
            lines.Add("  " + (node?.ToJsonString(CompactOptions) ?? "null"));
        }

        string text = "[" + Environment.NewLine + string.Join("," + Environment.NewLine, lines) + Environment.NewLine + "]" + Environment.NewLine;

        // 途中で失敗しても元のファイルが壊れないよう、一時ファイルへ書いてから置き換える。
        string path = PathOf(fileName);
        string temp = path + ".tmp";
        File.WriteAllText(temp, text, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, path, overwrite: true);
    }
}