namespace OpenBancho;

public sealed record Credentials(string? Name, string? Password);
public sealed record PasswordChange(string? Old, string? New);
public sealed record ChatPost(string? Text);
public sealed record Presence(string? Status, int Mode, int Tz);
public sealed record RoomCreate(string? Name, string? Password, int MaxPlayers = 8, int Mode = 0);
public sealed record RoomAction(string? Password, bool Ready, string? Hash, int SetId, int MapId, string? Title, string? Version,
    double Stars, int Mode, int Mods, long Points, int Combo, double Accuracy, bool Done, bool Failed, bool HasMap = true,
    string? Name = null, bool? FreeMod = null, string? TeamMode = null, string? WinCondition = null, int TargetId = 0);
public sealed record ScoreSubmit(string Hash, int SetId, int MapId, string? Title, string? Version, int Mode, int Mods,
    long Points, int Combo, double Accuracy, string? Rank, int[]? Counts, double Stars, bool Perfect, double Length = 0);
public sealed record ProfileEdit(string? Country, string? Bio, string? Location, string? Interests, string? Occupation,
    string? Twitter, string? Discord, string? Website, string? Playstyle, string? Title, string? Color);

public sealed class RankPoint
{
    public string Day { get; set; } = "";
    public int Rank { get; set; }
}

public sealed class User
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Hash { get; set; } = "";   // never sent to clients
    public string Salt { get; set; } = "";
    public DateTime Joined { get; set; }
    public DateTime LastSeen { get; set; }
    public string Country { get; set; } = "";
    public string Bio { get; set; } = "";
    public string Location { get; set; } = "";
    public string Interests { get; set; } = "";
    public string Occupation { get; set; } = "";
    public string Twitter { get; set; } = "";
    public string Discord { get; set; } = "";
    public string Website { get; set; } = "";
    public string Playstyle { get; set; } = "";
    public string Title { get; set; } = "";
    public string Color { get; set; } = "";
    public string AvatarExt { get; set; } = ""; // png / jpg / gif, "" without a picture
    public string CoverExt { get; set; } = "";
    public long PlayTimeMs { get; set; }
    public int MultiWins { get; set; }
    public Dictionary<int, List<RankPoint>> History { get; set; } = new();
}

public sealed class ScoreRec
{
    public long Id { get; set; }
    public int UserId { get; set; }
    public string Player { get; set; } = "";
    public string Hash { get; set; } = "";
    public int SetId { get; set; }
    public int MapId { get; set; }
    public string Title { get; set; } = "";
    public string Version { get; set; } = "";
    public int Mode { get; set; }
    public int Mods { get; set; }
    public long Points { get; set; }
    public int Combo { get; set; }
    public double Accuracy { get; set; }
    public string Rank { get; set; } = "D";
    public int[] Counts { get; set; } = new int[6];
    public double Stars { get; set; }
    public double Pp { get; set; }
    public bool Perfect { get; set; }
    public double Length { get; set; }
    public DateTime Date { get; set; }
}

public static class Countries
{
    // ISO code -> Spanish name; the web shows the flag from the code
    public static readonly Dictionary<string, string> All = new()
    {
        ["AR"] = "Argentina", ["BO"] = "Bolivia", ["BR"] = "Brasil", ["CL"] = "Chile", ["CO"] = "Colombia", ["CR"] = "Costa Rica",
        ["CU"] = "Cuba", ["DO"] = "República Dominicana", ["EC"] = "Ecuador", ["SV"] = "El Salvador", ["GT"] = "Guatemala",
        ["HN"] = "Honduras", ["MX"] = "México", ["NI"] = "Nicaragua", ["PA"] = "Panamá", ["PY"] = "Paraguay", ["PE"] = "Perú",
        ["PR"] = "Puerto Rico", ["UY"] = "Uruguay", ["VE"] = "Venezuela", ["US"] = "Estados Unidos", ["CA"] = "Canadá",
        ["ES"] = "España", ["PT"] = "Portugal", ["FR"] = "Francia", ["DE"] = "Alemania", ["IT"] = "Italia", ["GB"] = "Reino Unido",
        ["IE"] = "Irlanda", ["NL"] = "Países Bajos", ["BE"] = "Bélgica", ["CH"] = "Suiza", ["AT"] = "Austria", ["PL"] = "Polonia",
        ["CZ"] = "Chequia", ["SE"] = "Suecia", ["NO"] = "Noruega", ["FI"] = "Finlandia", ["DK"] = "Dinamarca", ["RU"] = "Rusia",
        ["UA"] = "Ucrania", ["BY"] = "Bielorrusia", ["TR"] = "Turquía", ["GR"] = "Grecia", ["RO"] = "Rumania", ["HU"] = "Hungría",
        ["JP"] = "Japón", ["KR"] = "Corea del Sur", ["CN"] = "China", ["TW"] = "Taiwán", ["HK"] = "Hong Kong", ["PH"] = "Filipinas",
        ["ID"] = "Indonesia", ["MY"] = "Malasia", ["SG"] = "Singapur", ["TH"] = "Tailandia", ["VN"] = "Vietnam", ["IN"] = "India",
        ["AU"] = "Australia", ["NZ"] = "Nueva Zelanda", ["ZA"] = "Sudáfrica", ["EG"] = "Egipto", ["MA"] = "Marruecos", ["SA"] = "Arabia Saudita",
        ["IL"] = "Israel",
    };

    public static string Name(string code) => All.TryGetValue(code, out var n) ? n : "";
}
