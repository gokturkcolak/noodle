using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Noodle;

/// Noodle's two Claude Code hooks in ~/.claude/settings.json, added and removed by hand-rolled JSON so
/// nothing else in the file changes, key order included. Same rules as ClaudeHooks.swift: adding is a
/// no-op when our command is already under that event, removing takes out exactly our command and drops a
/// group, event, or `hooks` object only if that emptied it.
///
/// One Windows addition: the command holds Noodle.exe's full path, so a Noodle hook pointing at some other
/// copy (moved, reinstalled elsewhere) counts as stale. Connecting replaces it, disconnecting removes it.
sealed partial class ClaudeHooks
{
    /// Claude Code runs hooks through Git Bash on Windows; a quoted forward-slash path works there and in cmd.
    public static string CommandFor(string ev, string? exe = null) =>
        $"\"{(exe ?? Environment.ProcessPath ?? "Noodle.exe").Replace('\\', '/')}\" --event {ev}";

    static (string ev, string arg)[] Ours => [("UserPromptSubmit", "prompt"), ("Stop", "stop")];

    [GeneratedRegex("""noodle\.exe"?\s+--event\s+(prompt|stop)\s*$""", RegexOptions.IgnoreCase)]
    private static partial Regex NoodleCommand();

    /// CLAUDE_SETTINGS overrides the path, like the scripts (handy for testing on a copy).
    public static string DefaultPath =>
        Environment.GetEnvironmentVariable("CLAUDE_SETTINGS") is { Length: > 0 } p ? p
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    public readonly string FilePath;
    readonly string exe;

    public ClaudeHooks(string? path = null, string? exe = null)
    {
        FilePath = path ?? DefaultPath;
        this.exe = exe ?? Environment.ProcessPath ?? "Noodle.exe";
    }

    public sealed class Failure(string message, bool changedOnDisk = false) : Exception(message)
    {
        public bool ChangedOnDisk { get; } = changedOnDisk;
        public static Failure Changed() => new("settings.json changed while this was open. Here's the change again.", true);
    }

    /// One proposed edit: the file as it is now and as it would be.
    public sealed record Plan(string Old, string New, bool Changed)
    {
        /// Unified-diff-style rows ["+", text], ["-", text], [" ", text], with "…" rows between hunks.
        public List<string[]> Diff(int context = 3)
        {
            var a = Lines(Old);
            var b = Lines(New);
            // Longest common subsequence; settings files are small enough for the plain table.
            var lcs = new int[a.Length + 1, b.Length + 1];
            for (int i = a.Length - 1; i >= 0; i--)
                for (int j = b.Length - 1; j >= 0; j--)
                    lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            var rows = new List<(string kind, string text)>();
            int x = 0, y = 0;
            while (x < a.Length || y < b.Length)
            {
                if (x < a.Length && y < b.Length && a[x] == b[y]) { rows.Add((" ", a[x])); x++; y++; }
                // Removals before insertions within a change, like the Swift version prints them.
                else if (x < a.Length && (y == b.Length || lcs[x + 1, y] >= lcs[x, y + 1])) { rows.Add(("-", a[x])); x++; }
                else { rows.Add(("+", b[y])); y++; }
            }
            var near = rows.Select((_, k) =>
                rows.Skip(Math.Max(0, k - context)).Take(Math.Min(rows.Count, k + context + 1) - Math.Max(0, k - context))
                    .Any(r => r.kind != " ")).ToList();
            var output = new List<string[]>();
            for (int k = 0; k < rows.Count; k++)
            {
                if (near[k]) output.Add([rows[k].kind, rows[k].text]);
                else if (output.Count == 0 || output[^1][0] != "…") output.Add(["…", ""]);
            }
            return output;
        }

        static string[] Lines(string s)
        {
            var l = s.Replace("\r\n", "\n").Split('\n').ToList();
            if (l.Count > 0 && l[^1] == "") l.RemoveAt(l.Count - 1);
            return l.ToArray();
        }
    }

    /// True when both hooks are there and point at this copy. Throws when the file can't be read as a JSON object.
    public bool IsConnected() => !MakePlan(connect: true).Changed;

    public Plan MakePlan(bool connect)
    {
        string old = "";
        if (File.Exists(FilePath))
        {
            try { old = File.ReadAllText(FilePath); }
            catch { throw new Failure($"Couldn't read {Tilde(FilePath)}."); }
        }
        Json root;
        if (string.IsNullOrWhiteSpace(old)) root = Json.Object();
        else if (Json.Parse(old) is { Kind: Json.Type.Object } parsed) root = parsed;
        else throw new Failure($"{Tilde(FilePath)} isn't a JSON object, so Noodle left it alone.");

        bool changed = false;
        foreach (var (ev, arg) in Ours)
        {
            var command = CommandFor(arg, exe);
            changed |= connect ? Add(root, ev, command) : Remove(root, ev, c => IsNoodle(c, arg));
        }
        // Keep the file's own line endings, so a CRLF settings.json stays CRLF.
        string nl = old.Contains("\r\n") ? "\r\n" : "\n";
        string next = changed ? root.Pretty(Json.IndentOf(old)).Replace("\n", nl) + nl : old;
        return new Plan(old, next, changed);
    }

    /// Writes `plan`, after checking the file still matches what was previewed. Backs the old file up to
    /// settings.json.bak-<timestamp> (never overwriting a backup). Returns the backup, if there was a file.
    public string? Apply(Plan plan)
    {
        if (!plan.Changed) return null;
        string current = File.Exists(FilePath) ? File.ReadAllText(FilePath) : "";
        if (current != plan.Old) throw Failure.Changed();
        string? backup = null;
        if (File.Exists(FilePath))
        {
            var b = FilePath + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var bak = b;
            for (int n = 1; File.Exists(bak); n++) bak = $"{b}-{n}";
            File.Copy(FilePath, bak);
            backup = bak;
        }
        else Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        // Write through (not replace), so a symlinked settings.json stays a symlink. No BOM, like every other editor of it.
        File.WriteAllText(FilePath, plan.New, new UTF8Encoding(false));
        return backup;
    }

    public static string Tilde(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? "~" + path[home.Length..].Replace('\\', '/') : path;
    }

    // MARK: The merge rules

    static bool IsNoodle(string? command, string arg) =>
        command != null && NoodleCommand().Match(command) is { Success: true } m && m.Groups[1].Value == arg;

    static bool Runs(Json group, Func<string?, bool> match) =>
        group["hooks"] is { Kind: Json.Type.Array } hs && hs.Items.Any(h => match(h["command"]?.Str));

    /// add($ev; $cmd), after clearing out Noodle commands for other copies. Returns whether it changed anything.
    bool Add(Json root, string ev, string command)
    {
        string arg = command.EndsWith("prompt") ? "prompt" : "stop";
        bool changed = Remove(root, ev, c => c != command && IsNoodle(c, arg));
        var hooks = root["hooks"];
        if (hooks != null && hooks.Kind != Json.Type.Object && hooks.Kind != Json.Type.Null) throw Shape("hooks");
        var groups = hooks?[ev];
        if (groups != null && groups.Kind != Json.Type.Array && groups.Kind != Json.Type.Null) throw Shape($"hooks.{ev}");
        if (groups is { Kind: Json.Type.Array } && groups.Items.Any(g => Runs(g, c => c == command))) return changed;
        var ours = Json.Object(("hooks", Json.Array(Json.Object(("type", Json.String("command")), ("command", Json.String(command))))));
        if (hooks == null || hooks.Kind == Json.Type.Null) { hooks = Json.Object(); root["hooks"] = hooks; }
        if (groups is { Kind: Json.Type.Array }) groups.Items.Add(ours);
        else hooks[ev] = Json.Array(ours);
        return true;
    }

    /// rm($ev; $cmd) for every command `match` picks. Returns whether it changed anything.
    static bool Remove(Json root, string ev, Func<string?, bool> match)
    {
        if (root["hooks"] is not { Kind: Json.Type.Object } h || h[ev] is not { Kind: Json.Type.Array } gs
            || !gs.Items.Any(g => Runs(g, match))) return false;
        var kept = new List<Json>();
        foreach (var g in gs.Items)
        {
            if (!Runs(g, match)) { kept.Add(g); continue; }
            var rest = g["hooks"]!.Items.Where(x => !match(x["command"]?.Str)).ToList();
            if (rest.Count == 0) continue;
            g["hooks"] = Json.Array([.. rest]);
            kept.Add(g);
        }
        if (kept.Count == 0) h[ev] = null; else h[ev] = Json.Array([.. kept]);
        if (h.Pairs.Count == 0) root["hooks"] = null;
        return true;
    }

    static Failure Shape(string key) => new($"settings.json has a \"{key}\" Noodle doesn't recognize, so it left the file alone.");
}

/// Just enough JSON to edit settings.json without reshuffling it: objects keep their key order, and
/// numbers and strings keep their original spelling. Prints the way `jq .` does.
sealed class Json
{
    public enum Type { Object, Array, String, Number, Bool, Null }

    public Type Kind { get; private init; }
    public List<(string key, Json value)> Pairs { get; private init; } = [];
    public List<Json> Items { get; private init; } = [];
    public string? Str { get; private init; }
    string? raw;    // a string's original spelling, or a number's text
    bool flag;

    public static Json Object(params (string, Json)[] pairs) => new() { Kind = Type.Object, Pairs = [.. pairs] };
    public static Json Array(params Json[] items) => new() { Kind = Type.Array, Items = [.. items] };
    public static Json String(string s, string? raw = null) => new() { Kind = Type.String, Str = s, raw = raw };
    static Json Number(string n) => new() { Kind = Type.Number, raw = n };
    static Json Bool(bool b) => new() { Kind = Type.Bool, flag = b };
    static Json Null() => new() { Kind = Type.Null };

    /// Object member, or null. Setting null deletes; setting a new key appends it, like jq.
    public Json? this[string key]
    {
        get => Kind == Type.Object ? Pairs.FirstOrDefault(p => p.key == key).value : null;
        set
        {
            if (Kind != Type.Object) return;
            int i = Pairs.FindIndex(p => p.key == key);
            if (i >= 0 && value != null) Pairs[i] = (key, value);
            else if (i >= 0) Pairs.RemoveAt(i);
            else if (value != null) Pairs.Add((key, value));
        }
    }

    // MARK: Printing

    public string Pretty(string indent = "  ")
    {
        var sb = new StringBuilder();
        Write(sb, indent, 0);
        return sb.ToString();
    }

    void Write(StringBuilder o, string indent, int depth)
    {
        string pad = string.Concat(Enumerable.Repeat(indent, depth + 1)), end = string.Concat(Enumerable.Repeat(indent, depth));
        switch (Kind)
        {
            case Type.Object:
                if (Pairs.Count == 0) { o.Append("{}"); return; }
                o.Append("{\n");
                for (int k = 0; k < Pairs.Count; k++)
                {
                    o.Append(pad).Append(Quote(Pairs[k].key)).Append(": ");
                    Pairs[k].value.Write(o, indent, depth + 1);
                    o.Append(k < Pairs.Count - 1 ? ",\n" : "\n");
                }
                o.Append(end).Append('}');
                break;
            case Type.Array:
                if (Items.Count == 0) { o.Append("[]"); return; }
                o.Append("[\n");
                for (int k = 0; k < Items.Count; k++)
                {
                    o.Append(pad);
                    Items[k].Write(o, indent, depth + 1);
                    o.Append(k < Items.Count - 1 ? ",\n" : "\n");
                }
                o.Append(end).Append(']');
                break;
            case Type.String: o.Append(raw ?? Quote(Str!)); break;
            case Type.Number: o.Append(raw); break;
            case Type.Bool: o.Append(flag ? "true" : "false"); break;
            default: o.Append("null"); break;
        }
    }

    public static string Quote(string s)
    {
        var o = new StringBuilder("\"");
        foreach (var r in s.EnumerateRunes())
        {
            switch (r.Value)
            {
                case '"': o.Append("\\\""); break;
                case '\\': o.Append("\\\\"); break;
                case '\n': o.Append("\\n"); break;
                case '\t': o.Append("\\t"); break;
                case '\r': o.Append("\\r"); break;
                case 0x08: o.Append("\\b"); break;
                case 0x0C: o.Append("\\f"); break;
                default:
                    if (r.Value < 0x20 || r.Value == 0x7F) o.Append($"\\u{r.Value:x4}");
                    else o.Append(r.ToString());
                    break;
            }
        }
        return o.Append('"').ToString();
    }

    /// The file's own indent (first indented line), so a 4-space or tab file stays that way. jq's 2 otherwise.
    public static string IndentOf(string text)
    {
        foreach (var line in text.Replace("\r\n", "\n").Split('\n').Skip(1))
        {
            var lead = new string(line.TakeWhile(c => c == ' ' || c == '\t').ToArray());
            if (lead.Length > 0) return lead.All(c => c == '\t') ? "\t" : lead;
        }
        return "  ";
    }

    // MARK: Parsing

    public static Json? Parse(string text)
    {
        var p = new Parser(Encoding.UTF8.GetBytes(text));
        var v = p.Value();
        if (v == null) return null;
        p.SkipSpace();
        return p.I == p.Bytes.Length ? v : null;
    }

    sealed class Parser(byte[] bytes)
    {
        public readonly byte[] Bytes = bytes;
        public int I;
        int depth;

        public void SkipSpace()
        {
            while (I < Bytes.Length && Bytes[I] is 0x20 or 0x09 or 0x0A or 0x0D) I++;
        }

        public Json? Value()
        {
            SkipSpace();
            if (I >= Bytes.Length || depth >= 512) return null;
            switch ((char)Bytes[I])
            {
                case '{': return ObjectValue();
                case '[': return ArrayValue();
                case '"':
                    int start = I;
                    var s = StringValue();
                    return s == null ? null : String(s, Encoding.UTF8.GetString(Bytes, start, I - start));
                case 't': return Word("true", Bool(true));
                case 'f': return Word("false", Bool(false));
                case 'n': return Word("null", Null());
                default: return NumberValue();
            }
        }

        Json? Word(string w, Json v)
        {
            if (I + w.Length > Bytes.Length || Encoding.ASCII.GetString(Bytes, I, w.Length) != w) return null;
            I += w.Length;
            return v;
        }

        bool Digits()
        {
            int s = I;
            while (I < Bytes.Length && Bytes[I] is >= 0x30 and <= 0x39) I++;
            return I > s;
        }

        Json? NumberValue()
        {
            int start = I;
            if (I < Bytes.Length && Bytes[I] == '-') I++;
            if (!Digits()) return null;
            if (I < Bytes.Length && Bytes[I] == '.') { I++; if (!Digits()) return null; }
            if (I < Bytes.Length && (Bytes[I] | 0x20) == 'e')
            {
                I++;
                if (I < Bytes.Length && Bytes[I] is (byte)'+' or (byte)'-') I++;
                if (!Digits()) return null;
            }
            return Number(Encoding.ASCII.GetString(Bytes, start, I - start));
        }

        string? StringValue()
        {
            I++;   // opening quote
            var o = new StringBuilder();
            int run = I;   // start of the current unescaped stretch
            while (I < Bytes.Length)
            {
                byte c = Bytes[I];
                if (c < 0x20) return null;
                if (c != '"' && c != '\\') { I++; continue; }
                o.Append(Encoding.UTF8.GetString(Bytes, run, I - run));
                I++;
                if (c == '"') return o.ToString();
                if (I >= Bytes.Length) return null;
                byte e = Bytes[I++];
                switch ((char)e)
                {
                    case 'u':
                        if (I + 4 > Bytes.Length || !ushort.TryParse(Encoding.ASCII.GetString(Bytes, I, 4),
                                NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var u)) return null;
                        o.Append((char)u);   // surrogate halves join up in the UTF-16 string
                        I += 4;
                        break;
                    case '"': o.Append('"'); break;
                    case '\\': o.Append('\\'); break;
                    case '/': o.Append('/'); break;
                    case 'b': o.Append('\b'); break;
                    case 'f': o.Append('\f'); break;
                    case 'n': o.Append('\n'); break;
                    case 'r': o.Append('\r'); break;
                    case 't': o.Append('\t'); break;
                    default: return null;
                }
                run = I;
            }
            return null;
        }

        Json? ArrayValue()
        {
            I++; depth++;
            try
            {
                var items = new List<Json>();
                SkipSpace();
                if (I < Bytes.Length && Bytes[I] == ']') { I++; return Array([.. items]); }
                while (true)
                {
                    var v = Value();
                    if (v == null) return null;
                    items.Add(v);
                    SkipSpace();
                    if (I >= Bytes.Length) return null;
                    if (Bytes[I] == ',') { I++; continue; }
                    if (Bytes[I] == ']') { I++; return Array([.. items]); }
                    return null;
                }
            }
            finally { depth--; }
        }

        Json? ObjectValue()
        {
            I++; depth++;
            try
            {
                var obj = Object();
                SkipSpace();
                if (I < Bytes.Length && Bytes[I] == '}') { I++; return obj; }
                while (true)
                {
                    SkipSpace();
                    if (I >= Bytes.Length || Bytes[I] != '"') return null;
                    var key = StringValue();
                    if (key == null) return null;
                    SkipSpace();
                    if (I >= Bytes.Length || Bytes[I] != ':') return null;
                    I++;
                    var v = Value();
                    if (v == null) return null;
                    obj[key] = v;   // a repeated key keeps its first spot and takes the last value, like jq
                    SkipSpace();
                    if (I >= Bytes.Length) return null;
                    if (Bytes[I] == ',') { I++; continue; }
                    if (Bytes[I] == '}') { I++; return obj; }
                    return null;
                }
            }
            finally { depth--; }
        }
    }
}
