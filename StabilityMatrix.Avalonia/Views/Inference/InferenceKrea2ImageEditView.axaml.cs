using Injectio.Attributes;
using StabilityMatrix.Avalonia.Controls.Dock;

namespace StabilityMatrix.Avalonia.Views.Inference;

[RegisterTransient<InferenceKrea2ImageEditView>]
public partial class InferenceKrea2ImageEditView : DockUserControlBase
{
    public InferenceKrea2ImageEditView()
    {
        InitializeComponent();
    }
}
