using System.Collections.ObjectModel;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Injectio.Attributes;
using StabilityMatrix.Avalonia.Controls;
using StabilityMatrix.Avalonia.Languages;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Avalonia.Views.Dialogs;
using StabilityMatrix.Core.Attributes;

namespace StabilityMatrix.Avalonia.ViewModels.Dialogs;

[View(typeof(InferenceQueueDialog))]
[ManagedService]
[RegisterTransient<InferenceQueueDialogViewModel>]
public partial class InferenceQueueDialogViewModel : ContentDialogViewModelBase
{
    private InferenceGenerationViewModelBase? owner;

    public ObservableCollection<InferenceQueueItem> Items { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private InferenceQueueItem? selectedItem;

    [ObservableProperty]
    private string editPrompt = string.Empty;

    [ObservableProperty]
    private string editNegativePrompt = string.Empty;

    public bool HasSelection => SelectedItem is not null;

    public override string? Title => Resources.Label_GenerationQueue;

    public void Attach(InferenceGenerationViewModelBase generationViewModel)
    {
        owner = generationViewModel;
        Items = generationViewModel.GenerationQueue;
        OnPropertyChanged(nameof(Items));
        SelectedItem = Items.Count > 0 ? Items[0] : null;
    }

    partial void OnSelectedItemChanged(InferenceQueueItem? value)
    {
        if (value is null)
        {
            EditPrompt = string.Empty;
            EditNegativePrompt = string.Empty;
            return;
        }

        InferenceGenerationViewModelBase.TryGetPromptPreviews(
            value.Project,
            out var prompt,
            out var negative
        );
        EditPrompt = prompt ?? string.Empty;
        EditNegativePrompt = negative ?? string.Empty;
    }

    [RelayCommand]
    private void MoveUp(InferenceQueueItem? item) => owner?.MoveQueuedGenerationUpCommand.Execute(item);

    [RelayCommand]
    private void MoveDown(InferenceQueueItem? item) => owner?.MoveQueuedGenerationDownCommand.Execute(item);

    [RelayCommand]
    private void Remove(InferenceQueueItem? item)
    {
        if (item is null)
        {
            return;
        }

        var wasSelected = ReferenceEquals(SelectedItem, item);
        owner?.RemoveQueuedGenerationCommand.Execute(item);
        if (wasSelected)
        {
            SelectedItem = Items.Count > 0 ? Items[0] : null;
        }
    }

    [RelayCommand]
    private void Clear()
    {
        owner?.ClearGenerationQueueCommand.Execute(null);
        SelectedItem = null;
    }

    [RelayCommand]
    private void ApplyEdits()
    {
        if (SelectedItem is null)
        {
            return;
        }

        InferenceGenerationViewModelBase.UpdateQueueItemPrompts(
            SelectedItem,
            EditPrompt,
            EditNegativePrompt
        );
    }

    [RelayCommand]
    private void AddCurrentSettings()
    {
        owner?.EnqueueGenerationCommand.Execute(GenerateFlags.None);
        if (Items.Count > 0)
        {
            SelectedItem = Items[^1];
        }
    }

    public override BetterContentDialog GetDialog()
    {
        var dialog = base.GetDialog();
        dialog.Padding = new Thickness(0);
        dialog.CloseOnClickOutside = true;
        dialog.CloseButtonText = Resources.Action_Close;
        dialog.MinDialogWidth = 640;
        dialog.MaxDialogWidth = 820;
        dialog.MinDialogHeight = 480;
        return dialog;
    }
}
