namespace EmulatorLauncher.Input;

// このファイルは tools の gen_layouts.py から生成した(コントローラー図の定義)。
// 手で直す場合は、ボタンの座標(キャンバス 760x360)、コンソール側の表記(Label)、
// RetroPad側のキー(Key)の対応をここで直せば、設定画面にそのまま反映される。

/// <summary>コントローラー図の部品の形。</summary>
public enum PadShape
{
    Round,
    Pill,
    DPad,
    Stick,
}

/// <summary>
/// コントローラー図の部品1つ。Keyがあればボタン割り当てを変えられる(RetroPadのキー名。
/// "A","B","X","Y","L","R","L2","R2","L3","R3","Start","Select")。Keyがnullなら固定(十字キー、アナログスティック等)。
/// Labelはそのコンソール上でのボタン名(例: メガドライブの「C」)。
/// </summary>
public sealed class PadElement
{
    public PadElement(string? key, string label, PadShape shape, double x, double y, double w, double h, string color)
    {
        Key = key; Label = label; Shape = shape; X = x; Y = y; W = w; H = h; Color = color;
    }

    public string? Key { get; }
    public string Label { get; }
    public PadShape Shape { get; }
    public double X { get; }
    public double Y { get; }
    public double W { get; }
    public double H { get; }
    public string Color { get; }
    public bool IsAssignable => Key is not null;
}

/// <summary>コントローラー図の下地(本体、画面)。</summary>
public sealed class PadBodyPart
{
    public PadBodyPart(double x, double y, double w, double h, double radius, bool isScreen)
    {
        X = x; Y = y; W = w; H = h; Radius = radius; IsScreen = isScreen;
    }

    public double X { get; }
    public double Y { get; }
    public double W { get; }
    public double H { get; }
    public double Radius { get; }
    public bool IsScreen { get; }
}

/// <summary>コンソール系統ごとのコントローラー図。</summary>
public sealed class ControllerLayout
{
    public ControllerLayout(string id, string name, PadBodyPart[] body, PadElement[] elements)
    {
        Id = id; Name = name; Body = body; Elements = elements;
    }

    public const double CanvasWidth = 760;
    public const double CanvasHeight = 360;

    public string Id { get; }
    public string Name { get; }
    public PadBodyPart[] Body { get; }
    public PadElement[] Elements { get; }
}

/// <summary>コンソールID(Consoles.jsonのid)から、そのコンソールのコントローラー図を引く。</summary>
public static class ConsoleControllerLayouts
{
    private static readonly Dictionary<string, ControllerLayout> Layouts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> ConsoleToLayout = new(StringComparer.OrdinalIgnoreCase);

    static ConsoleControllerLayouts()
    {
        Add(new ControllerLayout("famicom", "ファミコン風",
            new[]
            {
                new PadBodyPart(40, 80, 680, 230, 22, false),
            },
            new[]
            {
                new PadElement(null, "", PadShape.DPad, 80, 140, 120, 120, "#7A7A7A"),
                new PadElement("Select", "SELECT", PadShape.Pill, 260, 173, 100, 44, "#3B3B3B"),
                new PadElement("Start", "START", PadShape.Pill, 380, 173, 100, 44, "#3B3B3B"),
                new PadElement("B", "B", PadShape.Round, 540, 180, 68, 68, "#C0392B"),
                new PadElement("A", "A", PadShape.Round, 630, 180, 68, 68, "#C0392B"),
            }));

        Add(new ControllerLayout("snes", "スーパーファミコン風",
            new[]
            {
                new PadBodyPart(40, 90, 680, 240, 70, false),
            },
            new[]
            {
                new PadElement("L", "L", PadShape.Pill, 70, 45, 120, 40, "#3B3B3B"),
                new PadElement("R", "R", PadShape.Pill, 570, 45, 120, 40, "#3B3B3B"),
                new PadElement(null, "", PadShape.DPad, 80, 160, 110, 110, "#7A7A7A"),
                new PadElement("Select", "SELECT", PadShape.Pill, 290, 250, 100, 44, "#3B3B3B"),
                new PadElement("Start", "START", PadShape.Pill, 410, 250, 100, 44, "#3B3B3B"),
                new PadElement("X", "X", PadShape.Round, 568, 130, 64, 64, "#2E6DB4"),
                new PadElement("Y", "Y", PadShape.Round, 520, 178, 64, 64, "#2E8B57"),
                new PadElement("A", "A", PadShape.Round, 616, 178, 64, 64, "#C0392B"),
                new PadElement("B", "B", PadShape.Round, 568, 226, 64, 64, "#C99A12"),
            }));

        Add(new ControllerLayout("md6", "メガドライブ風",
            new[]
            {
                new PadBodyPart(40, 100, 680, 230, 80, false),
            },
            new[]
            {
                new PadElement(null, "", PadShape.DPad, 80, 170, 110, 110, "#7A7A7A"),
                new PadElement("Select", "MODE", PadShape.Pill, 270, 180, 100, 44, "#3B3B3B"),
                new PadElement("Start", "START", PadShape.Pill, 270, 240, 100, 44, "#3B3B3B"),
                new PadElement("L", "X", PadShape.Round, 430, 150, 68, 68, "#3B3B3B"),
                new PadElement("X", "Y", PadShape.Round, 510, 150, 68, 68, "#3B3B3B"),
                new PadElement("R", "Z", PadShape.Round, 590, 150, 68, 68, "#3B3B3B"),
                new PadElement("Y", "A", PadShape.Round, 430, 230, 68, 68, "#3B3B3B"),
                new PadElement("B", "B", PadShape.Round, 510, 230, 68, 68, "#3B3B3B"),
                new PadElement("A", "C", PadShape.Round, 590, 230, 68, 68, "#3B3B3B"),
            }));

        Add(new ControllerLayout("sms", "マスターシステム風",
            new[]
            {
                new PadBodyPart(130, 90, 500, 230, 36, false),
            },
            new[]
            {
                new PadElement(null, "", PadShape.DPad, 170, 160, 110, 110, "#7A7A7A"),
                new PadElement("Start", "PAUSE", PadShape.Pill, 330, 110, 100, 44, "#3B3B3B"),
                new PadElement("B", "1", PadShape.Round, 440, 200, 68, 68, "#3B3B3B"),
                new PadElement("A", "2", PadShape.Round, 530, 200, 68, 68, "#3B3B3B"),
            }));

        Add(new ControllerLayout("gg", "ゲームギア風",
            new[]
            {
                new PadBodyPart(40, 60, 680, 260, 50, false),
                new PadBodyPart(200, 90, 360, 200, 12, true),
            },
            new[]
            {
                new PadElement(null, "", PadShape.DPad, 70, 150, 110, 110, "#7A7A7A"),
                new PadElement("Start", "START", PadShape.Pill, 600, 75, 100, 44, "#3B3B3B"),
                new PadElement("A", "2", PadShape.Round, 620, 130, 68, 68, "#3B3B3B"),
                new PadElement("B", "1", PadShape.Round, 620, 210, 68, 68, "#3B3B3B"),
            }));

        Add(new ControllerLayout("pce", "PCエンジン風",
            new[]
            {
                new PadBodyPart(60, 100, 640, 210, 34, false),
            },
            new[]
            {
                new PadElement(null, "", PadShape.DPad, 100, 160, 110, 110, "#7A7A7A"),
                new PadElement("Select", "SELECT", PadShape.Pill, 300, 225, 100, 44, "#3B3B3B"),
                new PadElement("Start", "RUN", PadShape.Pill, 420, 225, 100, 44, "#3B3B3B"),
                new PadElement("B", "I", PadShape.Round, 530, 190, 68, 68, "#C0392B"),
                new PadElement("A", "II", PadShape.Round, 620, 190, 68, 68, "#C0392B"),
            }));

        // FM TOWNSのパッド(Geartownsコア)。マーティのパッドの割り当て: RetroPadのA="A"、B="B"、Start="RUN"、R="ZOOM"。
        // 形態(controllerProfiles.json)を選んだときは、その形態のボタン名が優先される。
        Add(new ControllerLayout("fmtowns", "FM TOWNSパッド風",
            new[]
            {
                new PadBodyPart(60, 100, 640, 210, 34, false),
            },
            new[]
            {
                new PadElement("R", "ZOOM", PadShape.Pill, 560, 56, 110, 38, "#3B3B3B"),
                new PadElement(null, "", PadShape.DPad, 100, 160, 110, 110, "#7A7A7A"),
                new PadElement("Select", "SELECT", PadShape.Pill, 300, 225, 100, 44, "#3B3B3B"),
                new PadElement("Start", "RUN", PadShape.Pill, 420, 225, 100, 44, "#3B3B3B"),
                new PadElement("A", "A", PadShape.Round, 530, 190, 68, 68, "#C0392B"),
                new PadElement("B", "B", PadShape.Round, 620, 190, 68, 68, "#C0392B"),
            }));

        Add(new ControllerLayout("pcfx", "PC-FX風",
            new[]
            {
                new PadBodyPart(40, 90, 680, 240, 50, false),
            },
            new[]
            {
                new PadElement(null, "", PadShape.DPad, 80, 160, 110, 110, "#7A7A7A"),
                new PadElement("Select", "SELECT", PadShape.Pill, 250, 250, 100, 44, "#3B3B3B"),
                new PadElement("Start", "RUN", PadShape.Pill, 360, 250, 100, 44, "#3B3B3B"),
                new PadElement("B", "I", PadShape.Round, 470, 130, 68, 68, "#3B3B3B"),
                new PadElement("A", "II", PadShape.Round, 550, 130, 68, 68, "#3B3B3B"),
                new PadElement("R", "III", PadShape.Round, 630, 130, 68, 68, "#3B3B3B"),
                new PadElement("Y", "IV", PadShape.Round, 470, 215, 68, 68, "#3B3B3B"),
                new PadElement("X", "V", PadShape.Round, 550, 215, 68, 68, "#3B3B3B"),
                new PadElement("L", "VI", PadShape.Round, 630, 215, 68, 68, "#3B3B3B"),
            }));

        Add(new ControllerLayout("gb", "ゲームボーイ風",
            new[]
            {
                new PadBodyPart(190, 10, 380, 340, 30, false),
                new PadBodyPart(225, 35, 310, 160, 10, true),
            },
            new[]
            {
                new PadElement(null, "", PadShape.DPad, 225, 215, 100, 100, "#7A7A7A"),
                new PadElement("B", "B", PadShape.Round, 390, 225, 68, 68, "#C0392B"),
                new PadElement("A", "A", PadShape.Round, 470, 200, 68, 68, "#C0392B"),
                new PadElement("Select", "SELECT", PadShape.Pill, 335, 302, 90, 40, "#3B3B3B"),
                new PadElement("Start", "START", PadShape.Pill, 435, 302, 90, 40, "#3B3B3B"),
            }));

        Add(new ControllerLayout("gba", "ゲームボーイアドバンス風",
            new[]
            {
                new PadBodyPart(50, 70, 660, 250, 90, false),
                new PadBodyPart(240, 100, 280, 170, 10, true),
            },
            new[]
            {
                new PadElement("L", "L", PadShape.Pill, 70, 30, 110, 38, "#3B3B3B"),
                new PadElement("R", "R", PadShape.Pill, 580, 30, 110, 38, "#3B3B3B"),
                new PadElement(null, "", PadShape.DPad, 80, 150, 110, 110, "#7A7A7A"),
                new PadElement("B", "B", PadShape.Round, 550, 205, 68, 68, "#3B3B3B"),
                new PadElement("A", "A", PadShape.Round, 625, 170, 68, 68, "#3B3B3B"),
                new PadElement("Select", "SELECT", PadShape.Pill, 530, 285, 90, 36, "#3B3B3B"),
                new PadElement("Start", "START", PadShape.Pill, 625, 285, 90, 36, "#3B3B3B"),
            }));

        Add(new ControllerLayout("ngp", "ネオジオポケット風",
            new[]
            {
                new PadBodyPart(200, 20, 360, 330, 40, false),
                new PadBodyPart(230, 50, 300, 140, 10, true),
            },
            new[]
            {
                new PadElement(null, "", PadShape.Stick, 235, 225, 90, 90, "#7A7A7A"),
                new PadElement("Start", "OPTION", PadShape.Pill, 340, 205, 100, 40, "#3B3B3B"),
                new PadElement("B", "B", PadShape.Round, 385, 275, 68, 68, "#3B3B3B"),
                new PadElement("A", "A", PadShape.Round, 465, 245, 68, 68, "#3B3B3B"),
            }));

        Add(new ControllerLayout("ws", "ワンダースワン風",
            new[]
            {
                new PadBodyPart(200, 10, 360, 340, 30, false),
                new PadBodyPart(230, 35, 300, 170, 10, true),
            },
            new[]
            {
                new PadElement(null, "", PadShape.DPad, 225, 225, 100, 100, "#7A7A7A"),
                new PadElement("Start", "START", PadShape.Pill, 340, 220, 90, 36, "#3B3B3B"),
                new PadElement("B", "B", PadShape.Round, 410, 275, 68, 68, "#3B3B3B"),
                new PadElement("A", "A", PadShape.Round, 480, 245, 68, 68, "#3B3B3B"),
            }));

        Add(new ControllerLayout("vb", "バーチャルボーイ風",
            new[]
            {
                new PadBodyPart(40, 100, 680, 220, 60, false),
            },
            new[]
            {
                new PadElement("L", "L", PadShape.Pill, 60, 55, 110, 38, "#3B3B3B"),
                new PadElement("R", "R", PadShape.Pill, 590, 55, 110, 38, "#3B3B3B"),
                new PadElement(null, "", PadShape.DPad, 80, 170, 100, 100, "#7A7A7A"),
                new PadElement(null, "", PadShape.DPad, 580, 170, 100, 100, "#7A7A7A"),
                new PadElement("Select", "SELECT", PadShape.Pill, 270, 130, 90, 36, "#3B3B3B"),
                new PadElement("Start", "START", PadShape.Pill, 400, 130, 90, 36, "#3B3B3B"),
                new PadElement("B", "B", PadShape.Round, 330, 220, 68, 68, "#3B3B3B"),
                new PadElement("A", "A", PadShape.Round, 420, 200, 68, 68, "#3B3B3B"),
            }));

        Add(new ControllerLayout("n64", "NINTENDO64風",
            new[]
            {
                new PadBodyPart(50, 70, 190, 280, 70, false),
                new PadBodyPart(520, 70, 190, 280, 70, false),
                new PadBodyPart(250, 70, 260, 280, 50, false),
            },
            new[]
            {
                new PadElement("L", "L", PadShape.Pill, 70, 25, 130, 38, "#3B3B3B"),
                new PadElement("R", "R", PadShape.Pill, 560, 25, 130, 38, "#3B3B3B"),
                new PadElement(null, "", PadShape.DPad, 85, 120, 100, 100, "#7A7A7A"),
                new PadElement("L2", "Z", PadShape.Pill, 325, 302, 110, 40, "#3B3B3B"),
                new PadElement("Start", "START", PadShape.Pill, 330, 90, 100, 40, "#C0392B"),
                new PadElement(null, "", PadShape.Stick, 330, 150, 100, 100, "#7A7A7A"),
                new PadElement(null, "C", PadShape.Round, 585, 100, 40, 40, "#C99A12"),
                new PadElement(null, "C", PadShape.Round, 540, 140, 40, 40, "#C99A12"),
                new PadElement(null, "C", PadShape.Round, 630, 140, 40, 40, "#C99A12"),
                new PadElement(null, "C", PadShape.Round, 585, 180, 40, 40, "#C99A12"),
                new PadElement("B", "A", PadShape.Round, 545, 235, 68, 68, "#2E6DB4"),
                new PadElement("Y", "B", PadShape.Round, 625, 215, 68, 68, "#2E8B57"),
            }));

        Add(new ControllerLayout("ps", "プレイステーション風",
            new[]
            {
                new PadBodyPart(50, 95, 660, 240, 100, false),
            },
            new[]
            {
                new PadElement("L2", "L2", PadShape.Pill, 70, 18, 110, 38, "#3B3B3B"),
                new PadElement("L", "L1", PadShape.Pill, 70, 58, 110, 38, "#3B3B3B"),
                new PadElement("R2", "R2", PadShape.Pill, 580, 18, 110, 38, "#3B3B3B"),
                new PadElement("R", "R1", PadShape.Pill, 580, 58, 110, 38, "#3B3B3B"),
                new PadElement(null, "", PadShape.DPad, 90, 145, 110, 110, "#7A7A7A"),
                new PadElement("Select", "SELECT", PadShape.Pill, 280, 150, 90, 38, "#3B3B3B"),
                new PadElement("Start", "START", PadShape.Pill, 390, 150, 90, 38, "#3B3B3B"),
                new PadElement("X", "△", PadShape.Round, 558, 137, 64, 64, "#2E8B57"),
                new PadElement("Y", "□", PadShape.Round, 512, 183, 64, 64, "#6B4FA0"),
                new PadElement("A", "○", PadShape.Round, 604, 183, 64, 64, "#C0392B"),
                new PadElement("B", "✕", PadShape.Round, 558, 229, 64, 64, "#2E6DB4"),
                new PadElement("L3", "L3", PadShape.Stick, 250, 225, 80, 80, "#7A7A7A"),
                new PadElement("R3", "R3", PadShape.Stick, 400, 225, 80, 80, "#7A7A7A"),
            }));

        Add(new ControllerLayout("psp", "PSP風",
            new[]
            {
                new PadBodyPart(30, 75, 700, 250, 50, false),
                new PadBodyPart(190, 100, 380, 200, 10, true),
            },
            new[]
            {
                new PadElement("L", "L", PadShape.Pill, 60, 30, 120, 38, "#3B3B3B"),
                new PadElement("R", "R", PadShape.Pill, 580, 30, 120, 38, "#3B3B3B"),
                new PadElement(null, "", PadShape.DPad, 70, 120, 100, 100, "#7A7A7A"),
                new PadElement(null, "", PadShape.Stick, 80, 240, 70, 70, "#7A7A7A"),
                new PadElement("X", "△", PadShape.Round, 610, 115, 60, 60, "#2E8B57"),
                new PadElement("Y", "□", PadShape.Round, 565, 160, 60, 60, "#6B4FA0"),
                new PadElement("A", "○", PadShape.Round, 655, 160, 60, 60, "#C0392B"),
                new PadElement("B", "✕", PadShape.Round, 610, 205, 60, 60, "#2E6DB4"),
                new PadElement("Select", "SELECT", PadShape.Pill, 570, 280, 80, 34, "#3B3B3B"),
                new PadElement("Start", "START", PadShape.Pill, 655, 280, 80, 34, "#3B3B3B"),
            }));

        Add(new ControllerLayout("3ds", "ニンテンドー3DS風",
            new[]
            {
                new PadBodyPart(30, 75, 700, 250, 50, false),
                new PadBodyPart(190, 100, 380, 200, 10, true),
            },
            new[]
            {
                new PadElement("L", "L", PadShape.Pill, 60, 30, 100, 38, "#3B3B3B"),
                new PadElement("L2", "ZL", PadShape.Pill, 170, 30, 100, 38, "#3B3B3B"),
                new PadElement("R2", "ZR", PadShape.Pill, 490, 30, 100, 38, "#3B3B3B"),
                new PadElement("R", "R", PadShape.Pill, 600, 30, 100, 38, "#3B3B3B"),
                new PadElement(null, "", PadShape.Stick, 70, 110, 90, 90, "#7A7A7A"),
                new PadElement(null, "", PadShape.DPad, 75, 215, 90, 90, "#7A7A7A"),
                new PadElement("X", "X", PadShape.Round, 610, 115, 60, 60, "#2E6DB4"),
                new PadElement("Y", "Y", PadShape.Round, 565, 160, 60, 60, "#2E8B57"),
                new PadElement("A", "A", PadShape.Round, 655, 160, 60, 60, "#C0392B"),
                new PadElement("B", "B", PadShape.Round, 610, 205, 60, 60, "#C99A12"),
                new PadElement("Select", "SELECT", PadShape.Pill, 570, 280, 80, 34, "#3B3B3B"),
                new PadElement("Start", "START", PadShape.Pill, 655, 280, 80, 34, "#3B3B3B"),
            }));

        Add(new ControllerLayout("saturn", "セガサターン風",
            new[]
            {
                new PadBodyPart(40, 100, 680, 240, 90, false),
            },
            new[]
            {
                new PadElement("L2", "L", PadShape.Pill, 70, 50, 120, 40, "#3B3B3B"),
                new PadElement("R2", "R", PadShape.Pill, 570, 50, 120, 40, "#3B3B3B"),
                new PadElement(null, "", PadShape.DPad, 80, 170, 110, 110, "#7A7A7A"),
                new PadElement("Start", "START", PadShape.Pill, 300, 250, 100, 44, "#3B3B3B"),
                new PadElement("Y", "X", PadShape.Round, 430, 150, 68, 68, "#3B3B3B"),
                new PadElement("X", "Y", PadShape.Round, 510, 150, 68, 68, "#3B3B3B"),
                new PadElement("L", "Z", PadShape.Round, 590, 150, 68, 68, "#3B3B3B"),
                new PadElement("B", "A", PadShape.Round, 430, 230, 68, 68, "#3B3B3B"),
                new PadElement("A", "B", PadShape.Round, 510, 230, 68, 68, "#3B3B3B"),
                new PadElement("R", "C", PadShape.Round, 590, 230, 68, 68, "#3B3B3B"),
            }));

        Add(new ControllerLayout("dc", "ドリームキャスト風",
            new[]
            {
                new PadBodyPart(40, 75, 680, 270, 100, false),
            },
            new[]
            {
                new PadElement("L2", "L", PadShape.Pill, 70, 25, 120, 40, "#3B3B3B"),
                new PadElement("R2", "R", PadShape.Pill, 570, 25, 120, 40, "#3B3B3B"),
                new PadElement(null, "", PadShape.DPad, 80, 145, 110, 110, "#7A7A7A"),
                new PadElement(null, "", PadShape.Stick, 230, 235, 100, 100, "#7A7A7A"),
                new PadElement("Start", "START", PadShape.Pill, 350, 130, 100, 44, "#3B3B3B"),
                new PadElement("X", "Y", PadShape.Round, 558, 132, 64, 64, "#C99A12"),
                new PadElement("Y", "X", PadShape.Round, 512, 178, 64, 64, "#2E6DB4"),
                new PadElement("A", "B", PadShape.Round, 604, 178, 64, 64, "#C0392B"),
                new PadElement("B", "A", PadShape.Round, 558, 224, 64, 64, "#2E8B57"),
            }));

        Add(new ControllerLayout("gc", "ゲームキューブ風",
            new[]
            {
                new PadBodyPart(40, 75, 680, 270, 110, false),
            },
            new[]
            {
                new PadElement("L2", "L", PadShape.Pill, 70, 25, 120, 40, "#3B3B3B"),
                new PadElement("R2", "R", PadShape.Pill, 570, 25, 120, 40, "#3B3B3B"),
                new PadElement("R", "Z", PadShape.Pill, 440, 30, 100, 40, "#6B4FA0"),
                new PadElement(null, "", PadShape.Stick, 95, 145, 100, 100, "#7A7A7A"),
                new PadElement(null, "", PadShape.DPad, 240, 240, 80, 80, "#7A7A7A"),
                new PadElement(null, "", PadShape.Stick, 410, 240, 80, 80, "#7A7A7A"),
                new PadElement("Start", "START", PadShape.Pill, 320, 130, 100, 44, "#3B3B3B"),
                new PadElement("A", "A", PadShape.Round, 570, 175, 84, 84, "#2E8B57"),
                new PadElement("B", "B", PadShape.Round, 495, 235, 56, 56, "#C0392B"),
                new PadElement("X", "X", PadShape.Round, 665, 180, 52, 52, "#7A7A7A"),
                new PadElement("Y", "Y", PadShape.Round, 585, 110, 52, 52, "#7A7A7A"),
            }));

        Add(new ControllerLayout("arcade", "アーケード風",
            new[]
            {
                new PadBodyPart(40, 90, 680, 250, 16, false),
            },
            new[]
            {
                new PadElement(null, "", PadShape.Stick, 80, 160, 110, 110, "#7A7A7A"),
                new PadElement("Select", "COIN", PadShape.Pill, 270, 125, 100, 40, "#3B3B3B"),
                new PadElement("Start", "START", PadShape.Pill, 390, 125, 100, 40, "#3B3B3B"),
                new PadElement("B", "1", PadShape.Round, 440, 190, 68, 68, "#3B3B3B"),
                new PadElement("A", "2", PadShape.Round, 520, 190, 68, 68, "#3B3B3B"),
                new PadElement("Y", "3", PadShape.Round, 600, 190, 68, 68, "#3B3B3B"),
                new PadElement("X", "4", PadShape.Round, 440, 265, 68, 68, "#3B3B3B"),
                new PadElement("L", "5", PadShape.Round, 520, 265, 68, 68, "#3B3B3B"),
                new PadElement("R", "6", PadShape.Round, 600, 265, 68, 68, "#3B3B3B"),
            }));

        Add(new ControllerLayout("ngcd", "ネオジオCD風",
            new[]
            {
                new PadBodyPart(40, 100, 680, 230, 40, false),
            },
            new[]
            {
                new PadElement(null, "", PadShape.Stick, 80, 170, 110, 110, "#7A7A7A"),
                new PadElement("Select", "SELECT", PadShape.Pill, 280, 135, 100, 40, "#3B3B3B"),
                new PadElement("Start", "START", PadShape.Pill, 400, 135, 100, 40, "#3B3B3B"),
                new PadElement("B", "A", PadShape.Round, 420, 225, 68, 68, "#3B3B3B"),
                new PadElement("A", "B", PadShape.Round, 495, 205, 68, 68, "#3B3B3B"),
                new PadElement("Y", "C", PadShape.Round, 570, 185, 68, 68, "#3B3B3B"),
                new PadElement("X", "D", PadShape.Round, 645, 165, 68, 68, "#3B3B3B"),
            }));

        Add(new ControllerLayout("3do", "3DO風",
            new[]
            {
                new PadBodyPart(40, 100, 680, 230, 60, false),
            },
            new[]
            {
                new PadElement("L", "L", PadShape.Pill, 70, 55, 120, 40, "#3B3B3B"),
                new PadElement("R", "R", PadShape.Pill, 570, 55, 120, 40, "#3B3B3B"),
                new PadElement(null, "", PadShape.DPad, 80, 170, 110, 110, "#7A7A7A"),
                new PadElement("Select", "X", PadShape.Pill, 290, 245, 90, 40, "#3B3B3B"),
                new PadElement("Start", "P", PadShape.Pill, 400, 245, 90, 40, "#3B3B3B"),
                new PadElement("B", "A", PadShape.Round, 500, 190, 68, 68, "#3B3B3B"),
                new PadElement("A", "B", PadShape.Round, 570, 170, 68, 68, "#3B3B3B"),
                new PadElement("Y", "C", PadShape.Round, 640, 150, 68, 68, "#3B3B3B"),
            }));

        Add(new ControllerLayout("retropad", "汎用パッド",
            new[]
            {
                new PadBodyPart(40, 100, 680, 240, 70, false),
            },
            new[]
            {
                new PadElement("L2", "L2", PadShape.Pill, 70, 15, 110, 38, "#3B3B3B"),
                new PadElement("L", "L", PadShape.Pill, 70, 56, 110, 38, "#3B3B3B"),
                new PadElement("R2", "R2", PadShape.Pill, 580, 15, 110, 38, "#3B3B3B"),
                new PadElement("R", "R", PadShape.Pill, 580, 56, 110, 38, "#3B3B3B"),
                new PadElement(null, "", PadShape.DPad, 80, 160, 110, 110, "#7A7A7A"),
                new PadElement("Select", "SELECT", PadShape.Pill, 290, 150, 90, 38, "#3B3B3B"),
                new PadElement("Start", "START", PadShape.Pill, 390, 150, 90, 38, "#3B3B3B"),
                new PadElement("L3", "L3", PadShape.Stick, 250, 235, 80, 80, "#7A7A7A"),
                new PadElement("R3", "R3", PadShape.Stick, 400, 235, 80, 80, "#7A7A7A"),
                new PadElement("X", "X", PadShape.Round, 568, 135, 64, 64, "#2E6DB4"),
                new PadElement("Y", "Y", PadShape.Round, 520, 183, 64, 64, "#2E8B57"),
                new PadElement("A", "A", PadShape.Round, 616, 183, 64, 64, "#C0392B"),
                new PadElement("B", "B", PadShape.Round, 568, 231, 64, 64, "#C99A12"),
            }));

        Add(new ControllerLayout("ds", "ニンテンドーDS風",
            new[]
            {
                new PadBodyPart(40, 90, 680, 240, 70, false),
            },
            new[]
            {
                new PadElement("L", "L", PadShape.Pill, 70, 45, 120, 40, "#3B3B3B"),
                new PadElement("R", "R", PadShape.Pill, 570, 45, 120, 40, "#3B3B3B"),
                new PadElement(null, "", PadShape.DPad, 80, 160, 110, 110, "#7A7A7A"),
                new PadElement("Select", "SELECT", PadShape.Pill, 290, 250, 100, 44, "#3B3B3B"),
                new PadElement("Start", "START", PadShape.Pill, 410, 250, 100, 44, "#3B3B3B"),
                new PadElement("X", "X", PadShape.Round, 568, 130, 64, 64, "#2E6DB4"),
                new PadElement("Y", "Y", PadShape.Round, 520, 178, 64, 64, "#2E8B57"),
                new PadElement("A", "A", PadShape.Round, 616, 178, 64, 64, "#C0392B"),
                new PadElement("B", "B", PadShape.Round, 568, 226, 64, 64, "#C99A12"),
            }));

        ConsoleToLayout["fc"] = "famicom";
        ConsoleToLayout["fds"] = "famicom";
        ConsoleToLayout["x68000"] = "famicom";
        ConsoleToLayout["fmtowns"] = "fmtowns";
        ConsoleToLayout["sfc"] = "snes";
        ConsoleToLayout["nds"] = "ds";
        ConsoleToLayout["n64"] = "n64";
        ConsoleToLayout["gc"] = "gc";
        ConsoleToLayout["wii"] = "retropad";
        ConsoleToLayout["wiiu"] = "retropad";
        ConsoleToLayout["xbox"] = "retropad";
        ConsoleToLayout["gb"] = "gb";
        ConsoleToLayout["gbc"] = "gb";
        ConsoleToLayout["pokemini"] = "gb";
        ConsoleToLayout["gba"] = "gba";
        ConsoleToLayout["vb"] = "vb";
        ConsoleToLayout["3ds"] = "3ds";
        ConsoleToLayout["sg1000"] = "sms";
        ConsoleToLayout["msx"] = "sms";
        ConsoleToLayout["msx2"] = "sms";
        ConsoleToLayout["mark3"] = "sms";
        ConsoleToLayout["gg"] = "gg";
        ConsoleToLayout["md"] = "md6";
        ConsoleToLayout["megacd"] = "md6";
        ConsoleToLayout["32x"] = "md6";
        ConsoleToLayout["stn"] = "saturn";
        ConsoleToLayout["dc"] = "dc";
        ConsoleToLayout["model3"] = "arcade";
        ConsoleToLayout["fbneo"] = "arcade";
        ConsoleToLayout["mame2003"] = "arcade";
        ConsoleToLayout["mame"] = "arcade";
        ConsoleToLayout["pce"] = "pce";
        ConsoleToLayout["pcecd"] = "pce";
        ConsoleToLayout["sgx"] = "pce";
        ConsoleToLayout["pcfx"] = "pcfx";
        ConsoleToLayout["ps1"] = "ps";
        ConsoleToLayout["ps2"] = "ps";
        ConsoleToLayout["ps3"] = "ps";
        ConsoleToLayout["psp"] = "psp";
        ConsoleToLayout["ngcd"] = "ngcd";
        ConsoleToLayout["neogeo"] = "ngcd";
        ConsoleToLayout["ngp"] = "ngp";
        ConsoleToLayout["ngpc"] = "ngp";
        ConsoleToLayout["ws"] = "ws";
        ConsoleToLayout["wsc"] = "ws";
        ConsoleToLayout["3do"] = "3do";
    }

    private static void Add(ControllerLayout layout) => Layouts[layout.Id] = layout;

    private static readonly Dictionary<string, ControllerLayout> FormLayouts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 形態("wii@nunchuk"のようなスコープID)のコントローラー図。汎用パッドの形をもとに、
    /// その形態で使うボタンだけを、形態でのボタン名(controllerProfiles.json)で並べる。
    /// </summary>
    /// <summary>
    /// Wiiリモコン単体(横持ち)と、Wiiリモコン+ヌンチャク(左にヌンチャク、右に縦のリモコン)の専用の図。
    /// ボタンの名前は形態の定義(Buttons)から取り、キーが定義に無いボタンは描かない。
    /// </summary>
    private static ControllerLayout? BuildWiiLayout(string scopeId, ControllerForm form)
    {
        var elements = new List<PadElement>();
        void Btn(string key, PadShape shape, double x, double y, double w, double h, string color)
        {
            if (form.Buttons.TryGetValue(key, out string? label)) elements.Add(new PadElement(key, label, shape, x, y, w, h, color));
        }
        void Fixed(PadShape shape, double x, double y, double w, double h, string color, string label = "")
            => elements.Add(new PadElement(null, label, shape, x, y, w, h, color));

        const string Dark = "#3B3B3B";
        const string Gray = "#7A7A7A";
        const string Blue = "#2E6DB4";

        if (string.Equals(form.Id, "wiimote", StringComparison.OrdinalIgnoreCase))
        {
            // 横持ち: 左に十字キー、中央にA、その右に−/HOME/+、右端に1と2、本体の下にBトリガー。
            Btn("R2", PadShape.Pill, 270, 30, 200, 38, Dark);        // リモコンを振る
            Fixed(PadShape.DPad, 80, 135, 100, 100, Gray);
            Btn("A", PadShape.Round, 250, 147, 76, 76, Blue);
            Btn("Select", PadShape.Pill, 350, 172, 64, 30, Gray);    // −
            Fixed(PadShape.Round, 428, 169, 36, 36, Gray);           // HOME
            Btn("Start", PadShape.Pill, 478, 172, 64, 30, Gray);     // +
            Btn("X", PadShape.Round, 575, 160, 48, 48, Dark);        // 1
            Btn("Y", PadShape.Round, 640, 160, 48, 48, Dark);        // 2
            Btn("B", PadShape.Pill, 270, 285, 130, 36, Dark);        // Bトリガー(本体の裏側)
            return new ControllerLayout(scopeId, form.Label,
                new[] { new PadBodyPart(40, 100, 680, 170, 40, false) }, elements.ToArray());
        }

        if (string.Equals(form.Id, "nunchuk", StringComparison.OrdinalIgnoreCase))
        {
            // 左: ヌンチャク(上からC、スティック、Z。枠の中で左右中央)  右: 横持ちのリモコン(単体の図と同じ向き)
            Btn("L2", PadShape.Pill, 20, 12, 180, 38, Dark);         // ヌンチャクを振る
            Btn("X", PadShape.Round, 83, 88, 54, 54, Dark);          // C
            Fixed(PadShape.Stick, 55, 156, 110, 110, Gray);
            Btn("Y", PadShape.Pill, 55, 288, 110, 36, Dark);         // Z

            Btn("R2", PadShape.Pill, 330, 70, 220, 38, Dark);        // リモコンを振る
            Fixed(PadShape.DPad, 255, 160, 90, 90, Gray);
            Btn("A", PadShape.Round, 365, 167, 76, 76, Blue);
            Btn("L", PadShape.Round, 466, 185, 40, 40, Gray);        // −
            Fixed(PadShape.Round, 524, 189, 32, 32, Gray);           // HOME
            Btn("R", PadShape.Round, 574, 185, 40, 40, Gray);        // +
            Btn("Start", PadShape.Round, 642, 183, 44, 44, Dark);    // 1
            Btn("Select", PadShape.Round, 694, 183, 44, 44, Dark);   // 2
            Btn("B", PadShape.Pill, 345, 292, 130, 36, Dark);        // Bトリガー(本体の裏側)
            return new ControllerLayout(scopeId, form.Label,
                new[]
                {
                    new PadBodyPart(20, 66, 180, 280, 80, false),
                    new PadBodyPart(235, 135, 510, 140, 40, false),
                }, elements.ToArray());
        }

        return null;
    }

    private static ControllerLayout? GetFormLayout(string scopeId)
    {
        if (FormLayouts.TryGetValue(scopeId, out ControllerLayout? cached)) return cached;
        ControllerForm? form = ControllerProfiles.FindForm(scopeId);
        if (form is null) return null;

        // ファミコン系は、専用のファミコン風の図をそのまま使う(汎用パッドの配置だとA/Bの並びが実機と違うため)
        // (X68000も同じ図を使うが、X/Yなどボタンが多いので対象外。ファミコンとディスクシステムだけ)
        string baseConsole = ControllerProfiles.BaseConsoleId(scopeId);
        if ((string.Equals(baseConsole, "fc", StringComparison.OrdinalIgnoreCase) || string.Equals(baseConsole, "fds", StringComparison.OrdinalIgnoreCase))
            && Layouts.TryGetValue("famicom", out ControllerLayout? famicom))
        {
            return famicom;
        }

        // Wiiリモコン/ヌンチャクは専用の図(クラシックコントローラは汎用パッドのまま)
        if (string.Equals(baseConsole, "wii", StringComparison.OrdinalIgnoreCase)
            && BuildWiiLayout(scopeId, form) is { } wii)
        {
            FormLayouts[scopeId] = wii;
            return wii;
        }

        ControllerLayout pad = Layouts["retropad"];
        var elements = new List<PadElement>();
        foreach (PadElement e in pad.Elements)
        {
            if (e.Key is null)
            {
                elements.Add(e);   // 十字キー等(割り当て対象外)
            }
            else if (form.Buttons.TryGetValue(e.Key, out string? label))
            {
                elements.Add(new PadElement(e.Key, label, e.Shape, e.X, e.Y, e.W, e.H, e.Color));
            }
        }

        // N64は、汎用パッドの図にジョイスティックと、右にCボタン4つを足す(どちらも割り当ての対象外=キーなし。
        // Cボタンは右スティックの4方向で、図の中の位置から上下左右に対応づける)。
        if (string.Equals(baseConsole, "n64", StringComparison.OrdinalIgnoreCase))
        {
            elements.Add(new PadElement(null, "", PadShape.Stick, 250, 180, 84, 84, "#7A7A7A"));
            elements.Add(new PadElement(null, "C", PadShape.Round, 632, 142, 36, 36, "#C99A12"));
            elements.Add(new PadElement(null, "C", PadShape.Round, 594, 180, 36, 36, "#C99A12"));
            elements.Add(new PadElement(null, "C", PadShape.Round, 670, 180, 36, 36, "#C99A12"));
            elements.Add(new PadElement(null, "C", PadShape.Round, 632, 218, 36, 36, "#C99A12"));
        }

        // ゲームキューブは、中央にコントロールスティック(灰)とCスティック(黄、「C」)の2つを足す。
        // Cスティックは右スティックの4方向の割り当て(RSUp等)に対応する。
        if (string.Equals(baseConsole, "gc", StringComparison.OrdinalIgnoreCase))
        {
            elements.Add(new PadElement(null, "", PadShape.Stick, 250, 235, 80, 80, "#7A7A7A"));
            elements.Add(new PadElement(null, "C", PadShape.Stick, 400, 235, 80, 80, "#C99A12"));
        }

        var layout = new ControllerLayout(scopeId, form.Label, pad.Body, elements.ToArray());
        FormLayouts[scopeId] = layout;
        return layout;
    }

    private static readonly Dictionary<string, ControllerLayout> ReferenceLayouts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>実機のボタンではない操作(ソフト側で用意した割り当て先)か。参照図には出さない。</summary>
    public static bool IsVirtualAction(string label)
    {
        if (string.IsNullOrEmpty(label)) return false;
        string[] words = { "振る", "ふる", "息", "マイク", "タッチ", "蓋", "同時", "画面回転", "画面の外", "ペダル" };
        foreach (string w in words)
        {
            if (label.Contains(w, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>
    /// 設定画面の「参照」用のコントローラー図。実機のボタンの種類と配置が分かればよいので、
    /// コンソール専用の図があればそれを使い、その形態に無いボタン(例: 3ボタンパッドのX/Y/Z)は描かない。
    /// 専用の図で足りないボタンがある形態は、<see cref="Get"/>の図(汎用パッドをもとにした図)のまま。
    /// ボタンの割り当て先の一覧(<see cref="Get"/>)とは別にしている(一覧の名前は形態の定義のまま使うため)。
    /// </summary>
    public static ControllerLayout GetReference(string scopeId)
    {
        ControllerLayout baseLayout = Get(scopeId);
        if (!ControllerProfiles.IsFormScope(scopeId)) return baseLayout;
        if (ReferenceLayouts.TryGetValue(scopeId, out ControllerLayout? cached)) return cached;

        ControllerForm? form = ControllerProfiles.FindForm(scopeId);
        string baseConsole = ControllerProfiles.BaseConsoleId(scopeId);

        // ファミコン、N64、ゲームキューブ、Wiiは、Getの図(個別に調整済み)をそのまま使う
        string[] custom = { "fc", "fds", "n64", "gc", "wii" };
        if (form is null || Array.Exists(custom, c => string.Equals(c, baseConsole, StringComparison.OrdinalIgnoreCase))
            || !ConsoleToLayout.TryGetValue(baseConsole, out string? layoutId)
            || layoutId == "retropad"
            || !Layouts.TryGetValue(layoutId, out ControllerLayout? dedicated))
        {
            ReferenceLayouts[scopeId] = baseLayout;
            return baseLayout;
        }

        // 専用の図に、この形態のボタンが全部あるか(実機に無い操作は、無くてよい)
        foreach (KeyValuePair<string, string> kv in form.Buttons)
        {
            if (ControllerMappingService.IsDirectionKey(kv.Key) || IsVirtualAction(kv.Value)) continue;
            if (!dedicated.Elements.Any(e => e.Key == kv.Key))
            {
                ReferenceLayouts[scopeId] = baseLayout;
                return baseLayout;
            }
        }

        var elements = new List<PadElement>();
        foreach (PadElement e in dedicated.Elements)
        {
            if (e.Key is null)
            {
                elements.Add(e);   // 十字キー、スティック等
            }
            else if (form.Buttons.TryGetValue(e.Key, out string? label))
            {
                // 図には実機の表記(専用の図の名前)を出す。実機に無い操作は、判定のため形態の名前のまま。
                string text = IsVirtualAction(label) || string.IsNullOrEmpty(e.Label) ? label : e.Label;
                elements.Add(new PadElement(e.Key, text, e.Shape, e.X, e.Y, e.W, e.H, e.Color));
            }
        }

        var layout = new ControllerLayout(scopeId, dedicated.Name, dedicated.Body, elements.ToArray());
        ReferenceLayouts[scopeId] = layout;
        return layout;
    }

    /// <summary>指定コンソールのコントローラー図。未知のコンソールは汎用パッド。形態のスコープID("wii@nunchuk")も受け付ける。</summary>
    public static ControllerLayout Get(string consoleId)
    {
        if (ControllerProfiles.IsFormScope(consoleId))
        {
            ControllerLayout? formLayout = GetFormLayout(consoleId);
            if (formLayout is not null) return formLayout;
            consoleId = ControllerProfiles.BaseConsoleId(consoleId);
        }

        if (!string.IsNullOrEmpty(consoleId)
            && ConsoleToLayout.TryGetValue(consoleId, out string? layoutId)
            && Layouts.TryGetValue(layoutId, out ControllerLayout? layout))
        {
            return layout;
        }

        return Layouts["retropad"];
    }
}
