using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Injectio.Attributes;
using StabilityMatrix.Avalonia.Controls;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Models.Api.Comfy.NodeTypes;
using StabilityMatrix.Core.Models.FileInterfaces;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

[View(typeof(SelectAudioCard))]
[ManagedService]
[RegisterTransient<SelectAudioCardViewModel>]
public partial class SelectAudioCardViewModel : LoadableViewModelBase, IComfyStep
{
    private static FilePickerFileType SupportedAudio { get; } =
        new("Audio")
        {
            Patterns = ["*.wav", "*.mp3", "*.flac", "*.ogg", "*.m4a"],
            MimeTypes = ["audio/*"],
        };

    [ObservableProperty]
    private FilePath? localFile;

    [ObservableProperty]
    private string? displayName;

    [JsonIgnore]
    public bool HasFile => LocalFile?.Exists == true;

    [RelayCommand]
    private async Task SelectFromFilePickerAsync()
    {
        var files = await App.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Select Audio",
                AllowMultiple = false,
                FileTypeFilter = [SupportedAudio],
            }
        );
        if (files is not { Count: > 0 })
            return;

        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
            return;

        LocalFile = path;
        DisplayName = Path.GetFileName(path);
    }

    [RelayCommand]
    private void Clear()
    {
        LocalFile = null;
        DisplayName = null;
    }

    public string? GetComfyRelativePath()
    {
        if (LocalFile is null || !LocalFile.Exists)
            return null;

        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(LocalFile.FullPath)))[
            ..16
        ];
        return Path.Combine("Inference", $"{hash}{LocalFile.Info.Extension}");
    }

    public void ApplyStep(ModuleApplyStepEventArgs e)
    {
        if (LocalFile is null || !LocalFile.Exists)
            return;

        var dest = GetComfyRelativePath()!;
        e.AddFileTransfer(LocalFile.FullPath, dest);
    }

    public AudioNodeConnection? LoadAudioNode(ModuleApplyStepEventArgs e)
    {
        var rel = GetComfyRelativePath();
        if (rel is null)
            return null;

        ApplyStep(e);
        return e
            .Nodes.AddTypedNode(
                new ComfyNodeBuilder.LoadAudio
                {
                    Name = e.Nodes.GetUniqueName(nameof(ComfyNodeBuilder.LoadAudio)),
                    Audio = rel.Replace('\\', '/'),
                }
            )
            .Output;
    }
}
