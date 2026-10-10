namespace OnScreenKeyboard;

public enum Lang { English, Arabic }

public enum KeyKind { Normal, Shift, Ctrl, Alt, Win, Caps }

/// <summary>
/// A physical key. Labels are per language; output is sent as a scan code (or VK for
/// navigation keys) so Windows produces the character of the active system layout.
/// </summary>
public sealed class KeyDef
{
    public string En = "", EnShift = "", Ar = "", ArShift = "";
    public ushort Scan, Vk;
    public bool Extended, Letter, Repeat;
    public double Width = 1;
    public KeyKind Kind = KeyKind.Normal;
    public bool IsModifier => Kind != KeyKind.Normal;

    public string Label(Lang lang, bool upper) => lang == Lang.Arabic
        ? (upper ? ArShift : Ar)
        : (upper ? EnShift : En);
}

public static class KeyboardLayouts
{
    private static KeyDef Letter(char en, string ar, string arShift, ushort scan) => new()
    {
        En = en.ToString(), EnShift = char.ToUpperInvariant(en).ToString(),
        Ar = ar, ArShift = arShift, Scan = scan, Letter = true, Repeat = true
    };

    private static KeyDef Sym(string en, string enShift, string ar, string arShift, ushort scan, double w = 1) => new()
    {
        En = en, EnShift = enShift, Ar = ar, ArShift = arShift, Scan = scan, Width = w, Repeat = true
    };

    private static KeyDef Fixed(string label, ushort scan, double w, string? ar = null, bool repeat = false,
        KeyKind kind = KeyKind.Normal) => new()
    {
        En = label, EnShift = label, Ar = ar ?? label, ArShift = ar ?? label,
        Scan = scan, Width = w, Repeat = repeat, Kind = kind
    };

    private static KeyDef Vk(string label, ushort vk, double w, bool repeat = true) => new()
    {
        En = label, EnShift = label, Ar = label, ArShift = label,
        Vk = vk, Extended = true, Width = w, Repeat = repeat
    };

    public static List<KeyDef> FunctionRow()
    {
        var row = new List<KeyDef> { Fixed("Esc", 1, 1.5) };
        ushort[] scans = [59, 60, 61, 62, 63, 64, 65, 66, 67, 68, 87, 88];
        for (int i = 0; i < scans.Length; i++) row.Add(Fixed($"F{i + 1}", scans[i], 1.125));
        return row;
    }

    public static List<KeyDef> NumberRow()
    {
        string[] enShift = ["!", "@", "#", "$", "%", "^", "&", "*", "(", ")"];
        string[] arDigits = ["١", "٢", "٣", "٤", "٥", "٦", "٧", "٨", "٩", "٠"];
        var row = new List<KeyDef> { Sym("`", "~", "ذ", "ّ", 41) };
        for (int i = 0; i < 10; i++)
            row.Add(Sym(((i + 1) % 10).ToString(), enShift[i], arDigits[i], enShift[i], (ushort)(2 + i)));
        row.Add(Sym("-", "_", "-", "_", 12));
        row.Add(Sym("=", "+", "=", "+", 13));
        row.Add(Fixed("⌫", 14, 2, repeat: true));
        return row;
    }

    public static List<KeyDef> QwertyRow() =>
    [
        Fixed("Tab", 15, 1.5, "Tab"),
        Letter('q', "ض", "َ", 16), Letter('w', "ص", "ً", 17), Letter('e', "ث", "ُ", 18),
        Letter('r', "ق", "ٌ", 19), Letter('t', "ف", "لإ", 20), Letter('y', "غ", "إ", 21),
        Letter('u', "ع", "‘", 22), Letter('i', "ه", "÷", 23), Letter('o', "خ", "×", 24),
        Letter('p', "ح", "؛", 25),
        Sym("[", "{", "ج", "<", 26), Sym("]", "}", "د", ">", 27),
        Sym("\\", "|", "\\", "|", 43, 1.5),
    ];

    public static List<KeyDef> HomeRow() =>
    [
        Fixed("Caps", 58, 1.75, "Caps", kind: KeyKind.Caps),
        Letter('a', "ش", "ِ", 30), Letter('s', "س", "ٍ", 31), Letter('d', "ي", "]", 32),
        Letter('f', "ب", "[", 33), Letter('g', "ل", "لأ", 34), Letter('h', "ا", "أ", 35),
        Letter('j', "ت", "ـ", 36), Letter('k', "ن", "،", 37), Letter('l', "م", "/", 38),
        Sym(";", ":", "ك", ":", 39), Sym("'", "\"", "ط", "\"", 40),
        Fixed("Enter ↵", 28, 2.25, "إدخال ↵", repeat: true),
    ];

    public static List<KeyDef> BottomLetterRow() =>
    [
        Fixed("Shift ⇧", 42, 2.25, "Shift ⇧", kind: KeyKind.Shift),
        Letter('z', "ئ", "~", 44), Letter('x', "ء", "ْ", 45), Letter('c', "ؤ", "{", 46),
        Letter('v', "ر", "}", 47), Letter('b', "لا", "لآ", 48), Letter('n', "ى", "آ", 49),
        Letter('m', "ة", "’", 50),
        Sym(",", "<", "و", ",", 51), Sym(".", ">", "ز", ".", 52), Sym("/", "?", "ظ", "؟", 53),
        Fixed("Shift ⇧", 54, 2.75, "Shift ⇧", kind: KeyKind.Shift),
    ];

    public static List<KeyDef> SpaceRow() =>
    [
        Fixed("Ctrl", 29, 1.25, kind: KeyKind.Ctrl),
        new KeyDef { En = "Win", EnShift = "Win", Ar = "Win", ArShift = "Win", Vk = 0x5B, Width = 1.25, Kind = KeyKind.Win },
        Fixed("Alt", 56, 1.25, kind: KeyKind.Alt),
        Fixed("Space", 57, 4.75, "مسافة", repeat: true),
        Fixed("Alt", 56, 1.25, kind: KeyKind.Alt),
        Fixed("Ctrl", 29, 1.25, kind: KeyKind.Ctrl),
        Vk("◄", 0x25, 1), Vk("▲", 0x26, 1), Vk("▼", 0x28, 1), Vk("►", 0x27, 1),
    ];

    public static List<KeyDef> NavRow() =>
    [
        Vk("Ins", 0x2D, 2.5, false), Vk("Del", 0x2E, 2.5), Vk("Home", 0x24, 2.5),
        Vk("End", 0x23, 2.5), Vk("PgUp", 0x21, 2.5), Vk("PgDn", 0x22, 2.5),
    ];
}
