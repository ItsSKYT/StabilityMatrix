using Injectio.Attributes;
using StabilityMatrix.Avalonia.Controls.Dock;

namespace StabilityMatrix.Avalonia.Views.Inference;

[RegisterTransient<InferenceAnimaImageEditView>]
public partial class InferenceAnimaImageEditView : DockUserControlBase
{
    public InferenceAnimaImageEditView()
    {
        InitializeComponent();
    }
}
