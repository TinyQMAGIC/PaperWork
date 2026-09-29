using System;
using System.IO;
using System.Text.Json;
using Paperwork.Data;

namespace Paperwork.Lifecycle;

/// <summary>
/// 窗口几何。<b>位置存物理像素、尺寸存 DIP</b>——两者必须分开：
/// 位置是屏幕绝对坐标，跨屏搬运时不该被缩放；尺寸是设计意图，应当随 DPI 缩放。
/// 早期版本把两者都存成物理像素，结果窗口在 125% 屏上被保存一次后
/// 回到 100% 屏就永久变成 648×467（524×1.25），且不会再缩回去。
/// </summary>
internal sealed record PanelBounds(int X, int Y, double WidthDip, double HeightDip);

/// <summary>
/// 窗口位置与尺寸的落盘。M0 只存这一项；M1 会扩成完整的 settings.json，
/// 但原子写的形状现在就定下来，避免以后换存储格式时踩数据丢失。
/// </summary>
internal static class WindowStateStore
{
    private static readonly string StoreDir = AppPaths.DataDir;

    private static readonly string FilePath = Path.Combine(StoreDir, "window.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static PanelBounds? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var saved = JsonSerializer.Deserialize<PanelBounds>(File.ReadAllText(FilePath));
            if (saved is null || saved.WidthDip <= 0 || saved.HeightDip <= 0) return null;
            return saved;
        }
        catch (Exception)
        {
            // 配置损坏不能让程序起不来；读不到就当没有，用默认位置
            return null;
        }
    }

    public static void Save(PanelBounds bounds)
    {
        var tmp = FilePath + ".tmp";
        try
        {
            Directory.CreateDirectory(StoreDir);
            File.WriteAllText(tmp, JsonSerializer.Serialize(bounds, Json));

            if (File.Exists(FilePath))
                File.Replace(tmp, FilePath, FilePath + ".bak");   // 保留上一份，可人工救回
            else
                File.Move(tmp, FilePath);
        }
        catch (Exception)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }
        }
    }
}
