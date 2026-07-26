using Injectio.Attributes;
using StabilityMatrix.Avalonia.Controls.Dock;

namespace StabilityMatrix.Avalonia.Views.Inference;

[RegisterTransient<InferenceLtxvVideoRetakeView>]
public partial class InferenceLtxvVideoRetakeView : DockUserControlBase
{
    public InferenceLtxvVideoRetakeView()
    {
        InitializeComponent();
    }
}
