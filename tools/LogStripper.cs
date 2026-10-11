using System;
using System.Collections.Generic;
using System.Text;

// Removes the Log.* calls of the Automata mod from C# source (the no-log deploy). C# 5 only (Add-Type in Windows PowerShell 5.1).
public class LogStripResult
{
    public string Text;
    public int Removed;
    public int Kept;                       // calls left alone (not a plain statement)
    public List<string> Notes = new List<string>();
}

public static class LogStripper
{
    private static readonly string[] Calls =
    {
        "Verbose", "Info", "Debug", "Warning", "Error", "LogPathfinding", "LogDroneNetwork", "LogDroneOrders",
        "IncreaseIndent", "DecreaseIndent", "ResetIndent"
    };

    public static LogStripResult Strip(string src, string file)
    {
        var r = new LogStripResult();
        src = src.Replace("Automata.Util.Logging.Log.", "Log.").Replace("Util.Logging.Log.", "Log.");   // qualified calls
        var sb = new StringBuilder(src.Length);
        char lastSig = '\0';        // last code character copied (comments and blanks don't count)
        string lastWord = null;     // the identifier it ended, if any
        int i = 0, n = src.Length;
        while (i < n)
        {
            char c = src[i];
            if (c == '/' && i + 1 < n && src[i + 1] == '/')
            {
                int e = LineEnd(src, i);
                sb.Append(src, i, e - i);
                i = e;
                continue;
            }
            if (c == '/' && i + 1 < n && src[i + 1] == '*')
            {
                int e = src.IndexOf("*/", i + 2, StringComparison.Ordinal);
                e = e < 0 ? n : e + 2;
                sb.Append(src, i, e - i);
                i = e;
                continue;
            }
            if (c == '#' && OnlyBlanksBefore(src, i))
            {
                int e = LineEnd(src, i);
                sb.Append(src, i, e - i);
                i = e;
                continue;
            }
            int end;
            if (TryString(src, i, out end))
            {
                sb.Append(src, i, end - i);
                i = end;
                lastSig = '"'; lastWord = null;
                continue;
            }
            if (c == '\'')
            {
                end = CharEnd(src, i);
                sb.Append(src, i, end - i);
                i = end;
                lastSig = '\''; lastWord = null;
                continue;
            }
            if (IsIdentStart(c))
            {
                int s = i;
                while (i < n && IsIdent(src[i])) i++;
                string word = src.Substring(s, i - s);
                if (word == "Log" && lastSig != '.' && i < n && src[i] == '.')
                {
                    int ns = i + 1, ne = ns;
                    while (ne < n && IsIdent(src[ne])) ne++;
                    string name = src.Substring(ns, ne - ns);
                    if (name == "DebugEnabled") { sb.Append("false"); i = ne; lastSig = 'e'; lastWord = "false"; continue; }
                    if (name == "LogLevel") { sb.Append("((LogLevel)0)"); i = ne; lastSig = ')'; lastWord = null; continue; }
                    if (Array.IndexOf(Calls, name) >= 0)
                    {
                        int p = ne;
                        while (p < n && char.IsWhiteSpace(src[p])) p++;
                        if (p < n && src[p] == '(')
                        {
                            int close = MatchParen(src, p);
                            int q = close < 0 ? -1 : close + 1;
                            while (q > 0 && q < n && char.IsWhiteSpace(src[q])) q++;
                            bool statement = lastSig == ';' || lastSig == '{' || lastSig == '}' || lastSig == ')' || lastSig == ':'
                                             || lastWord == "else" || lastWord == "do";
                            if (close > 0 && q < n && src[q] == ';' && statement)
                            {
                                CheckSideEffects(src, p + 1, close, file, s, r);
                                sb.Append("{}");
                                for (int k = s; k <= q; k++) if (src[k] == '\n' || src[k] == '\r') sb.Append(src[k]);   // line numbers stay
                                i = q + 1;
                                lastSig = '}'; lastWord = null;
                                r.Removed++;
                                continue;
                            }
                            r.Kept++;
                            r.Notes.Add(file + ":" + LineOf(src, s) + ": Log." + name + " left in (not a plain statement)");
                        }
                    }
                }
                sb.Append(word);
                lastSig = word[word.Length - 1];
                lastWord = word;
                continue;
            }
            sb.Append(c);
            if (!char.IsWhiteSpace(c)) { lastSig = c; lastWord = null; }
            i++;
        }
        r.Text = sb.ToString();
        return r;
    }

    // Arguments that change state (x++, y = 1): removing the call removes that too
    private static void CheckSideEffects(string src, int from, int to, string file, int at, LogStripResult r)
    {
        int i = from;
        while (i < to)
        {
            char c = src[i];
            int end;
            if (c == '/' && i + 1 < to && src[i + 1] == '/') { i = LineEnd(src, i); continue; }
            if (TryString(src, i, out end)) { i = end; continue; }
            if (c == '\'') { i = CharEnd(src, i); continue; }
            if ((c == '+' && src[i + 1] == '+') || (c == '-' && src[i + 1] == '-'))
            {
                r.Notes.Add(file + ":" + LineOf(src, at) + ": removed call has ++/-- in its arguments");
                return;
            }
            if (c == '=')
            {
                char prev = i > from ? src[i - 1] : ' ';
                char next = i + 1 < to ? src[i + 1] : ' ';
                if (next != '=' && next != '>' && prev != '=' && prev != '!' && prev != '<' && prev != '>')
                {
                    r.Notes.Add(file + ":" + LineOf(src, at) + ": removed call has an assignment in its arguments");
                    return;
                }
            }
            i++;
        }
    }

    // Index of the ')' matching the '(' at 'open', or -1
    private static int MatchParen(string src, int open)
    {
        int depth = 0, i = open, n = src.Length;
        while (i < n)
        {
            char c = src[i];
            int end;
            if (c == '/' && i + 1 < n && src[i + 1] == '/') { i = LineEnd(src, i); continue; }
            if (c == '/' && i + 1 < n && src[i + 1] == '*')
            {
                int e = src.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (e < 0) return -1;
                i = e + 2;
                continue;
            }
            if (TryString(src, i, out end)) { i = end; continue; }
            if (c == '\'') { i = CharEnd(src, i); continue; }
            if (c == '(') depth++;
            else if (c == ')') { depth--; if (depth == 0) return i; }
            i++;
        }
        return -1;
    }

    // A string literal starting at i ("..", @"..", $"..", $@".." / @$".."): the index after it
    private static bool TryString(string src, int i, out int end)
    {
        end = i;
        int n = src.Length, j = i;
        bool verbatim = false, interp = false;
        while (j < n && (src[j] == '@' || src[j] == '$'))
        {
            if (src[j] == '@') verbatim = true; else interp = true;
            j++;
            if (j - i > 2) return false;
        }
        if (j >= n || src[j] != '"') return false;
        if (j > i && i > 0 && IsIdent(src[i - 1])) return false;   // part of an identifier
        j++;
        while (j < n)
        {
            char c = src[j];
            if (!verbatim && c == '\\') { j += 2; continue; }
            if (c == '"')
            {
                if (verbatim && j + 1 < n && src[j + 1] == '"') { j += 2; continue; }
                end = j + 1;
                return true;
            }
            if (interp && c == '{')
            {
                if (j + 1 < n && src[j + 1] == '{') { j += 2; continue; }
                j = HoleEnd(src, j + 1);
                continue;
            }
            j++;
        }
        end = n;
        return true;
    }

    // Past the '}' closing an interpolation hole that starts at i
    private static int HoleEnd(string src, int i)
    {
        int depth = 1, n = src.Length;
        while (i < n)
        {
            char c = src[i];
            int end;
            if (TryString(src, i, out end)) { i = end; continue; }
            if (c == '\'') { i = CharEnd(src, i); continue; }
            if (c == '{') depth++;
            else if (c == '}') { depth--; if (depth == 0) return i + 1; }
            i++;
        }
        return n;
    }

    private static int CharEnd(string src, int i)
    {
        int n = src.Length, j = i + 1;
        if (j < n && src[j] == '\\') j += 2; else j++;
        while (j < n && src[j] != '\'' && src[j] != '\n') j++;
        return j < n && src[j] == '\'' ? j + 1 : Math.Min(n, i + 1);
    }

    private static int LineEnd(string src, int i)
    {
        while (i < src.Length && src[i] != '\n') i++;
        return i;
    }

    private static bool OnlyBlanksBefore(string src, int i)
    {
        for (int j = i - 1; j >= 0 && src[j] != '\n'; j--)
            if (src[j] != ' ' && src[j] != '\t' && src[j] != '\r' && src[j] != '\uFEFF') return false;
        return true;
    }

    private static int LineOf(string src, int at)
    {
        int line = 1;
        for (int i = 0; i < at && i < src.Length; i++) if (src[i] == '\n') line++;
        return line;
    }

    private static bool IsIdentStart(char c) { return char.IsLetter(c) || c == '_'; }
    private static bool IsIdent(char c) { return char.IsLetterOrDigit(c) || c == '_'; }
}
