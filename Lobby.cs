using System.Text.RegularExpressions;

namespace OpenBancho;

public sealed class Slot
{
    public int UserId;
    public string Name = "", Team = "red";
    public bool Ready, HasMap = true, Playing, Done, Failed;
    public long Points, Seen;
    public int Combo, Mods;
    public double Accuracy = 1;
}

public sealed class Room
{
    public int Id, HostId, Max = 8, Mode, Mods, Round;
    public string Name = "", Password = "", State = "open"; // open | playing
    public string Hash = "", Title = "", Version = "";
    public string TeamMode = "head", WinCondition = "score"; // head | team ; score | accuracy | combo
    public bool FreeMod;
    public int SetId, MapId;
    public double Stars;
    public long StartAt;
    public string LastWinner = "";
    public readonly List<Slot> Slots = new();
}

/// <summary>Online presence, chat channels (public, rooms and private messages) and multiplayer rooms. In memory only.</summary>
public sealed class Lobby
{
    public const string Bot = "open!bancho";

    public static readonly (string Key, string Name, string Topic)[] Public =
    {
        ("osu", "#osu", "Canal general de open!stable"),
        ("lobby", "#lobby", "Busca gente para jugar en multijugador"),
        ("espanol", "#español", "Hablemos en español"),
        ("help", "#help", "¿Dudas? Pregunta aquí"),
        ("ranked", "#ranked", "Mapas, récords y pp"),
    };

    readonly Store store;
    readonly object gate = new();
    readonly Dictionary<int, (string Name, long Seen, string Status, int Mode, int Tz)> online = new();
    readonly Dictionary<string, List<(long Id, int UserId, string User, string Text, long Time)>> chat = new();
    readonly List<Room> rooms = new();
    readonly Random rng = new();
    long nextMsg = 1;
    int nextRoom = 1;

    public Lobby(Store store) { this.store = store; }

    static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ---------- presence ----------

    public void Seen(User u)
    {
        lock (gate)
            online[u.Id] = online.TryGetValue(u.Id, out var o) ? (u.Name, NowMs(), o.Status, o.Mode, o.Tz) : (u.Name, NowMs(), "En línea", 0, 0);
    }

    public void SetPresence(User u, Presence p)
    {
        var status = (p.Status ?? "").Trim();
        if (status.Length > 120) status = status[..120];
        lock (gate) online[u.Id] = (u.Name, NowMs(), status == "" ? "En línea" : status, Math.Clamp(p.Mode, 0, 3), Math.Clamp(p.Tz, -14 * 60, 14 * 60));
    }

    public bool IsOnline(int id) { lock (gate) return online.TryGetValue(id, out var o) && NowMs() - o.Seen < 90_000; }

    public string StatusOf(int id) { lock (gate) return online.TryGetValue(id, out var o) && NowMs() - o.Seen < 90_000 ? o.Status : ""; }

    public int OnlineCount() { lock (gate) return online.Values.Count(o => NowMs() - o.Seen < 90_000); }

    public List<(int Id, string Name, string Status, int Mode, int Tz)> OnlineList()
    {
        lock (gate)
            return online.Where(o => NowMs() - o.Value.Seen < 90_000)
                .Select(o => (o.Key, o.Value.Name, o.Value.Status, o.Value.Mode, o.Value.Tz)).OrderBy(o => o.Name).ToList();
    }

    // ---------- chat ----------

    public static string PmKey(int a, int b) => $"pm_{Math.Min(a, b)}_{Math.Max(a, b)}";

    /// <summary>Who may read or write a channel: everyone for public and room channels, only the two people for a PM.</summary>
    public static bool CanUse(string channel, int userId)
    {
        if (Public.Any(p => p.Key == channel) || Regex.IsMatch(channel, @"^mp_\d+$")) return true;
        var m = Regex.Match(channel, @"^pm_(\d+)_(\d+)$");
        return m.Success && (m.Groups[1].Value == userId.ToString() || m.Groups[2].Value == userId.ToString());
    }

    public static bool IsPm(string channel) => channel.StartsWith("pm_");

    public void Say(string channel, User u, string text)
    {
        lock (gate)
        {
            Add(channel, u.Id, u.Name, text);
            if (text.StartsWith("!roll")) Add(channel, 0, Bot, $"{u.Name} sacó un {rng.Next(0, 101)}.");
            else if (text == "!help") Add(channel, 0, Bot, "Comandos: !roll (número al azar), !online (quién está conectado), !help.");
            else if (text == "!online")
                Add(channel, 0, Bot, "Conectados: " + string.Join(", ", online.Where(o => NowMs() - o.Value.Seen < 90_000).Select(o => o.Value.Name)));
        }
    }

    public void BotSay(string channel, string text) { lock (gate) Add(channel, 0, Bot, text); }

    void Add(string channel, int userId, string user, string text)
    {
        if (!chat.TryGetValue(channel, out var list)) chat[channel] = list = new();
        list.Add((nextMsg++, userId, user, text, NowMs()));
        if (list.Count > 300) list.RemoveAt(0);
    }

    public object Messages(string channel, long since)
    {
        lock (gate)
            return chat.TryGetValue(channel, out var list)
                ? list.Where(m => m.Id > since).Select(m => new { id = m.Id, userId = m.UserId, user = m.User, text = m.Text, time = m.Time }).ToList<object>()
                : new List<object>();
    }

    public object Channels()
    {
        lock (gate)
            return Public.Select(p => new { key = p.Key, name = p.Name, topic = p.Topic, messages = chat.TryGetValue(p.Key, out var l) ? l.Count : 0 }).ToList();
    }

    /// <summary>The private conversations of a user, so the game can open a tab when someone writes.</summary>
    public object Pms(int userId)
    {
        lock (gate)
            return chat.Where(c => IsPm(c.Key) && CanUse(c.Key, userId) && c.Value.Count > 0)
                .Select(c =>
                {
                    var ids = c.Key.Split('_').Skip(1).Select(int.Parse).ToArray();
                    int other = ids[0] == userId ? ids[1] : ids[0];
                    var name = c.Value.FirstOrDefault(m => m.UserId == other).User ?? store.FindUser(other.ToString())?.Name ?? "?";
                    return new { key = c.Key, withId = other, withName = name, lastId = c.Value[^1].Id };
                }).ToList();
    }

    // ---------- rooms ----------

    void Clean()
    {
        long now = NowMs();
        foreach (var r in rooms.ToList())
            foreach (var gone in r.Slots.Where(s => now - s.Seen > 20_000).ToList()) Leave(r, gone.UserId);
    }

    void Leave(Room r, int userId)
    {
        r.Slots.RemoveAll(s => s.UserId == userId);
        if (r.Slots.Count == 0) { rooms.Remove(r); return; }
        if (r.HostId == userId)
        {
            r.HostId = r.Slots[0].UserId;
            Add($"mp_{r.Id}", 0, Bot, $"{r.Slots[0].Name} es ahora el anfitrión.");
        }
        CheckRoundOver(r);
    }

    double Value(Slot s, string cond) => cond switch { "accuracy" => s.Accuracy, "combo" => s.Combo, _ => s.Points };

    void CheckRoundOver(Room r)
    {
        if (r.State != "playing" || !r.Slots.Where(s => s.Playing).All(s => s.Done)) return;
        r.State = "open";
        var players = r.Slots.Where(s => s.Playing).ToList();
        if (players.Count == 0) return;
        string what = r.WinCondition switch { "accuracy" => "precisión", "combo" => "combo", _ => "puntos" };
        if (r.TeamMode == "team")
        {
            var red = players.Where(s => s.Team == "red").ToList();
            var blue = players.Where(s => s.Team == "blue").ToList();
            double rv = red.Sum(s => Value(s, r.WinCondition)), bv = blue.Sum(s => Value(s, r.WinCondition));
            var won = rv >= bv ? red : blue;
            r.LastWinner = rv >= bv ? "Equipo rojo" : "Equipo azul";
            Add($"mp_{r.Id}", 0, Bot, $"¡Ganó el {r.LastWinner.ToLowerInvariant()} ({what})!");
            foreach (var s in won) store.AddWin(s.UserId);
        }
        else
        {
            var winner = players.OrderByDescending(s => Value(s, r.WinCondition)).First();
            r.LastWinner = winner.Name;
            Add($"mp_{r.Id}", 0, Bot, $"¡{winner.Name} ganó por {what}! ({winner.Points:N0} puntos, {winner.Accuracy * 100:0.00}%)");
            if (players.Count > 1) store.AddWin(winner.UserId);
        }
    }

    public object ListRooms()
    {
        lock (gate)
        {
            Clean();
            return rooms.Select(r => new
            {
                id = r.Id, name = r.Name, hasPassword = r.Password != "", players = r.Slots.Count, max = r.Max, mode = r.Mode,
                state = r.State, title = r.Title, version = r.Version, hash = r.Hash, host = r.Slots.FirstOrDefault(s => s.UserId == r.HostId)?.Name ?? "",
                names = r.Slots.Select(s => s.Name).ToList(),
            }).ToList();
        }
    }

    public Room? Find(int id) { lock (gate) { Clean(); return rooms.FirstOrDefault(r => r.Id == id); } }

    public Room Create(User u, string name, string password, int max, int mode)
    {
        lock (gate)
        {
            foreach (var old in rooms.Where(r => r.Slots.Any(s => s.UserId == u.Id)).ToList()) Leave(old, u.Id); // one room at a time
            var r = new Room { Id = nextRoom++, Name = name, Password = password, HostId = u.Id, Max = max, Mode = mode };
            r.Slots.Add(new Slot { UserId = u.Id, Name = u.Name, Seen = NowMs() });
            rooms.Add(r);
            Add($"mp_{r.Id}", 0, Bot, $"Partida creada por {u.Name}.");
            return r;
        }
    }

    public void Touch(Room r, User u)
    {
        lock (gate) { var s = r.Slots.FirstOrDefault(x => x.UserId == u.Id); if (s != null) s.Seen = NowMs(); }
    }

    public string? Act(Room r, User u, string action, RoomAction a)
    {
        lock (gate)
        {
            var me = r.Slots.FirstOrDefault(s => s.UserId == u.Id);
            if (action == "join")
            {
                if (me != null) { me.Seen = NowMs(); return null; }
                if (r.Password != "" && a.Password != r.Password) return "Contraseña incorrecta.";
                if (r.Slots.Count >= r.Max) return "La partida está llena.";
                foreach (var old in rooms.Where(x => x != r && x.Slots.Any(s => s.UserId == u.Id)).ToList()) Leave(old, u.Id);
                var blueCount = r.Slots.Count(s => s.Team == "blue");
                r.Slots.Add(new Slot { UserId = u.Id, Name = u.Name, Seen = NowMs(), Team = blueCount < r.Slots.Count - blueCount ? "blue" : "red" });
                Add($"mp_{r.Id}", 0, Bot, $"{u.Name} entró a la partida.");
                return null;
            }
            if (me == null) return "No estás en esta partida.";
            me.Seen = NowMs();
            bool host = r.HostId == u.Id;
            bool idle = r.State != "playing";
            var target = r.Slots.FirstOrDefault(s => s.UserId == a.TargetId);
            switch (action)
            {
                case "leave":
                    Add($"mp_{r.Id}", 0, Bot, $"{u.Name} salió de la partida.");
                    Leave(r, u.Id);
                    return null;
                case "ready":
                    if (!idle) return "La partida ya empezó.";
                    me.Ready = a.Ready;
                    me.HasMap = a.HasMap;
                    return null;
                case "hasmap":
                    me.HasMap = a.HasMap;
                    if (!a.HasMap) me.Ready = false;
                    return null;
                case "team":
                    if (!idle) return "Espera a que termine la ronda.";
                    me.Team = me.Team == "red" ? "blue" : "red";
                    return null;
                case "map":
                    if (!host) return "Solo el anfitrión puede cambiar el mapa.";
                    if (!idle) return "Espera a que termine la ronda.";
                    r.Hash = (a.Hash ?? "").ToUpperInvariant(); r.SetId = a.SetId; r.MapId = a.MapId;
                    r.Title = (a.Title ?? "")[..Math.Min(200, (a.Title ?? "").Length)]; r.Version = (a.Version ?? "")[..Math.Min(100, (a.Version ?? "").Length)];
                    r.Stars = a.Stars; r.Mode = Math.Clamp(a.Mode, 0, 3); r.Mods = a.Mods;
                    foreach (var s in r.Slots) { s.Ready = false; s.HasMap = s.UserId == u.Id; }
                    Add($"mp_{r.Id}", 0, Bot, $"Mapa: {r.Title} [{r.Version}]");
                    return null;
                case "mode":
                    if (!host) return "Solo el anfitrión puede cambiar el modo.";
                    if (!idle) return "Espera a que termine la ronda.";
                    r.Mode = Math.Clamp(a.Mode, 0, 3);
                    foreach (var s in r.Slots) s.Ready = false;
                    return null;
                case "mods":
                    if (!idle) return "Espera a que termine la ronda.";
                    if (host && !r.FreeMod) { r.Mods = a.Mods; foreach (var s in r.Slots) s.Ready = false; }
                    else if (r.FreeMod) me.Mods = a.Mods; // free mods: everyone picks their own
                    else return "Solo el anfitrión puede cambiar los mods.";
                    return null;
                case "settings":
                    if (!host) return "Solo el anfitrión puede cambiar los ajustes.";
                    if (a.Name is { Length: > 0 and <= 50 } n) r.Name = n.Trim();
                    if (a.Password != null) r.Password = a.Password.Length <= 40 ? a.Password : r.Password;
                    if (a.FreeMod is { } fm) { r.FreeMod = fm; if (fm) r.Mods = 0; }
                    if (a.TeamMode is "head" or "team") r.TeamMode = a.TeamMode;
                    if (a.WinCondition is "score" or "accuracy" or "combo") r.WinCondition = a.WinCondition;
                    return null;
                case "host":
                    if (!host) return "Solo el anfitrión puede ceder el puesto.";
                    if (target == null) return "Ese jugador ya no está.";
                    r.HostId = target.UserId;
                    Add($"mp_{r.Id}", 0, Bot, $"{target.Name} es ahora el anfitrión.");
                    return null;
                case "kick":
                    if (!host) return "Solo el anfitrión puede expulsar.";
                    if (target == null || target.UserId == u.Id) return "No puedes expulsar a ese jugador.";
                    Add($"mp_{r.Id}", 0, Bot, $"{target.Name} fue expulsado de la partida.");
                    Leave(r, target.UserId);
                    return null;
                case "start":
                    if (!host) return "Solo el anfitrión puede empezar.";
                    if (r.Hash == "") return "Elige un mapa primero.";
                    if (!idle) return null;
                    if (!r.Slots.Any(s => s.HasMap)) return "Nadie tiene el mapa.";
                    r.State = "playing";
                    r.Round++;
                    r.StartAt = NowMs() + 3000; // everyone gets the same 3 s countdown
                    foreach (var s in r.Slots)
                    {
                        s.Playing = s.HasMap;
                        s.Done = !s.HasMap;
                        s.Failed = false; s.Points = 0; s.Combo = 0; s.Accuracy = 1; s.Ready = false;
                    }
                    Add($"mp_{r.Id}", 0, Bot, "¡La partida empieza en 3 segundos!");
                    return null;
                case "abort":
                    if (!host) return "Solo el anfitrión puede cancelar.";
                    if (!idle) { r.State = "open"; Add($"mp_{r.Id}", 0, Bot, "El anfitrión canceló la ronda."); }
                    return null;
                case "progress":
                    if (idle || !me.Playing) return null;
                    me.Points = Math.Max(0, a.Points); me.Combo = Math.Max(0, a.Combo); me.Accuracy = Math.Clamp(a.Accuracy, 0, 1);
                    me.Failed |= a.Failed;
                    if (a.Done) { me.Done = true; CheckRoundOver(r); }
                    return null;
                default:
                    return "Acción desconocida.";
            }
        }
    }

    public object View(Room r, long now)
    {
        lock (gate)
            return new
            {
                id = r.Id, name = r.Name, hasPassword = r.Password != "", hostId = r.HostId, max = r.Max, mode = r.Mode, mods = r.Mods,
                state = r.State, round = r.Round, startIn = r.State == "playing" ? r.StartAt - now : 0,
                hash = r.Hash, setId = r.SetId, mapId = r.MapId, title = r.Title, version = r.Version, stars = r.Stars,
                freeMod = r.FreeMod, teamMode = r.TeamMode, winCondition = r.WinCondition, lastWinner = r.LastWinner,
                slots = r.Slots.Select(s => new
                {
                    userId = s.UserId, name = s.Name, ready = s.Ready, hasMap = s.HasMap, playing = s.Playing, done = s.Done,
                    failed = s.Failed, points = s.Points, combo = s.Combo, accuracy = s.Accuracy, team = s.Team, mods = s.Mods,
                }).ToList(),
            };
    }
}
