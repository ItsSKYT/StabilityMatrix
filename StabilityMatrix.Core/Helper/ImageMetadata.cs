using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using ExifLibrary;
using Force.Crc32;
using KGySoft.CoreLibraries;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Png;
using MetadataExtractor.Formats.WebP;
using StabilityMatrix.Core.Extensions;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Models.FileInterfaces;
using Directory = MetadataExtractor.Directory;

namespace StabilityMatrix.Core.Helper;

public class ImageMetadata
{
    private IReadOnlyList<Directory>? Directories { get; set; }

    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] Idat = "IDAT"u8.ToArray();
    private static readonly byte[] Text = "tEXt"u8.ToArray();
    private static readonly byte[] InternationalText = "iTXt"u8.ToArray();

    private static readonly byte[] Riff = "RIFF"u8.ToArray();
    private static readonly byte[] Webp = "WEBP"u8.ToArray();

    private static readonly JsonSerializerOptions UnicodeJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    /// <summary>
    /// Embeds generation parameters into PNG or WebP image bytes. Other formats are returned unchanged.
    /// </summary>
    public static byte[] EmbedGenerationParameters(
        byte[] inputImage,
        string extension,
        GenerationParameters generationParameters
    )
    {
        if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            return AddGenerationParametersToPng(inputImage, generationParameters);
        }

        if (extension.Equals(".webp", StringComparison.OrdinalIgnoreCase))
        {
            var paramsJson = JsonSerializer.Serialize(generationParameters, UnicodeJsonOptions);
            var result = AddMetadataToWebp(
                    inputImage,
                    new Dictionary<ExifTag, string> { { ExifTag.ImageDescription, paramsJson } }
                )
                .ToArray();

            return result.Length > 0 ? result : inputImage;
        }

        return inputImage;
    }

    /// <summary>
    /// Inserts <c>parameters</c> and <c>parameters-json</c> PNG text chunks before the first IDAT.
    /// </summary>
    public static byte[] AddGenerationParametersToPng(
        byte[] inputImage,
        GenerationParameters generationParameters
    )
    {
        if (inputImage.Length < 8 || !inputImage[..8].AsSpan().SequenceEqual(PngHeader))
        {
            return inputImage;
        }

        using var memoryStream = new MemoryStream();
        var position = 8;
        memoryStream.Write(inputImage, 0, position);

        var metadataInserted = false;

        while (position + 12 <= inputImage.Length)
        {
            var chunkLength = BitConverter.ToInt32(
                inputImage[position..(position + 4)].AsEnumerable().Reverse().ToArray(),
                0
            );

            var totalChunkSize = chunkLength + 12;

            if (chunkLength < 0 || position + totalChunkSize > inputImage.Length)
            {
                memoryStream.Write(inputImage, position, inputImage.Length - position);
                break;
            }

            var chunkType = Encoding.ASCII.GetString(inputImage[(position + 4)..(position + 8)]);

            switch (chunkType)
            {
                case "IHDR":
                {
                    var imageWidth = BitConverter.ToInt32(
                        inputImage[(position + 8)..(position + 12)].AsEnumerable().Reverse().ToArray()
                    );
                    var imageHeight = BitConverter.ToInt32(
                        inputImage[(position + 12)..(position + 16)].AsEnumerable().Reverse().ToArray()
                    );

                    generationParameters.Width = imageWidth;
                    generationParameters.Height = imageHeight;
                    break;
                }
                case "IDAT" when !metadataInserted:
                {
                    var paramsData = generationParameters.GetParametersText();
                    var paramsChunk = BuildTextChunk("parameters", paramsData);
                    var paramsJson = JsonSerializer.Serialize(generationParameters, UnicodeJsonOptions);
                    var paramsJsonChunk = BuildTextChunk("parameters-json", paramsJson);

                    memoryStream.Write(paramsChunk, 0, paramsChunk.Length);
                    memoryStream.Write(paramsJsonChunk, 0, paramsJsonChunk.Length);

                    metadataInserted = true;
                    break;
                }
            }

            memoryStream.Write(inputImage, position, totalChunkSize);
            position += totalChunkSize;
        }

        return memoryStream.ToArray();
    }

    private static byte[] BuildTextChunk(string key, string value)
    {
        if (value.Any(c => c > 0xFF))
        {
            return BuildInternationalTextChunk(key, value);
        }

        var textData = $"{key}\0{value}";
        var dataBytes = Encoding.UTF8.GetBytes(textData);
        var textDataLength = BitConverter.GetBytes(dataBytes.Length).AsEnumerable().Reverse().ToArray();
        var textDataBytes = Text.Concat(dataBytes).ToArray();
        var crc = BitConverter
            .GetBytes(Crc32Algorithm.Compute(textDataBytes))
            .AsEnumerable()
            .Reverse()
            .ToArray();

        return textDataLength.Concat(textDataBytes).Concat(crc).ToArray();
    }

    private static byte[] BuildInternationalTextChunk(string key, string value)
    {
        var keyBytes = Encoding.Latin1.GetBytes(key);
        var valueBytes = Encoding.UTF8.GetBytes(value);
        byte[] dataBytes = [.. keyBytes, 0, 0, 0, 0, 0, .. valueBytes];
        var dataLength = BitConverter.GetBytes(dataBytes.Length).AsEnumerable().Reverse().ToArray();
        var chunkTypeAndData = InternationalText.Concat(dataBytes).ToArray();
        var crc = BitConverter
            .GetBytes(Crc32Algorithm.Compute(chunkTypeAndData))
            .AsEnumerable()
            .Reverse()
            .ToArray();

        return dataLength.Concat(chunkTypeAndData).Concat(crc).ToArray();
    }

    public static ImageMetadata ParseFile(FilePath path)
    {
        return new ImageMetadata { Directories = ImageMetadataReader.ReadMetadata(path) };
    }

    public static ImageMetadata ParseFile(Stream stream)
    {
        return new ImageMetadata { Directories = ImageMetadataReader.ReadMetadata(stream) };
    }

    public System.Drawing.Size? GetImageSize()
    {
        if (Directories?.OfType<PngDirectory>().FirstOrDefault() is { } header)
        {
            header.TryGetInt32(PngDirectory.TagImageWidth, out var width);
            header.TryGetInt32(PngDirectory.TagImageHeight, out var height);

            return new System.Drawing.Size(width, height);
        }

        return null;
    }

    public static System.Drawing.Size GetImageSize(byte[] inputImage)
    {
        var imageWidthBytes = inputImage[0x10..0x14];
        var imageHeightBytes = inputImage[0x14..0x18];
        var imageWidth = BitConverter.ToInt32(imageWidthBytes.AsEnumerable().Reverse().ToArray());
        var imageHeight = BitConverter.ToInt32(imageHeightBytes.AsEnumerable().Reverse().ToArray());

        return new System.Drawing.Size(imageWidth, imageHeight);
    }

    public static System.Drawing.Size GetImageSize(BinaryReader reader)
    {
        var oldPosition = reader.BaseStream.Position;

        reader.BaseStream.Position = 0x10;
        var imageWidthBytes = reader.ReadBytes(4);
        var imageHeightBytes = reader.ReadBytes(4);

        var imageWidth = BitConverter.ToInt32(imageWidthBytes.AsEnumerable().Reverse().ToArray());
        var imageHeight = BitConverter.ToInt32(imageHeightBytes.AsEnumerable().Reverse().ToArray());

        reader.BaseStream.Position = oldPosition;

        return new System.Drawing.Size(imageWidth, imageHeight);
    }

    public static (
        string? Parameters,
        string? ParametersJson,
        string? SMProject,
        string? ComfyNodes,
        string? CivitParameters
    ) GetAllFileMetadata(FilePath filePath)
    {
        if (filePath.Extension.Equals(".webp", StringComparison.OrdinalIgnoreCase))
        {
            var paramsJson = ReadTextChunkFromWebp(filePath, ExifDirectoryBase.TagImageDescription);
            var smProj = ReadTextChunkFromWebp(filePath, ExifDirectoryBase.TagSoftware);

            return (null, paramsJson, smProj, null, null);
        }

        if (
            filePath.Extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || filePath.Extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
        )
        {
            var file = ImageFile.FromFile(filePath.Info.FullName);
            var userComment = file.Properties.Get(ExifTag.UserComment);
            var bytes = userComment.Interoperability.Data.Skip(8).ToArray();
            var userCommentString = Encoding.BigEndianUnicode.GetString(bytes);

            return (null, null, null, null, userCommentString);
        }

        using var stream = filePath.Info.OpenRead();
        using var reader = new BinaryReader(stream);

        var parameters = ReadTextChunk(reader, "parameters");
        var parametersJson = ReadTextChunk(reader, "parameters-json");
        var smProject = ReadTextChunk(reader, "smproj");
        var comfyNodes = ReadTextChunk(reader, "prompt");
        var civitParameters = ReadTextChunk(reader, "user_comment");

        return (
            string.IsNullOrEmpty(parameters) ? null : parameters,
            string.IsNullOrEmpty(parametersJson) ? null : parametersJson,
            string.IsNullOrEmpty(smProject) ? null : smProject,
            string.IsNullOrEmpty(comfyNodes) ? null : comfyNodes,
            string.IsNullOrEmpty(civitParameters) ? null : civitParameters
        );
    }

    public IEnumerable<Tag>? GetTextualData()
    {
        // Get the PNG-tEXt directory
        return Directories
            ?.Where(d => d.Name is "PNG-tEXt" or "PNG-iTXt" or "PNG-zTXt")
            .SelectMany(d => d.Tags)
            .Where(t => t.Name == "Textual Data");
    }

    public GenerationParameters? GetGenerationParameters()
    {
        var textualData = GetTextualData()?.ToArray();
        if (textualData is null)
        {
            return null;
        }

        // Use "parameters-json" tag if exists
        if (
            textualData.FirstOrDefault(tag =>
                tag.Description is { } desc && desc.StartsWith("parameters-json: ")
            ) is
            { Description: { } description }
        )
        {
            description = description.StripStart("parameters-json: ");

            return JsonSerializer.Deserialize<GenerationParameters>(description);
        }

        // Otherwise parse "parameters" tag
        if (
            textualData.FirstOrDefault(tag =>
                tag.Description is { } desc && desc.StartsWith("parameters: ")
            ) is
            { Description: { } parameters }
        )
        {
            parameters = parameters.StripStart("parameters: ");

            if (GenerationParameters.TryParse(parameters, out var generationParameters))
            {
                return generationParameters;
            }
        }

        return null;
    }

    public static string ReadTextChunk(BinaryReader byteStream, string key)
    {
        byteStream.BaseStream.Position = 0;

        // Read first 8 bytes and make sure they match the png header
        if (!byteStream.ReadBytes(8).SequenceEqual(PngHeader))
        {
            return string.Empty;
        }

        while (byteStream.BaseStream.Position < byteStream.BaseStream.Length - 4)
        {
            var chunkSize = BitConverter.ToInt32(byteStream.ReadBytes(4).AsEnumerable().Reverse().ToArray());
            var chunkType = Encoding.UTF8.GetString(byteStream.ReadBytes(4));

            if (chunkType == Encoding.UTF8.GetString(Idat))
            {
                return string.Empty;
            }

            if (chunkType == Encoding.UTF8.GetString(Text))
            {
                var textBytes = byteStream.ReadBytes(chunkSize);
                var text = Encoding.UTF8.GetString(textBytes);
                if (text.StartsWith($"{key}\0"))
                {
                    return text[(key.Length + 1)..];
                }
            }
            else if (chunkType == Encoding.UTF8.GetString(InternationalText))
            {
                var chunkBytes = byteStream.ReadBytes(chunkSize);
                // iTXt: Keyword \0 CompressionFlag CompressionMethod LanguageTag \0 TranslatedKeyword \0 Text
                var nullIndex = Array.IndexOf(chunkBytes, (byte)0);
                if (nullIndex >= 0)
                {
                    var keyword = Encoding.Latin1.GetString(chunkBytes, 0, nullIndex);
                    if (keyword == key && nullIndex + 3 < chunkBytes.Length)
                    {
                        // Skip: null separator + compression flag + compression method
                        var pos = nullIndex + 3;
                        // Skip language tag (to next null)
                        var langEnd = Array.IndexOf(chunkBytes, (byte)0, pos);
                        if (langEnd >= 0)
                        {
                            // Skip translated keyword (to next null)
                            var transEnd = Array.IndexOf(chunkBytes, (byte)0, langEnd + 1);
                            if (transEnd >= 0)
                            {
                                return Encoding.UTF8.GetString(
                                    chunkBytes,
                                    transEnd + 1,
                                    chunkBytes.Length - transEnd - 1
                                );
                            }
                        }
                    }
                }
            }
            else
            {
                // skip chunk data
                byteStream.BaseStream.Position += chunkSize;
            }

            // skip crc
            byteStream.BaseStream.Position += 4;
        }

        return string.Empty;
    }

    public static MemoryStream? BuildImageWithoutMetadata(FilePath imagePath)
    {
        using var byteStream = new BinaryReader(File.OpenRead(imagePath));
        byteStream.BaseStream.Position = 0;

        if (!byteStream.ReadBytes(8).SequenceEqual(PngHeader))
        {
            return null;
        }

        var memoryStream = new MemoryStream();
        memoryStream.Write(PngHeader);

        // add the IHDR chunk
        var ihdrStuff = byteStream.ReadBytes(25);
        memoryStream.Write(ihdrStuff);

        // find IDATs
        while (byteStream.BaseStream.Position < byteStream.BaseStream.Length - 4)
        {
            var chunkSizeBytes = byteStream.ReadBytes(4);
            var chunkSize = BitConverter.ToInt32(chunkSizeBytes.AsEnumerable().Reverse().ToArray());
            var chunkTypeBytes = byteStream.ReadBytes(4);
            var chunkType = Encoding.UTF8.GetString(chunkTypeBytes);

            if (chunkType != Encoding.UTF8.GetString(Idat))
            {
                // skip chunk data
                byteStream.BaseStream.Position += chunkSize;
                // skip crc
                byteStream.BaseStream.Position += 4;
                continue;
            }

            memoryStream.Write(chunkSizeBytes);
            memoryStream.Write(chunkTypeBytes);
            var idatBytes = byteStream.ReadBytes(chunkSize);
            memoryStream.Write(idatBytes);
            var crcBytes = byteStream.ReadBytes(4);
            memoryStream.Write(crcBytes);
        }

        // Add IEND chunk
        memoryStream.Write([0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82]);
        memoryStream.Position = 0;
        return memoryStream;
    }

    /// <summary>
    /// Reads an EXIF tag from a webp file and returns the value as string
    /// </summary>
    /// <param name="filePath">The webp file to read EXIF data from</param>
    /// <param name="exifTag">Use <see cref="ExifDirectoryBase"/> constants for the tag you'd like to search for</param>
    /// <returns></returns>
    public static string ReadTextChunkFromWebp(FilePath filePath, int exifTag)
    {
        var exifDirs = WebPMetadataReader.ReadMetadata(filePath).OfType<ExifIfd0Directory>().FirstOrDefault();
        return exifDirs is null ? string.Empty : exifDirs.GetString(exifTag) ?? string.Empty;
    }

    public static IEnumerable<byte> AddMetadataToWebp(
        byte[] inputImage,
        Dictionary<ExifTag, string> exifTagData
    )
    {
        using var byteStream = new BinaryReader(new MemoryStream(inputImage));
        byteStream.BaseStream.Position = 0;

        // Read first 8 bytes and make sure they match the RIFF header
        if (!byteStream.ReadBytes(4).SequenceEqual(Riff))
        {
            return Array.Empty<byte>();
        }

        // skip 4 bytes then read next 4 for webp header
        byteStream.BaseStream.Position += 4;
        if (!byteStream.ReadBytes(4).SequenceEqual(Webp))
        {
            return Array.Empty<byte>();
        }

        while (byteStream.BaseStream.Position < byteStream.BaseStream.Length - 4)
        {
            var chunkType = Encoding.UTF8.GetString(byteStream.ReadBytes(4));
            var chunkSize = BitConverter.ToInt32(byteStream.ReadBytes(4).ToArray());

            if (chunkType != "EXIF")
            {
                // skip chunk data
                byteStream.BaseStream.Position += chunkSize;
                continue;
            }

            var exifStart = byteStream.BaseStream.Position - 8;
            var exifBytes = byteStream.ReadBytes(chunkSize);
            Debug.WriteLine($"Found exif chunk of size {chunkSize}");

            using var stream = new MemoryStream(exifBytes[6..]);
            var img = new MyTiffFile(stream, Encoding.UTF8);

            foreach (var (key, value) in exifTagData)
            {
                img.Properties.Set(key, value);
            }

            using var newStream = new MemoryStream();
            img.Save(newStream);
            newStream.Seek(0, SeekOrigin.Begin);
            var newExifBytes = exifBytes[..6].Concat(newStream.ToArray());
            var newExifSize = newExifBytes.Count();
            var newChunkSize = BitConverter.GetBytes(newExifSize);
            var newChunk = "EXIF"u8.ToArray().Concat(newChunkSize).Concat(newExifBytes).ToArray();

            var inputEndIndex = (int)exifStart;
            var newImage = inputImage[..inputEndIndex].Concat(newChunk).ToArray();

            // webp or tiff or something requires even number of bytes
            if (newImage.Length % 2 != 0)
            {
                newImage = newImage.Concat(new byte[] { 0x00 }).ToArray();
            }

            // no clue why the minus 8 is needed but it is
            var newImageSize = BitConverter.GetBytes(newImage.Length - 8);
            newImage[4] = newImageSize[0];
            newImage[5] = newImageSize[1];
            newImage[6] = newImageSize[2];
            newImage[7] = newImageSize[3];
            return newImage;
        }

        return Array.Empty<byte>();
    }

    private static byte[] GetExifChunks(FilePath imagePath)
    {
        using var byteStream = new BinaryReader(File.OpenRead(imagePath));
        byteStream.BaseStream.Position = 0;

        // Read first 8 bytes and make sure they match the RIFF header
        if (!byteStream.ReadBytes(4).SequenceEqual(Riff))
        {
            return Array.Empty<byte>();
        }

        // skip 4 bytes then read next 4 for webp header
        byteStream.BaseStream.Position += 4;
        if (!byteStream.ReadBytes(4).SequenceEqual(Webp))
        {
            return Array.Empty<byte>();
        }

        while (byteStream.BaseStream.Position < byteStream.BaseStream.Length - 4)
        {
            var chunkType = Encoding.UTF8.GetString(byteStream.ReadBytes(4));
            var chunkSize = BitConverter.ToInt32(byteStream.ReadBytes(4).ToArray());

            if (chunkType != "EXIF")
            {
                // skip chunk data
                byteStream.BaseStream.Position += chunkSize;
                continue;
            }

            var exifStart = byteStream.BaseStream.Position;
            var exifBytes = byteStream.ReadBytes(chunkSize);
            var exif = Encoding.UTF8.GetString(exifBytes);
            Debug.WriteLine($"Found exif chunk of size {chunkSize}");
            return exifBytes;
        }

        return Array.Empty<byte>();
    }
}
