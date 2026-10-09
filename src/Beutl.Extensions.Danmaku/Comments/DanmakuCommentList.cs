using System.Text.Json;
using System.Text.Json.Serialization;

namespace Beutl.Extensions.Danmaku.Comments;

/// <summary>
/// An immutable list of comments, always sorted by time. Edits return a new list, so a property holding one
/// records each edit as a plain value change that undo can restore.
/// </summary>
/// <remarks>
/// Deliberately not <see cref="IEnumerable{T}"/>: Beutl's serializer writes any enumerable value element by
/// element and skips <see cref="DanmakuCommentListJsonConverter"/>, which the reader then cannot parse.
/// Enumerate <see cref="Items"/> instead.
/// </remarks>
[JsonConverter(typeof(DanmakuCommentListJsonConverter))]
public sealed class DanmakuCommentList : IEquatable<DanmakuCommentList>
{
    private readonly DanmakuComment[] _items;

    public DanmakuCommentList(IEnumerable<DanmakuComment> comments)
    {
        // Keep the original order for comments posted at the same time.
        _items = comments.Select((c, i) => (c, i)).OrderBy(p => p.c.Time).ThenBy(p => p.i).Select(p => p.c).ToArray();
        Items = Array.AsReadOnly(_items);
    }

    public static DanmakuCommentList Empty { get; } = new([]);

    public int Count => _items.Length;

    public DanmakuComment this[int index] => _items[index];

    public IReadOnlyList<DanmakuComment> Items { get; }

    public DanmakuCommentList Add(DanmakuComment comment) => new(_items.Append(comment));

    public DanmakuCommentList AddRange(IEnumerable<DanmakuComment> comments) => new(_items.Concat(comments));

    public DanmakuCommentList Replace(int index, DanmakuComment comment)
    {
        var items = (DanmakuComment[])_items.Clone();
        items[index] = comment;
        return new DanmakuCommentList(items);
    }

    public DanmakuCommentList RemoveAt(IEnumerable<int> indices)
    {
        var removed = new HashSet<int>(indices);
        return new DanmakuCommentList(_items.Where((_, i) => !removed.Contains(i)));
    }

    public bool Equals(DanmakuCommentList? other)
    {
        return other != null && (ReferenceEquals(this, other) || _items.AsSpan().SequenceEqual(other._items));
    }

    public override bool Equals(object? obj) => Equals(obj as DanmakuCommentList);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(_items.Length);
        foreach (DanmakuComment item in _items.Take(16))
            hash.Add(item);
        return hash.ToHashCode();
    }

    public override string ToString() => $"{Count} comments";
}

/// <summary>
/// Stores comments as <c>[[seconds, "commands", "text"], …]</c>, the compact form the project file keeps.
/// </summary>
public sealed class DanmakuCommentListJsonConverter : JsonConverter<DanmakuCommentList>
{
    public override DanmakuCommentList Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return DanmakuCommentList.Empty;
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("コメントの一覧は配列で書く必要があります。");

        var comments = new List<DanmakuComment>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
                throw new JsonException("コメントは [秒, コマンド, 本文] の配列で書く必要があります。");

            reader.Read();
            double seconds = reader.GetDouble();
            reader.Read();
            string commands = reader.GetString() ?? "";
            reader.Read();
            string text = reader.GetString() ?? "";
            reader.Read();
            if (reader.TokenType != JsonTokenType.EndArray)
                throw new JsonException("コメントの配列の要素が多すぎます。");

            if (CommentCommands.Apply(TimeSpan.FromSeconds(seconds), text, commands.Split(' ')) is { } comment)
                comments.Add(comment);
        }

        return new DanmakuCommentList(comments);
    }

    public override void Write(Utf8JsonWriter writer, DanmakuCommentList value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (DanmakuComment comment in value.Items)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(Math.Round(comment.Time.TotalSeconds, 3));
            writer.WriteStringValue(CommentCommands.Format(comment));
            writer.WriteStringValue(comment.Text);
            writer.WriteEndArray();
        }

        writer.WriteEndArray();
    }
}
