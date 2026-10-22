using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OpenBancho;

/// <summary>
/// Everything persistent (accounts, sessions, scores, map status). With a bucket (--storage) bancho.json lives only
/// there: it's read at startup and every change is uploaded right away in the background. Without one it's data/bancho.json.
/// </summary>
public sealed class Store
{
    public List<User> Users { get; set; } = new();
    public List<ScoreRec> Scores { get; set; } = new();
    public Dictionary<string, int> Tokens { get; set; } = new();
    public Dictionary<string, string> Status { get; set; } = new(); // md5 or "set:<id>" -> status
    public long NextScore { get; set; } = 1;

    string file = "";
    readonly object gate = new();
    [JsonIgnore] public string Dir { get; private set; } = "";
    CloudStorage? cloud;
    Action<string> log = _ => { };
    string? pending;            // the newest database not uploaded yet
    volatile bool uploading;
    readonly SemaphoreSlim dirty = new(0);

    public static Store Open(string dir, CloudStorage? cloud, Action<string> log)
    {
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "bancho.json");
        Store s = new();
        bool migrate = false;
        if (cloud != null)
        {
            var json = cloud.Get("bancho.json").GetAwaiter().GetResult();
            if (json != null) s = JsonSerializer.Deserialize<Store>(json) ?? new(); // a damaged file stops the server instead of being overwritten
            else if (File.Exists(file)) { s = JsonSerializer.Deserialize<Store>(File.ReadAllText(file)) ?? new(); migrate = true; }
        }
        else
        {
            Directory.CreateDirectory(Path.Combine(dir, "avatars"));
            Directory.CreateDirectory(Path.Combine(dir, "covers"));
            if (File.Exists(file))
            {
                try { s = JsonSerializer.Deserialize<Store>(File.ReadAllText(file)) ?? new(); }
                catch (JsonException)
                {
                    File.Copy(file, file + ".bak", true); // never lose a damaged database
                    log("AVISO: bancho.json estaba dañado, se guardó una copia en bancho.json.bak");
                }
            }
        }
        s.file = file;
        s.Dir = dir;
        s.log = log;
        // databases from before pictures were tracked: look at the files once
        foreach (var u in s.Users)
            foreach (var ext in new[] { "png", "jpg", "gif" })
            {
                if (u.AvatarExt == "" && File.Exists(Path.Combine(dir, "avatars", $"{u.Id}.{ext}"))) u.AvatarExt = ext;
                if (u.CoverExt == "" && File.Exists(Path.Combine(dir, "covers", $"{u.Id}.{ext}"))) u.CoverExt = ext;
            }
        if (cloud != null)
        {
            s.cloud = cloud;
            _ = Task.Run(s.WriteLoop);
            if (migrate) s.Migrate(dir);
        }
        return s;
    }

    /// <summary>First start with a bucket: moves this PC's data/ (database and pictures) up to it.</summary>
    void Migrate(string dir)
    {
        log("[nube] el bucket está vacío: subiendo los datos que había en este PC...");
        foreach (var folder in new[] { "avatars", "covers" })
            foreach (var path in Directory.Exists(Path.Combine(dir, folder)) ? Directory.GetFiles(Path.Combine(dir, folder)) : Array.Empty<string>())
                cloud!.Put($"{folder}/{Path.GetFileName(path)}", File.ReadAllBytes(path), "image/" + Path.GetExtension(path).TrimStart('.').Replace("jpg", "jpeg")).GetAwaiter().GetResult();
        lock (gate) Save();
        Flush();
        log("[nube] listo: desde ahora todo se guarda en el bucket.");
    }

    void Save()
    {
        if (cloud != null) // always called under the lock: snapshot now, upload in the background
        {
            pending = JsonSerializer.Serialize(this);
            if (dirty.CurrentCount == 0) dirty.Release();
            return;
        }
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this));
        File.Move(tmp, file, true); // atomic: a crash can't leave half a database
    }

    async Task WriteLoop()
    {
        while (true)
        {
            await dirty.WaitAsync();
            string? json;
            lock (gate) { json = pending; pending = null; uploading = json != null; }
            if (json == null) continue;
            try { await cloud!.Put("bancho.json", System.Text.Encoding.UTF8.GetBytes(json), "application/json"); }
            catch (Exception e)
            {
                log($"[nube] no se pudo guardar, reintento en 5 s: {e.Message}");
                lock (gate) pending ??= json; // keep it unless something newer is already waiting
                await Task.Delay(5000);
                if (dirty.CurrentCount == 0) dirty.Release();
            }
            finally { uploading = false; }
        }
    }

    /// <summary>Waits (at most 15 s) until the latest changes are in the bucket. Called when the server closes.</summary>
    public void Flush()
    {
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (cloud != null && wait.ElapsedMilliseconds < 15000)
        {
            lock (gate) if (pending == null && !uploading) return;
            Thread.Sleep(50);
        }
    }

    public string ImageExt(int userId, string folder)
    {
        lock (gate)
        {
            var u = Users.FirstOrDefault(x => x.Id == userId);
            return u == null ? "" : folder == "avatars" ? u.AvatarExt : u.CoverExt;
        }
    }

    public void SetImage(User u, string folder, string ext)
    {
        lock (gate)
        {
            if (folder == "avatars") u.AvatarExt = ext; else u.CoverExt = ext;
            Save();
        }
    }

    [JsonIgnore] public int UserCount { get { lock (gate) return Users.Count; } }
    [JsonIgnore] public int ScoreCount { get { lock (gate) return Scores.Count; } }

    // ---------- accounts ----------

    static string HashPassword(string password, string salt) =>
        Convert.ToHexString(Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromHexString(salt), 100_000, HashAlgorithmName.SHA256, 32));

    public User? Register(string name, string password)
    {
        lock (gate)
        {
            if (Users.Any(u => u.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return null;
            var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var u = new User
            {
                Id = Users.Count == 0 ? 1 : Users.Max(x => x.Id) + 1, Name = name, Salt = salt, Hash = HashPassword(password, salt),
                Joined = DateTime.UtcNow, LastSeen = DateTime.UtcNow,
            };
            Users.Add(u);
            Save();
            return u;
        }
    }

    public User? Login(string name, string password)
    {
        User? u;
        lock (gate) u = Users.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (u == null) return null;
        var given = Convert.FromHexString(HashPassword(password, u.Salt));
        return CryptographicOperations.FixedTimeEquals(given, Convert.FromHexString(u.Hash)) ? u : null;
    }

    public bool ChangePassword(User u, string old, string password)
    {
        if (Login(u.Name, old) == null) return false;
        lock (gate)
        {
            u.Salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            u.Hash = HashPassword(password, u.Salt);
            foreach (var t in Tokens.Where(t => t.Value == u.Id).Select(t => t.Key).ToList()) Tokens.Remove(t); // log out everywhere
            Save();
        }
        return true;
    }

    public string NewToken(User u)
    {
        var t = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        lock (gate) { Tokens[t] = u.Id; Save(); }
        return t;
    }

    public void DropToken(string t) { lock (gate) if (Tokens.Remove(t)) Save(); }

    public User? UserForToken(string t)
    {
        lock (gate)
        {
            var u = Tokens.TryGetValue(t, out var id) ? Users.FirstOrDefault(x => x.Id == id) : null;
            if (u != null) u.LastSeen = DateTime.UtcNow;
            return u;
        }
    }

    public User? FindUser(string nameOrId)
    {
        lock (gate)
            return Users.FirstOrDefault(u => u.Name.Equals(nameOrId, StringComparison.OrdinalIgnoreCase))
                   ?? (int.TryParse(nameOrId, out var id) ? Users.FirstOrDefault(u => u.Id == id) : null);
    }

    static string Clip(string? s, int max)
    {
        var t = (s ?? "").Trim();
        return t.Length > max ? t[..max] : t;
    }

    /// <summary>Validates and saves the editable profile fields; returns an error message or null.</summary>
    public string? UpdateProfile(User u, ProfileEdit e)
    {
        var country = (e.Country ?? "").Trim().ToUpperInvariant();
        if (country != "" && !Countries.All.ContainsKey(country)) return "País desconocido.";
        var twitter = Clip(e.Twitter, 30).TrimStart('@');
        if (twitter != "" && !Regex.IsMatch(twitter, @"^[A-Za-z0-9_]{1,15}$")) return "Usuario de Twitter/X no válido.";
        var website = Clip(e.Website, 200);
        if (website != "" && !Regex.IsMatch(website, @"^https?://[^\s<>""']+$")) return "La web debe empezar por http:// o https://";
        var color = Clip(e.Color, 7);
        if (color != "" && !Regex.IsMatch(color, @"^#[0-9a-fA-F]{6}$")) return "Color no válido.";
        var styles = (e.Playstyle ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x is "mouse" or "keyboard" or "tablet" or "touch").Distinct();
        lock (gate)
        {
            u.Country = country;
            u.Bio = Clip(e.Bio, 4000);
            u.Location = Clip(e.Location, 40);
            u.Interests = Clip(e.Interests, 60);
            u.Occupation = Clip(e.Occupation, 40);
            u.Twitter = twitter;
            u.Discord = Clip(e.Discord, 40);
            u.Website = website;
            u.Playstyle = string.Join(",", styles);
            u.Title = Clip(e.Title, 30);
            u.Color = color;
            Save();
        }
        return null;
    }

    // ---------- scores ----------

    // shortcut: a simple star-based pp (stars^2.4 × accuracy^5 × miss and mod factors), not osu!'s pp system; swap in a real calculator if rankings need to match it
    public static double Pp(double stars, double acc, int misses, int m)
    {
        double mods = 1;
        if ((m & 64) != 0 || (m & 512) != 0) mods *= 1.35;   // DT / NC
        if ((m & 16) != 0) mods *= 1.1;                       // HR
        if ((m & 8) != 0) mods *= 1.06;                       // HD
        if ((m & 1024) != 0) mods *= 1.12;                    // FL
        if ((m & 2) != 0) mods *= 0.5;                        // EZ
        if ((m & 256) != 0) mods *= 0.3;                      // HT
        if ((m & 1) != 0) mods *= 0.9;                        // NF
        if ((m & 4096) != 0) mods *= 0.95;                    // SO
        if ((m & (128 | 2048 | 8192 | 4194304)) != 0) mods = 0; // RX, AT, AP, CN
        return Math.Pow(Math.Max(0, stars), 2.4) * 8 * Math.Pow(acc, 5) * Math.Pow(0.97, misses) * mods;
    }

    public ScoreRec AddScore(User u, ScoreSubmit s, string mapStatus)
    {
        var rec = new ScoreRec
        {
            UserId = u.Id, Player = u.Name, Hash = s.Hash.ToUpperInvariant(), SetId = s.SetId, MapId = s.MapId,
            Title = Clip(s.Title, 200), Version = Clip(s.Version, 100), Mode = s.Mode, Mods = s.Mods, Points = s.Points, Combo = s.Combo,
            Accuracy = s.Accuracy, Rank = Clip(s.Rank, 3) is { Length: > 0 } r ? r : "D", Counts = s.Counts!, Stars = Math.Clamp(s.Stars, 0, 20),
            Perfect = s.Perfect, Length = Math.Clamp(s.Length, 0, 3 * 3600_000), Date = DateTime.UtcNow,
        };
        rec.Pp = mapStatus is "loved" or "qualified" ? 0 : Pp(rec.Stars, rec.Accuracy, rec.Counts[5], rec.Mods); // like stable: loved gives no pp
        lock (gate)
        {
            rec.Id = NextScore++;
            Scores.Add(rec);
            u.PlayTimeMs += (long)rec.Length;
            RecordRank(u, s.Mode, AllStats(s.Mode));
            Save();
        }
        return rec;
    }

    public void AddWin(int userId)
    {
        lock (gate)
        {
            var u = Users.FirstOrDefault(x => x.Id == userId);
            if (u == null) return;
            u.MultiWins++;
            Save();
        }
    }

    /// <summary>Each player's best score on the map, highest first.</summary>
    public List<ScoreRec> Leaderboard(string hash, int mode, int count)
    {
        hash = hash.ToUpperInvariant();
        lock (gate) return Board(hash, mode).Take(count).ToList();
    }

    IEnumerable<ScoreRec> Board(string hash, int mode) =>
        Scores.Where(s => s.Hash == hash && s.Mode == mode)
            .GroupBy(s => s.UserId).Select(g => g.OrderByDescending(s => s.Points).First())
            .OrderByDescending(s => s.Points).ThenBy(s => s.Date);

    // ---------- statistics ----------

    sealed record Stat(User User, double Pp, double Accuracy, long RankedScore, long TotalScore, int Plays, int MaxCombo, long Hits,
        int SS, int SSH, int S, int SH, int A, List<ScoreRec> Best);

    List<Stat> AllStats(int mode)
    {
        var byUser = Scores.Where(s => s.Mode == mode).GroupBy(s => s.UserId).ToDictionary(g => g.Key, g => g.ToList());
        return Users.Select(u =>
        {
            var mine = byUser.TryGetValue(u.Id, out var l) ? l : new List<ScoreRec>();
            var best = mine.GroupBy(s => s.Hash).Select(g => g.OrderByDescending(s => s.Pp).ThenByDescending(s => s.Points).First())
                           .OrderByDescending(s => s.Pp).ThenByDescending(s => s.Points).ToList();
            double pp = 0, acc = 0, wsum = 0;
            for (int i = 0; i < best.Count; i++)
            {
                double w = Math.Pow(0.95, i); // the same weighting curve stable uses
                pp += best[i].Pp * w;
                acc += best[i].Accuracy * w;
                wsum += w;
            }
            var top = mine.GroupBy(s => s.Hash).Select(g => g.OrderByDescending(s => s.Points).First()).ToList();
            return new Stat(u, pp, wsum > 0 ? acc / wsum : 0, top.Sum(s => s.Points), mine.Sum(s => s.Points), mine.Count,
                mine.Count == 0 ? 0 : mine.Max(s => s.Combo), mine.Sum(s => (long)s.Counts[0] + s.Counts[1] + s.Counts[2] + (mode == 3 ? s.Counts[3] + s.Counts[4] : 0)),
                top.Count(s => s.Rank == "SS"), top.Count(s => s.Rank == "SSH"), top.Count(s => s.Rank == "S"), top.Count(s => s.Rank == "SH"),
                top.Count(s => s.Rank == "A"), best);
        }).OrderByDescending(s => s.Pp).ThenByDescending(s => s.RankedScore).ThenBy(s => s.User.Id).ToList();
    }

    static void RecordRank(User u, int mode, List<Stat> all)
    {
        int rank = all.FindIndex(s => s.User.Id == u.Id) + 1;
        if (!u.History.TryGetValue(mode, out var h)) u.History[mode] = h = new();
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        if (h.Count > 0 && h[^1].Day == today) h[^1].Rank = rank;
        else h.Add(new RankPoint { Day = today, Rank = rank });
        if (h.Count > 90) h.RemoveAt(0);
    }

    /// <summary>osu!'s published level curve: total score needed for level n.</summary>
    static double LevelOf(long total)
    {
        static double Need(int n) => n <= 100
            ? 5000.0 / 3 * (4.0 * n * n * n - 3.0 * n * n - n) + 1.25 * Math.Pow(1.8, n - 60)
            : 26931190827 + 99999999999.0 * (n - 100);
        int lv = 1;
        while (lv < 200 && Need(lv + 1) <= total) lv++;
        return lv + Math.Clamp((total - Need(lv)) / Math.Max(1, Need(lv + 1) - Need(lv)), 0, 0.999);
    }

    static object Brief(Stat st, int rank, int countryRank, int mode) => new
    {
        id = st.User.Id, name = st.User.Name, mode, rank, countryRank, country = st.User.Country, countryName = Countries.Name(st.User.Country),
        pp = Math.Round(st.Pp, 2), accuracy = st.Accuracy, rankedScore = st.RankedScore, totalScore = st.TotalScore, plays = st.Plays,
        level = LevelOf(st.TotalScore), maxCombo = st.MaxCombo, ss = st.SS, ssh = st.SSH, s = st.S, sh = st.SH, a = st.A,
        title = st.User.Title, color = st.User.Color,
    };

    int CountryRank(List<Stat> all, Stat st) =>
        st.User.Country == "" ? 0 : all.Where(x => x.User.Country == st.User.Country).ToList().FindIndex(x => x.User.Id == st.User.Id) + 1;

    /// <summary>The short profile the game uses (top bar, panels).</summary>
    public object Profile(User u, int mode)
    {
        lock (gate)
        {
            var all = AllStats(mode);
            int idx = all.FindIndex(s => s.User.Id == u.Id);
            return Brief(all[idx], idx + 1, CountryRank(all, all[idx]), mode);
        }
    }

    public (int Rank, double Pp, string Country, string CountryName)? RankInfo(int userId, int mode)
    {
        lock (gate)
        {
            var all = AllStats(mode);
            int idx = all.FindIndex(s => s.User.Id == userId);
            if (idx < 0) return null;
            var c = all[idx].User.Country;
            return (idx + 1, Math.Round(all[idx].Pp, 2), c, Countries.Name(c));
        }
    }

    static object ScoreView(ScoreRec s, double weight = 1) => new
    {
        id = s.Id, player = s.Player, userId = s.UserId, hash = s.Hash, setId = s.SetId, mapId = s.MapId, title = s.Title, version = s.Version,
        mode = s.Mode, mods = s.Mods, points = s.Points, combo = s.Combo, accuracy = s.Accuracy, rank = s.Rank, counts = s.Counts,
        stars = s.Stars, pp = Math.Round(s.Pp, 2), weighted = Math.Round(s.Pp * weight, 2), weight = Math.Round(weight * 100), perfect = s.Perfect, date = s.Date,
    };

    /// <summary>Achievements computed from the player's scores (original names and icons, not osu!'s medals).</summary>
    static List<object> Medals(User u, List<ScoreRec> all, Stat st)
    {
        var list = new List<(string Id, string Name, string Desc, string Group, bool Got)>
        {
            ("first", "¡Primera partida!", "Termina tu primer mapa.", "Primeros pasos", all.Count >= 1),
            ("plays100", "Dedicación", "Juega 100 partidas.", "Constancia", all.Count >= 100),
            ("plays1000", "Incansable", "Juega 1000 partidas.", "Constancia", all.Count >= 1000),
            ("combo500", "Combo x500", "Consigue un combo de 500.", "Habilidad", all.Any(s => s.Combo >= 500)),
            ("combo1000", "Combo x1000", "Consigue un combo de 1000.", "Habilidad", all.Any(s => s.Combo >= 1000)),
            ("fc", "Sin fallos", "Termina un mapa sin ningún miss.", "Habilidad", all.Any(s => s.Counts[5] == 0)),
            ("rankS", "Rango S", "Consigue una S.", "Habilidad", all.Any(s => s.Rank is "S" or "SH" or "SS" or "SSH")),
            ("rankSS", "Perfección", "Consigue una SS.", "Habilidad", all.Any(s => s.Rank is "SS" or "SSH")),
            ("stars5", "Cinco estrellas", "Supera un mapa de 5 estrellas o más.", "Habilidad", all.Any(s => s.Stars >= 5)),
            ("pp100", "100pp", "Llega a 100pp en un modo.", "Rendimiento", st.Pp >= 100),
            ("pp1000", "1000pp", "Llega a 1000pp en un modo.", "Rendimiento", st.Pp >= 1000),
            ("allmodes", "Todoterreno", "Juega en los cuatro modos.", "Exploración", all.Select(s => s.Mode).Distinct().Count() == 4),
            ("multiwin", "Campeón", "Gana una partida multijugador.", "Multijugador", u.MultiWins >= 1),
            ("multiwin10", "Leyenda de la sala", "Gana 10 partidas multijugador.", "Multijugador", u.MultiWins >= 10),
            ("profile", "Con estilo", "Personaliza tu perfil (bio o país).", "Comunidad", u.Bio != "" || u.Country != ""),
        };
        return list.Select(m => (object)new { id = m.Id, name = m.Name, description = m.Desc, group = m.Group, achieved = m.Got }).ToList();
    }

    /// <summary>Everything the web profile shows.</summary>
    public object FullProfile(User u, int mode, bool online, string status, bool hasAvatar, bool hasCover)
    {
        lock (gate)
        {
            var all = AllStats(mode);
            int idx = all.FindIndex(s => s.User.Id == u.Id);
            var st = all[idx];
            RecordRank(u, mode, all); // keeps the rank graph moving even on days without plays
            var mine = Scores.Where(s => s.UserId == u.Id).ToList();
            var firsts = st.Best.Where(b => Board(b.Hash, mode).FirstOrDefault()?.UserId == u.Id).ToList();
            var pinned = st.Best.Take(5).ToList();
            return new
            {
                user = Brief(st, idx + 1, CountryRank(all, st), mode),
                joined = u.Joined, lastSeen = u.LastSeen, online, status,
                bio = u.Bio, location = u.Location, interests = u.Interests, occupation = u.Occupation,
                twitter = u.Twitter, discord = u.Discord, website = u.Website, playstyle = u.Playstyle,
                playTimeMs = u.PlayTimeMs, totalHits = st.Hits, multiWins = u.MultiWins,
                hitsPerPlay = st.Plays == 0 ? 0 : st.Hits / st.Plays,
                hasAvatar, hasCover,
                history = u.History.TryGetValue(mode, out var h) ? h.Select(p => new { day = p.Day, rank = p.Rank }).ToList<object>() : new List<object>(),
                best = st.Best.Take(50).Select((b, i) => ScoreView(b, Math.Pow(0.95, i))).ToList(),
                pinned = pinned.Select(b => ScoreView(b)).ToList(),
                firsts = firsts.Select(b => ScoreView(b)).ToList(),
                recent = mine.Where(s => s.Mode == mode).OrderByDescending(s => s.Date).Take(20).Select(s => ScoreView(s)).ToList(),
                medals = Medals(u, mine, st),
                modesPlayed = mine.Select(s => s.Mode).Distinct().OrderBy(m => m).ToList(),
            };
        }
    }

    public object Rankings(int mode, string country, int page)
    {
        lock (gate)
        {
            var all = AllStats(mode);
            var shown = all.Where(s => s.Plays > 0 && (country == "" || s.User.Country == country)).ToList();
            return new
            {
                total = shown.Count,
                page,
                rows = shown.Skip(page * 50).Take(50).Select(s => Brief(s, all.IndexOf(s) + 1, CountryRank(all, s), mode)).ToList(),
                countries = all.Where(s => s.Plays > 0 && s.User.Country != "").GroupBy(s => s.User.Country)
                    .Select(g => new { code = g.Key, name = Countries.Name(g.Key), players = g.Count(), pp = Math.Round(g.Sum(x => x.Pp), 2) })
                    .OrderByDescending(c => c.pp).ToList(),
            };
        }
    }

    public object Maps(string query)
    {
        lock (gate)
            return Scores.Where(s => query == "" || s.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || s.Version.Contains(query, StringComparison.OrdinalIgnoreCase))
                .GroupBy(s => s.Hash)
                .Select(g =>
                {
                    var top = g.OrderByDescending(s => s.Points).First();
                    var last = g.Max(s => s.Date);
                    return new { hash = g.Key, title = top.Title, version = top.Version, setId = top.SetId, mapId = top.MapId, mode = top.Mode,
                        stars = top.Stars, plays = g.Count(), players = g.Select(s => s.UserId).Distinct().Count(), top = top.Player, last,
                        status = Status.TryGetValue(g.Key, out var st) ? st : top.SetId > 0 && Status.TryGetValue($"set:{top.SetId}", out var st2) ? st2 : "unknown" };
                })
                .OrderByDescending(m => m.last).Take(200).ToList();
    }

    public object? Map(string hash)
    {
        hash = hash.ToUpperInvariant();
        lock (gate)
        {
            var any = Scores.FirstOrDefault(s => s.Hash == hash);
            if (any == null) return null;
            var modes = Scores.Where(s => s.Hash == hash).Select(s => s.Mode).Distinct().OrderBy(m => m).ToList();
            return new
            {
                hash, title = any.Title, version = any.Version, setId = any.SetId, mapId = any.MapId, stars = any.Stars,
                plays = Scores.Count(s => s.Hash == hash),
                status = Status.TryGetValue(hash, out var st) ? st : "unknown",
                boards = modes.Select(m => new { mode = m, scores = Board(hash, m).Take(50).Select(s => ScoreView(s)).ToList() }).ToList(),
            };
        }
    }

    public object Recent(int count)
    {
        lock (gate)
            return Scores.OrderByDescending(s => s.Date).Take(count)
                .Select(s => new { score = ScoreView(s), country = Users.FirstOrDefault(u => u.Id == s.UserId)?.Country ?? "" }).ToList();
    }

    public string? StatusFor(string key) { lock (gate) return Status.TryGetValue(key, out var v) ? v : null; }

    public void SetStatus(string key, string value) { lock (gate) { Status[key] = value; Save(); } }
}
