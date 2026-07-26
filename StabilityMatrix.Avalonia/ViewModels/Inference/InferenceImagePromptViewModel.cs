using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using AsyncAwaitBestPractices;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DesktopNotifications;
using Injectio.Attributes;
using NLog;
using Refit;
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
using StabilityMatrix.Core.Models.Inference;
using StabilityMatrix.Core.Models.Notifications;
using StabilityMatrix.Core.Models.Settings;
using StabilityMatrix.Core.Services;
using Notification = DesktopNotifications.Notification;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

[View(typeof(InferenceImagePromptView), IsPersistent = true)]
[RegisterScoped<InferenceImagePromptViewModel>, ManagedService]
public partial class InferenceImagePromptViewModel : InferenceGenerationViewModelBase, IParametersLoadableState
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly INotificationService notificationService;

    public static IReadOnlyList<string> AvailableModels { get; } =
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

    [ObservableProperty]
    [property: JsonPropertyName("Format")]
    private ImagePromptFormat selectedFormat = ImagePromptFormat.Tags;

    [ObservableProperty]
    [property: JsonPropertyName("FlorenceModel")]
    private string selectedModel = AvailableModels[0];

    [JsonPropertyName("Prompt")]
    public PromptCardViewModel PromptCardViewModel { get; }

    [JsonPropertyName("ImageLoader")]
    public SelectImageCardViewModel SelectImageCardViewModel { get; }

    [JsonPropertyName("Seed")]
    public SeedCardViewModel SeedCardViewModel { get; }

    public InferenceImagePromptViewModel(
        IServiceManager<ViewModelBase> vmFactory,
        IInferenceClientManager inferenceClientManager,
        INotificationService notificationService,
        ISettingsManager settingsManager,
        RunningPackageService runningPackageService
    )
        : base(vmFactory, inferenceClientManager, notificationService, settingsManager, runningPackageService)
    {
        this.notificationService = notificationService;

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

        var applyArgs = args.ToModuleApplyStepEventArgs();
        var builder = args.Builder;
        var nodes = builder.Nodes;

        SelectImageCardViewModel.ApplyStep(applyArgs);

        if (builder.Connections.Primary is not { IsT1: true } primaryImage)
        {
            throw new ValidationException("Input image is required");
        }

        var image = primaryImage.AsT1;

        var florenceModel = nodes
            .AddTypedNode(
                new ComfyNodeBuilder.DownloadAndLoadFlorence2Model
                {
                    Name = nodes.GetUniqueName(nameof(ComfyNodeBuilder.DownloadAndLoadFlorence2Model)),
                    Model = SelectedModel,
                    Precision = "fp16",
                }
            )
            .Output;

        var florenceRun = nodes.AddTypedNode(
            new ComfyNodeBuilder.Florence2Run
            {
                Name = nodes.GetUniqueName(nameof(ComfyNodeBuilder.Florence2Run)),
                Image = image,
                Florence2Model = florenceModel,
                TextInput = "",
                Task = FormatToFlorenceTask(SelectedFormat),
                FillMask = false,
                KeepModelLoaded = false,
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

        var preview = nodes.AddTypedNode(
            new ComfyNodeBuilder.PreviewAny
            {
                Name = nodes.GetUniqueName(nameof(ComfyNodeBuilder.PreviewAny)),
                Source = florenceRun.Output3,
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
        if (!await CheckClientConnectedWithPrompt() || !ClientManager.IsConnected)
        {
            return;
        }

        if (SelectImageCardViewModel.ImageSource is null)
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

        var buildPromptArgs = new BuildPromptEventArgs
        {
            Overrides = overrides,
            SeedOverride = seedCard.Seed,
        };
        BuildPrompt(buildPromptArgs);

        var client = ClientManager.Client!;
        var nodes = buildPromptArgs.Builder.ToNodeDictionary();
        var outputNodeNames = buildPromptArgs.Builder.Connections.OutputNodeNames.ToArray();

        if (outputNodeNames.Length == 0)
            throw new InvalidOperationException("OutputNodeNames is empty");

        if (!await CheckPromptExtensionsInstalled(nodes))
        {
            throw new ValidationException("Prompt extensions not installed");
        }

        await UploadInputImages(client);
        await UploadPromptFiles(buildPromptArgs.FilesToTransfer, client);

        var promptInterrupt = cancellationToken.Register(() =>
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
                return;
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
                return;
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
                notificationService.Show(
                    "No output",
                    "Did not receive any prompt text from Florence2.",
                    NotificationType.Warning
                );
                return;
            }

            await promptInterrupt.DisposeAsync();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                PromptCardViewModel.PromptDocument.Text = caption.Trim();
            });

            await notificationService.ShowAsync(
                NotificationKey.Inference_PromptCompleted,
                new Notification
                {
                    Title = "Prompt Generated",
                    Body = $"Prompt [{promptTask.Id[..7].ToLower()}] completed successfully",
                },
                action: new NavigateToPageAction(typeof(InferenceViewModel).AssemblyQualifiedName!)
            );
        }
        finally
        {
            OutputProgress.ClearProgress();
            ImageGalleryCardViewModel.PreviewImage = null;
            ImageGalleryCardViewModel.IsPreviewOverlayEnabled = false;
            promptTask?.Dispose();
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
