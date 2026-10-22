using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EmulatorLauncher.Models;

/// <summary>
/// ROMフォルダ(<see cref="RomFolder"/>)配下で見つかった1本のROMファイルの管理情報
/// (RomFileテーブルに対応)。
///
/// <see cref="FileName"/>はフォルダパスを含まない純粋なファイル名のみを持つ。実際の
/// フルパスは <see cref="ParentId"/>(RomFolder.Noと同じ値)から親フォルダの
/// <see cref="RomFolder.Path"/>を引き、それとFileNameを結合して得る想定
/// (<see cref="Data.RomFileRepository.ResolveFullPathAsync"/>参照)。
/// <see cref="VolumeSerial"/>と<see cref="FileId"/>の組み合わせはNTFS上でこのファイルを
/// 一意に識別できる想定(RomFolderのVolumeSerial+FolderIdと同じ考え方)。
///
/// GameSelectPage/GamePlayPageでは、ダミーデータのGameInfoの代わりにこのクラスを
/// そのままゲーム選択画面のカードの表示・選択に使う(<see cref="IsSelected"/>と
/// <see cref="AccentColorHex"/>はそのためのUI用プロパティで、DBには保存されない
/// ―RomFileRepositoryの読み書きはNo/ParentId/FileId等の実データ列だけを対象にしている)。
/// </summary>
public class RomFile : INotifyPropertyChanged, ISelectableCard
{
    /// <summary>SQLite側でAUTOINCREMENT採番される一意な番号(RomFolder.Noと同じ考え方)。
    /// 新規追加前は未使用(0)。</summary>
    public int No { get; set; }

    /// <summary>
    /// ゲームを持たない「本体(BIOS)起動」用の擬似エントリか。DBには保存されず、
    /// ゲーム選択画面が起動時にだけ作る(GameSelectPage.StartBiosBoot)。
    /// </summary>
    public bool IsBiosBoot { get; set; }

    /// <summary>親フォルダを指す、RomFolder.Noそのもの(型もintで揃えてある。DB上もFOREIGN KEYで参照する)。</summary>
    public int ParentId { get; set; }

    /// <summary>NTFS上のファイル識別子。VolumeSerialとの組み合わせで一意に識別する。</summary>
    public Guid FileId { get; set; }

    /// <summary>NTFSボリュームのシリアル番号。FileIdとの組み合わせで一意に識別する。</summary>
    public ulong VolumeSerial { get; set; }

    /// <summary>フォルダパスを含まない、純粋なファイル名のみ(例: "Super Mario.sfc")。</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>複数枚組のゲーム(.m3uファイル)か。メンバーのディスクはDB(RomDiscMember)で対応づけてあり、一覧には出ない。</summary>
    public bool IsDiscSet => FileName.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase);

    /// <summary>論理削除フラグ。trueの間は一覧などから除外する想定(物理削除はDeleteAsync参照)。</summary>
    public bool IsDeleted { get; set; }

    /// <summary>このファイルに対応するコンソールのId("fc"、"sfc"等。ConsoleInfo.Idと同じ)。</summary>
    public string ConsoleId { get; set; } = string.Empty;

    /// <summary>英語タイトル(RDB由来。RDBに無ければ拡張子を除いたファイル名)。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// <see cref="Title"/>がRDBの名前で決まったか。スキャン中だけ使う一時的なフラグ(DBには保存されない)。
    /// RDBに無いゲームだけ、日本語表(data/jdb/*.csv)の英語タイトルで<see cref="Title"/>を補うために使う
    /// (英語タイトルはRDBを優先する)。
    /// </summary>
    public bool TitleFromRdb { get; set; }

    /// <summary>日本語タイトル(data/jdb/{コンソールId}.csvのjpname。表に無ければ空)。</summary>
    public string TitleJa { get; set; } = string.Empty;

    /// <summary>日本語タイトルのひらがな読み(同CSVのkana。並べ替え・検索用。無ければ空)。</summary>
    public string TitleKana { get; set; } = string.Empty;

    /// <summary>画面に出すタイトル。日本語タイトルがあればそれを、無ければ英語タイトルを返す
    /// (DBには保存されないUI用プロパティ)。</summary>
    public string DisplayTitle => string.IsNullOrEmpty(TitleJa) ? Title : TitleJa;

    /// <summary>「あいうえお順」の並べ替えキー。ひらがな読みがあればそれ、無ければ表示タイトル。</summary>
    public string SortKey => string.IsNullOrEmpty(TitleKana) ? DisplayTitle : TitleKana;

    /// <summary>検索語が、英語タイトル・日本語タイトル・ひらがな読みのどれかに含まれるか(大文字小文字は区別しない)。</summary>
    public bool MatchesSearch(string text)
        => Title.Contains(text, StringComparison.CurrentCultureIgnoreCase)
           || TitleJa.Contains(text, StringComparison.CurrentCultureIgnoreCase)
           || TitleKana.Contains(text, StringComparison.CurrentCultureIgnoreCase);

    public string Publisher { get; set; } = string.Empty;
    public int ReleaseYear { get; set; }
    public string Players { get; set; } = "1人用";
    public string Genre { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>最後にプレイした日時(未プレイならnull)。</summary>
    public DateTimeOffset? LastPlayed { get; set; }

    public int PlayTimeMinutes { get; set; }

    public string IconPath { get; set; } = string.Empty;

    /// <summary>
    /// このゲームだけで使うランチャー名(LauncherInfo.Name。コンソールIdが同じものに限る)。
    /// 空文字ならコンソールの既定ランチャー(ConsoleInfo.LauncherName)で起動する。
    /// 実体はCustomの"launcher"(DBに専用の列は無い)。
    /// 指定したランチャーが削除されて見つからない場合も、既定ランチャーへ戻って起動する。
    /// </summary>
    public string Launcher
    {
        get => RomCustom.GetLauncher(Custom);
        set
        {
            var obj = RomCustom.Parse(Custom);
            RomCustom.SetLauncher(obj, value ?? string.Empty);
            Custom = RomCustom.Serialize(obj);
        }
    }

    /// <summary>
    /// このゲームだけの個別設定(標準との差分だけを持つJSON文字列。形式は <see cref="RomCustom"/> 参照)。
    /// 空文字なら個別設定なし。保存はRomFileRepository.UpdateCustomAsync(部分更新)だけで行う
    /// (画面に持っているこのオブジェクトの値で丸ごと上書きして、他の項目を消さないため)。
    /// </summary>
    public string Custom { get; set; } = string.Empty;

    /// <summary>
    /// カード表示用のプレースホルダー背景色。RomFileテーブルにはまだ対応する列が無いため、
    /// 常に既定色を返すUI専用プロパティ(DBへは保存されない)。今後、タイトルごとに色を
    /// 変えたくなったらRomFileテーブルへ列を追加し、RomFileRepository側の読み書きも
    /// 対応させる想定。
    /// </summary>
    public string AccentColorHex { get; set; } = "#455A64";

    /// <summary>
    /// カード表示用のパッケージ画像縦横比(幅÷高さ)。RomFileテーブルには対応する列が無い
    /// UI専用プロパティで、DBへは保存されない(AccentColorHexと同じ扱い)。
    /// GameSelectPageがロード時に、ConsoleId経由でConsoleInfo.AspectRatioから値を
    /// 反映する(1本のRomFileがどのコンソールに属するかは変わらないため、ここに複製して
    /// 持たせることで、x:Bindのデータテンプレート内から直接参照できるようにしている)。
    /// </summary>
    public double AspectRatio { get; set; } = 1.0;

    private bool _isSelected;
    /// <summary>ゲーム選択画面での選択状態(UI専用。DBへは保存されない)。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// ゲーム情報の編集(タイトル・発売元・概要・画像など)を保存したあと、一覧のカードなどの表示を更新するために呼ぶ。
    /// 画面側が OneWay でバインドしている項目(DisplayTitle・IconPath など)へ通知する。
    /// </summary>
    public void NotifyGameInfoChanged()
    {
        foreach (string name in new[]
        {
            nameof(Title), nameof(TitleJa), nameof(TitleKana), nameof(DisplayTitle), nameof(SortKey),
            nameof(Publisher), nameof(ReleaseYear), nameof(Players), nameof(Genre), nameof(Description), nameof(IconPath),
        })
        {
            OnPropertyChanged(name);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}