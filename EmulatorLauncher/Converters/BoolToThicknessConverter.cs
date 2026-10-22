using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace EmulatorLauncher.Converters;

/// <summary>IsSelected(bool) を BorderThickness に変換する。</summary>
public class BoolToThicknessConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool isSelected = value is bool b && b;
        return isSelected ? new Thickness(4) : new Thickness(0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>IsSelected(bool) を拡大率(ScaleTransformのScaleX/ScaleY用double)に変換する。</summary>
public class BoolToScaleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool isSelected = value is bool b && b;
        return isSelected ? 1.13 : 1.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>IsSelected(bool) をリスト行のハイライト背景(薄いグレー/透明)に変換する。</summary>
public class BoolToRowHighlightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool isSelected = value is bool b && b;
        return isSelected
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(EmulatorLauncher.Views.AppThemeRouter.IsDarkActive
                ? Windows.UI.Color.FromArgb(255, 0x36, 0x40, 0x5A)
                : Windows.UI.Color.FromArgb(255, 0xE0, 0xE0, 0xE0))
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>bool を Visibility に変換する(x:Bindの暗黙変換に頼らず明示する)。</summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool flag = value is bool b && b;
        bool invert = parameter is string s && s == "Invert";
        if (invert) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>16進数カラー文字列 ("#RRGGBB") を SolidColorBrush に変換する。</summary>
public class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var hex = value as string ?? "#607D8B";
        try
        {
            hex = hex.TrimStart('#');
            byte r = System.Convert.ToByte(hex.Substring(0, 2), 16);
            byte g = System.Convert.ToByte(hex.Substring(2, 2), 16);
            byte b = System.Convert.ToByte(hex.Substring(4, 2), 16);
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
        }
        catch
        {
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray);
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>文字列が空でなければVisible、空(またはnull)ならCollapsedに変換する。
/// RomCollectionInfo.IconPath等、「値があれば表示、無ければ隠す」用途に使う。</summary>
public class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>
/// 文字列パス("ms-appx:///..."やファイルパス)をImageSourceへ変換する。
/// Image.SourceへIconPathを直接x:Bindすると、空文字や不正なパスの場合に内部のUri変換で
/// 例外が発生し、同じテンプレート内の他のバインディング(タイトル文字など)まで
/// 適用されなくなることがあるため、必ずこのコンバータを経由させて安全に変換する。
/// 空文字、または読み込みに失敗するパスの場合はnullを返す(Image.Source=nullは
/// 単に何も表示しないだけで、例外にはならない)。
/// </summary>
public class IconPathToImageSourceConverter : IValueConverter
{
    // BitmapImageはDecodePixelWidth/Heightを指定しない限り、画面上でどれだけ小さく
    // 表示されるかに関わらず「元画像の実解像度のまま」デコードする。ここで使っている
    // ボックスアート(libretro-thumbnailsから取得したもの。ダウンロード時にリサイズしていない)
    // は、コンソールやタイトルによって元画像の解像度がまちまちで、大きいものは
    // 1000px四方を優に超える。カード自体は最大でも380px角(ConsoleSelectPage)程度でしか
    // 表示しないため、この差はまるごと無駄なデコードコスト・メモリになる。
    //
    // これが特定のコンソール(ゲームボーイアドバンス等)でだけゲーム選択画面のスクロールが
    // もたつく主因だった: GridViewはスクロールに応じてコンテナをリサイクルしながら次々と
    // 新しいImage.Sourceをこの変換経由で生成するため、そのコンソールのボックスアート画像群が
    // たまたま他より大きい(=libretro-thumbnails側の元データが大きい)と、スクロール中に
    // 新しいカードが視界に入るたびのデコード負荷がそのまま体感できるカクつきになる。
    //
    // DecodePixelWidthを指定すると、BitmapImageは(可能な場合)そのサイズへ直接デコードして
    // くれる(WICのサムネイル/縮小デコードパスを使うため、フル解像度でデコードしてから
    // 後で縮小するよりずっと安い)。高さは指定しないことで元のアスペクト比のまま縮小される。
    // 実際の表示サイズはこのアプリ内で最大でも380px角(ConsoleSelectPageのコンソール/
    // コレクションカード)なので、余裕を見て400にしておけば、通常のDPIスケーリング
    // (125%程度まで)でも目に見えて粗くなることはない。
    private const int MaxDecodePixelWidth = 400;

    // 同じ画像(パス+デコード幅)を、まだ使われている間は使い回す(弱参照)。ソートでカードが
    // 作り直されたとき、読み込み済みの画像をそのまま使えるので、画像が一瞬空白になるのを防げる。
    // 弱参照なので、どこからも使われなくなった画像はそのまま回収される(メモリは増えない)。
    private static readonly Dictionary<string, WeakReference<Microsoft.UI.Xaml.Media.Imaging.BitmapImage>> Cache = new();

    /// <summary>trueのときだけ、読み込み済みの画像を使い回す。使い回した画像は「読み込み完了」の
    /// イベント(ImageOpened)が再度発火しないため、それを待つ処理(詳細・起動時のカバー画像の
    /// 連結アニメーション)に使う変換では、falseのまま(毎回新しく読み込む)にしておくこと。
    /// ソートでカードを作り直す一覧だけ、XAMLのリソースでtrueにしている。</summary>
    public bool UseCache { get; set; }

    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        var path = value as string;
        if (string.IsNullOrEmpty(path)) return null;

        try
        {
            int width = parameter is string p && int.TryParse(p, out int w) && w > 0 ? w : MaxDecodePixelWidth;
            string key = width + "|" + path;
            if (UseCache && Cache.TryGetValue(key, out var weak) && weak.TryGetTarget(out var cached))
                return cached;

            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage
            {
                DecodePixelWidth = width,
                DecodePixelType = Microsoft.UI.Xaml.Media.Imaging.DecodePixelType.Logical,
            };
            bitmap.UriSource = new Uri(path);

            if (UseCache && Cache.Count > 1000)
            {
                foreach (string dead in Cache.Where(kv => !kv.Value.TryGetTarget(out _)).Select(kv => kv.Key).ToList())
                    Cache.Remove(dead);
            }
            if (UseCache) Cache[key] = new WeakReference<Microsoft.UI.Xaml.Media.Imaging.BitmapImage>(bitmap);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
