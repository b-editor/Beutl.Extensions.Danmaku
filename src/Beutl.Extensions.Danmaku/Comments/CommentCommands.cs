using Beutl.Media;

namespace Beutl.Extensions.Danmaku.Comments;

/// <summary>Interprets the space-separated command words of a comment, such as "ue red big".</summary>
public static class CommentCommands
{
    private static readonly Dictionary<string, Color> s_colors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["white"] = Rgb(0xFFFFFF),
        ["red"] = Rgb(0xFF0000),
        ["pink"] = Rgb(0xFF8080),
        ["orange"] = Rgb(0xFFC000),
        ["yellow"] = Rgb(0xFFFF00),
        ["green"] = Rgb(0x00FF00),
        ["cyan"] = Rgb(0x00FFFF),
        ["blue"] = Rgb(0x0000FF),
        ["purple"] = Rgb(0xC000FF),
        ["black"] = Rgb(0x000000),
        ["white2"] = Rgb(0xCCCC99),
        ["niconicowhite"] = Rgb(0xCCCC99),
        ["red2"] = Rgb(0xCC0033),
        ["truered"] = Rgb(0xCC0033),
        ["pink2"] = Rgb(0xFF33CC),
        ["orange2"] = Rgb(0xFF6600),
        ["passionorange"] = Rgb(0xFF6600),
        ["yellow2"] = Rgb(0x999900),
        ["madyellow"] = Rgb(0x999900),
        ["green2"] = Rgb(0x00CC66),
        ["elementalgreen"] = Rgb(0x00CC66),
        ["cyan2"] = Rgb(0x00CCCC),
        ["blue2"] = Rgb(0x3399FF),
        ["marineblue"] = Rgb(0x3399FF),
        ["purple2"] = Rgb(0x6633CC),
        ["nobleviolet"] = Rgb(0x6633CC),
        ["black2"] = Rgb(0x666666),
    };

    /// <summary>The basic colours every niconico user can pick, with their Japanese names, for the colour palette.</summary>
    public static IReadOnlyList<(string Name, string DisplayName, Color Color)> StandardColors { get; } =
    [
        ("white", "白", Rgb(0xFFFFFF)),
        ("red", "赤", Rgb(0xFF0000)),
        ("pink", "ピンク", Rgb(0xFF8080)),
        ("orange", "オレンジ", Rgb(0xFFC000)),
        ("yellow", "黄", Rgb(0xFFFF00)),
        ("green", "緑", Rgb(0x00FF00)),
        ("cyan", "水色", Rgb(0x00FFFF)),
        ("blue", "青", Rgb(0x0000FF)),
        ("purple", "紫", Rgb(0xC000FF)),
        ("black", "黒", Rgb(0x000000)),
    ];

    /// <summary>Applies the commands to a comment. Returns null when a command hides the comment.</summary>
    public static DanmakuComment? Apply(TimeSpan time, string text, IEnumerable<string> commands)
    {
        var position = CommentPosition.Scroll;
        var size = CommentSize.Medium;
        Color? color = null;

        foreach (string command in commands)
        {
            string word = command.Trim();
            switch (word.ToLowerInvariant())
            {
                case "":
                    break;
                case "ue":
                    position = CommentPosition.Top;
                    break;
                case "shita":
                    position = CommentPosition.Bottom;
                    break;
                case "naka":
                    position = CommentPosition.Scroll;
                    break;
                case "big":
                    size = CommentSize.Big;
                    break;
                case "small":
                    size = CommentSize.Small;
                    break;
                case "medium":
                    size = CommentSize.Medium;
                    break;
                case "invisible":
                    return null;
                default:
                    if (s_colors.TryGetValue(word, out Color named))
                        color = named;
                    else if (TryParseHex(word, out Color hex))
                        color = hex;
                    break;
            }
        }

        return new DanmakuComment(time, text, position, size, color);
    }

    /// <summary>The command words that reproduce the comment's position, size and colour.</summary>
    public static string Format(DanmakuComment comment)
    {
        var words = new List<string>(3);
        if (comment.Position == CommentPosition.Top)
            words.Add("ue");
        else if (comment.Position == CommentPosition.Bottom)
            words.Add("shita");

        if (comment.Size == CommentSize.Big)
            words.Add("big");
        else if (comment.Size == CommentSize.Small)
            words.Add("small");

        if (comment.Color is { } color)
            words.Add(FormatColor(color));

        return string.Join(' ', words);
    }

    public static string FormatColor(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>Parses a colour command: a name such as "red" or "#RRGGBB".</summary>
    public static bool TryParseColor(string word, out Color color)
    {
        if (s_colors.TryGetValue(word.Trim(), out color))
            return true;
        return TryParseHex(word.Trim(), out color);
    }

    private static bool TryParseHex(string word, out Color color)
    {
        color = default;
        if (word.Length != 7 || word[0] != '#'
            || !uint.TryParse(word.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out uint rgb))
        {
            return false;
        }

        color = Rgb(rgb);
        return true;
    }

    private static Color Rgb(uint rgb)
    {
        return Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }
}
