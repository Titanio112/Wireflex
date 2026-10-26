using System.Windows;

namespace Wireflex.App;

/// <summary>Diálogo "abrir o espelhamento?". Só mostra e devolve a resposta (<c>null</c> = fechou sem responder).</summary>
public partial class AskWindow : Window
{
    private const double Gap = 12;
    private bool? _answer;

    public AskWindow(string message)
    {
        InitializeComponent();
        MessageText.Text = message;

        // No canto inferior direito da área de trabalho, perto da bandeja.
        SizeChanged += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - Gap;
            Top = area.Bottom - ActualHeight - Gap;
        };
        Closed += (_, _) => Answered?.Invoke(_answer);
    }

    /// <summary>Quando a janela fecha: <c>true</c> = abrir, <c>false</c> = não, <c>null</c> = sem resposta.</summary>
    public event Action<bool?>? Answered;

    private void OnYes(object sender, RoutedEventArgs e) => Answer(true);

    private void OnNo(object sender, RoutedEventArgs e) => Answer(false);

    private void Answer(bool answer)
    {
        _answer = answer;
        Close();
    }
}
