namespace Beutl.Extensions.Danmaku.Comments;

/// <summary>Reads and writes comment files.</summary>
public static class CommentFile
{
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="System.Xml.XmlException">The XML is malformed.</exception>
    /// <exception cref="System.Text.Json.JsonException">The JSON is malformed.</exception>
    public static List<DanmakuComment> Read(string path)
    {
        return CommentParser.Parse(TextFileDecoder.Decode(File.ReadAllBytes(path)));
    }

    /// <summary>Writes the comments in the text format, UTF-8 with a BOM so spreadsheet apps detect it.</summary>
    public static void Write(string path, IEnumerable<DanmakuComment> comments)
    {
        File.WriteAllText(path, CommentWriter.ToText(comments), new System.Text.UTF8Encoding(true));
    }
}
