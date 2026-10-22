using System;
using System.Runtime.InteropServices;
using EmulatorLauncher.Input;

namespace EmulatorLauncher.Emulation;

/// <summary>
/// libretroのセンサー(ジャイロ/加速度。RETRO_ENVIRONMENT_GET_SENSOR_INTERFACE)と
/// 振動(GET_RUMBLE_INTERFACE)のインターフェースを、SDLのゲームパッド(DualSense等)へ繋ぐ。
/// Wii(Dolphin)のように、傾き/振り/振動を使うコアが対象。ポート番号は1P=0、2P=1。
/// </summary>
internal sealed class CoreMotionRumble
{
    private const int SDL_SENSOR_ACCEL = 1;
    private const int SDL_SENSOR_GYRO = 2;

    // retro_sensor_action / retro_sensor_type(libretro.h)
    private const uint SENSOR_ENABLE = 0;
    private const uint SENSOR_DISABLE = 1;
    private const uint SENSOR_ACCELEROMETER_X = 0;
    private const uint SENSOR_GYROSCOPE_X = 3;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte SetSensorStateT(uint port, uint action, uint rate);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate float GetSensorInputT(uint port, uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte SetRumbleStateT(uint port, uint effect, ushort strength);

    private readonly SetSensorStateT _setSensorState;
    private readonly GetSensorInputT _getSensorInput;
    private readonly SetRumbleStateT _setRumbleState;
    private readonly bool[] _accelOn = new bool[2];
    private readonly bool[] _gyroOn = new bool[2];
    private readonly ushort[] _strong = new ushort[2];
    private readonly ushort[] _weak = new ushort[2];
    private readonly float[] _buf = new float[3];

    /// <summary>コアがセンサー/振動を問い合わせた(=使う)か。</summary>
    public bool SensorRequested { get; private set; }
    public bool RumbleRequested { get; private set; }

    /// <summary>
    /// trueのとき、センサー値をDolphin(Wii)が期待する単位/軸へ変換して返す(加速度=g、軸=Wiiリモコン軸)。
    /// falseのときはSDLの値(加速度=m/s²、パッド軸)をそのまま返す(他のコア用)。
    /// </summary>
    public bool WiiRemoteAxes { get; set; }

    /// <summary>falseのとき(WiiRemoteAxes時のみ)、動きを無効にする(ジャイロ=0、加速度=静止状態の1g)。</summary>
    public volatile bool MotionToCore = true;

    /// <summary>振動を有効にするか(設定で切れるようにする)。</summary>
    public volatile bool RumbleEnabled = true;

    public CoreMotionRumble()
    {
        _setSensorState = SetSensorState;
        _getSensorInput = GetSensorInput;
        _setRumbleState = SetRumbleState;
    }

    public bool HandleGetSensorInterface(nint data)
    {
        if (data == nint.Zero) return false;
        SensorRequested = true;
        EmulatorLauncher.Services.AppLog.Info("Sensor", "コアがセンサーインターフェースを要求した");
        Marshal.WriteIntPtr(data, 0, Marshal.GetFunctionPointerForDelegate(_setSensorState));
        Marshal.WriteIntPtr(data, nint.Size, Marshal.GetFunctionPointerForDelegate(_getSensorInput));
        return true;
    }

    public bool HandleGetRumbleInterface(nint data)
    {
        if (data == nint.Zero) return false;
        RumbleRequested = true;
        Marshal.WriteIntPtr(data, 0, Marshal.GetFunctionPointerForDelegate(_setRumbleState));
        return true;
    }

    private byte SetSensorState(uint port, uint action, uint rate)
    {
        if (port >= 2) { EmulatorLauncher.Services.AppLog.Info("Sensor", $"SetSensorState port={port} action={action} rate={rate} -> 0(port範囲外)"); return 0; }
        nint pad = GamepadSlots.GetHandle((int)port);
        if (pad == nint.Zero) { EmulatorLauncher.Services.AppLog.Info("Sensor", $"SetSensorState port={port} action={action} rate={rate} -> 0(パッド無し)"); return 0; }
        // retro_sensor_action: 0=加速度ON 1=加速度OFF 2=ジャイロON 3=ジャイロOFF(4,5=照度は非対応)。
        // 加速度とジャイロは別々に切り替える(以前は「0以外=全部OFF」と扱っていたため、
        // コアがジャイロをONにした(2)瞬間に加速度もジャイロも止まってしまっていた)。
        if (action > 3) { EmulatorLauncher.Services.AppLog.Info("Sensor", $"SetSensorState port={port} action={action} rate={rate} -> 0(非対応action)"); return 0; }
        bool gyroAction = action >= 2;
        bool enable = (action & 1) == 0;
        int sensor = gyroAction ? SDL_SENSOR_GYRO : SDL_SENSOR_ACCEL;
        try
        {
            bool has = Sdl.SDL_GamepadHasSensor(pad, sensor);
            if (!has) { EmulatorLauncher.Services.AppLog.Info("Sensor", $"SetSensorState port={port} action={action} rate={rate} -> 0(このパッドにセンサー無し)"); return 0; }
            Sdl.SDL_SetGamepadSensorEnabled(pad, sensor, enable);
            if (gyroAction) _gyroOn[port] = enable; else _accelOn[port] = enable;
            EmulatorLauncher.Services.AppLog.Info("Sensor", $"SetSensorState port={port} action={action} rate={rate} -> 1({(gyroAction ? "ジャイロ" : "加速度")}{(enable ? "ON" : "OFF")})");
            return 1;
        }
        catch { return 0; }
    }

    /// <summary>
    /// 加速度はg単位(重力込み)、ジャイロはrad/s。軸はWiiリモコン軸へ変換して返す。
    /// </summary>
    private float GetSensorInput(uint port, uint id)
    {
        if (port >= 2 || id > 5) return 0f;
        nint pad = GamepadSlots.GetHandle((int)port);
        if (pad == nint.Zero) return 0f;
        bool gyro = id >= SENSOR_GYROSCOPE_X;
        if (gyro ? !_gyroOn[port] : !_accelOn[port]) return 0f;
        try
        {
            if (!Sdl.SDL_GetGamepadSensorData(pad, gyro ? SDL_SENSOR_GYRO : SDL_SENSOR_ACCEL, _buf, 3)) return 0f;
            // SDLのパッド軸(+X右 +Y上 +Z手前)を、コア(Dolphin)が期待するWiiリモコン軸(+X左 +Y手前 +Z上)へ回す。
            // 「パッドを前に構えて、リモコンの先を画面へ向ける」持ち方に対応。加速度はm/s²→g(コア側でg*9.80665する)。
            int axis = (int)(gyro ? id - SENSOR_GYROSCOPE_X : id - SENSOR_ACCELEROMETER_X);
            float v = _buf[axis];
            if (WiiRemoteAxes)
            {
                v = axis switch
                {
                    0 => -_buf[0],
                    1 => _buf[2],
                    _ => _buf[1],
                };
                if (!gyro) v /= 9.80665f;
                if (!MotionToCore) v = (!gyro && axis == 2) ? 1f : 0f;
            }
            return v;
        }
        catch { return 0f; }
    }

    private byte SetRumbleState(uint port, uint effect, ushort strength)
    {
        if (port >= 2) return 0;
        nint pad = GamepadSlots.GetHandle((int)port);
        if (pad == nint.Zero) return 0;
        // RETRO_RUMBLE_STRONG=0(低周波/大きいモーター)、RETRO_RUMBLE_WEAK=1(高周波/小さいモーター)
        if (effect == 0) _strong[port] = strength; else _weak[port] = strength;
        try
        {
            // コントローラー設定の「振動の強さ」(0〜100%)をかける。読み込み結果はキャッシュ済み。
            int level = Math.Clamp(ControllerMappingService.Load().RumbleLevel, 0, 100);
            ushort strong = (ushort)(_strong[port] * level / 100);
            ushort weak = (ushort)(_weak[port] * level / 100);
            if (!RumbleEnabled || (strong == 0 && weak == 0))
                Sdl.SDL_RumbleGamepad(pad, 0, 0, 0);
            else
                Sdl.SDL_RumbleGamepad(pad, strong, weak, 10000);
            return 1;
        }
        catch { return 0; }
    }

    /// <summary>終了時に、振動とセンサーを止める。</summary>
    public void StopAll()
    {
        for (int port = 0; port < 2; port++)
        {
            nint pad = GamepadSlots.GetHandle(port);
            if (pad == nint.Zero) continue;
            try
            {
                if (_strong[port] != 0 || _weak[port] != 0) Sdl.SDL_RumbleGamepad(pad, 0, 0, 0);
                if (_accelOn[port]) Sdl.SDL_SetGamepadSensorEnabled(pad, SDL_SENSOR_ACCEL, false);
                if (_gyroOn[port]) Sdl.SDL_SetGamepadSensorEnabled(pad, SDL_SENSOR_GYRO, false);
            }
            catch { }
            _strong[port] = 0; _weak[port] = 0; _accelOn[port] = false; _gyroOn[port] = false;
        }
    }
}
