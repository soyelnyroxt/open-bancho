using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace OpenBancho;

/// <summary>
/// The server's data lives in an S3-compatible bucket (Fil.one, AWS, R2, MinIO...) instead of the PC:
/// bancho.json, avatars/ and covers/. Credentials come from your rclone remote (the one made with "rclone config"),
/// so nothing secret goes into the project. Requests are signed with AWS Signature V4; no SDK needed.
/// </summary>
public sealed class CloudStorage
{
    readonly string endpoint, region, bucket, prefix, accessKey, secretKey;
    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public string Where => $"{bucket}/{prefix}";

    CloudStorage(string endpoint, string region, string bucket, string prefix, string accessKey, string secretKey)
    {
        this.endpoint = endpoint.TrimEnd('/');
        this.region = region == "" ? "us-east-1" : region;
        this.bucket = bucket;
        this.prefix = prefix.Trim('/');
        this.accessKey = accessKey;
        this.secretKey = secretKey;
    }

    /// <summary>"filone:open-bancho/data" → the [filone] section of rclone.conf, bucket open-bancho, folder data.</summary>
    public static CloudStorage FromRclone(string spec)
    {
        int colon = spec.IndexOf(':');
        if (colon <= 0) throw new ArgumentException("Usa el formato remoto:bucket/carpeta, por ejemplo filone:open-bancho/data");
        string remote = spec[..colon], path = spec[(colon + 1)..].Trim('/');
        string bucket = path.Split('/')[0], prefix = path.Length > bucket.Length ? path[(bucket.Length + 1)..] : "";
        if (bucket == "") throw new ArgumentException("Falta el nombre del bucket, por ejemplo filone:open-bancho/data");

        var conf = Environment.GetEnvironmentVariable("RCLONE_CONFIG") is { Length: > 0 } env ? env
            : OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "rclone", "rclone.conf")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "rclone", "rclone.conf");
        if (!File.Exists(conf)) throw new FileNotFoundException($"No encontré la configuración de rclone ({conf}). Crea el remoto con: rclone config");
        var values = new Dictionary<string, string>();
        bool inside = false;
        foreach (var raw in File.ReadAllLines(conf))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { inside = line.Trim('[', ']') == remote; continue; }
            int eq = line.IndexOf('=');
            if (inside && eq > 0) values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        string Need(string key) => values.TryGetValue(key, out var v) && v != "" ? v
            : throw new InvalidOperationException($"El remoto \"{remote}\" de rclone no tiene {key}.");
        return new CloudStorage(Need("endpoint"), values.GetValueOrDefault("region", ""), bucket, prefix, Need("access_key_id"), Need("secret_access_key"));
    }

    /// <summary>For hosting services: S3_ENDPOINT, S3_REGION, S3_BUCKET, S3_PREFIX, S3_ACCESS_KEY, S3_SECRET_KEY.</summary>
    public static CloudStorage FromEnv()
    {
        static string Env(string name, bool required = true) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v.Trim()
            : required ? throw new InvalidOperationException($"Falta la variable de entorno {name}.") : "";
        return new CloudStorage(Env("S3_ENDPOINT"), Env("S3_REGION", false), Env("S3_BUCKET"), Env("S3_PREFIX", false), Env("S3_ACCESS_KEY"), Env("S3_SECRET_KEY"));
    }

    static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
    static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    static string EncodePath(string path) =>
        string.Join("/", path.Split('/').Select(seg => Uri.EscapeDataString(seg)));

    /// <summary>AWS Signature V4 for one request (path style: /bucket/key).</summary>
    internal static string Authorization(string method, string host, string canonicalPath, IList<(string Name, string Value)> headers,
        string payloadHash, DateTime now, string region, string accessKey, string secretKey)
    {
        string amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture), day = amzDate[..8];
        var signed = headers.Select(h => (Name: h.Name.ToLowerInvariant(), Value: h.Value.Trim())).Append(("host", host)).OrderBy(h => h.Item1, StringComparer.Ordinal).ToList();
        string canonical = string.Join("\n",
            method, canonicalPath, "",
            string.Concat(signed.Select(h => $"{h.Item1}:{h.Item2}\n")),
            string.Join(";", signed.Select(h => h.Item1)),
            payloadHash);
        string scope = $"{day}/{region}/s3/aws4_request";
        string toSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))}";
        var key = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), day), region), "s3"), "aws4_request");
        return $"AWS4-HMAC-SHA256 Credential={accessKey}/{scope}, SignedHeaders={string.Join(";", signed.Select(h => h.Item1))}, Signature={Hex(Hmac(key, toSign))}";
    }

    async Task<HttpResponseMessage> Send(HttpMethod method, string key, byte[]? body, string? contentType)
    {
        var url = new Uri($"{endpoint}/{EncodePath(bucket)}/{EncodePath(prefix == "" ? key : prefix + "/" + key)}");
        var now = DateTime.UtcNow;
        string payloadHash = Hex(SHA256.HashData(body ?? Array.Empty<byte>()));
        var headers = new List<(string, string)>
        {
            ("x-amz-content-sha256", payloadHash),
            ("x-amz-date", now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)),
        };
        if (body != null) headers.Add(("content-md5", Convert.ToBase64String(MD5.HashData(body)))); // buckets with Object Lock require it
        var req = new HttpRequestMessage(method, url);
        string auth = Authorization(method.Method, url.IsDefaultPort ? url.Host : $"{url.Host}:{url.Port}", url.AbsolutePath, headers, payloadHash, now, region, accessKey, secretKey);
        req.Headers.TryAddWithoutValidation("Authorization", auth);
        foreach (var (name, value) in headers)
            if (name != "content-md5") req.Headers.TryAddWithoutValidation(name, value);
        if (body != null)
        {
            req.Content = new ByteArrayContent(body);
            req.Content.Headers.TryAddWithoutValidation("Content-MD5", headers.First(h => h.Item1 == "content-md5").Item2);
            if (contentType != null) req.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }
        return await http.SendAsync(req);
    }

    static async Task<string> Explain(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        var code = System.Text.RegularExpressions.Regex.Match(text, "<Code>([^<]+)</Code>").Groups[1].Value;
        return $"{(int)r.StatusCode} {(code == "" ? r.ReasonPhrase : code)}";
    }

    /// <summary>The object's bytes, or null if it doesn't exist.</summary>
    public async Task<byte[]?> Get(string key)
    {
        using var r = await Send(HttpMethod.Get, key, null, null);
        if (r.StatusCode == HttpStatusCode.NotFound) return null;
        if (!r.IsSuccessStatusCode) throw new IOException($"No se pudo leer {key} del bucket: {await Explain(r)}");
        return await r.Content.ReadAsByteArrayAsync();
    }

    public async Task Put(string key, byte[] body, string contentType)
    {
        using var r = await Send(HttpMethod.Put, key, body, contentType);
        if (!r.IsSuccessStatusCode) throw new IOException($"No se pudo guardar {key} en el bucket: {await Explain(r)}");
    }

    public async Task Delete(string key)
    {
        using var r = await Send(HttpMethod.Delete, key, null, null);
        if (!r.IsSuccessStatusCode && r.StatusCode != HttpStatusCode.NotFound) throw new IOException($"No se pudo borrar {key}: {await Explain(r)}");
    }

    /// <summary>Checks the signing against AWS's published example (dotnet run -- --selftest).</summary>
    public static bool SelfTest()
    {
        var auth = Authorization("GET", "examplebucket.s3.amazonaws.com", "/test.txt",
            new List<(string, string)> { ("range", "bytes=0-9"), ("x-amz-content-sha256", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"), ("x-amz-date", "20130524T000000Z") },
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", new DateTime(2013, 5, 24, 0, 0, 0, DateTimeKind.Utc),
            "us-east-1", "AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY");
        return auth.EndsWith("Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41");
    }
}
