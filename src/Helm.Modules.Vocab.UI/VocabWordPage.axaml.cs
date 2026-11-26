using Avalonia.Controls;

namespace Helm.Modules.Vocab.Ui;

/// <summary>An opened word (see the XAML); DataContext: <see cref="VocabViewModel"/>, its <see cref="VocabViewModel.Detail"/> inside.</summary>
public partial class VocabWordPage : UserControl
{
    public VocabWordPage() => InitializeComponent();
}
