// Lockwell - local-only encrypted vault
// Copyright (C) 2026 Lockwell
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT
// ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS
// FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License along with
// this program. If not, see <https://www.gnu.org/licenses/>.

using Lockwell.Mobile.Services;
using Lockwell.Mobile.Views;

namespace Lockwell.Mobile;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();

		// A plain navigation stack rather than Shell: the flow is a short, deliberate
		// chain (vaults -> unlock -> vault) and Shell's flyout would invite wandering
		// between pages that only make sense in order.
		//
		// The platform nav bar is hidden throughout. Every page draws its own header, so
		// the app looks like Lockwell rather than a stock .NET template with our content
		// poured into it.
		var root = new NavigationPage(new VaultsPage());
		NavigationPage.SetHasNavigationBar(root.CurrentPage, false);
		MainPage = root;
	}

	/// <summary>
	/// The app has left the foreground. Start the clock that decides whether the vault
	/// should be locked by the time it comes back.
	/// </summary>
	protected override void OnSleep()
	{
		base.OnSleep();
		AppLock.WentToBackground();

		// Scratch files are decrypted media. Leaving the app is as good a moment as
		// locking to be rid of them.
		MobileVaultPaths.PurgeTemp();
	}

	protected override void OnResume()
	{
		base.OnResume();
		AppLock.CameToForeground();
	}
}
