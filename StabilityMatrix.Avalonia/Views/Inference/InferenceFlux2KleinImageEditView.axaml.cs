using Injectio.Attributes;
using StabilityMatrix.Avalonia.Controls.Dock;

namespace StabilityMatrix.Avalonia.Views.Inference;

[RegisterTransient<InferenceFlux2KleinImageEditView>]
public partial class InferenceFlux2KleinImageEditView : DockUserControlBase
{
    public InferenceFlux2KleinImageEditView()
    {
        InitializeComponent();
    }
}
