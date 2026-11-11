using System.Collections.Generic;
using System.Text;

// Removes the comments from C# source (the deployed scripts). C# 5 only (Add-Type in Windows PowerShell 5.1).
// Strings (regular, verbatim, interpolated with their holes) and char literals are kept as they are. A line left empty
// by a removed comment goes (line numbers in the game's compile errors no longer match the repository's), and so do the
// blanks a removed comment leaves at a line's end.
public static class CommentStripper
{
    private enum Kind { Code, Hole, Str, Verbatim }

    private sealed class Frame
    {
        public Kind Kind;
        public bool Interpolated;   // Str / Verbatim: $"..." holes in it
        public int Depth;           // Code / Hole: open braces
    }

    public static string Strip(string src)
    {
        var sb = new StringBuilder(src.Length);
        var stack = new Stack<Frame>();
        stack.Push(new Frame { Kind = Kind.Code });
        int i = 0, n = src.Length;
        while (i < n)
        {
            var top = stack.Peek();
            char c = src[i];
            char next = i + 1 < n ? src[i + 1] : '\0';
            if (top.Kind == Kind.Code || top.Kind == Kind.Hole)
            {
                if (c == '/' && next == '/')
                {
                    while (i < n && src[i] != '\r' && src[i] != '\n') i++;
                    TrimBlanks(sb);
                    if (AtLineStart(sb)) i = SkipLineBreak(src, i);   // a comment line: gone
                    continue;
                }
                if (c == '/' && next == '*')
                {
                    int end = src.IndexOf("*/", i + 2, System.StringComparison.Ordinal);
                    end = end < 0 ? n : end + 2;
                    int brk = -1;   // the comment's first line break
                    for (int k = i; k < end && brk < 0; k++) if (src[k] == '\r' || src[k] == '\n') brk = k;
                    int after = end;
                    while (after < n && IsBlank(src[after])) after++;
                    bool lineEnds = after >= n || src[after] == '\r' || src[after] == '\n';
                    if (brk >= 0 || lineEnds) TrimBlanks(sb);
                    if (AtLineStart(sb))
                    {
                        // Nothing before it on its line: nothing of it stays; nothing after it either: its line goes too
                        i = lineEnds ? SkipLineBreak(src, after) : end;
                        continue;
                    }
                    i = end;
                    if (brk >= 0 && !lineEnds)
                        sb.Append(src[brk] == '\r' && brk + 1 < n && src[brk + 1] == '\n' ? "\r\n" : src[brk].ToString());   // code on both sides: one line break
                    else if (!lineEnds && !IsBlank(src[i]) && !IsBlank(sb[sb.Length - 1]))
                        sb.Append(' ');   // inline (a/*x*/b): a blank keeps the tokens apart
                    continue;
                }
                if (c == '\'')
                {
                    // Char literal: up to the closing quote (escapes skipped)
                    sb.Append(c);
                    i++;
                    while (i < n && src[i] != '\'' && src[i] != '\n')
                    {
                        if (src[i] == '\\' && i + 1 < n) { sb.Append(src[i]); i++; }
                        sb.Append(src[i]);
                        i++;
                    }
                    if (i < n && src[i] == '\'') { sb.Append('\''); i++; }
                    continue;
                }
                if (c == '"') { sb.Append(c); i++; stack.Push(new Frame { Kind = Kind.Str }); continue; }
                if ((c == '@' && next == '"'))
                {
                    sb.Append("@\"");
                    i += 2;
                    stack.Push(new Frame { Kind = Kind.Verbatim });
                    continue;
                }
                if (c == '$' && next == '"')
                {
                    sb.Append("$\"");
                    i += 2;
                    stack.Push(new Frame { Kind = Kind.Str, Interpolated = true });
                    continue;
                }
                if ((c == '$' && next == '@' || c == '@' && next == '$') && i + 2 < n && src[i + 2] == '"')
                {
                    sb.Append(src, i, 3);
                    i += 3;
                    stack.Push(new Frame { Kind = Kind.Verbatim, Interpolated = true });
                    continue;
                }
                if (c == '{') top.Depth++;
                else if (c == '}')
                {
                    if (top.Kind == Kind.Hole && top.Depth == 0)
                    {
                        sb.Append(c);
                        i++;
                        stack.Pop();   // back in its string
                        continue;
                    }
                    top.Depth--;
                }
                sb.Append(c);
                i++;
                continue;
            }
            // In a string
            if (top.Interpolated && (c == '{' || c == '}'))
            {
                if (next == c) { sb.Append(c).Append(c); i += 2; continue; }   // {{ / }}: literal
                sb.Append(c);
                i++;
                if (c == '{') stack.Push(new Frame { Kind = Kind.Hole });
                continue;
            }
            if (top.Kind == Kind.Str)
            {
                if (c == '\\' && i + 1 < n) { sb.Append(c).Append(next); i += 2; continue; }
                sb.Append(c);
                i++;
                if (c == '"' || c == '\n') stack.Pop();   // (a line break: an unterminated string, the compiler's to report)
                continue;
            }
            // Verbatim
            if (c == '"' && next == '"') { sb.Append("\"\""); i += 2; continue; }
            sb.Append(c);
            i++;
            if (c == '"') stack.Pop();
        }
        return sb.ToString();
    }

    private static bool IsBlank(char c) { return c == ' ' || c == '\t'; }

    private static bool AtLineStart(StringBuilder sb) { return sb.Length == 0 || sb[sb.Length - 1] == '\n'; }

    // Past the line break at 'i' (\r\n, \n or \r), if there is one
    private static int SkipLineBreak(string src, int i)
    {
        if (i < src.Length && src[i] == '\r') i++;
        if (i < src.Length && src[i] == '\n') i++;
        return i;
    }

    private static void TrimBlanks(StringBuilder sb)
    {
        int len = sb.Length;
        while (len > 0 && IsBlank(sb[len - 1])) len--;
        sb.Length = len;
    }
}
