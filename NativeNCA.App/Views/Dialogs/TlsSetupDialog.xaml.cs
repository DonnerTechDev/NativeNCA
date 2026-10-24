using Wpf.Ui.Controls;

namespace NativeNCA.App.Views.Dialogs;

/// <summary>Explains the own browser certificate before Windows shows its security prompt.</summary>
public partial class TlsSetupDialog : ContentDialog
{
    public TlsSetupDialog(ContentDialogHost host) : base(host)
    {
        InitializeComponent();
    }
}
