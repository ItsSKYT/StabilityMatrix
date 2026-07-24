using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NSec.Cryptography;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

return args[0] switch
{
    "gen-keys" => GenerateKeys(args.Skip(1).ToArray()),
    "sign-release" => SignRelease(args.Skip(1).ToArray()),
    _ => PrintUsage(),
};

static int PrintUsage()
{
    Console.Error.WriteLine(
        """
        Usage:
          UpdateSigner gen-keys <keys-dir>
          UpdateSigner sign-release --zip <path> --version <semver> --url <download-url> --changelog <url> --keys-dir <dir> --out <update-v3.json> [--channel stable] [--platform win-x64]
        """
    );
    return 1;
}

static int GenerateKeys(string[] args)
{
    var keysDir = args.ElementAtOrDefault(0) ?? "Build/update-keys";
    Directory.CreateDirectory(keysDir);

    var algorithm = SignatureAlgorithm.Ed25519;
    using var key = Key.Create(
        algorithm,
        new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport }
    );

    var privatePkix = key.Export(KeyBlobFormat.PkixPrivateKeyText);
    var publicPkix = key.Export(KeyBlobFormat.PkixPublicKeyText);

    var privatePath = Path.Combine(keysDir, "private.pem");
    var publicPath = Path.Combine(keysDir, "public.pem");
    File.WriteAllBytes(privatePath, privatePkix);
    File.WriteAllBytes(publicPath, publicPkix);

    Console.WriteLine($"Wrote {privatePath}");
    Console.WriteLine($"Wrote {publicPath}");
    Console.WriteLine();
    Console.WriteLine("Public key (paste into SignatureChecker.cs):");
    Console.WriteLine(Encoding.ASCII.GetString(publicPkix).Trim());
    return 0;
}

static int SignRelease(string[] args)
{
    string? zip = null;
    string? version = null;
    string? url = null;
    string? changelog = null;
    string keysDir = "Build/update-keys";
    string outPath = "update/update-v3.json";
    string channel = "stable";
    string platform = "win-x64";

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--zip":
                zip = args[++i];
                break;
            case "--version":
                version = args[++i];
                break;
            case "--url":
                url = args[++i];
                break;
            case "--changelog":
                changelog = args[++i];
                break;
            case "--keys-dir":
                keysDir = args[++i];
                break;
            case "--out":
                outPath = args[++i];
                break;
            case "--channel":
                channel = args[++i];
                break;
            case "--platform":
                platform = args[++i];
                break;
        }
    }

    if (zip is null || version is null || url is null || changelog is null)
    {
        Console.Error.WriteLine("Missing required arguments.");
        return PrintUsage();
    }

    var privatePem = File.ReadAllBytes(Path.Combine(keysDir, "private.pem"));
    var algorithm = SignatureAlgorithm.Ed25519;
    using var key = Key.Import(algorithm, privatePem, KeyBlobFormat.PkixPrivateKeyText);

    var fileBytes = File.ReadAllBytes(zip);
    var hashBlake3 = Convert.ToHexString(Blake3.Hasher.Hash(fileBytes).AsSpan()).ToLowerInvariant();

    var releaseDate = DateTimeOffset.UtcNow;
    var date = releaseDate.ToString(@"yyyy-MM-ddTHH\:mm\:ss.ffffffzzz", CultureInfo.InvariantCulture);
    var channelLower = channel.ToLowerInvariant();
    const int type = 1; // Normal
    var signedData = $"{version};{date};{channelLower};{type};{url};{changelog};{hashBlake3}";
    var signature = Convert.ToBase64String(algorithm.Sign(key, Encoding.UTF8.GetBytes(signedData)));

    JsonObject root;
    if (File.Exists(outPath))
    {
        root = JsonNode.Parse(File.ReadAllText(outPath))!.AsObject();
    }
    else
    {
        root = new JsonObject { ["updates"] = new JsonObject() };
    }

    var updates = root["updates"]!.AsObject();
    if (updates[channelLower] is not JsonObject platforms)
    {
        platforms = new JsonObject();
        updates[channelLower] = platforms;
    }

    platforms[platform] = new JsonObject
    {
        ["version"] = version,
        ["releaseDate"] = date,
        ["channel"] = channelLower,
        ["type"] = type,
        ["url"] = url,
        ["changelog"] = changelog,
        ["hashBlake3"] = hashBlake3,
        ["signature"] = signature,
    };

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
    var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(outPath, json + Environment.NewLine);

    Console.WriteLine($"Signed {zip}");
    Console.WriteLine($"  version   = {version}");
    Console.WriteLine($"  blake3    = {hashBlake3}");
    Console.WriteLine($"  signature = {signature}");
    Console.WriteLine($"Wrote {outPath}");
    return 0;
}
