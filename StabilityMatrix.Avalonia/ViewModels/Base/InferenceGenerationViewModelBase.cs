using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AsyncAwaitBestPractices;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExifLibrary;
using FluentAvalonia.UI.Controls;
using NLog;
using Refit;
using Semver;
using SkiaSharp;
using StabilityMatrix.Avalonia.Extensions;
using StabilityMatrix.Avalonia.Helpers;
using StabilityMatrix.Avalonia.Languages;
using StabilityMatrix.Avalonia.Models;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Dialogs;
using StabilityMatrix.Avalonia.ViewModels.Inference;
using StabilityMatrix.Avalonia.ViewModels.Inference.Modules;
using StabilityMatrix.Core.Exceptions;
using StabilityMatrix.Core.Extensions;
using StabilityMatrix.Core.Helper;
using StabilityMatrix.Core.Inference;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Models.Api.Comfy;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Models.Api.Comfy.WebSocketData;
using StabilityMatrix.Core.Models.FileInterfaces;
using StabilityMatrix.Core.Models.Inference;
using StabilityMatrix.Core.Models.Notifications;
using StabilityMatrix.Core.Models.PackageModification;
using StabilityMatrix.Core.Models.Packages;
using StabilityMatrix.Core.Models.Packages.Extensions;
using StabilityMatrix.Core.Models.Settings;
using StabilityMatrix.Core.Services;
using Notification = DesktopNotifications.Notification;

namespace StabilityMatrix.Avalonia.ViewModels.Base;

/// <summary>
/// Abstract base class for tab view models that generate images using ClientManager.
/// This includes a progress reporter, image output view model, and generation virtual methods.
/// </summary>
[SuppressMessage("ReSharper", "VirtualMemberNeverOverridden.Global")]
public abstract partial class InferenceGenerationViewModelBase
    : InferenceTabViewModelBase,
        IImageGalleryComponent
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly ISettingsManager settingsManager;
    private readonly RunningPackageService runningPackageService;
    private readonly IServiceManager<ViewModelBase> vmFactory;
    private readonly INotificationService notificationService;

    [JsonPropertyName("ImageGallery")]
    public ImageGalleryCardViewModel ImageGalleryCardViewModel { get; }

    [JsonIgnore]
    public ImageFolderCardViewModel ImageFolderCardViewModel { get; }

    [JsonIgnore]
    public ProgressViewModel OutputProgress { get; } = new();

    [JsonIgnore]
    public IInferenceClientManager ClientManager { get; }

    /// <summary>
    /// Pending generation jobs for this tab. Snapshots are taken at enqueue time.
    /// </summary>
    [JsonIgnore]
    public ObservableCollection<InferenceQueueItem> GenerationQueue { get; } = [];

    [JsonIgnore]
    public int GenerationQueueCount => GenerationQueue.Count;

    [ObservableProperty]
    [property: JsonIgnore]
    private bool isProcessingGenerationQueue;

    private CancellationTokenSource? queueDrainCts;

    /// <inheritdoc />
    protected InferenceGenerationViewModelBase(
        IServiceManager<ViewModelBase> vmFactory,
        IInferenceClientManager inferenceClientManager,
        INotificationService notificationService,
        ISettingsManager settingsManager,
        RunningPackageService runningPackageService
    )
        : base(notificationService)
    {
        this.notificationService = notificationService;
        this.settingsManager = settingsManager;
        this.runningPackageService = runningPackageService;
        this.vmFactory = vmFactory;

        ClientManager = inferenceClientManager;

        ImageGalleryCardViewModel = vmFactory.Get<ImageGalleryCardViewModel>();
        ImageFolderCardViewModel = AddDisposable(vmFactory.Get<ImageFolderCardViewModel>());

        GenerateImageCommand.WithConditionalNotificationErrorHandler(notificationService);
        GenerationQueue.CollectionChanged += (_, _) => OnPropertyChanged(nameof(GenerationQueueCount));
    }

    /// <summary>
    /// Write an image to the default output folder
    /// </summary>
    protected Task<FilePath> WriteOutputImageAsync(
        Stream imageStream,
        ImageGenerationEventArgs args,
        int batchNum = 0,
        int batchTotal = 0,
        bool isGrid = false,
        string fileExtension = "png"
    )
    {
        var defaultOutputDir = settingsManager.ImagesInferenceDirectory;
        defaultOutputDir.Create();

        return WriteOutputImageAsync(
            imageStream,
            defaultOutputDir,
            args,
            batchNum,
            batchTotal,
            isGrid,
            fileExtension
        );
    }

    /// <summary>
    /// Write an image to an output folder
    /// </summary>
    protected async Task<FilePath> WriteOutputImageAsync(
        Stream imageStream,
        DirectoryPath outputDir,
        ImageGenerationEventArgs args,
        int batchNum = 0,
        int batchTotal = 0,
        bool isGrid = false,
        string fileExtension = "png"
    )
    {
        var formatTemplateStr = settingsManager.Settings.InferenceOutputImageFileNameFormat;

        var formatProvider = new FileNameFormatProvider
        {
            GenerationParameters = args.Parameters,
            ProjectType = args.Project?.ProjectType,
            ProjectName = ProjectFile?.NameWithoutExtension,
        };

        // Parse to format
        if (
            string.IsNullOrEmpty(formatTemplateStr)
            || !FileNameFormat.TryParse(formatTemplateStr, formatProvider, out var format)
        )
        {
            // Fallback to default
            Logger.Warn(
                "Failed to parse format template: {FormatTemplate}, using default",
                formatTemplateStr
            );

            format = FileNameFormat.Parse(FileNameFormat.DefaultTemplate, formatProvider);
        }

        if (isGrid)
        {
            format = format.WithGridPrefix();
        }

        if (batchNum >= 1 && batchTotal > 1)
        {
            format = format.WithBatchPostFix(batchNum, batchTotal);
        }

        var fileName = format.GetFileName();
        var file = outputDir.JoinFile($"{fileName}.{fileExtension}");

        // Until the file is free, keep adding _{i} to the end
        for (var i = 0; i < 100; i++)
        {
            if (!file.Exists)
                break;

            file = outputDir.JoinFile($"{fileName}_{i + 1}.{fileExtension}");
        }

        // If that fails, append an 7-char uuid
        if (file.Exists)
        {
            var uuid = Guid.NewGuid().ToString("N")[..7];
            file = outputDir.JoinFile($"{fileName}_{uuid}.{fileExtension}");
        }

        if (file.Info.DirectoryName != null)
        {
            Directory.CreateDirectory(file.Info.DirectoryName);
        }

        await using var fileStream = file.Info.OpenWrite();
        await imageStream.CopyToAsync(fileStream);

        return file;
    }

    /// <summary>
    /// Builds the image generation prompt
    /// </summary>
    protected virtual void BuildPrompt(BuildPromptEventArgs args) { }

    /// <summary>
    /// Uploads files required for the prompt
    /// </summary>
    protected virtual async Task UploadPromptFiles(
        IEnumerable<(string SourcePath, string DestinationRelativePath)> files,
        ComfyClient client
    )
    {
        foreach (var (sourcePath, destinationRelativePath) in files)
        {
            Logger.Debug(
                "Uploading prompt file {SourcePath} to relative path {DestinationPath}",
                sourcePath,
                destinationRelativePath
            );

            await client.UploadFileAsync(sourcePath, destinationRelativePath);
        }
    }

    /// <summary>
    /// Gets ImageSources that need to be uploaded as inputs
    /// </summary>
    protected virtual IEnumerable<ImageSource> GetInputImages()
    {
        return Enumerable.Empty<ImageSource>();
    }

    protected async Task UploadInputImages(ComfyClient client)
    {
        foreach (var image in GetInputImages())
        {
            await ClientManager.UploadInputImageAsync(image);
        }
    }

    public async Task RunCustomGeneration(
        InferenceQueueCustomPromptEventArgs args,
        CancellationToken cancellationToken = default
    )
    {
        if (ClientManager.Client is not { } client)
        {
            throw new InvalidOperationException("Client is not connected");
        }

        var generationArgs = new ImageGenerationEventArgs
        {
            Client = client,
            Nodes = args.Builder.ToNodeDictionary(),
            OutputNodeNames = args.Builder.Connections.OutputNodeNames.ToArray(),
            Project = InferenceProjectDocument.FromLoadable(this),
            FilesToTransfer = args.FilesToTransfer,
            Parameters = new GenerationParameters(),
            ClearOutputImages = true,
        };

        await RunGeneration(generationArgs, cancellationToken);
    }

    /// <summary>
    /// Runs a generation task
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if args.Parameters or args.Project are null</exception>
    protected async Task RunGeneration(ImageGenerationEventArgs args, CancellationToken cancellationToken)
    {
        var client = args.Client;
        var nodes = args.Nodes;

        // Checks
        if (args.Parameters is null)
            throw new InvalidOperationException("Parameters is null");
        if (args.Project is null)
            throw new InvalidOperationException("Project is null");
        if (args.OutputNodeNames.Count == 0)
            throw new InvalidOperationException("OutputNodeNames is empty");
        if (client.OutputImagesDir is null)
            throw new InvalidOperationException("OutputImagesDir is null");

        // Only check extensions for first batch index
        if (args.BatchIndex == 0)
        {
            if (!await CheckPromptExtensionsInstalled(args.Nodes))
            {
                throw new ValidationException("Prompt extensions not installed");
            }
        }

        // Upload input images
        await UploadInputImages(client);

        // Upload required files
        await UploadPromptFiles(args.FilesToTransfer, client);

        // Connect preview image handler
        client.PreviewImageReceived += OnPreviewImageReceived;

        // Register to interrupt if user cancels
        var promptInterrupt = cancellationToken.Register(() =>
        {
            Logger.Info("Cancelling prompt");
            client
                .InterruptPromptAsync(new CancellationTokenSource(5000).Token)
                .SafeFireAndForget(ex =>
                {
                    Logger.Warn(ex, "Error while interrupting prompt");
                });
        });

        ComfyTask? promptTask = null;

        try
        {
            var timer = Stopwatch.StartNew();

            try
            {
                promptTask = await client.QueuePromptAsync(nodes, cancellationToken);
            }
            catch (ApiException e)
            {
                Logger.Warn(e, "Api exception while queuing prompt");
                await DialogHelper.CreateApiExceptionDialog(e, "Api Error").ShowAsync();
                return;
            }

            // Register progress handler
            promptTask.ProgressUpdate += OnProgressUpdateReceived;

            // Delay attaching running node change handler to not show indeterminate progress
            // if progress updates are received before the prompt starts
            Task.Run(
                    async () =>
                    {
                        try
                        {
                            var delayTime = 250 - (int)timer.ElapsedMilliseconds;
                            if (delayTime > 0)
                            {
                                await Task.Delay(delayTime, cancellationToken);
                            }

                            // ReSharper disable once AccessToDisposedClosure
                            AttachRunningNodeChangedHandler(promptTask);
                        }
                        catch (TaskCanceledException) { }
                    },
                    cancellationToken
                )
                .SafeFireAndForget(ex =>
                {
                    if (ex is TaskCanceledException)
                        return;

                    Logger.Error(ex, "Error while attaching running node change handler");
                });

            // Wait for prompt to finish
            try
            {
                await promptTask.Task.WaitAsync(cancellationToken);
                Logger.Debug($"Prompt task {promptTask.Id} finished");
            }
            catch (ComfyNodeException e)
            {
                Logger.Warn(e, "Comfy node exception while queuing prompt");
                await DialogHelper
                    .CreateJsonDialog(e.JsonData, "Comfy Error", "Node execution encountered an error")
                    .ShowAsync();
                return;
            }

            // Get output images
            var imageOutputs = await client.GetImagesForExecutedPromptAsync(promptTask.Id, cancellationToken);

            if (imageOutputs.Values.All(images => images is null or { Count: 0 }))
            {
                // No images match
                notificationService.Show(
                    "No output",
                    "Did not receive any output images",
                    NotificationType.Warning
                );
                return;
            }

            if (args.Parameters?.AddVideoAudio == true)
            {
                args.AudioOutputs = await client.GetAudioForExecutedPromptAsync(
                    promptTask.Id,
                    cancellationToken
                );

                // Some Comfy builds put SaveAudio results under images — scavenge audio extensions.
                foreach (var (nodeKey, imgs) in imageOutputs)
                {
                    if (imgs is null || imgs.Count == 0)
                        continue;

                    var audioImgs = imgs.Where(i => IsAudioFileName(i.FileName)).ToList();
                    if (audioImgs.Count == 0)
                        continue;

                    args.AudioOutputs ??= new Dictionary<string, List<ComfyImage>?>();
                    if (
                        args.AudioOutputs.TryGetValue(nodeKey, out var existing) && existing is { Count: > 0 }
                    )
                    {
                        existing.AddRange(audioImgs);
                    }
                    else
                    {
                        args.AudioOutputs[nodeKey] = audioImgs;
                    }
                }
            }

            // Disable cancellation
            await promptInterrupt.DisposeAsync();

            if (args.ClearOutputImages)
            {
                ImageGalleryCardViewModel.ImageSources.Clear();
            }

            var outputImages = await ProcessAllOutputImages(imageOutputs, args);

            var notificationImage = outputImages.FirstOrDefault()?.LocalFile;

            await notificationService.ShowAsync(
                NotificationKey.Inference_PromptCompleted,
                new Notification
                {
                    Title = "Prompt Completed",
                    Body = $"Prompt [{promptTask.Id[..7].ToLower()}] completed successfully",
                    BodyImagePath = notificationImage?.FullPath,
                },
                action: new NavigateToPageAction(typeof(InferenceViewModel).AssemblyQualifiedName!)
            );
        }
        finally
        {
            // Disconnect progress handler
            client.PreviewImageReceived -= OnPreviewImageReceived;

            // Clear progress
            OutputProgress.ClearProgress();
            // ImageGalleryCardViewModel.PreviewImage?.Dispose();
            ImageGalleryCardViewModel.PreviewImage = null;
            ImageGalleryCardViewModel.IsPreviewOverlayEnabled = false;

            // Cleanup tasks
            promptTask?.Dispose();
        }
    }

    private async Task<IEnumerable<ImageSource>> ProcessAllOutputImages(
        IReadOnlyDictionary<string, List<ComfyImage>?> images,
        ImageGenerationEventArgs args
    )
    {
        if (ShouldEncodeWithFfmpeg(args.Parameters))
        {
            var frames = images
                .Values.Where(v => v is { Count: > 0 })
                .SelectMany(v => v!)
                .Where(img => !IsAudioFileName(img.FileName))
                .ToList();

            var audio = (args.AudioOutputs?.Values ?? Enumerable.Empty<List<ComfyImage>?>())
                .Where(v => v is { Count: > 0 })
                .SelectMany(v => v!)
                .ToList();

            if (frames.Count == 0 && audio.Count > 0)
                return await ProcessAudioOnlyOutputAsync(audio, args);

            return await ProcessFfmpegVideoOutputAsync(frames, audio, args, imageLabel: null);
        }

        var results = new List<ImageSource>();

        foreach (var (nodeName, imageList) in images)
        {
            if (imageList is null)
            {
                Logger.Warn("No images for node {NodeName}", nodeName);
                continue;
            }

            results.AddRange(await ProcessOutputImages(imageList, args, nodeName.Replace('_', ' ')));
        }

        return results;
    }

    /// <summary>
    /// Handles image output metadata for generation runs
    /// </summary>
    private async Task<List<ImageSource>> ProcessOutputImages(
        IReadOnlyCollection<ComfyImage> images,
        ImageGenerationEventArgs args,
        string? imageLabel = null
    )
    {
        var client = args.Client;

        // Write metadata to images
        var outputImagesBytes = new List<byte[]>();
        var outputImages = new List<ImageSource>();

        foreach (var (i, comfyImage) in images.Enumerate())
        {
            Logger.Debug("Downloading image: {FileName}", comfyImage.FileName);
            var imageStream = await client.GetImageStreamAsync(comfyImage);

            using var ms = new MemoryStream();
            await imageStream.CopyToAsync(ms);

            var imageArray = ms.ToArray();
            outputImagesBytes.Add(imageArray);

            var parameters = args.Parameters!;
            var project = args.Project!;

            // Lock seed
            project.TryUpdateModel<SeedCardModel>("Seed", model => model with { IsRandomizeEnabled = false });

            // Seed and batch override for batches
            if (images.Count > 1 && project.ProjectType is InferenceProjectType.TextToImage)
            {
                project = (InferenceProjectDocument)project.Clone();

                // Set batch size indexes
                project.TryUpdateModel(
                    "BatchSize",
                    node =>
                    {
                        node[nameof(BatchSizeCardViewModel.BatchCount)] = 1;
                        node[nameof(BatchSizeCardViewModel.IsBatchIndexEnabled)] = true;
                        node[nameof(BatchSizeCardViewModel.BatchIndex)] = i + 1;
                        return node;
                    }
                );
            }

            if (comfyImage.FileName.EndsWith(".png"))
            {
                var bytesWithMetadata = PngDataHelper.AddMetadata(imageArray, parameters, project);

                // Write using generated name
                var filePath = await WriteOutputImageAsync(
                    new MemoryStream(bytesWithMetadata),
                    args,
                    i + 1,
                    images.Count
                );

                outputImages.Add(new ImageSource(filePath) { Label = imageLabel });
                EventManager.Instance.OnImageFileAdded(filePath);
            }
            else if (comfyImage.FileName.EndsWith(".webp"))
            {
                var opts = new JsonSerializerOptions
                {
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                    Converters = { new JsonStringEnumConverter() },
                };
                var paramsJson = JsonSerializer.Serialize(parameters, opts);
                var smProject = JsonSerializer.Serialize(project, opts);
                var metadata = new Dictionary<ExifTag, string>
                {
                    { ExifTag.ImageDescription, paramsJson },
                    { ExifTag.Software, smProject },
                };

                var bytesWithMetadata = ImageMetadata.AddMetadataToWebp(imageArray, metadata);

                // Write using generated name
                var filePath = await WriteOutputImageAsync(
                    new MemoryStream(bytesWithMetadata.ToArray()),
                    args,
                    i + 1,
                    images.Count,
                    fileExtension: Path.GetExtension(comfyImage.FileName).Replace(".", "")
                );

                outputImages.Add(new ImageSource(filePath) { Label = imageLabel });
                EventManager.Instance.OnImageFileAdded(filePath);
            }
            else
            {
                // Write using generated name
                var filePath = await WriteOutputImageAsync(
                    new MemoryStream(imageArray),
                    args,
                    i + 1,
                    images.Count,
                    fileExtension: Path.GetExtension(comfyImage.FileName).Replace(".", "")
                );

                var imageSource = new ImageSource(filePath) { Label = imageLabel };

                if (IsVideoFileName(comfyImage.FileName))
                {
                    await TryAttachVideoThumbnailAsync(imageSource);
                }

                outputImages.Add(imageSource);
                EventManager.Instance.OnImageFileAdded(filePath);
            }
        }

        // Download all images to make grid, if multiple (skip for video outputs)
        if (outputImages.Count > 1 && !images.Any(img => IsVideoFileName(img.FileName)))
        {
            var loadedImages = outputImagesBytes.Select(SKImage.FromEncodedData).ToImmutableArray();

            var project = args.Project!;

            // Lock seed
            project.TryUpdateModel<SeedCardModel>("Seed", model => model with { IsRandomizeEnabled = false });

            var grid = ImageProcessor.CreateImageGrid(loadedImages);
            var gridBytes = grid.Encode().ToArray();
            var gridBytesWithMetadata = PngDataHelper.AddMetadata(gridBytes, args.Parameters!, args.Project!);

            // Save to disk
            var gridPath = await WriteOutputImageAsync(
                new MemoryStream(gridBytesWithMetadata),
                args,
                isGrid: true
            );

            // Insert to start of images
            var gridImage = new ImageSource(gridPath);
            outputImages.Insert(0, gridImage);
            EventManager.Instance.OnImageFileAdded(gridPath);
        }

        foreach (var img in outputImages)
        {
            // Preload
            await img.GetOrRefreshTemplateKeyAsync();
            if (img.TemplateKey is not ImageSourceTemplateType.Video)
            {
                await img.GetBitmapAsync();
            }
            // Add images
            ImageGalleryCardViewModel.ImageSources.Add(img);
        }

        return outputImages;
    }

    private static bool ShouldEncodeWithFfmpeg(GenerationParameters? parameters)
    {
        if (parameters is null)
            return false;

        if (parameters.AddVideoAudio)
            return true;

        if (parameters.VideoOutputMethod is not { } method)
            return false;

        return method.Equals("FfmpegMp4", StringComparison.OrdinalIgnoreCase)
            || method.Equals("Mp4", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<List<ImageSource>> ProcessAudioOnlyOutputAsync(
        IReadOnlyCollection<ComfyImage> audioFiles,
        ImageGenerationEventArgs args
    )
    {
        var client = args.Client;
        var outputDir = settingsManager.ImagesInferenceDirectory;
        Directory.CreateDirectory(outputDir);
        var results = new List<ImageSource>();

        foreach (var audioFile in audioFiles)
        {
            await using var stream = await client.GetImageStreamAsync(audioFile);
            var ext = Path.GetExtension(audioFile.FileName);
            if (string.IsNullOrWhiteSpace(ext))
                ext = ".flac";
            var dest = Path.Combine(
                outputDir,
                $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}-ltx-audio-{Guid.NewGuid():N}{ext}"
            );
            await using (var fs = File.Create(dest))
                await stream.CopyToAsync(fs);

            var src = new ImageSource(dest) { Label = "Audio", HasAudio = true };
            results.Add(src);
            ImageGalleryCardViewModel.ImageSources.Add(src);
        }

        return results;
    }

    private async Task<List<ImageSource>> ProcessFfmpegVideoOutputAsync(
        IReadOnlyCollection<ComfyImage> images,
        IReadOnlyCollection<ComfyImage> audioFiles,
        ImageGenerationEventArgs args,
        string? imageLabel
    )
    {
        var client = args.Client;
        var parameters = args.Parameters!;
        var project = args.Project!;
        project.TryUpdateModel<SeedCardModel>("Seed", model => model with { IsRandomizeEnabled = false });

        var frameDir = Path.Combine(Path.GetTempPath(), $"sm-frames-{Guid.NewGuid():N}");
        Directory.CreateDirectory(frameDir);
        var framePaths = new List<string>();
        string? audioPath = null;

        try
        {
            foreach (var (i, comfyImage) in images.Enumerate())
            {
                Logger.Debug("Downloading video frame: {FileName}", comfyImage.FileName);
                await using var imageStream = await client.GetImageStreamAsync(comfyImage);
                var ext = Path.GetExtension(comfyImage.FileName);
                if (string.IsNullOrWhiteSpace(ext))
                    ext = ".png";

                var framePath = Path.Combine(frameDir, $"frame_{i + 1:D5}{ext}");
                await using (var fs = File.Create(framePath))
                {
                    await imageStream.CopyToAsync(fs);
                }

                framePaths.Add(framePath);
            }

            if (audioFiles.Count > 0)
            {
                var audioFile = audioFiles.First();
                Logger.Debug("Downloading video audio: {FileName}", audioFile.FileName);
                await using var audioStream = await client.GetImageStreamAsync(audioFile);
                var audioExt = Path.GetExtension(audioFile.FileName);
                if (string.IsNullOrWhiteSpace(audioExt))
                    audioExt = ".flac";
                audioPath = Path.Combine(frameDir, $"audio{audioExt}");
                await using (var fs = File.Create(audioPath))
                {
                    await audioStream.CopyToAsync(fs);
                }
            }
            else if (parameters.AddVideoAudio)
            {
                var sourceHint =
                    parameters.VideoAudioSource?.Equals("Ltx", StringComparison.OrdinalIgnoreCase) == true
                        ? "LTX Audio VAE (LTX23_audio_vae_bf16.safetensors in Checkpoints)"
                        : "ComfyUI-MMAudio + models/mmaudio";
                notificationService.Show(
                    "Add Audio",
                    $"Add Audio was enabled but no audio was returned. Check {sourceHint}.",
                    NotificationType.Warning
                );
            }

            var encoder =
                App.Services?.GetService(typeof(IFfmpegVideoEncoder)) as IFfmpegVideoEncoder
                ?? throw new InvalidOperationException("IFfmpegVideoEncoder not registered");

            var stagingBase = Path.Combine(frameDir, "encoded");
            var encodedPath = await encoder.EncodeFramesAsync(
                framePaths,
                stagingBase,
                parameters.OutputFps > 0 ? parameters.OutputFps : 24,
                parameters.Lossless,
                parameters.VideoQuality > 0 ? parameters.VideoQuality : 90
            );

            if (encodedPath is null || !File.Exists(encodedPath))
            {
                notificationService.Show(
                    "FFmpeg encode failed",
                    "Could not encode video with FFmpeg/NVENC. Falling back to frame images.",
                    NotificationType.Warning
                );

                var fallbackArgs = new ImageGenerationEventArgs
                {
                    Client = args.Client,
                    Nodes = args.Nodes,
                    OutputNodeNames = args.OutputNodeNames,
                    BatchIndex = args.BatchIndex,
                    Parameters = parameters with { VideoOutputMethod = "Webp", AddVideoAudio = false },
                    Project = args.Project,
                    ClearOutputImages = false,
                    FilesToTransfer = args.FilesToTransfer,
                };
                return await ProcessOutputImages(images, fallbackArgs, imageLabel);
            }

            var hasAudioTrack = false;
            if (audioPath is not null)
            {
                var muxedPath = Path.Combine(frameDir, "muxed.mp4");
                var muxed = await encoder.MuxAudioAsync(encodedPath, audioPath, muxedPath);
                if (muxed is not null)
                {
                    encodedPath = muxed;
                    hasAudioTrack = true;
                }
                else
                {
                    notificationService.Show(
                        "Audio mux failed",
                        "Video saved without audio track.",
                        NotificationType.Warning
                    );
                }
            }

            await using var encodedStream = File.OpenRead(encodedPath);
            var extOut = Path.GetExtension(encodedPath).TrimStart('.');
            var filePath = await WriteOutputImageAsync(encodedStream, args, fileExtension: extOut);

            var imageSource = new ImageSource(filePath) { Label = imageLabel, HasAudio = hasAudioTrack };
            if (IsVideoFileName(filePath.Name))
            {
                await TryAttachVideoThumbnailAsync(imageSource);

                // Fallback still: use first encoded frame so gallery isn't blank if FFmpeg thumbs fail
                if (imageSource.Bitmap is null && framePaths.Count > 0 && File.Exists(framePaths[0]))
                {
                    try
                    {
                        var videoDir = Path.GetDirectoryName(filePath.FullPath);
                        if (!string.IsNullOrEmpty(videoDir))
                        {
                            var thumbsDir = Path.Combine(videoDir, ".sm-thumbs");
                            Directory.CreateDirectory(thumbsDir);
                            var fallbackThumb = Path.Combine(
                                thumbsDir,
                                Path.GetFileNameWithoutExtension(filePath.Name)
                                    + "_frame"
                                    + Path.GetExtension(framePaths[0])
                            );
                            File.Copy(framePaths[0], fallbackThumb, overwrite: true);
                            imageSource.ThumbnailFile = new FilePath(fallbackThumb);
                        }

                        await using var frameStream = File.OpenRead(framePaths[0]);
                        imageSource.Bitmap = new global::Avalonia.Media.Imaging.Bitmap(frameStream);
                    }
                    catch (Exception e)
                    {
                        Logger.Warn(e, "Failed to attach fallback frame thumbnail");
                    }
                }
            }
            else if (filePath.Extension.Equals(".webp", StringComparison.OrdinalIgnoreCase))
            {
                await imageSource.GetOrRefreshTemplateKeyAsync();
            }

            await imageSource.GetOrRefreshTemplateKeyAsync();
            if (imageSource.TemplateKey is not ImageSourceTemplateType.Video)
            {
                await imageSource.GetBitmapAsync();
            }

            ImageGalleryCardViewModel.ImageSources.Add(imageSource);
            EventManager.Instance.OnImageFileAdded(filePath);

            if (parameters is not null)
            {
                try
                {
                    await VideoSidecarMetadata.WriteAsync(filePath, parameters, args.Project);
                }
                catch (Exception e)
                {
                    Logger.Warn(e, "Failed to write video sidecar metadata");
                }
            }

            await TryDeleteComfyTempOutputsAsync(client, images, audioFiles);

            return [imageSource];
        }
        finally
        {
            try
            {
                Directory.Delete(frameDir, recursive: true);
            }
            catch
            {
                // ignore
            }
        }
    }

    private static async Task TryDeleteComfyTempOutputsAsync(
        ComfyClient client,
        IReadOnlyCollection<ComfyImage> frames,
        IReadOnlyCollection<ComfyImage> audioFiles
    )
    {
        if (client.OutputImagesDir is not { } outputDir)
            return;

        foreach (var file in frames.Concat(audioFiles))
        {
            try
            {
                if (!IsComfyTempVideoOutput(file.FileName))
                    continue;

                var path = file.ToFilePath(outputDir);
                if (path.Exists)
                    await path.DeleteAsync();
            }
            catch (Exception e)
            {
                Logger.Warn(e, "Failed to delete temp Comfy video output {File}", file.FileName);
            }
        }
    }

    private static bool IsComfyTempVideoOutput(string fileName)
    {
        return fileName.StartsWith("InferenceVideoFrames", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("InferenceVideoAudio", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("sm_vid_frames", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("sm_vid_audio", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAudioFileName(string fileName) =>
        fileName.EndsWith(".flac", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".opus", StringComparison.OrdinalIgnoreCase);

    private static bool IsVideoFileName(string fileName) =>
        fileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".avi", StringComparison.OrdinalIgnoreCase);

    private static async Task TryAttachVideoThumbnailAsync(ImageSource imageSource)
    {
        if (imageSource.LocalFile?.FullPath is not { } videoPath || App.Services is null)
            return;

        try
        {
            var thumbnailService =
                App.Services.GetService(typeof(IVideoThumbnailService)) as IVideoThumbnailService;
            if (thumbnailService is null)
                return;

            var thumbPath = await thumbnailService.GetOrCreateThumbnailAsync(videoPath);
            if (thumbPath is not null && File.Exists(thumbPath))
            {
                imageSource.ThumbnailFile = new FilePath(thumbPath);
                await using var stream = File.OpenRead(thumbPath);
                imageSource.Bitmap = new global::Avalonia.Media.Imaging.Bitmap(stream);
            }

            var previewPath = await thumbnailService.GetOrCreateAnimatedPreviewAsync(videoPath);
            if (previewPath is not null && File.Exists(previewPath))
            {
                imageSource.PlaybackFile = new FilePath(previewPath);
            }

            if (!imageSource.HasAudio)
            {
                imageSource.HasAudio = await thumbnailService.HasAudioStreamAsync(videoPath);
            }
        }
        catch (Exception e)
        {
            Logger.Warn(e, "Failed to create video thumbnail for {Path}", videoPath);
        }
    }

    /// <summary>
    /// Implementation for Generate Image
    /// </summary>
    protected virtual Task GenerateImageImpl(GenerateOverrides overrides, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Command for the Generate Image button
    /// </summary>
    /// <param name="options">Optional overrides (side buttons)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    [RelayCommand(IncludeCancelCommand = true, FlowExceptionsToTaskScheduler = true)]
    private async Task GenerateImage(
        GenerateFlags options = default,
        CancellationToken cancellationToken = default
    )
    {
        var overrides = GenerateOverrides.FromFlags(options);

        try
        {
            await GenerateImageImpl(overrides, cancellationToken);
            await DrainGenerationQueueAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Logger.Debug("Image Generation Canceled");
        }
        catch (ValidationException e)
        {
            Logger.Debug("Image Generation Validation Error: {Message}", e.Message);
            notificationService.Show("Validation Error", e.Message, NotificationType.Error);
        }
    }

    /// <summary>
    /// Snapshot the current tab settings into the generation queue.
    /// If nothing is generating, starts processing the queue immediately.
    /// </summary>
    [RelayCommand]
    private async Task EnqueueGeneration(GenerateFlags options = default)
    {
        var item = CreateQueueItemFromCurrentState(options);
        GenerationQueue.Add(item);

        notificationService.Show(
            Resources.Label_GenerationQueue,
            string.Format(Resources.Label_GenerationQueuedCount, GenerationQueue.Count),
            NotificationType.Success
        );

        if (GenerateImageCommand.IsRunning || IsProcessingGenerationQueue)
        {
            return;
        }

        queueDrainCts = new CancellationTokenSource();
        try
        {
            await DrainGenerationQueueAsync(queueDrainCts.Token);
        }
        catch (OperationCanceledException)
        {
            Logger.Debug("Queued generation canceled");
        }
        catch (ValidationException e)
        {
            notificationService.Show("Validation Error", e.Message, NotificationType.Error);
        }
        finally
        {
            queueDrainCts.Dispose();
            queueDrainCts = null;
        }
    }

    /// <summary>
    /// Cancels the current generation and any queue drain started from enqueue.
    /// </summary>
    [RelayCommand]
    private void CancelGenerationPipeline()
    {
        if (GenerateImageCancelCommand.CanExecute(null))
        {
            GenerateImageCancelCommand.Execute(null);
        }

        queueDrainCts?.Cancel();
    }

    [RelayCommand]
    private async Task ShowGenerationQueue()
    {
        var vm = vmFactory.Get<InferenceQueueDialogViewModel>();
        vm.Attach(this);
        await vm.GetDialog().ShowAsync();
    }

    [RelayCommand]
    private void RemoveQueuedGeneration(InferenceQueueItem? item)
    {
        if (item is null)
        {
            return;
        }

        GenerationQueue.Remove(item);
    }

    [RelayCommand]
    private void ClearGenerationQueue()
    {
        GenerationQueue.Clear();
    }

    [RelayCommand]
    private void MoveQueuedGenerationUp(InferenceQueueItem? item)
    {
        if (item is null)
        {
            return;
        }

        var index = GenerationQueue.IndexOf(item);
        if (index <= 0)
        {
            return;
        }

        GenerationQueue.Move(index, index - 1);
    }

    [RelayCommand]
    private void MoveQueuedGenerationDown(InferenceQueueItem? item)
    {
        if (item is null)
        {
            return;
        }

        var index = GenerationQueue.IndexOf(item);
        if (index < 0 || index >= GenerationQueue.Count - 1)
        {
            return;
        }

        GenerationQueue.Move(index, index + 1);
    }

    internal InferenceQueueItem CreateQueueItemFromCurrentState(GenerateFlags options = default)
    {
        var project = InferenceProjectDocument.FromLoadable(this);
        ApplySeedForQueueSnapshot(project, options);
        TryGetPromptPreviews(project, out var prompt, out var negative);

        return new InferenceQueueItem
        {
            Project = project,
            Flags = options | GenerateFlags.UseCurrentSeed,
            PromptPreview = InferenceQueueItem.MakePreview(prompt),
            NegativePromptPreview = string.IsNullOrWhiteSpace(negative)
                ? null
                : InferenceQueueItem.MakePreview(negative, 80),
        };
    }

    internal static void TryGetPromptPreviews(
        InferenceProjectDocument project,
        out string? prompt,
        out string? negativePrompt
    )
    {
        prompt = null;
        negativePrompt = null;

        if (project.State is null)
        {
            return;
        }

        if (
            project.State.TryGetPropertyValue("Prompt", out var promptNode)
            && promptNode is JsonObject promptObj
        )
        {
            prompt = promptObj["Prompt"]?.GetValue<string>();
            negativePrompt = promptObj["NegativePrompt"]?.GetValue<string>();
        }
    }

    internal static void UpdateQueueItemPrompts(
        InferenceQueueItem item,
        string prompt,
        string? negativePrompt
    )
    {
        if (item.Project.State is null)
        {
            return;
        }

        item.Project.TryUpdateModel(
            "Prompt",
            node =>
            {
                var obj = node as JsonObject ?? new JsonObject();
                obj["Prompt"] = prompt;
                obj["NegativePrompt"] = negativePrompt ?? string.Empty;
                return obj;
            }
        );

        item.PromptPreview = InferenceQueueItem.MakePreview(prompt);
        item.NegativePromptPreview = string.IsNullOrWhiteSpace(negativePrompt)
            ? null
            : InferenceQueueItem.MakePreview(negativePrompt, 80);
    }

    private static void ApplySeedForQueueSnapshot(InferenceProjectDocument project, GenerateFlags options)
    {
        if (options.HasFlag(GenerateFlags.UseCurrentSeed) || project.State is null)
        {
            return;
        }

        project.TryUpdateModel<SeedCardModel>(
            "Seed",
            model =>
            {
                if (!model.IsRandomizeEnabled && !options.HasFlag(GenerateFlags.UseRandomSeed))
                {
                    return model;
                }

                return model with
                {
                    Seed = Random.Shared.NextInt64(0, int.MaxValue),
                };
            }
        );
    }

    private async Task DrainGenerationQueueAsync(CancellationToken cancellationToken)
    {
        if (IsProcessingGenerationQueue || GenerationQueue.Count == 0)
        {
            return;
        }

        IsProcessingGenerationQueue = true;

        try
        {
            while (GenerationQueue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var next = GenerationQueue[0];
                GenerationQueue.RemoveAt(0);

                try
                {
                    await RunQueuedGenerationAsync(next, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (ValidationException e)
                {
                    Logger.Debug(e, "Queued generation validation failed");
                    notificationService.Show(
                        Resources.Label_GenerationQueue,
                        e.Message,
                        NotificationType.Error
                    );
                }
                catch (Exception e)
                {
                    Logger.Error(e, "Queued generation failed");
                    notificationService.Show(
                        Resources.Label_GenerationQueue,
                        $"{e.GetType().Name}: {e.Message}",
                        NotificationType.Error
                    );
                }
            }
        }
        finally
        {
            IsProcessingGenerationQueue = false;
        }
    }

    private async Task RunQueuedGenerationAsync(InferenceQueueItem item, CancellationToken cancellationToken)
    {
        if (item.Project.State is null)
        {
            throw new ValidationException("Queued project has no state");
        }

        var previousState = SaveStateToJsonObject();

        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => LoadStateFromJsonObject(item.Project.State));

            var overrides = GenerateOverrides.FromFlags(item.Flags | GenerateFlags.UseCurrentSeed);
            await GenerateImageImpl(overrides, cancellationToken);
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => LoadStateFromJsonObject(previousState));
        }
    }

    /// <summary>
    /// Shows a prompt and return false if client not connected
    /// </summary>
    protected async Task<bool> CheckClientConnectedWithPrompt(CancellationToken cancellationToken = default)
    {
        if (ClientManager.IsConnected)
            return true;

        var vm = vmFactory.Get<InferenceConnectionHelpViewModel>();
        var result = await vm.CreateDialog().ShowAsync();

        if (ClientManager.IsConnected)
            return true;

        // If the user chose to launch ComfyUI, the package is now starting up. The connection
        // is established automatically by InferenceViewModel once startup completes, so wait for
        // it here and let the generation resume instead of forcing the user to press Generate again.
        if (result == ContentDialogResult.Primary && vm.IsLaunchMode)
        {
            return await WaitForConnectedAsync(cancellationToken);
        }

        return ClientManager.IsConnected;
    }

    /// <summary>
    /// Waits for the ClientManager to become connected, showing indeterminate progress.
    /// Used after launching ComfyUI from the connection prompt so a queued generation can
    /// resume automatically once the backend is ready. Stops waiting early if ComfyUI is
    /// shut down or crashes before connecting (it is removed from RunningPackages either way).
    /// </summary>
    protected async Task<bool> WaitForConnectedAsync(CancellationToken cancellationToken)
    {
        if (ClientManager.IsConnected)
            return true;

        // RunContinuationsAsynchronously so the await resumption (and UI updates in finally)
        // don't run synchronously on the thread that raised the completing event.
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        bool IsAnyComfyRunning() =>
            runningPackageService.RunningPackages.Values.Any(vm => vm.RunningPackage.BasePackage is ComfyUI);

        void OnPropertyChanged(object? sender, PropertyChangedEventArgs args)
        {
            // null/empty PropertyName means "all properties changed" per INotifyPropertyChanged
            if (
                args.PropertyName is nameof(ClientManager.IsConnected) or null or ""
                && ClientManager.IsConnected
            )
            {
                tcs.TrySetResult();
            }
        }

        void OnRunningPackagesChanged(object? sender, NotifyCollectionChangedEventArgs args)
        {
            // ComfyUI was shut down or crashed before connecting - stop waiting
            if (!IsAnyComfyRunning())
            {
                tcs.TrySetResult();
            }
        }

        ClientManager.PropertyChanged += OnPropertyChanged;
        runningPackageService.RunningPackages.CollectionChanged += OnRunningPackagesChanged;
        try
        {
            // Re-check in case it connected, or the package already stopped, between the
            // initial checks and subscribing
            if (ClientManager.IsConnected)
                return true;
            if (!IsAnyComfyRunning())
                return false;

            // Give up waiting after a generous timeout in case startup never completes
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));

            OutputProgress.IsIndeterminate = true;
            OutputProgress.Text = "Waiting for ComfyUI to start...";

            await using (timeoutCts.Token.Register(() => tcs.TrySetCanceled(timeoutCts.Token)))
            {
                await tcs.Task;
            }

            return ClientManager.IsConnected;
        }
        catch (OperationCanceledException)
        {
            return ClientManager.IsConnected;
        }
        finally
        {
            ClientManager.PropertyChanged -= OnPropertyChanged;
            runningPackageService.RunningPackages.CollectionChanged -= OnRunningPackagesChanged;
            OutputProgress.ClearProgress();
        }
    }

    /// <summary>
    /// Shows a dialog and return false if prompt required extensions not installed
    /// </summary>
    protected async Task<bool> CheckPromptExtensionsInstalled(NodeDictionary nodeDictionary)
    {
        // Get prompt required extensions
        // Just static for now but could do manifest lookup when we support custom workflows
        var requiredExtensionSpecifiers = nodeDictionary
            .RequiredExtensions.DistinctBy(ext => ext.Name)
            .ToList();

        // Skip if no extensions required
        if (requiredExtensionSpecifiers.Count == 0)
        {
            return true;
        }

        // Get installed extensions
        var localPackagePair = ClientManager.Client?.LocalServerPackage.Unwrap()!;
        var manager = localPackagePair.BasePackage.ExtensionManager.Unwrap();

        var localExtensions = (
            await ((GitPackageExtensionManager)manager).GetInstalledExtensionsLiteAsync(
                localPackagePair.InstalledPackage
            )
        ).ToList();

        // Normalize .git suffix — git remotes often end with .git, required URLs usually do not
        var localExtensionsByGitUrl = localExtensions
            .Where(ext => ext.GitRepositoryUrl is not null)
            .GroupBy(
                ext => ext.GitRepositoryUrl!.StripEnd(".git").TrimEnd('/'),
                StringComparer.OrdinalIgnoreCase
            )
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var requiredExtensionReferences = requiredExtensionSpecifiers
            .Select(specifier => specifier.Name)
            .ToHashSet();

        var missingExtensions = new List<ExtensionSpecifier>();
        var outOfDateExtensions =
            new List<(ExtensionSpecifier Specifier, InstalledPackageExtension Installed)>();

        // Check missing extensions and out of date extensions
        foreach (var specifier in requiredExtensionSpecifiers)
        {
            var specifierKey = specifier.Name.StripEnd(".git").TrimEnd('/');
            if (!localExtensionsByGitUrl.TryGetValue(specifierKey, out var localExtension))
            {
                missingExtensions.Add(specifier);
                continue;
            }

            // Check if constraint is specified
            if (specifier.Constraint is not null && specifier.TryGetSemVersionRange(out var semVersionRange))
            {
                // Get version to compare
                localExtension = await manager.GetInstalledExtensionInfoAsync(localExtension);

                // Try to parse local tag to semver
                if (
                    localExtension.Version?.Tag is not null
                    && SemVersion.TryParse(
                        localExtension.Version.Tag,
                        SemVersionStyles.AllowV,
                        out var localSemVersion
                    )
                )
                {
                    // Check if not satisfied
                    if (!semVersionRange.Contains(localSemVersion))
                    {
                        outOfDateExtensions.Add((specifier, localExtension));
                    }
                }
            }
        }

        if (missingExtensions.Count == 0 && outOfDateExtensions.Count == 0)
        {
            return true;
        }

        await ComfyExtensionInstallHelper.PromptInstallAndRestartAsync(
            manager,
            localPackagePair,
            missingExtensions,
            outOfDateExtensions,
            runningPackageService,
            notificationService
        );

        return false;
    }

    /// <summary>
    /// Handles the preview image received event from the websocket.
    /// Updates the preview image in the image gallery.
    /// </summary>
    protected virtual void OnPreviewImageReceived(object? sender, ComfyWebSocketImageData args)
    {
        ImageGalleryCardViewModel.SetPreviewImage(args.ImageBytes);
    }

    /// <summary>
    /// Handles the progress update received event from the websocket.
    /// Updates the progress view model.
    /// </summary>
    protected virtual void OnProgressUpdateReceived(object? sender, ComfyProgressUpdateEventArgs args)
    {
        Dispatcher.UIThread.Post(() =>
        {
            OutputProgress.Value = args.Value;
            OutputProgress.Maximum = args.Maximum;
            OutputProgress.IsIndeterminate = false;

            OutputProgress.Text =
                $"({args.Value} / {args.Maximum})" + (args.RunningNode != null ? $" {args.RunningNode}" : "");
        });
    }

    private void AttachRunningNodeChangedHandler(ComfyTask comfyTask)
    {
        // Do initial update
        if (comfyTask.RunningNodesHistory.TryPeek(out var lastNode))
        {
            OnRunningNodeChanged(comfyTask, lastNode);
        }

        comfyTask.RunningNodeChanged += OnRunningNodeChanged;
    }

    /// <summary>
    /// Handles the node executing updates received event from the websocket.
    /// </summary>
    protected virtual void OnRunningNodeChanged(object? sender, string? nodeName)
    {
        var task = sender as ComfyTask;
        if (task == null)
        {
            return;
        }

        // Ignore if regular progress updates started, unless the running node is different from the one reporting progress
        if (task.HasProgressUpdateStarted && task.LastProgressUpdate?.RunningNode == nodeName)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            OutputProgress.IsIndeterminate = true;
            OutputProgress.Value = 100;
            OutputProgress.Maximum = 100;
            OutputProgress.Text = nodeName;
        });
    }

    public class ImageGenerationEventArgs : EventArgs
    {
        public required ComfyClient Client { get; init; }
        public required NodeDictionary Nodes { get; init; }
        public required IReadOnlyList<string> OutputNodeNames { get; init; }
        public int BatchIndex { get; init; }
        public GenerationParameters? Parameters { get; init; }
        public InferenceProjectDocument? Project { get; init; }
        public bool ClearOutputImages { get; init; } = true;
        public List<(string SourcePath, string DestinationRelativePath)> FilesToTransfer { get; init; } = [];
        public Dictionary<string, List<ComfyImage>?>? AudioOutputs { get; set; }
    }

    public class BuildPromptEventArgs : EventArgs
    {
        public ComfyNodeBuilder Builder { get; } = new();
        public GenerateOverrides Overrides { get; init; } = new();
        public long? SeedOverride { get; init; }
        public List<(string SourcePath, string DestinationRelativePath)> FilesToTransfer { get; init; } = [];

        public ModuleApplyStepEventArgs ToModuleApplyStepEventArgs()
        {
            var overrides = new Dictionary<Type, bool>();

            if (Overrides.IsHiresFixEnabled.HasValue)
            {
                overrides[typeof(HiresFixModule)] = Overrides.IsHiresFixEnabled.Value;
            }

            return new ModuleApplyStepEventArgs
            {
                Builder = Builder,
                IsEnabledOverrides = overrides,
                FilesToTransfer = FilesToTransfer,
            };
        }

        public static implicit operator ModuleApplyStepEventArgs(BuildPromptEventArgs args)
        {
            return args.ToModuleApplyStepEventArgs();
        }
    }
}
