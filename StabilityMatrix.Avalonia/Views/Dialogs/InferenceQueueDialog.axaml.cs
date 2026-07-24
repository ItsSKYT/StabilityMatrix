using Injectio.Attributes;
using StabilityMatrix.Avalonia.Controls;

namespace StabilityMatrix.Avalonia.Views.Dialogs;

[RegisterTransient<InferenceQueueDialog>]
public partial class InferenceQueueDialog : UserControlBase
{
    public InferenceQueueDialog()
    {
        InitializeComponent();
    }
}
