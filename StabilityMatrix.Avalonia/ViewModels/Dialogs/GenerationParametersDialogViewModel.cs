using System.Collections.Generic;
using System.Linq;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Injectio.Attributes;
using StabilityMatrix.Avalonia.Controls;
using StabilityMatrix.Avalonia.Languages;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Avalonia.Views.Dialogs;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Models;

namespace StabilityMatrix.Avalonia.ViewModels.Dialogs;

[View(typeof(GenerationParametersDialog))]
[ManagedService]
[RegisterTransient<GenerationParametersDialogViewModel>]
public partial class GenerationParametersDialogViewModel : ContentDialogViewModelBase
{
    [ObservableProperty]
    private GenerationParameters? parameters;

    [ObservableProperty]
    private string? fileName;

    public IReadOnlyList<ParameterItem> Items { get; private set; } = [];

    public override string? Title => Resources.Label_Parameters;

    partial void OnParametersChanged(GenerationParameters? value)
    {
        Items = BuildItems(value);
        OnPropertyChanged(nameof(Items));
    }

    [RelayCommand]
    private async Task CopyValueAsync(string? value)
    {
        if (string.IsNullOrEmpty(value) || App.Clipboard is null)
        {
            return;
        }

        await App.Clipboard.SetTextAsync(value);
    }

    [RelayCommand]
    private async Task CopyAllAsync()
    {
        if (Parameters is null || App.Clipboard is null)
        {
            return;
        }

        await App.Clipboard.SetTextAsync(Parameters.GetParametersText());
    }

    public override BetterContentDialog GetDialog()
    {
        var dialog = base.GetDialog();
        dialog.Padding = new Thickness(0);
        dialog.CloseOnClickOutside = true;
        dialog.CloseButtonText = Resources.Action_Close;
        dialog.MinDialogWidth = 520;
        dialog.MaxDialogWidth = 720;
        return dialog;
    }

    private static IReadOnlyList<ParameterItem> BuildItems(GenerationParameters? parameters)
    {
        if (parameters is null)
        {
            return [];
        }

        var items = new List<ParameterItem>
        {
            new(Resources.Label_Prompt, parameters.PositivePrompt),
            new(Resources.Label_NegativePrompt, parameters.NegativePrompt),
            new(Resources.Label_Model, parameters.ModelName),
            new(Resources.Label_ModelHash, parameters.ModelHash),
            new(Resources.Label_Sampler, parameters.Sampler),
            new(Resources.Label_Steps, parameters.Steps > 0 ? parameters.Steps.ToString() : null),
            new(Resources.Label_CFGScale, parameters.CfgScale > 0 ? parameters.CfgScale.ToString() : null),
            new(Resources.Label_Seed, parameters.Seed > 0 ? parameters.Seed.ToString() : null),
            new(
                Resources.Label_Size,
                parameters is { Width: > 0, Height: > 0 }
                    ? $"{parameters.Width}x{parameters.Height}"
                    : null
            ),
        };

        return items.Where(i => !string.IsNullOrWhiteSpace(i.Value)).ToList();
    }

    public record ParameterItem(string Label, string? Value);
}
