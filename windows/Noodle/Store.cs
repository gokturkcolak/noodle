using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Noodle;

public sealed class Exercise
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Instruction { get; set; } = "";
    public string Spec { get; set; } = "";
    public double DurationSeconds { get; set; }
    public string Figure { get; set; } = "";
    public List<string>? Steps { get; set; }     // beginner walkthrough shown under "How to"
    public string? Tip { get; set; }
}

public sealed class QuietHours
{
    public bool Enabled { get; set; }
    public string Start { get; set; } = "22:00";
    public string End { get; set; } = "07:00";

    /// True when `date` falls inside the window. Handles windows that wrap past midnight.
    public bool Contains(DateTime date)
    {
        if (!Enabled || Minutes(Start) is not int s || Minutes(End) is not int e || s == e) return false;
        int m = date.Hour * 60 + date.Minute;
        return s < e ? (m >= s && m < e) : (m >= s || m < e);
    }

    static int? Minutes(string s)
    {
        var p = s.Split(':');
        if (p.Length != 2 || !int.TryParse(p[0], out var h) || !int.TryParse(p[1], out var m)) return null;
        if (h is < 0 or > 23 || m is < 0 or > 59) return null;
        return h * 60 + m;
    }
}

public sealed class Config
{
    public string Mode { get; set; } = "claude";                // claude (cards while Claude works, via the hooks) or timer (one every cooldownMinutes)
    public double DelaySeconds { get; set; } = 15;
    public double CooldownMinutes { get; set; } = 20;
    public double SnoozeMinutes { get; set; } = 10;
    public string Position { get; set; } = "top-right";         // top-right, top-left, bottom-right, bottom-left
    public double Inset { get; set; } = 16;
    public QuietHours QuietHours { get; set; } = new();
    public List<string>? EnabledExercises { get; set; }         // null means all, in exercises.json order
    public string Character { get; set; } = "rigatoni";         // rigatoni, spaghetti, penne, lasagna, or sprout (see Cast)
    public bool ExpandSteps { get; set; }                       // open "How to" on every card, for when the moves are new
    public string Entrance { get; set; } = "card";              // card (slides in from the screen edge) or noodle (rides a strand down from the top)
    public bool Announce { get; set; }                          // open each card with "It's time to stretch!" before the exercise
    public bool HoldDuringCalls { get; set; } = true;           // no cards while any app is using the mic or camera
    public bool HoldDuringCalendarEvents { get; set; }          // macOS only for now, kept so a shared config round-trips
    public string Focus { get; set; } = Noodle.Focus.All.Raw(); // neck, hips, all, or "custom" once exercises are picked by hand

    [JsonIgnore] public bool OnTimer => Mode == "timer";

    public Config Clone() => JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(this, Store.Json), Store.Json)!;
}

/// The cast. Raw values match the generated figure folders, figures/<id>/.
public enum Cast { Rigatoni, Spaghetti, Penne, Lasagna, Sprout }

public static class CastInfo
{
    public static readonly Cast[] All = Enum.GetValues<Cast>();
    public static string Raw(this Cast c) => c.ToString().ToLowerInvariant();

    public static string Name(this Cast c) => c switch
    {
        Cast.Rigatoni => "Toni",
        Cast.Spaghetti => "Sammy",
        Cast.Penne => "Patty",
        Cast.Lasagna => "Lenny",
        _ => "Sprout",
    };

    public static string FullName(this Cast c) => c == Cast.Sprout ? "Sprout" : $"{c.Name()} the {c}";

    public static Cast From(string id) => All.FirstOrDefault(c => c.Raw() == id, Cast.Rigatoni);
}

/// What someone wants to work on. Picks which exercises are in the rotation.
public enum Focus { Neck, Hips, All }

public static class FocusInfo
{
    public static readonly Focus[] All = Enum.GetValues<Focus>();
    public static string Raw(this Focus f) => f.ToString().ToLowerInvariant();
    public static Focus? From(string raw) => All.Cast<Focus?>().FirstOrDefault(f => f!.Value.Raw() == raw);

    public static string Title(this Focus f) => f switch
    {
        Focus.Neck => "Tech neck",
        Focus.Hips => "Tight hips",
        _ => "General stiffness",
    };

    /// Exercise ids for this focus, or null for all of them.
    public static HashSet<string>? ExerciseIds(this Focus f) => f switch
    {
        Focus.Neck => ["chin-tuck", "neck-side", "scap", "doorway", "t-ext", "walk"],
        Focus.Hips => ["hip-flexor", "calf-raise", "walk", "t-ext"],
        _ => null,
    };
}

public sealed class State
{
    public int RotationIndex { get; set; }
    public DateTime? LastShown { get; set; }
    public DateTime? NextAllowed { get; set; }   // lastShown + cooldown, pushed out by snooze
    public DateTime? PausedUntil { get; set; }
    public bool? Onboarded { get; set; }         // set once the intro card has been shown
}

/// Owns ~/.claude-posture (or $CLAUDE_POSTURE_HOME): config.json, state.json, log.jsonl. Same files as the Mac app.
public sealed class Store
{
    public string Dir { get; }
    public string Resources { get; }
    public IReadOnlyList<Exercise> Exercises { get; }
    public string ConfigPath => Path.Combine(Dir, "config.json");
    public string StatePath => Path.Combine(Dir, "state.json");
    public string LogPath => Path.Combine(Dir, "log.jsonl");
    Config lastGoodConfig = new();

    /// camelCase keys, sorted like the Swift encoder (declaration order is close enough for a person reading it).
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new UtcDateConverter() },
    };
    static readonly JsonSerializerOptions Compact = new(Json) { WriteIndented = false };

    public Store(string dir, string resources)
    {
        Dir = dir;
        Resources = resources;
        try
        {
            Exercises = JsonSerializer.Deserialize<List<Exercise>>(File.ReadAllText(Path.Combine(resources, "exercises.json")), Json) ?? [];
        }
        catch (Exception e)
        {
            Exercises = [];
            Log.Debug($"exercises.json unreadable: {e.Message}");
        }
        EnsureConfig();
    }

    /// Writes a default config.json (all exercises enabled) if there isn't one.
    public void EnsureConfig()
    {
        Directory.CreateDirectory(Dir);
        if (File.Exists(ConfigPath)) return;
        Write(new Config { EnabledExercises = Exercises.Select(e => e.Id).ToList() }, ConfigPath);
    }

    /// Re-read on every use so edits apply without a restart. A broken file keeps the last good config.
    public Config Config()
    {
        try
        {
            lastGoodConfig = JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath), Json) ?? lastGoodConfig;
        }
        catch (Exception e)
        {
            Log.Debug($"config.json unreadable, using last good config: {e.Message}");
        }
        return lastGoodConfig.Clone();
    }

    /// Sets the focus and the exercise rotation that goes with it.
    public void Apply(Focus focus)
    {
        var c = Config();
        var all = Exercises.Select(e => e.Id).ToList();
        c.Focus = focus.Raw();
        var ids = focus.ExerciseIds();
        c.EnabledExercises = ids is null ? all : all.Where(ids.Contains).ToList();
        Save(c);
    }

    public List<Exercise> EnabledExercises(Config c)
    {
        if (c.EnabledExercises is null) return Exercises.ToList();
        return c.EnabledExercises.Select(id => Exercises.FirstOrDefault(e => e.Id == id)).OfType<Exercise>().ToList();
    }

    public State State()
    {
        try { return JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath), Json) ?? new(); }
        catch { return new(); }
    }

    public void Save(State s) => Write(s, StatePath);
    public void Save(Config c) { Write(c, ConfigPath); lastGoodConfig = c.Clone(); }

    public void LogOutcome(string exercise, string outcome)
    {
        var line = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["ts"] = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
            ["exercise"] = exercise,
            ["outcome"] = outcome,
        }, Compact);
        try { File.AppendAllText(LogPath, line + "\n"); }
        catch (Exception e) { Log.Debug($"log write failed: {e.Message}"); }
        Log.Debug($"log {exercise} {outcome}");
    }

    public int DoneToday()
    {
        string text;
        try { text = File.ReadAllText(LogPath); } catch { return 0; }
        var today = DateTime.Today;
        int n = 0;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.GetProperty("outcome").GetString() != "done") continue;
                if (DateTimeOffset.TryParse(root.GetProperty("ts").GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var ts) && ts.LocalDateTime.Date == today) n++;
            }
            catch { }
        }
        return n;
    }

    /// Writes through a temp file so a crash mid-write never leaves half a config.
    void Write<T>(T value, string path)
    {
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) { Log.Debug($"write {Path.GetFileName(path)} failed: {e.Message}"); }
    }
}

/// ISO 8601 in UTC without fractional seconds, like Swift's .iso8601, so state.json reads the same on both.
sealed class UtcDateConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o) =>
        DateTime.Parse(reader.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal).ToLocalTime();

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions o) =>
        writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
}

static class Log
{
    /// A windowed exe has no stderr to read, so NOODLE_DEBUG=1 also appends to %LOCALAPPDATA%\Noodle\debug.log.
    static readonly string? file = Environment.GetEnvironmentVariable("NOODLE_DEBUG") == "1"
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Noodle", "debug.log") : null;

    public static void Debug(string msg)
    {
        var t = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 1_000_000 / 1000.0).ToString("F2", CultureInfo.InvariantCulture);
        var line = $"[noodle {t}] {msg}";
        System.Diagnostics.Debug.WriteLine(line);
        if (file == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.AppendAllText(file, line + Environment.NewLine);
        }
        catch { }
    }
}
