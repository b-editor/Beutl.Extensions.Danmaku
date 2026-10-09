using System.Globalization;
using System.Text.Json;
using System.Xml;

namespace Beutl.Extensions.Danmaku.Comments;

/// <summary>
/// Reads comment files: the legacy XML format (&lt;chat vpos="…" mail="…"&gt;), the JSON format with
/// "body"/"vposMs"/"commands" fields, YouTube's live chat replay (JSON lines written by yt-dlp), and a plain
/// text format of "time, commands, text" lines.
/// </summary>
public static class CommentParser
{
    // Niconico hides comments at or below this shared-NG score with its default ("medium") filter.
    private const int NiconicoHiddenScore = -4800;

    public static List<DanmakuComment> Parse(string content)
    {
        string trimmed = content.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        List<DanmakuComment> comments = trimmed.StartsWith('<') ? ParseXml(trimmed)
            : IsYouTubeLiveChat(trimmed) ? ParseYouTubeLiveChat(trimmed)
            : trimmed.StartsWith('{') || trimmed.StartsWith('[') ? ParseJson(trimmed)
            : ParseText(trimmed);

        // Keep the file order for comments posted at the same time.
        return comments.Select((c, i) => (c, i)).OrderBy(p => p.c.Time).ThenBy(p => p.i).Select(p => p.c).ToList();
    }

    public static List<DanmakuComment> ParseXml(string content)
    {
        var comments = new List<DanmakuComment>();
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
        };

        using var reader = XmlReader.Create(new StringReader(content), settings);
        reader.MoveToContent();
        while (!reader.EOF)
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "chat")
            {
                reader.Read();
                continue;
            }

            string? vpos = reader.GetAttribute("vpos");
            string mail = reader.GetAttribute("mail") ?? "";
            bool deleted = reader.GetAttribute("deleted") is { } d && d != "0";
            // This also moves the reader past the element, so the loop must not call Read() again.
            string text = reader.ReadElementContentAsString();
            if (deleted || vpos == null || !long.TryParse(vpos, NumberStyles.Integer, CultureInfo.InvariantCulture, out long centiseconds))
                continue;

            Add(comments, TimeSpan.FromMilliseconds(centiseconds * 10), text, mail.Split(' '));
        }

        return comments;
    }

    public static List<DanmakuComment> ParseJson(string content)
    {
        var comments = new List<DanmakuComment>();
        using JsonDocument document = JsonDocument.Parse(content, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        Collect(document.RootElement, comments);
        return comments;
    }

    private static void Collect(JsonElement element, List<DanmakuComment> comments)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                    Collect(item, comments);
                break;
            case JsonValueKind.Object:
                if (TryReadJsonComment(element, out TimeSpan time, out string? text, out List<string> commands))
                {
                    Add(comments, time, text!, commands);
                    break;
                }

                foreach (JsonProperty property in element.EnumerateObject())
                    Collect(property.Value, comments);
                break;
        }
    }

    private static bool TryReadJsonComment(JsonElement element, out TimeSpan time, out string? text, out List<string> commands)
    {
        time = default;
        text = null;
        commands = [];

        if (!TryGetString(element, out text, "body", "content", "text"))
            return false;

        if (element.TryGetProperty("vposMs", out JsonElement vposMs) && vposMs.TryGetDouble(out double ms))
            time = TimeSpan.FromMilliseconds(ms);
        else if (element.TryGetProperty("vpos", out JsonElement vpos) && vpos.TryGetDouble(out double cs))
            time = TimeSpan.FromMilliseconds(cs * 10);
        else if (element.TryGetProperty("time", out JsonElement seconds) && seconds.TryGetDouble(out double s))
            time = TimeSpan.FromSeconds(s);
        else
            return false;

        if (element.TryGetProperty("isDeleted", out JsonElement deleted) && deleted.ValueKind == JsonValueKind.True)
        {
            text = null;
            return false;
        }

        if (element.TryGetProperty("score", out JsonElement score) && score.TryGetInt32(out int value)
            && value <= NiconicoHiddenScore)
        {
            text = null;
            return false;
        }

        if (element.TryGetProperty("commands", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement command in list.EnumerateArray())
            {
                if (command.ValueKind == JsonValueKind.String)
                    commands.Add(command.GetString()!);
            }
        }
        else if (TryGetString(element, out string? mail, "mail", "command"))
        {
            commands.AddRange(mail!.Split(' '));
        }

        return true;
    }

    private static bool TryGetString(JsonElement element, out string? value, params string[] names)
    {
        foreach (string name in names)
        {
            if (element.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.String)
            {
                value = property.GetString();
                return true;
            }
        }

        value = null;
        return false;
    }

    private static bool IsYouTubeLiveChat(string content)
    {
        if (!content.StartsWith('{'))
            return false;

        int lineEnd = content.IndexOf('\n');
        ReadOnlySpan<char> firstLine = lineEnd < 0 ? content : content.AsSpan(0, lineEnd);
        return firstLine.Contains("\"replayChatItemAction\"", StringComparison.Ordinal)
               || firstLine.Contains("\"addChatItemAction\"", StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads a YouTube live chat replay: one JSON object per line, each holding chat actions and the
    /// video offset they were posted at. Super Chats show their amount and colour; custom emoji are dropped
    /// because only their images carry meaning.
    /// </summary>
    public static List<DanmakuComment> ParseYouTubeLiveChat(string content)
    {
        var comments = new List<DanmakuComment>();
        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                JsonElement root = document.RootElement;
                JsonElement replay = root.TryGetProperty("replayChatItemAction", out JsonElement r) ? r : root;
                if (!TryGetOffset(replay, out long offsetMs) && !TryGetOffset(root, out offsetMs))
                    continue;
                if (offsetMs < 0 || !replay.TryGetProperty("actions", out JsonElement actions)
                                 || actions.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                TimeSpan time = TimeSpan.FromMilliseconds(offsetMs);
                foreach (JsonElement action in actions.EnumerateArray())
                {
                    if (!action.TryGetProperty("addChatItemAction", out JsonElement add)
                        || !add.TryGetProperty("item", out JsonElement item))
                    {
                        continue;
                    }

                    if (item.TryGetProperty("liveChatTextMessageRenderer", out JsonElement message))
                    {
                        Add(comments, time, ReadRuns(message), []);
                    }
                    else if (item.TryGetProperty("liveChatPaidMessageRenderer", out JsonElement paid))
                    {
                        string amount = paid.TryGetProperty("purchaseAmountText", out JsonElement a)
                                        && a.TryGetProperty("simpleText", out JsonElement simple)
                            ? simple.GetString() ?? ""
                            : "";
                        string text = $"{amount} {ReadRuns(paid)}".Trim();
                        var commands = new List<string>();
                        if (paid.TryGetProperty("bodyBackgroundColor", out JsonElement color) && color.TryGetUInt32(out uint argb))
                            commands.Add($"#{argb & 0xFFFFFF:X6}");
                        Add(comments, time, text, commands);
                    }
                }
            }
        }

        return comments;
    }

    private static bool TryGetOffset(JsonElement element, out long offsetMs)
    {
        offsetMs = 0;
        if (!element.TryGetProperty("videoOffsetTimeMsec", out JsonElement offset))
            return false;

        return offset.ValueKind switch
        {
            JsonValueKind.String => long.TryParse(offset.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out offsetMs),
            JsonValueKind.Number => offset.TryGetInt64(out offsetMs),
            _ => false,
        };
    }

    private static string ReadRuns(JsonElement renderer)
    {
        if (!renderer.TryGetProperty("message", out JsonElement message)
            || !message.TryGetProperty("runs", out JsonElement runs) || runs.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        var sb = new System.Text.StringBuilder();
        foreach (JsonElement run in runs.EnumerateArray())
        {
            if (run.TryGetProperty("text", out JsonElement text))
            {
                sb.Append(text.GetString());
            }
            else if (run.TryGetProperty("emoji", out JsonElement emoji)
                     && !(emoji.TryGetProperty("isCustomEmoji", out JsonElement custom) && custom.ValueKind == JsonValueKind.True)
                     && emoji.TryGetProperty("emojiId", out JsonElement id))
            {
                // A standard emoji's id is the emoji itself.
                sb.Append(id.GetString());
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Reads lines of "time&lt;TAB&gt;commands&lt;TAB&gt;text" (or comma separated). The commands column may be
    /// omitted. Times are seconds ("12.5") or clock times ("1:02.50", "0:01:02.5"). Lines starting with '#' are
    /// skipped, and "\n" in the text is a line break.
    /// </summary>
    public static List<DanmakuComment> ParseText(string content)
    {
        var comments = new List<DanmakuComment>();
        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            char separator = line.Contains('\t') ? '\t' : ',';
            string[] fields = line.Split(separator, 3);
            if (fields.Length < 2 || !TryParseTime(fields[0].Trim(), out TimeSpan time))
                continue;

            string commands = fields.Length == 3 ? fields[1] : "";
            string text = (fields.Length == 3 ? fields[2] : fields[1]).Replace("\\n", "\n");
            Add(comments, time, text, commands.Split(' '));
        }

        return comments;
    }

    public static bool TryParseTime(string value, out TimeSpan time)
    {
        time = default;
        string[] parts = value.Split(':');
        double seconds = 0;
        foreach (string part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || number < 0)
                return false;
            seconds = seconds * 60 + number;
        }

        if (parts.Length > 3)
            return false;

        time = TimeSpan.FromSeconds(seconds);
        return true;
    }

    private static void Add(List<DanmakuComment> comments, TimeSpan time, string text, IEnumerable<string> commands)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (CommentCommands.Apply(time, text, commands) is { } comment)
            comments.Add(comment);
    }
}
