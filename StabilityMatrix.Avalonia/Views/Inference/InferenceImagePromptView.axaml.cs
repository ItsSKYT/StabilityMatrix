using Injectio.Attributes;
using StabilityMatrix.Avalonia.Controls.Dock;

namespace StabilityMatrix.Avalonia.Views.Inference;

[RegisterTransient<InferenceImagePromptView>]
public partial class InferenceImagePromptView : DockUserControlBase
{
    public InferenceImagePromptView()
    {
        InitializeComponent();
    }
}
