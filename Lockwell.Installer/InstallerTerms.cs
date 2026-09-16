namespace Lockwell.Installer;

/// <summary>
/// The text shown on the Terms step.
///
/// Lockwell is GPL-3.0-or-later, so this is not an end-user licence agreement that takes
/// rights away. It is a plain summary of the licence plus the two facts someone should
/// know before they put real secrets into a vault: nobody can recover it for them, and
/// setup fetches a signed package over the network. The full licence ships with the app
/// and is in the repository.
///
/// Paragraphs are single lines on purpose. The control that shows this wraps text to its
/// own width, so line breaks written in here would survive as ragged breaks partway
/// across the card.
/// </summary>
public static class InstallerTerms
{
    public const string Text =
        """
        Lockwell is free software under the GNU General Public License, version 3 or later.

        What that means for you

        You may use Lockwell for anything, study how it works, change it, and pass it on. If you distribute a modified version, you have to release your changes under the same licence and make the source available. The full licence text is installed alongside the app, and the source code is at github.com/slashneck/Lockwell.

        There is no warranty. Lockwell is provided as-is, to the extent the law allows.

        Before you store anything important

        Your vault is encrypted with your master password and stays on this PC. There is no account, no cloud copy, and no backdoor. If you lose both your master password and your recovery key, nobody can get your data back. Not the maintainers, not anyone. Write the recovery key down and keep it somewhere safe.

        What setup does

        It downloads the release package from GitHub over HTTPS, checks that it carries a valid signature from the Lockwell signing key, and only installs it if that check passes. It sends nothing about you or your vault.

        Uninstalling

        Remove Lockwell from Windows Settings at any time. Your vault files stay where they are unless you explicitly ask for them to be removed during uninstall.
        """;
}
