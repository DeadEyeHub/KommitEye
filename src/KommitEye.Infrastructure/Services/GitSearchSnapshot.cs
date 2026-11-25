using System.Globalization;
using System.Text;

namespace KommitEye.Services;

// git grep -n --column -z emits "revision:path NUL line NUL byte-column NUL matching line LF".
// Keep a line prefix and a window around the first match, regardless of the length of the source line.
internal static class GitSearchSnapshot
{
    internal static async Task<bool> CaptureAsync(Stream input, Stream destination, string query, int limit, CancellationToken token)
    {
        const int shortLine = 768, context = 160;
        var buffer = new byte[16_384];
        using var header = new MemoryStream();
        var prefix = new byte[shortLine];
        var window = new byte[2 * context + Encoding.UTF8.GetByteCount(query)];
        var field = 0;
        string file = "", line = "";
        long column = 0, length = 0, windowStart = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            for (var pos = 0; pos < count;)
            {
                var delimiter = field == 3 ? (byte)'\n' : (byte)0;
                var end = Array.IndexOf(buffer, delimiter, pos, count - pos);
                var size = (end < 0 ? count : end) - pos;
                if (field < 3)
                {
                    if (header.Length + size > 8192) throw new GitToolQueryException("Search match path or location exceeds supported length.");
                    header.Write(buffer, pos, size);
                    if (end >= 0)
                    {
                        var text = Encoding.UTF8.GetString(header.GetBuffer(), 0, (int)header.Length);
                        header.SetLength(0);
                        if (field == 0) file = text.Replace("\r", "\\r").Replace("\n", "\\n");
                        else if (field == 1) line = text;
                        else
                        {
                            if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out column) || column < 1)
                                throw new GitException("Git returned an invalid search match column.");
                            windowStart = Math.Max(0, column - 1 - context);
                        }
                        field++;
                    }
                }
                else
                {
                    if (length < shortLine)
                        Array.Copy(buffer, pos, prefix, (int)length, (int)Math.Min(size, shortLine - length));
                    var from = Math.Max(length, windowStart);
                    var until = Math.Min(length + size, windowStart + window.Length);
                    if (from < until)
                        Array.Copy(buffer, pos + (int)(from - length), window, (int)(from - windowStart), (int)(until - from));
                    length += size;
                    if (end >= 0)
                    {
                        string content;
                        if (length <= shortLine) content = Encoding.UTF8.GetString(prefix, 0, (int)length);
                        else
                        {
                            int first = 0, last = (int)Math.Min(length - windowStart, window.Length);
                            if (last <= 0) throw new GitException("Git search match lies outside the returned line.");
                            // Trim partial UTF-8 characters only at clipped window edges.
                            while (windowStart > 0 && first < last && (window[first] & 0xc0) == 0x80) first++;
                            if (windowStart + last < length)
                            {
                                int start = last - 1;
                                while (start > first && (window[start] & 0xc0) == 0x80) start--;
                                if (Rune.DecodeFromUtf8(window.AsSpan(start, last - start), out _, out _) == System.Buffers.OperationStatus.NeedMoreData)
                                    last = start;
                            }
                            content = (windowStart > 0 ? "…" : "") + Encoding.UTF8.GetString(window, first, last - first) +
                                (windowStart + last < length ? "…" : "");
                        }
                        // NUL separates the location on disk even when paths or code contain colons.
                        var row = file + ":" + line + "\0" + content + "\n";
                        if (row.Length > GitToolSession.PageSize)
                            throw new GitToolQueryException("A search location exceeds the maximum page size. Narrow the path.");
                        if (row.Length > limit) return true;
                        await destination.WriteAsync(Encoding.Unicode.GetBytes(row), token);
                        limit -= row.Length;
                        length = 0;
                        field = 0;
                    }
                }
                pos += size + (end < 0 ? 0 : 1);
            }
        }
        if (field != 0 || header.Length > 0) throw new GitException("Incomplete git grep record.");
        return false;
    }
}
