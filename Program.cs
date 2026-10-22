// open!bancho: a small self-hosted online server and website for open!stable.
// Accounts, sessions, profiles (avatar, cover, bio, country, links), score submission, per-map leaderboards,
// global and country rankings with a simple pp, beatmap status (ranked / loved...), presence, chat channels,
// private messages and multiplayer rooms. JSON over HTTP; the website lives in wwwroot/.
// It has nothing to do with the official osu! servers and never talks to them.
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using OpenBancho;

int port = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var envPort) ? envPort : 13381; // hosts like Render set PORT
string mirror = "https://catboy.best";
string? storageSpec = null; // rclone remote + bucket/folder, e.g. filone:open-bancho/data
if (args.Contains("--selftest")) { Console.WriteLine(CloudStorage.SelfTest() ? "selftest OK" : "selftest FAILED"); return; }
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--port" && int.TryParse(args[i + 1], out var p)) port = p;
    if (args[i] == "--mirror") mirror = args[i + 1].TrimEnd('/');
    if (args[i] == "--storage") storageSpec = args[i + 1];
}

// Requests never write to the console themselves: on Windows a click in the console window pauses its output
// (QuickEdit), and every request that logs would hang until you press a key. A background thread prints instead.
var logQueue = new System.Collections.Concurrent.BlockingCollection<string>();
new Thread(() => { foreach (var line in logQueue.GetConsumingEnumerable()) Console.WriteLine(line); }) { IsBackground = true }.Start();
void Log(string text) => logQueue.Add($"{DateTime.Now:HH:mm:ss} {text}");
ConsoleMode.DisableQuickEdit();


var dataDir = Path.Combine(Directory.GetCurrentDirectory(), "data");
Directory.CreateDirectory(dataDir);
CloudStorage? cloud = null;
Store store;
try
{
    if (storageSpec is { Length: > 0 }) cloud = CloudStorage.FromRclone(storageSpec);
    else if (Environment.GetEnvironmentVariable("S3_BUCKET") is { Length: > 0 }) { cloud = CloudStorage.FromEnv(); storageSpec = cloud.Where; } // hosted: keys in environment variables
    store = Store.Open(dataDir, cloud, Log);
}
catch (Exception e)
{
    // with a bucket configured the server never falls back to the PC: two copies of the data would drift apart
    Console.WriteLine($"ERROR: no se pudo abrir el almacenamiento {(cloud != null ? "del bucket " + cloud.Where : "")}: {e.Message}");
    Thread.Sleep(500);
    Environment.Exit(1);
    return;
}
var builder = WebApplication.CreateBuilder();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
var app = builder.Build();
app.Lifetime.ApplicationStopping.Register(store.Flush); // Ctrl+C: the last changes reach the bucket first
var lobby = new Lobby(store);
var status = new StatusLookup(store, mirror);
app.UseDefaultFiles();
app.UseStaticFiles(); // the website (wwwroot/index.html, app.js, style.css)


long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
IResult Error(string message, int code = 400) => Results.Json(new { error = message }, statusCode: code);

User? Auth(HttpRequest r)
{
    var h = r.Headers.Authorization.ToString();
    if (!h.StartsWith("Bearer ")) return null;
    var u = store.UserForToken(h[7..].Trim());
    if (u != null) lobby.Seen(u);
    return u;
}

var images = new System.Collections.Concurrent.ConcurrentDictionary<string, byte[]>(); // pictures already read from the bucket

object Full(User u, int mode) =>
    store.FullProfile(u, mode, lobby.IsOnline(u.Id), lobby.StatusOf(u.Id), u.AvatarExt != "", u.CoverExt != "");

app.MapGet("/api/ping", () => new { name = "open!bancho", version = 2, online = lobby.OnlineCount(), users = store.UserCount, scores = store.ScoreCount });
app.MapGet("/api/countries", () => Countries.All.OrderBy(c => c.Value).Select(c => new { code = c.Key, name = c.Value }));

// ---------- accounts ----------

app.MapPost("/api/register", (Credentials c) =>
{
    var name = (c.Name ?? "").Trim();
    if (!Regex.IsMatch(name, @"^[A-Za-z0-9_\-\[\] ]{3,15}$")) return Error("El nombre debe tener de 3 a 15 caracteres (letras, números, espacio, _ - [ ]).");
    if ((c.Password ?? "").Length < 4) return Error("La contraseña debe tener al menos 4 caracteres.");
    var u = store.Register(name, c.Password!);
    if (u == null) return Error("Ese nombre ya está en uso.");
    Log($"[cuenta] nueva: {u.Name} (#{u.Id})");
    lobby.BotSay("osu", $"¡Démosle la bienvenida a {u.Name}!");
    return Results.Json(new { token = store.NewToken(u), user = store.Profile(u, 0) });
});

app.MapPost("/api/login", (Credentials c) =>
{
    var u = store.Login((c.Name ?? "").Trim(), c.Password ?? "");
    if (u == null) return Error("Usuario o contraseña incorrectos.", 401);
    lobby.Seen(u);
    Log($"[sesión] {u.Name} se conectó");
    return Results.Json(new { token = store.NewToken(u), user = store.Profile(u, 0) });
});

app.MapPost("/api/logout", (HttpRequest r) =>
{
    var h = r.Headers.Authorization.ToString();
    if (h.StartsWith("Bearer ")) store.DropToken(h[7..].Trim());
    return Results.Json(new { ok = true });
});

app.MapGet("/api/me", (HttpRequest r, int? mode) =>
{
    var u = Auth(r);
    return u == null ? Error("Sesión no válida.", 401) : Results.Json(store.Profile(u, Math.Clamp(mode ?? 0, 0, 3)));
});

app.MapGet("/api/me/full", (HttpRequest r) =>
{
    var u = Auth(r);
    return u == null ? Error("Sesión no válida.", 401) : Results.Json(Full(u, 0));
});

app.MapPost("/api/me/profile", (HttpRequest r, ProfileEdit e) =>
{
    var u = Auth(r);
    if (u == null) return Error("Inicia sesión.", 401);
    var err = store.UpdateProfile(u, e);
    return err != null ? Error(err) : Results.Json(Full(u, 0));
});

app.MapPost("/api/me/password", (HttpRequest r, PasswordChange c) =>
{
    var u = Auth(r);
    if (u == null) return Error("Inicia sesión.", 401);
    if ((c.New ?? "").Length < 4) return Error("La contraseña nueva debe tener al menos 4 caracteres.");
    return store.ChangePassword(u, c.Old ?? "", c.New!) ? Results.Json(new { ok = true }) : Error("La contraseña actual no es correcta.");
});

static string? Sniff(byte[] b) =>
    b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 ? "png"
    : b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF ? "jpg"
    : b.Length > 6 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x38 ? "gif" : null;

async Task<IResult> Upload(HttpRequest r, string folder, int maxBytes)
{
    var u = Auth(r);
    if (u == null) return Error("Inicia sesión.", 401);
    if (r.ContentLength is null or 0 || r.ContentLength > maxBytes) return Error($"La imagen debe pesar menos de {maxBytes / 1_000_000} MB.");
    using var ms = new MemoryStream();
    await r.Body.CopyToAsync(ms);
    var bytes = ms.ToArray();
    var ext = bytes.Length <= maxBytes ? Sniff(bytes) : null; // trust the bytes, not the file name
    if (ext == null) return Error("Solo se aceptan imágenes PNG, JPG o GIF.");
    var old = store.ImageExt(u.Id, folder);
    try
    {
        if (cloud != null)
        {
            await cloud.Put($"{folder}/{u.Id}.{ext}", bytes, ext == "jpg" ? "image/jpeg" : "image/" + ext);
            if (old != "" && old != ext) await cloud.Delete($"{folder}/{u.Id}.{old}");
        }
        else
        {
            Directory.CreateDirectory(Path.Combine(dataDir, folder));
            if (old != "") File.Delete(Path.Combine(dataDir, folder, $"{u.Id}.{old}"));
            await File.WriteAllBytesAsync(Path.Combine(dataDir, folder, $"{u.Id}.{ext}"), bytes);
        }
    }
    catch (Exception e) when (e is IOException or HttpRequestException or TaskCanceledException) { return Error("No se pudo guardar la imagen: " + e.Message, 502); }
    images[$"{folder}/{u.Id}"] = bytes;
    store.SetImage(u, folder, ext);
    return Results.Json(new { ok = true });
}

async Task<IResult> RemoveImage(HttpRequest r, string folder)
{
    var u = Auth(r);
    if (u == null) return Error("Inicia sesión.", 401);
    var old = store.ImageExt(u.Id, folder);
    if (old != "")
    {
        try
        {
            if (cloud != null) await cloud.Delete($"{folder}/{u.Id}.{old}");
            else File.Delete(Path.Combine(dataDir, folder, $"{u.Id}.{old}"));
        }
        catch (Exception e) when (e is IOException or HttpRequestException or TaskCanceledException) { return Error("No se pudo borrar la imagen: " + e.Message, 502); }
    }
    images.TryRemove($"{folder}/{u.Id}", out _);
    store.SetImage(u, folder, "");
    return Results.Json(new { ok = true });
}

async Task<IResult> Image(string folder, int id)
{
    var ext = store.ImageExt(id, folder);
    if (ext == "") return Results.NotFound();
    if (!images.TryGetValue($"{folder}/{id}", out var bytes))
    {
        try
        {
            bytes = cloud != null ? await cloud.Get($"{folder}/{id}.{ext}")
                : File.Exists(Path.Combine(dataDir, folder, $"{id}.{ext}")) ? await File.ReadAllBytesAsync(Path.Combine(dataDir, folder, $"{id}.{ext}")) : null;
        }
        catch (Exception e) when (e is IOException or HttpRequestException or TaskCanceledException) { return Results.StatusCode(502); }
        if (bytes == null) return Results.NotFound();
        images[$"{folder}/{id}"] = bytes;
    }
    return Results.File(bytes, ext == "jpg" ? "image/jpeg" : "image/" + ext);
}

app.MapPost("/api/me/avatar", (HttpRequest r) => Upload(r, "avatars", 2_000_000));
app.MapPost("/api/me/cover", (HttpRequest r) => Upload(r, "covers", 6_000_000));
app.MapDelete("/api/me/avatar", (HttpRequest r) => RemoveImage(r, "avatars"));
app.MapDelete("/api/me/cover", (HttpRequest r) => RemoveImage(r, "covers"));
app.MapGet("/a/{id:int}", (int id) => Image("avatars", id));
app.MapGet("/c/{id:int}", (int id) => Image("covers", id));

app.MapGet("/api/users/{name}", (string name, int? mode) =>
{
    var u = store.FindUser(name);
    return u == null ? Error("No existe ese usuario.", 404) : Results.Json(Full(u, Math.Clamp(mode ?? 0, 0, 3)));
});

app.MapGet("/api/users/{name}/brief", (string name, int? mode) =>
{
    var u = store.FindUser(name);
    return u == null ? Error("No existe ese usuario.", 404) : Results.Json(store.Profile(u, Math.Clamp(mode ?? 0, 0, 3)));
});

app.MapGet("/api/rankings", (int? mode, string? country, int? page) =>
    Results.Json(store.Rankings(Math.Clamp(mode ?? 0, 0, 3), (country ?? "").ToUpperInvariant(), Math.Max(0, page ?? 0))));

// shortcut: one stats pass per online user; fine for a few dozen players, cache AllStats if it gets slow
app.MapGet("/api/online", () => Results.Json(lobby.OnlineList().Select(o =>
{
    var ri = store.RankInfo(o.Id, o.Mode) ?? (0, 0, "", "");
    return new
    {
        id = o.Id, name = o.Name, status = o.Status, mode = o.Mode, tz = o.Tz, hasAvatar = store.ImageExt(o.Id, "avatars") != "",
        rank = ri.Rank, pp = ri.Pp, country = ri.Country, countryName = ri.CountryName,
    };
}).ToList()));

app.MapPost("/api/presence", (HttpRequest r, Presence p) =>
{
    var u = Auth(r);
    if (u == null) return Error("Inicia sesión.", 401);
    lobby.SetPresence(u, p);
    return Results.Json(new { ok = true });
});

// ---------- scores and maps ----------

app.MapPost("/api/scores", (HttpRequest r, ScoreSubmit s) =>
{
    var u = Auth(r);
    if (u == null) return Error("Inicia sesión para enviar puntuaciones.", 401);
    if (string.IsNullOrEmpty(s.Hash) || s.Hash.Length > 64 || s.Counts is not { Length: 6 } || s.Points < 0 || s.Mode is < 0 or > 3
        || s.Accuracy is < 0 or > 1 || s.Combo < 0 || s.Counts.Any(c => c < 0))
        return Error("Puntuación no válida.");
    var rec = store.AddScore(u, s, status.Cached(s.Hash, s.SetId));
    var board = store.Leaderboard(s.Hash, s.Mode, 100);
    int place = board.FindIndex(b => b.UserId == u.Id) + 1;
    Log($"[score] {u.Name}: {s.Title} {s.Points:N0} ({s.Accuracy * 100:0.00}%) {rec.Pp:0}pp");
    if (place == 1 && board.Count > 1) lobby.BotSay("ranked", $"¡{u.Name} logró el primer puesto en {rec.Title} [{rec.Version}]!");
    return Results.Json(new { place, pp = rec.Pp, user = store.Profile(u, s.Mode) });
});

app.MapGet("/api/scores", (string hash, int? mode) => Results.Json(store.Leaderboard(hash, mode ?? 0, 50)));
app.MapGet("/api/recent", () => Results.Json(store.Recent(15)));
app.MapGet("/api/maps", (string? q) => Results.Json(store.Maps((q ?? "").Trim())));
app.MapGet("/api/maps/{hash}", (string hash) => store.Map(hash) is { } m ? Results.Json(m) : Error("Nadie ha jugado ese mapa en este servidor.", 404));

app.MapGet("/api/beatmaps/status", async (string hash, int? setId) =>
    Results.Json(new { status = await status.Get(hash, setId ?? 0) }));

// ---------- chat ----------

app.MapGet("/api/channels", () => Results.Json(lobby.Channels()));

app.MapGet("/api/chat/{channel}", (HttpRequest r, string channel, long? since) =>
{
    if (Lobby.IsPm(channel) && (Auth(r) is not { } u || !Lobby.CanUse(channel, u.Id))) return Error("Ese chat es privado.", 403);
    return Results.Json(lobby.Messages(channel, since ?? 0));
});

app.MapPost("/api/chat/{channel}", (HttpRequest r, string channel, ChatPost m) =>
{
    var u = Auth(r);
    if (u == null) return Error("Inicia sesión para hablar.", 401);
    var text = (m.Text ?? "").Trim();
    if (text.Length == 0 || text.Length > 300) return Error("Mensaje vacío o demasiado largo.");
    if (!Lobby.CanUse(channel, u.Id)) return Error("No puedes escribir en ese canal.", 403);
    lobby.Say(channel, u, text);
    return Results.Json(new { ok = true });
});

app.MapPost("/api/chat/pm/{userId:int}", (HttpRequest r, int userId) =>
{
    var u = Auth(r);
    if (u == null) return Error("Inicia sesión.", 401);
    var other = store.FindUser(userId.ToString());
    if (other == null || other.Id == u.Id) return Error("No puedes escribirle a ese usuario.");
    return Results.Json(new { key = Lobby.PmKey(u.Id, other.Id), withId = other.Id, withName = other.Name });
});

app.MapGet("/api/me/pms", (HttpRequest r) =>
{
    var u = Auth(r);
    return u == null ? Error("Inicia sesión.", 401) : Results.Json(lobby.Pms(u.Id));
});

// ---------- multiplayer ----------

app.MapGet("/api/rooms", () => Results.Json(lobby.ListRooms()));

app.MapPost("/api/rooms", (HttpRequest r, RoomCreate c) =>
{
    var u = Auth(r);
    if (u == null) return Error("Inicia sesión para jugar en multijugador.", 401);
    var name = (c.Name ?? "").Trim();
    if (name.Length is 0 or > 50) return Error("Ponle un nombre a la partida.");
    var room = lobby.Create(u, name, c.Password ?? "", Math.Clamp(c.MaxPlayers, 2, 16), Math.Clamp(c.Mode, 0, 3));
    Log($"[multi] {u.Name} creó \"{name}\" (#{room.Id})");
    return Results.Json(lobby.View(room, NowMs()));
});

app.MapGet("/api/rooms/{id:int}", (HttpRequest r, int id) =>
{
    var u = Auth(r);
    var room = lobby.Find(id);
    if (room == null) return Error("La partida ya no existe.", 404);
    if (u != null) lobby.Touch(room, u);
    return Results.Json(lobby.View(room, NowMs()));
});

app.MapPost("/api/rooms/{id:int}/{action}", (HttpRequest r, int id, string action, RoomAction a) =>
{
    var u = Auth(r);
    if (u == null) return Error("Inicia sesión.", 401);
    var room = lobby.Find(id);
    if (room == null) return Error("La partida ya no existe.", 404);
    string? err = lobby.Act(room, u, action, a);
    return err != null ? Error(err) : Results.Json(lobby.View(room, NowMs()));
});

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("==============================================");
Console.WriteLine(" open!bancho - servidor local de open!stable");
Console.WriteLine("==============================================");
Console.WriteLine($" Sitio web y servidor:  http://localhost:{port}");
foreach (var ip in LanAddresses()) Console.WriteLine($" En tu red:             http://{ip}:{port}");
Console.WriteLine(cloud != null ? $" En este PC solo queda: {Path.Combine(dataDir, "status.txt")}" : $" Datos:                 {dataDir}");
Console.WriteLine(cloud != null ? $" Guardado en la nube:   {storageSpec} (cuentas, récords, fotos y portadas)" : " Guardado en la nube:   desactivado (STORAGE en local_bancho.bat)");
Console.WriteLine($" Cuentas: {store.UserCount}  ·  Puntuaciones: {store.ScoreCount}  ·  Ctrl+C para cerrar");
Console.WriteLine();
Console.WriteLine(" En el juego: Opciones > Mantenimiento > Servidor personalizado,");
Console.WriteLine($" escribe http://localhost:{port} y presiona Enter.");
Console.WriteLine();
app.Run();

static IEnumerable<string> LanAddresses()
{
    try
    {
        return Dns.GetHostAddresses(Dns.GetHostName())
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            .Select(a => a.ToString()).ToList();
    }
    catch (SocketException) { return Array.Empty<string>(); }
}
