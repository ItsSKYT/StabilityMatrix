using Injectio.Attributes;
using StabilityMatrix.Avalonia.Controls.Dock;

namespace StabilityMatrix.Avalonia.Views.Inference;

[RegisterTransient<InferenceMiniMaxH3TextToImageView>]
public partial class InferenceMiniMaxH3TextToImageView : DockUserControlBase
{
    public InferenceMiniMaxH3TextToImageView()
    {
        InitializeComponent();
    }
}
