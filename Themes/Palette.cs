using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Paperwork.Themes;

/// <summary>一种主题色。名称与 design/ui-mock.html 与 README 的色号表一一对应。</summary>
public sealed record Accent(string Key, string DisplayName, string Hex, string NightHex)
{
    /// <summary>夜空纸下统一提亮到 L≈72%，保证 ≥4.5:1 对比度（README 色号一节）。</summary>
    public string Resolve(bool night) => night ? NightHex : Hex;
}

/// <summary>一种纸张底色及其配套中性色。</summary>
public sealed record Paper(
    string Key, string DisplayName, string Hex,
    string Paper2, string Edge, string Rule,
    string Ink, string Ink2, string Ink3,
    bool Night)
{
    /// <summary>纹理不透明度。牛皮和夜空要更明显一点才看得出纤维。</summary>
    public double GrainOpacity => Night ? 0.09 : Key == "kraft" ? 0.075 : 0.05;
}

public static class Palette
{
    public static readonly IReadOnlyList<Accent> Accents =
    [
        new("forest", "森林绿", "#2F6B4F", "#6FBF97"),
        new("sky",    "天空蓝", "#2E7BB5", "#68B6E8"),
        new("sunset", "夕阳橙", "#D2762E", "#EFA25C")
    ];

    public static readonly IReadOnlyList<Paper> Papers =
    [
        new("ivory", "象牙纸", "#F4EFE3", "#EDE6D6", "#DCCFB4", "#DED6C4",
            "#2B2A26", "#6E6A5F", "#A29C8D", Night: false),
        new("kraft", "牛皮纸", "#E8DCC2", "#DFD0B0", "#C9B48A", "#CDBA97",
            "#33291B", "#6B5A3E", "#9C8862", Night: false),
        new("night", "夜空纸", "#23211D", "#2C2A25", "#3E3A32", "#3C3830",
            "#EDE7DA", "#ABA493", "#6F695D", Night: true)
    ];

    public static Accent FindAccent(string? key) =>
        Accents.FirstOrDefault(a => a.Key == key) ?? Accents[0];

    public static Paper FindPaper(string? key) =>
        Papers.FirstOrDefault(p => p.Key == key) ?? Papers[0];

    /// <summary>把一套 (主题色, 纸张) 组合写进应用级资源字典。磁贴模板用 DynamicResource 绑定这些键，所以换主题不用重建视觉树。</summary>
    public static void Apply(string? accentKey, string? paperKey)
    {
        var accent = FindAccent(accentKey);
        var paper = FindPaper(paperKey);
        var res = Application.Current.Resources;

        string accentHex = accent.Resolve(paper.Night);

        Put(res, "Paper", paper.Hex);
        Put(res, "Paper2", paper.Paper2);
        Put(res, "Edge", paper.Edge);
        Put(res, "Rule", paper.Rule);
        Put(res, "Ink", paper.Ink);
        Put(res, "Ink2", paper.Ink2);
        Put(res, "Ink3", paper.Ink3);
        Put(res, "Accent", accentHex);

        // 淡底与描边一律由主题色加 alpha 派生，不引入新的独立色值（D15）
        Put(res, "AccentSoft", WithAlpha(accentHex, paper.Night ? 0.16 : 0.12));
        Put(res, "AccentLine", WithAlpha(accentHex, paper.Night ? 0.40 : 0.34));
        Put(res, "AccentOn", paper.Night ? paper.Hex : "#F6F2E8");

        // 实心按钮（备忘录 FAB）的悬停色：同一支强调色压暗一档，仍然是从强调色派生。
        // 夜空纸下强调色已经被提亮过，压暗反而会和纸底撞在一起，所以那一档改成提亮。
        Put(res, "AccentStrong", paper.Night ? Lighten(accentHex, 0.14) : Darken(accentHex, 0.14));

        // 危险色固定在暖红区间，三种纸张下都可读
        Put(res, "Danger", paper.Night ? "#E08373" : "#B8402E");
        Put(res, "DangerSoft", WithAlpha(paper.Night ? "#E08373" : "#B8402E", 0.13));

        // 右下角伸缩提示的描边。夜空纸下 Edge(#3E3A32) 对纸底只有 1.4:1，几乎看不见，
        // 所以这一处单独给一个键：浅色纸沿用 Edge 的 80%（和原来的观感一致），夜空纸提到 Ink3 全值（3.0:1）。
        Put(res, "GripLine", paper.Night ? paper.Ink3 : WithAlpha(paper.Edge, 0.8));

        res["GrainOpacity"] = paper.GrainOpacity;
        res["IsNight"] = paper.Night;
    }

    private static void Put(ResourceDictionary res, string key, string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        res[key] = brush;
    }

    /// <summary>把 #RRGGBB 换成带 alpha 的 #AARRGGBB。</summary>
    private static string WithAlpha(string hex, double alpha)
    {
        int a = (int)System.Math.Round(System.Math.Clamp(alpha, 0, 1) * 255);
        return $"#{a:x2}{hex.TrimStart('#')}";
    }

    private static string Shade(string hex, double amount, bool up)
    {
        int v = int.Parse(hex.TrimStart('#'), System.Globalization.NumberStyles.HexNumber);
        int r = (v >> 16) & 0xFF, g = (v >> 8) & 0xFF, b = v & 0xFF;
        double k = System.Math.Clamp(amount, 0, 1);

        // 往暗走是乘 (1-k)，往亮走是往 255 补 k —— 两边不能用同一个式子，
        // 否则提亮时深色会几乎不动（乘一个大于 1 的系数对暗色分量作用很小）。
        int Mix(int c) => up ? (int)System.Math.Round(c + (255 - c) * k)
                             : (int)System.Math.Round(c * (1 - k));

        return $"#{Mix(r):x2}{Mix(g):x2}{Mix(b):x2}";
    }

    /// <summary>压暗（0 = 不变，1 = 全黑）。</summary>
    private static string Darken(string hex, double amount) => Shade(hex, amount, up: false);

    /// <summary>提亮（0 = 不变，1 = 全白）。</summary>
    private static string Lighten(string hex, double amount) => Shade(hex, amount, up: true);
}
