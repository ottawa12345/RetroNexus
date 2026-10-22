namespace EmulatorLauncher.Input;

/// <summary>ゲームパッドの十字キーで「リストの中の選択インデックスを動かす」という、各ページで個別にMath.Clampを書いて実装していたお決まりの処理を共通化するヘルパー。</summary>
public static class GamepadListNavigator
{
    /// <summary>現在のインデックスをdelta分だけ動かした新しいインデックスを返す。</summary>
    public static int Move(int current, int delta, int count, bool wrap = false)
    {
        if (count <= 0)
        {
            return current;
        }

        int next = current + delta;

        if (wrap)
        {
            next %= count;
            if (next < 0)
            {
                next += count;
            }
        }
        else
        {
            next = Math.Clamp(next, 0, count - 1);
        }

        return next;
    }
}
