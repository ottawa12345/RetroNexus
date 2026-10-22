using System;

namespace EmulatorLauncher.Input;

/// <summary>「今この瞬間、指定の論理ボタンが(1P・2Pのどちらかのゲームパッドで)押されているか」を読む静的ヘルパー。</summary>
public static class GamepadHoldState
{
    public static bool IsButtonHeld(GamepadButton button)
    {
        try
        {
            // ゲーム以外の操作は1P・2Pのどちらのコントローラーでも行えるので、どちらかが押していれば押下扱い。
            for (int slot = 0; slot < GamepadSlots.MaxSlots; slot++)
            {
                nint pad = GamepadSlots.GetHandle(slot);
                if (pad != nint.Zero && GamepadPhysicalButtonReader.IsPressed(pad, button)) return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            // SDL未初期化・DLL読み込み失敗などは「押されていない」扱いにする(押しっぱなしスクロールが単発のページ送りに縮退するだけで、操作不能にはならない)。
            System.Diagnostics.Debug.WriteLine($"[GamepadHoldState] 押下状態の取得に失敗しました: {ex.Message}");
            return false;
        }
    }
}
