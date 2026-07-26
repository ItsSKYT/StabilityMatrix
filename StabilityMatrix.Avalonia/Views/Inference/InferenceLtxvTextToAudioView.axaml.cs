using Injectio.Attributes;
using StabilityMatrix.Avalonia.Controls.Dock;

namespace StabilityMatrix.Avalonia.Views.Inference;

[RegisterTransient<InferenceLtxvTextToAudioView>]
public partial class InferenceLtxvTextToAudioView : DockUserControlBase
{
    public InferenceLtxvTextToAudioView()
    {
        InitializeComponent();
    }
}
