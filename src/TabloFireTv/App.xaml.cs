namespace TabloFireTv;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    // MainActivity.OnCreate replaces the whole native content view with the WebView + player
    // layer, so the page this Window carries is never actually shown to anyone — it exists
    // only because UseMaui=true expects one. Deliberately a plain ContentPage, not a Shell: a
    // Shell's ShellItemRenderer restores itself via Android Fragments at onStart(), and since
    // MainActivity has already swapped out the content view those fragments render into, that
    // restore crashes with "No view found for id 0x1 for fragment ShellItemRenderer" — a plain
    // page has no such Fragment-based renderer to go looking for its container.
    protected override Window CreateWindow(IActivationState? activationState)
        => new Window(new Pages.BlankPage()) { Title = "Tablo for Fire TV" };
}
