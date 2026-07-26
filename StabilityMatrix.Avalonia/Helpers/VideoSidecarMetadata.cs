using System.Text.Json;
using System.Text.Json.Serialization;
using StabilityMatrix.Avalonia.Models;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Models.FileInterfaces;

namespace StabilityMatrix.Avalonia.Helpers;

/// <summary>
/// Sidecar metadata for video outputs (prompts / SM project), since MP4 has no PNG-style chunks.
/// File: {video}.smmeta.json
/// </summary>
public static class VideoSidecarMetadata
{
    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() },
        };

    public static string GetPath(string videoPath) => videoPath + ".smmeta.json";

    public static string GetPath(FilePath videoPath) => GetPath(videoPath.FullPath);

    public static async Task WriteAsync(
        FilePath videoPath,
        GenerationParameters parameters,
        InferenceProjectDocument? project
    )
    {
        var payload = new Payload
        {
            Parameters = parameters,
            Project = project,
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await File.WriteAllTextAsync(GetPath(videoPath), json);
    }

    public static bool TryRead(
        string videoPath,
        out GenerationParameters? parameters,
        out string? projectJson
    )
    {
        parameters = null;
        projectJson = null;

        var sidecar = GetPath(videoPath);
        if (!File.Exists(sidecar))
            return false;

        try
        {
            var json = File.ReadAllText(sidecar);
            var payload = JsonSerializer.Deserialize<Payload>(json, JsonOptions);
            if (payload is null)
                return false;

            parameters = payload.Parameters;
            projectJson = payload.Project is null
                ? null
                : JsonSerializer.Serialize(payload.Project, JsonOptions);
            return parameters is not null || projectJson is not null;
        }
        catch
        {
            return false;
        }
    }

    private sealed class Payload
    {
        public GenerationParameters? Parameters { get; set; }
        public InferenceProjectDocument? Project { get; set; }
    }
}
