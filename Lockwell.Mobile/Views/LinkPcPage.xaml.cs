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

using System.Net.Sockets;
using Lockwell.Mobile.Services;
using Lockwell.Sync;
using Microsoft.Maui.Controls.Shapes;

namespace Lockwell.Mobile.Views;

/// <summary>
/// The phone half of linking.
///
/// Runs the same handshake from Lockwell.Core that the PC runs, as the initiator. The
/// typed code becomes the pairing secret; without it the handshake completes but the
/// two sides never agree on keys, so a wrong code simply cannot move data.
///
/// The fingerprint step is not decoration. The handshake proves the peer holds the
/// private key matching the one the code was derived against, but only a human
/// comparing the two screens rules out having been steered to a different PC entirely.
/// </summary>
public partial class LinkPcPage : ContentPage
{
    private readonly PhoneTrustStore _trust;
    private readonly Action _onLinked;

    private HandshakeResult? _pending;
    private string _pendingAddress = "";
    private string _pendingName = "PC";

    // The connection stays open between "connected" and the user answering, because the
    // answer has to travel back to the PC through the tunnel the handshake established.
    // Closing and reconnecting would mean a second handshake and a different code.
    private TcpClient? _openClient;
    private NetworkStream? _openStream;
    private SyncSession? _openSession;

    private CancellationTokenSource? _searching;
    private DiscoveryBeacon? _chosen;

    public LinkPcPage(PhoneTrustStore trust, Action onLinked)
    {
        InitializeComponent();
        _trust = trust;
        _onLinked = onLinked;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = SearchAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        StopSearching();
        CloseConnection();
    }

    private void StopSearching()
    {
        try { _searching?.Cancel(); } catch { /* already done */ }
        _searching?.Dispose();
        _searching = null;
    }

    // ------------------------------------------------------------- finding

    /// <summary>
    /// Look for PCs that are waiting to pair.
    ///
    /// This replaces the worst step in the whole flow: reading an IP address off one
    /// screen and typing it into another. A PC only answers while its own linking screen
    /// is open, so anything that appears here is a PC that is actually ready, which also
    /// makes "nothing found" a useful answer rather than an ambiguous one.
    /// </summary>
    private async Task SearchAsync()
    {
        StopSearching();
        _searching = new CancellationTokenSource();
        CancellationToken token = _searching.Token;

        FoundList.Clear();
        _chosen = null;

        SearchSpinner.IsVisible = true;
        SearchSpinner.IsRunning = true;
        SearchStatus.Text = "Looking for your PC...";
        SearchAgainButton.IsVisible = false;

        IReadOnlyList<DiscoveryBeacon> found;
        try
        {
            found = await LocalDiscovery.FindAsync(TimeSpan.FromSeconds(4), token);
        }
        catch (Exception ex)
        {
            SearchSpinner.IsRunning = false;
            SearchSpinner.IsVisible = false;
            SearchStatus.Text = "Could not search this network: " + ex.Message;
            SearchAgainButton.IsVisible = true;
            return;
        }

        if (token.IsCancellationRequested) return;

        SearchSpinner.IsRunning = false;
        SearchSpinner.IsVisible = false;
        SearchAgainButton.IsVisible = true;

        if (found.Count == 0)
        {
            SearchStatus.Text =
                "No PC found yet. Make sure Lockwell is open on your PC with the " +
                "\"Link a device\" window showing, and that both are on the same Wi-Fi.";
            return;
        }

        SearchStatus.Text = found.Count == 1
            ? "Found 1 PC. Tap it, then type the code it shows."
            : $"Found {found.Count} PCs. Tap the one you want.";

        foreach (DiscoveryBeacon beacon in found)
            FoundList.Add(BuildFoundRow(beacon));
    }

    private void OnSearchAgainClicked(object sender, EventArgs e) => _ = SearchAsync();

    private View BuildFoundRow(DiscoveryBeacon beacon)
    {
        DiscoveryBeacon captured = beacon;

        var row = new Border
        {
            BackgroundColor = Theme.Color("Bg2"),
            Stroke = new SolidColorBrush(Theme.Color("Stroke")),
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            Padding = new Thickness(14, 12),
        };

        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
        };

        grid.Add(new Label
        {
            Text = "\U0001F4BB",
            FontSize = 20,
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(0, 0, 12, 0),
        }, 0);

        var labels = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center };
        labels.Add(new Label
        {
            Text = beacon.Name.Length > 0 ? beacon.Name : beacon.Address,
            FontSize = 15,
            TextColor = Theme.Color("TextColor"),
            LineBreakMode = LineBreakMode.TailTruncation,
        });
        labels.Add(new Label
        {
            Text = beacon.AcceptingPairing ? $"{beacon.Address} · ready to pair" : beacon.Address,
            FontSize = 11,
            TextColor = Theme.Color(beacon.AcceptingPairing ? "Accent" : "Muted"),
        });
        grid.Add(labels, 1);

        var tick = new Label
        {
            Text = "\u2713",
            FontSize = 18,
            TextColor = Theme.Color("Accent"),
            VerticalOptions = LayoutOptions.Center,
            IsVisible = false,
        };
        grid.Add(tick, 2);

        row.Content = grid;

        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) =>
        {
            _chosen = captured;
            AddressEntry.Text = captured.Address;

            // Show which one is selected by ticking it and clearing the others.
            foreach (View other in FoundList.Children.OfType<View>())
            {
                if (other is Border b && b.Content is Grid g &&
                    g.Children.OfType<Label>().LastOrDefault() is { } mark)
                {
                    mark.IsVisible = ReferenceEquals(other, row);
                }
            }

            SearchStatus.Text = $"Selected \"{captured.Name}\". Now type the code it is showing.";
            CodeEntry.Focus();
        };
        row.GestureRecognizers.Add(tap);

        return row;
    }

    private void OnManualToggleClicked(object sender, EventArgs e)
    {
        ManualField.IsVisible = !ManualField.IsVisible;
        ManualToggle.Text = ManualField.IsVisible
            ? "Hide the address box"
            : "Enter the address myself";

        if (ManualField.IsVisible) AddressEntry.Focus();
    }

    private void OnCodeChanged(object sender, TextChangedEventArgs e)
    {
        string normalised = PairingCode.Normalise(e.NewTextValue ?? "");
        int missing = PairingCode.Length - normalised.Length;

        if (normalised.Length == 0)
        {
            StatusLabel.IsVisible = false;
            return;
        }

        StatusLabel.IsVisible = true;
        if (missing > 0)
        {
            StatusLabel.Text = $"{missing} more character(s).";
            StatusLabel.TextColor = Theme.Color("Muted");
        }
        else if (missing == 0)
        {
            StatusLabel.Text = "Code looks complete.";
            StatusLabel.TextColor = Theme.Color("Accent");
        }
        else
        {
            StatusLabel.Text = "That is longer than a pairing code.";
            StatusLabel.TextColor = Theme.Color("Warning");
        }
    }

    private async void OnConnectClicked(object sender, EventArgs e)
    {
        string address = (AddressEntry.Text ?? "").Trim();
        string code = CodeEntry.Text ?? "";

        if (address.Length == 0)
        {
            await this.ShowAlert("Pick your PC first",
                "Tap your PC in the list above. If it is not there, make sure the " +
                "\"Link a device\" window is open on it, or enter the address by hand.",
                "OK");
            return;
        }

        if (!PairingCode.LooksValid(code))
        {
            await this.ShowAlert("Check the code",
                $"A pairing code is {PairingCode.Length} digits. Spaces do not matter.", "OK");
            return;
        }

        ConnectButton.IsEnabled = false;
        ConnectButton.Text = "Connecting...";
        SetStatus("Reaching your PC...", "Muted");

        try
        {
            var result = await ConnectAsync(address, code);
            if (result is null) return;

            _pending = result;
            _pendingAddress = address;

            // Derived from the finished handshake, so the PC shows the identical number.
            // This used to show the PC's identity fingerprint while the PC showed the
            // phone's, which meant the two screens could never match and the comparison
            // proved nothing.
            FingerprintLabel.Text = result.SessionCode;
            PcNameLabel.Text = $"\"{_pendingName}\" is showing a code. It must match this one.";
            ConfirmPanel.IsVisible = true;
            SetStatus("Connected. Check the codes match, then answer below.", "Accent");
        }
        finally
        {
            ConnectButton.IsEnabled = true;
            ConnectButton.Text = "Connect";
        }
    }

    private async Task<HandshakeResult?> ConnectAsync(string address, string code)
    {
        CloseConnection();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        try
        {
            var client = new TcpClient();
            _openClient = client;
            await client.ConnectAsync(address, SyncProtocol.DefaultPort, cts.Token);

            NetworkStream stream = client.GetStream();
            _openStream = stream;

            // The PC states its public key first. That key salts the pairing code, so an
            // impostor substituting their own key derives a different secret and cannot
            // reach ours -- the code is what makes this mean anything, not the key.
            (byte[] pcKey, string pcName) = await PairingGreeting.ReceiveAsync(stream, cts.Token);
            _pendingName = pcName;

            SetStatus($"Found \"{pcName}\". Verifying...", "Muted");

            // Argon2id is deliberately slow, so keep it off the UI thread or the app
            // freezes mid-handshake.
            byte[] secret = await Task.Run(() => PairingCode.ToSecret(code, pcKey), cts.Token);

            HandshakeResult result = await Handshake.InitiateAsync(
                stream, _trust.Identity, pcKey,
                HandshakeMode.Pair, secret, cts.Token);

            // Held open so the answer can be sent back over it.
            _openSession = new SyncSession(stream, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            // Deliberately not "check your network": the network is usually fine, and
            // saying so sends people hunting for a fault that is not there. The common
            // causes, in order, are the PC not waiting and its firewall refusing us.
            SetStatus(
                "No answer from that PC. Its \"Link a device\" window has to be open, and " +
                "Windows Firewall has to allow Lockwell -- the PC will say so if it does not.",
                "Danger");
        }
        catch (SocketException)
        {
            SetStatus(
                "Could not reach that address. If you picked the PC from the list, it may " +
                "have stopped waiting; search again.",
                "Danger");
        }
        catch (HandshakeException ex)
        {
            SetStatus(ex.Reason switch
            {
                HandshakeFailure.VersionMismatch => "That PC runs a different version of Lockwell.",
                HandshakeFailure.BadPairingSecret => "That code did not match. Check it and try again.",
                _ => "The PC could not be verified. Nothing was linked.",
            }, "Danger");
        }
        catch (Exception ex)
        {
            SetStatus("Could not connect: " + ex.Message, "Danger");
        }

        CloseConnection();
        return null;
    }

    /// <summary>Let go of the socket, whatever state it is in.</summary>
    private void CloseConnection()
    {
        try { _openSession?.Dispose(); } catch { }
        _openSession = null;

        try { _openStream?.Dispose(); } catch { }
        _openStream = null;

        try { _openClient?.Dispose(); } catch { }
        _openClient = null;
    }

    /// <summary>
    /// The person is holding this device, so this is where the question is asked and this
    /// is the only place it is answered. The PC cannot see this screen and has nothing to
    /// verify on its own; it waits for what is sent here.
    /// </summary>
    private async void OnConfirmClicked(object sender, EventArgs e)
    {
        if (_pending is null || _openSession is null) return;

        string? name = await this.ShowPrompt("Name this PC",
            "What should this computer be called?",
            initialValue: _pendingName, maxLength: 40);

        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            // Sent through the tunnel the handshake established, so nothing on the network
            // can forge it or turn a refusal into an acceptance.
            await PairingConfirmation.SendAsync(_openSession, matched: true, _trust.SelfName);
        }
        catch (Exception ex)
        {
            SetStatus("Lost the connection before it could be confirmed: " + ex.Message, "Danger");
            CloseConnection();
            _pending = null;
            ConfirmPanel.IsVisible = false;
            return;
        }

        StopSearching();
        _trust.Add(_pending.PeerPublicKey, name.Trim(), _pendingAddress);
        _pending = null;
        CloseConnection();

        await this.ShowAlert("Linked",
            $"\"{name.Trim()}\" is now linked to this vault. Nothing has been transferred yet.", "OK");

        _onLinked();
        await Navigation.PopAsync();
    }

    private async void OnRejectClicked(object sender, EventArgs e)
    {
        // Tell the PC rather than just hanging up, so it can say the codes were rejected
        // instead of reporting a vague connection problem.
        if (_openSession is not null)
        {
            try { await PairingConfirmation.SendAsync(_openSession, matched: false, _trust.SelfName); }
            catch { /* the PC will notice the connection drop either way */ }
        }

        CloseConnection();
        _pending = null;
        ConfirmPanel.IsVisible = false;
        SetStatus("Nothing was linked. Try again, and check the address is your own PC.", "Danger");

        await this.ShowAlert("Not linked",
            "Good call. Codes that do not match mean the connection did not reach the PC you " +
            "expected, and nothing was paired.", "OK");
    }

    private void SetStatus(string text, string colour)
    {
        StatusLabel.Text = text;
        StatusLabel.TextColor = Theme.Color(colour);
        StatusLabel.IsVisible = true;
    }

    private async void OnBackClicked(object sender, EventArgs e) => await Navigation.PopAsync();
}
