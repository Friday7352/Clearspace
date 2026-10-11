using System.Windows;
using Clearspace.Services;

namespace Clearspace;

// NEW (theme codes): copying a card or the whole deck as a code, and pasting a code someone sent you.
// CHANGED (decks of cards): a deck code is all the theme's cards (Services/ThemeCodes.cs).
//   - Copy code: on a card (right-click in the hand, the card's box in the Cards tab, a borrowed card).
//   - Copy deck code: Theme tab › Everything else.
//   - Paste a code: Cards tab (Borrow cards) and Theme tab (Mood). A card code adds the card to your theme and
//     keeps it in My cards; a deck code puts that whole deck in the editor (Undo brings yours back).
// The codes themselves: Services/ThemeCodes.cs. What a code cannot carry (pictures, font files) is said when it
// is copied.
internal sealed partial class ThemeEditor
{
    private void CopyCardCode(ThemeCard card)
    {
        if (ThemeCodes.CardCode(card, out var note) is not { } code) { Report(note ?? "This card has no code.", true); return; }
        if (!CopyText(code)) return;
        Report($"Code for {card.Name} copied ({code.Length:N0} characters). Anyone can paste it under Cards › Paste a code." + (note is null ? "" : " " + note));
    }

    private void CopyThemeCode()
    {
        var code = ThemeCodes.DeckCode(_document, DeckName, out var note);
        if (!CopyText(code)) return;
        Report($"Deck code copied: all your cards, {code.Length:N0} characters. Anyone can paste it on their Themes page." + (note is null ? "" : " " + note));
    }

    private void PasteCode()
    {
        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
        catch (System.Runtime.InteropServices.ExternalException) { Report("The clipboard is busy. Try again.", true); return; }
        if (!ThemeCodes.LooksLikeCode(text)) { Report("Copy a Clearspace code first (it starts with CS-), then choose Paste a code.", true); return; }
        if (!ThemeCodes.TryRead(text, out var card, out var theme, out var name, out var problem)) { Report(problem ?? "That code could not be read.", true); return; }
        if (theme is not null)
        {
            Change(theme);
            _name.Text = name;
            Report($"{name} from a code. Undo brings your theme back.");
            return;
        }
        if (card is null) return;
        PlayCard(card, null);
        try { ThemeCards.Keep(card); RefreshSavedCards(); }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { }
        if (_tab == "Cards") SyncPanel();
    }

    private bool CopyText(string text)
    {
        try { Clipboard.SetText(text); return true; }
        catch (System.Runtime.InteropServices.ExternalException) { Report("The clipboard is busy. Try again.", true); return false; }
    }
}
