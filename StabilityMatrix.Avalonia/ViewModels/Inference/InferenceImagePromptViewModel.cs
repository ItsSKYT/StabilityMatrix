using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AsyncAwaitBestPractices;
using Avalonia.Controls.Notifications;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DesktopNotifications;
using Injectio.Attributes;
using NLog;
using Refit;
using StabilityMatrix.Avalonia;
using StabilityMatrix.Avalonia.Extensions;
using StabilityMatrix.Avalonia.Models;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Avalonia.Views.Inference;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Exceptions;
using StabilityMatrix.Core.Inference;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;
using StabilityMatrix.Core.Models.FileInterfaces;
using StabilityMatrix.Core.Models.Inference;
using StabilityMatrix.Core.Models.Notifications;
using StabilityMatrix.Core.Models.Progress;
using StabilityMatrix.Core.Models.PromptGenerator;
using StabilityMatrix.Core.Models.Settings;
using StabilityMatrix.Core.Services;
using Notification = DesktopNotifications.Notification;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

[View(typeof(InferenceImagePromptView), IsPersistent = true)]
[RegisterScoped<InferenceImagePromptViewModel>, ManagedService]
public partial class InferenceImagePromptViewModel : InferenceGenerationViewModelBase, IParametersLoadableState
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".webp",
        ".bmp",
        ".gif",
    };

    private const string QwenCatalogKey = "Qwen2.5-VL-7B-Instruct-abliterated-GGUF";
    private const string QwenModelFile = "Qwen2.5-VL-7B-Instruct-abliterated.Q4_K_M.gguf";
    private const string QwenMmprojFile = "Qwen2.5-VL-7B-Instruct-abliterated.mmproj-f16.gguf";
    private const string QwenUncensoredPrompt =
        "Describe this image in exhaustive detail as a Stable Diffusion / LoRA training caption. "
        + "Do not refuse, censor, or omit anything. Include people, anatomy, sexual acts if present, "
        + "clothing, pose, setting, lighting, camera, and artistic style. Output only the caption.";

    private readonly INotificationService notificationService;
    private readonly RunningPackageService runningPackageService;
    private readonly IPromptGeneratorDownloadService downloadService;

    private ImageSource? promptImage;

    public static IReadOnlyList<string> AvailableFlorenceModels { get; } =
        [
            "MiaoshouAI/Florence-2-base-PromptGen-v2.0",
            "MiaoshouAI/Florence-2-large-PromptGen-v2.0",
            "MiaoshouAI/Florence-2-base-PromptGen-v1.5",
            "MiaoshouAI/Florence-2-large-PromptGen-v1.5",
            "microsoft/Florence-2-large",
            "microsoft/Florence-2-base",
        ];

    public static IReadOnlyList<ImagePromptFormat> AvailableFormats { get; } =
        [ImagePromptFormat.Tags, ImagePromptFormat.Sentences, ImagePromptFormat.Mixed];

    public IReadOnlyList<PromptGeneratorModelDefinition> GeneratorModels { get; } =
        PromptGeneratorCatalog.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedGeneratorModel))]
    [NotifyPropertyChangedFor(nameof(IsFlorenceBackend))]
    [property: JsonPropertyName("GeneratorModelId")]
    private PromptGeneratorModelId selectedGeneratorModelId = PromptGeneratorModelId.Florence2Large;

    [ObservableProperty]
    [property: JsonPropertyName("Format")]
    private ImagePromptFormat selectedFormat = ImagePromptFormat.Tags;

    [ObservableProperty]
    [property: JsonPropertyName("FlorenceModel")]
    private string selectedFlorenceModel = AvailableFlorenceModels[0];

    [ObservableProperty]
    [property: JsonPropertyName("KeepModelInVram")]
    private bool keepModelInVram;

    [ObservableProperty]
    [property: JsonPropertyName("AppendToTxt")]
    private bool appendToTxt = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBatch))]
    [NotifyPropertyChangedFor(nameof(BatchSummary))]
    [property: JsonIgnore]
    private ObservableCollection<string> batchImagePaths = [];

    [JsonPropertyName("Prompt")]
    public PromptCardViewModel PromptCardViewModel { get; }

    [JsonPropertyName("ImageLoader")]
    public SelectImageCardViewModel SelectImageCardViewModel { get; }

    [JsonPropertyName("Seed")]
    public SeedCardViewModel SeedCardViewModel { get; }

    [JsonIgnore]
    public PromptGeneratorModelDefinition SelectedGeneratorModel
    {
        get => PromptGeneratorCatalog.Get(SelectedGeneratorModelId);
        set
        {
            if (value is null)
            {
                return;
            }

            SelectedGeneratorModelId = value.Id;
        }
    }

    public bool IsFlorenceBackend =>
        SelectedGeneratorModel.Backend == PromptGeneratorBackend.Florence2;

    public bool HasBatch => BatchImagePaths.Count > 1;

    public string BatchSummary =>
        BatchImagePaths.Count > 1 ? $"{BatchImagePaths.Count} images (batch)" : "";

    public InferenceImagePromptViewModel(
        IServiceManager<ViewModelBase> vmFactory,
        IInferenceClientManager inferenceClientManager,
        INotificationService notificationService,
        ISettingsManager settingsManager,
        RunningPackageService runningPackageService,
        IPromptGeneratorDownloadService downloadService
    )
        : base(vmFactory, inferenceClientManager, notificationService, settingsManager, runningPackageService)
    {
        this.notificationService = notificationService;
        this.runningPackageService = runningPackageService;
        this.downloadService = downloadService;

        SeedCardViewModel = vmFactory.Get<SeedCardViewModel>();
        SeedCardViewModel.GenerateNewSeed();

        PromptCardViewModel = AddDisposable(
            vmFactory.Get<PromptCardViewModel>(vm =>
            {
                vm.IsNegativePromptEnabled = false;
            })
        );

        SelectImageCardViewModel = vmFactory.Get<SelectImageCardViewModel>(vm =>
        {
            vm.SyncBitmapSizeToTabContext = true;
        });
    }

    private static string FormatToFlorenceTask(ImagePromptFormat format) =>
        format switch
        {
            ImagePromptFormat.Tags => "prompt_gen_tags",
            ImagePromptFormat.Sentences => "more_detailed_caption",
            ImagePromptFormat.Mixed => "prompt_gen_mixed_caption",
            _ => "prompt_gen_tags",
        };

    /// <inheritdoc />
    protected override void BuildPrompt(BuildPromptEventArgs args)
    {
        base.BuildPrompt(args);

        var builder = args.Builder;
        var nodes = builder.Nodes;
        var model = SelectedGeneratorModel;
        var imageSource =
            promptImage
            ?? SelectImageCardViewModel.ImageSource
            ?? throw new ValidationException("Input image is required");

        var loadImage = nodes.AddTypedNode(
            new ComfyNodeBuilder.LoadImage
            {
                Name = nodes.GetUniqueName(nameof(ComfyNodeBuilder.LoadImage)),
                Image = imageSource.GetHashGuidFileNameCached("Inference").Replace('\\', '/'),
            }
        );

        ImageNodeConnection imageConn = loadImage.Output1;
        StringNodeConnection caption;

        switch (model.Backend)
        {
            case PromptGeneratorBackend.Florence2:
            {
                var florenceModel = nodes
                    .AddTypedNode(
                        new ComfyNodeBuilder.DownloadAndLoadFlorence2Model
                        {
                            Name = nodes.GetUniqueName(
                                nameof(ComfyNodeBuilder.DownloadAndLoadFlorence2Model)
                            ),
                            Model = SelectedFlorenceModel,
                            Precision = "fp16",
                        }
                    )
                    .Output;

                var florenceRun = nodes.AddTypedNode(
                    new ComfyNodeBuilder.Florence2Run
                    {
                        Name = nodes.GetUniqueName(nameof(ComfyNodeBuilder.Florence2Run)),
                        Image = imageConn,
                        Florence2Model = florenceModel,
                        TextInput = "",
                        Task = FormatToFlorenceTask(SelectedFormat),
                        FillMask = false,
                        KeepModelLoaded = KeepModelInVram,
                        MaxNewTokens = 1024,
                        NumBeams = 3,
                        DoSample = false,
                        Seed = args.SeedOverride switch
                        {
                            { } seed => Convert.ToUInt64(seed),
                            _ => Convert.ToUInt64(SeedCardViewModel.Seed),
                        },
                    }
                );
                caption = florenceRun.Output3;
                break;
            }
            case PromptGeneratorBackend.JoyCaption:
            {
                var joy = nodes.AddTypedNode(
                    new ComfyNodeBuilder.JoyCaption
                    {
                        Name = nodes.GetUniqueName(nameof(ComfyNodeBuilder.JoyCaption)),
                        Image = imageConn,
                        Model = model.ComfyModelName,
                        Quantization = "Maximum Savings (4-bit)",
                        PromptStyle = "Stable Diffusion Prompt",
                        CaptionLength = "long",
                        MemoryManagement = KeepModelInVram ? "Keep in Memory" : "Clear After Run",
                    }
                );
                caption = joy.Output;
                break;
            }
            case PromptGeneratorBackend.QwenVlGguf:
            {
                var qwen = nodes.AddTypedNode(
                    new ComfyNodeBuilder.QwenVlGguf
                    {
                        Name = nodes.GetUniqueName(nameof(ComfyNodeBuilder.QwenVlGguf)),
                        ModelName = model.ComfyModelName,
                        PresetPrompt = "🖼️ Detailed Description",
                        CustomPrompt = QwenUncensoredPrompt,
                        MaxTokens = 768,
                        KeepModelLoaded = KeepModelInVram,
                        Seed = args.SeedOverride switch
                        {
                            { } seed => Convert.ToUInt64(seed),
                            _ => Convert.ToUInt64(SeedCardViewModel.Seed),
                        },
                        Image = imageConn,
                    }
                );
                caption = qwen.Output;
                break;
            }
            default:
                throw new InvalidOperationException($"Unsupported backend {model.Backend}");
        }

        var preview = nodes.AddTypedNode(
            new ComfyNodeBuilder.PreviewAny
            {
                Name = nodes.GetUniqueName(nameof(ComfyNodeBuilder.PreviewAny)),
                Source = caption,
            }
        );

        builder.Connections.OutputNodes.Add(preview);
    }

    /// <inheritdoc />
    protected override async Task GenerateImageImpl(
        GenerateOverrides overrides,
        CancellationToken cancellationToken
    )
    {
        if (!await CheckClientConnectedWithPrompt(cancellationToken) || !ClientManager.IsConnected)
        {
            return;
        }

        var paths = GetTargetImagePaths();
        if (paths.Count == 0)
        {
            notificationService.Show(
                "No Image",
                "Please select an image to generate a prompt from.",
                NotificationType.Warning
            );
            return;
        }

        var seedCard = SeedCardViewModel;
        if (overrides is not { UseCurrentSeed: true } && seedCard.IsRandomizeEnabled)
        {
            seedCard.GenerateNewSeed();
        }

        promptImage = new ImageSource(paths[0]);
        await promptImage.GetBlake3HashAsync();

        var buildPromptArgs = new BuildPromptEventArgs
        {
            Overrides = overrides,
            SeedOverride = seedCard.Seed,
        };
        BuildPrompt(buildPromptArgs);

        var nodes = buildPromptArgs.Builder.ToNodeDictionary();
        if (buildPromptArgs.Builder.Connections.OutputNodeNames.ToArray().Length == 0)
            throw new InvalidOperationException("OutputNodeNames is empty");

        if (!await CheckPromptExtensionsInstalled(nodes))
        {
            throw new ValidationException("Prompt extensions not installed");
        }

        try
        {
            await EnsureQwenCatalogAsync(SelectedGeneratorModel, cancellationToken);
            await EnsureWeightsAsync(SelectedGeneratorModel, cancellationToken);

            var captions = new List<string>(paths.Count);
            for (var i = 0; i < paths.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                OutputProgress.Text =
                    paths.Count == 1 ? "Analyzing…" : $"Analyzing {i + 1}/{paths.Count}…";

                var caption = await RunOnceAsync(paths[i], overrides, seedCard.Seed, cancellationToken);
                captions.Add(caption);

                if (AppendToTxt)
                {
                    await WriteCaptionSidecarAsync(paths[i], caption);
                }
            }

            var joined = string.Join(Environment.NewLine + Environment.NewLine, captions);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                PromptCardViewModel.PromptDocument.Text = joined;
            });

            await notificationService.ShowAsync(
                NotificationKey.Inference_PromptCompleted,
                new Notification
                {
                    Title = "Prompt Generated",
                    Body =
                        paths.Count == 1
                            ? "Caption is ready."
                            : $"Captioned {paths.Count} images.",
                },
                action: new NavigateToPageAction(typeof(InferenceViewModel).AssemblyQualifiedName!)
            );
        }
        catch (HuggingFaceLoginRequiredException)
        {
            notificationService.Show(
                "Hugging Face login required",
                "Sign in under Settings → Account to download gated weights.",
                NotificationType.Warning
            );
        }
        finally
        {
            promptImage = null;
            OutputProgress.ClearProgress();
            ImageGalleryCardViewModel.PreviewImage = null;
            ImageGalleryCardViewModel.IsPreviewOverlayEnabled = false;
        }
    }

    [RelayCommand]
    private async Task BrowseFolderAsync()
    {
        var folders = await App.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions());
        if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } folder)
        {
            return;
        }

        var files = Directory
            .EnumerateFiles(folder)
            .Where(path => ImageExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0)
        {
            notificationService.Show(
                "No images",
                "That folder does not contain any supported images.",
                NotificationType.Warning
            );
            return;
        }

        BatchImagePaths = new ObservableCollection<string>(files);
        SelectImageCardViewModel.ImageSource = new ImageSource(files[0]);
    }

    private List<string> GetTargetImagePaths()
    {
        if (BatchImagePaths.Count > 0)
        {
            return BatchImagePaths.ToList();
        }

        if (SelectImageCardViewModel.ImageSource?.LocalFile is { Exists: true } file)
        {
            return [file.FullPath];
        }

        return [];
    }

    private async Task<string> RunOnceAsync(
        string imagePath,
        GenerateOverrides overrides,
        long seed,
        CancellationToken cancellationToken
    )
    {
        var image = new ImageSource(imagePath);
        await image.GetBlake3HashAsync();
        promptImage = image;

        var buildPromptArgs = new BuildPromptEventArgs { Overrides = overrides, SeedOverride = seed };
        BuildPrompt(buildPromptArgs);

        var client = ClientManager.Client!;
        await ClientManager.UploadInputImageAsync(image, cancellationToken);
        await UploadPromptFiles(buildPromptArgs.FilesToTransfer, client);

        var nodes = buildPromptArgs.Builder.ToNodeDictionary();
        var outputNodeNames = buildPromptArgs.Builder.Connections.OutputNodeNames.ToArray();

        await using var promptInterrupt = cancellationToken.Register(() =>
        {
            Logger.Info("Cancelling prompt");
            client
                .InterruptPromptAsync(new CancellationTokenSource(5000).Token)
                .SafeFireAndForget(ex => Logger.Warn(ex, "Error while interrupting prompt"));
        });

        ComfyTask? promptTask = null;
        try
        {
            try
            {
                promptTask = await client.QueuePromptAsync(nodes, cancellationToken);
            }
            catch (ApiException e)
            {
                Logger.Warn(e, "Api exception while queuing prompt");
                await DialogHelper.CreateApiExceptionDialog(e, "Api Error").ShowAsync();
                throw new OperationCanceledException();
            }

            promptTask.ProgressUpdate += OnProgressUpdateReceived;

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
                throw new OperationCanceledException();
            }

            var textOutputs = await client.GetTextsForExecutedPromptAsync(promptTask.Id, cancellationToken);
            var caption = outputNodeNames
                .Select(name => textOutputs.GetValueOrDefault(name))
                .FirstOrDefault(texts => texts is { Count: > 0 })
                ?.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));

            caption ??= textOutputs
                .Values.Where(texts => texts is { Count: > 0 })
                .SelectMany(texts => texts!)
                .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));

            if (string.IsNullOrWhiteSpace(caption))
            {
                throw new InvalidOperationException("ComfyUI did not return any caption text.");
            }

            return caption.Trim();
        }
        finally
        {
            promptTask?.Dispose();
        }
    }

    private async Task EnsureWeightsAsync(
        PromptGeneratorModelDefinition model,
        CancellationToken cancellationToken
    )
    {
        if (model.Files.Count == 0)
        {
            OutputProgress.Text = "Loading model into VRAM…";
            return;
        }

        var modelsRoot = GetComfyModelsDirectory();
        if (downloadService.IsModelReady(modelsRoot, model))
        {
            OutputProgress.Text = "Loading model into VRAM…";
            return;
        }

        var progress = new Progress<ProgressReport>(report =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                OutputProgress.IsIndeterminate = report.IsIndeterminate;
                OutputProgress.Maximum = 100;
                OutputProgress.Value = report.Percentage;
                OutputProgress.Text = report.Message ?? report.Title;
                OutputProgress.DownloadSpeedInMBps = report.SpeedInMBps;
            });
        });

        await downloadService.EnsureModelAsync(modelsRoot, model, progress, cancellationToken);
        OutputProgress.Text = "Loading model into VRAM…";
    }

    private DirectoryPath GetComfyModelsDirectory()
    {
        var packagePath =
            ClientManager.Client?.LocalServerPackage?.InstalledPackage.FullPath
            ?? throw new InvalidOperationException("A local ComfyUI package is required to download weights.");
        return new DirectoryPath(packagePath, "models");
    }

    private async Task EnsureQwenCatalogAsync(
        PromptGeneratorModelDefinition model,
        CancellationToken cancellationToken
    )
    {
        if (model.Backend != PromptGeneratorBackend.QwenVlGguf)
        {
            return;
        }

        if (!await WriteQwenGgufCatalogIfNeededAsync())
        {
            return;
        }

        if (ClientManager.Client?.LocalServerPackage is not { } pair)
        {
            return;
        }

        OutputProgress.Text = "Restarting ComfyUI to load the Qwen catalog…";
        await runningPackageService.StopPackage(pair.InstalledPackage.Id);
        await runningPackageService.StartPackage(pair.InstalledPackage);
        if (!await WaitForConnectedAsync(cancellationToken))
        {
            throw new InvalidOperationException("ComfyUI did not reconnect after updating the Qwen catalog.");
        }
    }

    private async Task<bool> WriteQwenGgufCatalogIfNeededAsync()
    {
        var packagePath = ClientManager.Client?.LocalServerPackage?.InstalledPackage.FullPath;
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return false;
        }

        var pluginDir = FindQwenPluginDir(packagePath);
        if (pluginDir is null)
        {
            return false;
        }

        var catalogPath = pluginDir.JoinFile("gguf_models.json");
        JsonObject root;
        if (catalogPath.Exists)
        {
            try
            {
                root =
                    JsonNode.Parse(await File.ReadAllTextAsync(catalogPath)) as JsonObject ?? new JsonObject();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Could not parse {Path}", catalogPath);
                notificationService.Show(
                    "Qwen catalog unreadable",
                    "Could not update ComfyUI-QwenVL/gguf_models.json.",
                    NotificationType.Warning
                );
                return false;
            }
        }
        else
        {
            root = new JsonObject { ["base_dir"] = "LLM/GGUF" };
        }

        if (root["qwenVL_model"] is not JsonObject qwenVl)
        {
            qwenVl = new JsonObject();
            root["qwenVL_model"] = qwenVl;
        }

        if (CatalogAlreadyHasQwenModel(qwenVl))
        {
            return false;
        }

        qwenVl[QwenCatalogKey] = new JsonObject
        {
            ["author"] = "mradermacher",
            ["repo_name"] = QwenCatalogKey,
            ["repo_id"] = $"mradermacher/{QwenCatalogKey}",
            ["mmproj_file"] = QwenMmprojFile,
            ["model_files"] = new JsonArray(QwenModelFile),
            ["defaults"] = new JsonObject { ["context_length"] = 4096, ["gpu_layers"] = -1 },
        };

        await File.WriteAllTextAsync(
            catalogPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
        );
        return true;
    }

    private static bool CatalogAlreadyHasQwenModel(JsonObject qwenVl)
    {
        if (qwenVl[QwenCatalogKey] is not null)
        {
            return true;
        }

        foreach (var (_, node) in qwenVl)
        {
            if (node is not JsonObject repo || repo["model_files"] is not JsonArray files)
            {
                continue;
            }

            if (
                files.Any(file =>
                    string.Equals(file?.GetValue<string>(), QwenModelFile, StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                return true;
            }
        }

        return false;
    }

    private static DirectoryPath? FindQwenPluginDir(string packagePath)
    {
        var customNodes = new DirectoryPath(packagePath, "custom_nodes");
        if (!customNodes.Exists)
        {
            return null;
        }

        var exact = new DirectoryPath(packagePath, "custom_nodes", "ComfyUI-QwenVL");
        if (exact.Exists)
        {
            return exact;
        }

        return customNodes
            .Info.EnumerateDirectories("*QwenVL*", SearchOption.TopDirectoryOnly)
            .Select(info => new DirectoryPath(info))
            .FirstOrDefault();
    }

    private static async Task WriteCaptionSidecarAsync(string imagePath, string caption)
    {
        var txtPath = Path.ChangeExtension(imagePath, ".txt");
        if (File.Exists(txtPath) && new FileInfo(txtPath).Length > 0)
        {
            await File.AppendAllTextAsync(txtPath, Environment.NewLine + caption, Encoding.UTF8);
        }
        else
        {
            await File.WriteAllTextAsync(txtPath, caption, Encoding.UTF8);
        }
    }

    /// <inheritdoc />
    protected override IEnumerable<ImageSource> GetInputImages()
    {
        if (SelectImageCardViewModel.ImageSource is { } image)
        {
            yield return image;
        }
    }

    /// <inheritdoc />
    public void LoadStateFromParameters(GenerationParameters parameters)
    {
        PromptCardViewModel.LoadStateFromParameters(parameters);
        SeedCardViewModel.Seed = Convert.ToInt64(parameters.Seed);
    }

    /// <inheritdoc />
    public GenerationParameters SaveStateToParameters(GenerationParameters parameters)
    {
        parameters = PromptCardViewModel.SaveStateToParameters(parameters);
        parameters.Seed = (ulong)SeedCardViewModel.Seed;
        return parameters;
    }
}
