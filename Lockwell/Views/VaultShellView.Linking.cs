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

using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lockwell.Helpers;
using Lockwell.Sync;

namespace Lockwell.Views;

/// <summary>
/// Linking a new device.
///
/// The PC shows a code, the phone enters it, and the two run the handshake in
/// Lockwell.Core. The code is a one-time pairing secret: without it, anyone who
/// discovered this PC's public key could attempt to link. It is valid for one attempt
/// and expires quickly, so a code glimpsed over a shoulder is not a standing key.
///
/// Both screens end up showing the same short fingerprint. Comparing them is what
/// proves the two devices agreed on the same keys rather than each talking to someone
/// in the middle.
///
/// This screen also tells the user everything the phone needs and nothing it does not.
/// Two earlier mistakes are deliberately corrected here. It used to print a single
/// "best guess" address, picked by asking the OS which interface it would use to reach
/// the public internet -- on a PC with wired and wireless up at once that is a coin
/// flip, and the wrong half of the time it named an address the phone could not reach.
/// And it said nothing at all about the firewall, so a connection Windows dropped
/// before Lockwell ever saw it surfaced on the phone as "check you are on the same
/// network", sending the user after a fault that did not exist.
/// </summary>
public partial class VaultShellView
{
    private CancellationTokenSource? _linkCancel;
    private TcpListener? _linkListener;

    private void LinkDevice_Click(object sender, RoutedEventArgs e) => BeginLinking();

    private PairingSession? _pairing;
    private TextBlock? _codeText;

    /// <summary>Status line, kept so a rejected attempt can go back to reporting progress.</summary>
    private TextBlock? _linkStatus;

    /// <summary>
    /// Write to whichever status label is on screen right now. Redrawing the dialog after a
    /// rejected pairing replaces the label, and a captured reference would quietly go on
    /// updating the discarded one while the visible screen said nothing.
    /// </summary>
    private void SetLinkStatus(string text)
    {
        if (_linkStatus is not null) _linkStatus.Text = text;
    }

    private void BeginLinking()
    {
        // The session owns the code and counts wrong answers, so a short code cannot be
        // ground at by something on the network retrying forever.
        _pairing = new PairingSession(Trust.Identity.PublicKey);

        BuildLinkingUi(startListening: true);
    }

    /// <summary>
    /// Draw the waiting screen. Split out from starting the listener so that a pairing the
    /// phone rejected can put this back on screen without tearing down the socket and
    /// forcing a new code.
    /// </summary>
    private void BuildLinkingUi(bool startListening)
    {
        if (_pairing is null) return;

        OverlayTitle.Text = "Link a device";
        OverlayCard.MaxWidth = 560;
        OverlayBody.Children.Clear();
        _fieldEditors.Clear();

        AddCompressParagraph(
            "On your phone, open Lockwell, choose Link a PC, and pick this computer from " +
            "the list it finds. Then type the code below. If your phone does not find it, " +
            "the addresses underneath can be typed in by hand instead.");

        OverlayBody.Children.Add(BuildCodeCard(_pairing.Code));

        // Filled in by the firewall preflight, which runs off the UI thread because
        // enumerating firewall policy is a slow COM walk over hundreds of rules.
        var firewallHost = new StackPanel();
        OverlayBody.Children.Add(firewallHost);

        OverlayBody.Children.Add(BuildAddressCard());

        var status = new TextBlock
        {
            Text = "Waiting for your phone. Leave this open.",
            Style = (Style)FindResource("Caption"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 0),
        };
        OverlayBody.Children.Add(status);
        _linkStatus = status;

        OverlayBody.Children.Add(new TextBlock
        {
            Text = "This PC stays discoverable and keeps waiting until you close this window. " +
                   "The code carries no vault data.",
            Style = (Style)FindResource("Caption"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 14, 0, 0),
        });

        OverlaySave.Visibility = Visibility.Collapsed;
        _overlaySave = null;

        ShowOverlay();
        _ = CheckFirewallAsync(firewallHost);

        if (startListening) _ = ListenForPairingAsync(status);
    }

    // ------------------------------------------------------- dialog pieces

    private Border BuildCodeCard(string code)
    {
        var codeCard = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(20, 18, 20, 18),
            Margin = new Thickness(0, 14, 0, 0),
            Background = (Brush)FindResource("Bg1"),
            BorderBrush = (Brush)FindResource("Accent"),
            BorderThickness = new Thickness(1),
        };

        var codeStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        codeStack.Children.Add(new TextBlock
        {
            Text = "PAIRING CODE",
            Style = (Style)FindResource("Overline"),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        _codeText = new TextBlock
        {
            Text = code,
            FontSize = 34,
            FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Consolas"),
            Foreground = (Brush)FindResource("Accent"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        };
        codeStack.Children.Add(_codeText);
        codeStack.Children.Add(new TextBlock
        {
            Text = $"This PC: {Trust.SelfName}",
            Style = (Style)FindResource("Caption"),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0),
        });

        codeCard.Child = codeStack;
        return codeCard;
    }

    /// <summary>
    /// Every address a phone could reach this PC on, rather than one guess. The listener
    /// binds <see cref="IPAddress.Any"/> and accepts whichever one the phone actually
    /// used, so listing them all costs nothing and removes the guesswork entirely.
    /// </summary>
    private Border BuildAddressCard()
    {
        IReadOnlyList<LocalAddress> addresses = LocalNetwork.Detect();

        var card = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(18, 16, 18, 16),
            Margin = new Thickness(0, 12, 0, 0),
            Background = (Brush)FindResource("Bg2"),
            BorderBrush = (Brush)FindResource("GlassStroke"),
            BorderThickness = new Thickness(1),
        };

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = addresses.Count > 1 ? "ADDRESSES TO TRY ON THE PHONE" : "ADDRESS TO TYPE ON THE PHONE",
            Style = (Style)FindResource("Overline"),
        });

        if (addresses.Count == 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "This PC does not appear to be on a network. Connect it to the same " +
                       "Wi-Fi or router as your phone and reopen this screen.",
                Style = (Style)FindResource("Caption"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0),
            });
            card.Child = stack;
            return card;
        }

        int index = 0;
        foreach (LocalAddress address in addresses)
        {
            var row = new Grid { Margin = new Thickness(0, index == 0 ? 10 : 8, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var value = new TextBlock
            {
                Text = address.Address,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 16,
                // The first entry is the likeliest to work, so it is the one that reads
                // as the answer. The rest stay visible as alternatives, not noise.
                Foreground = (Brush)FindResource(index == 0 ? "Accent" : "Text"),
                FontWeight = index == 0 ? FontWeights.Bold : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            row.Children.Add(value);

            var kind = new TextBlock
            {
                Text = $"{address.KindLabel}  ·  {address.InterfaceName}",
                Style = (Style)FindResource("Caption"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource(address.IsVirtual ? "Faint" : "Muted"),
            };
            Grid.SetColumn(kind, 1);
            row.Children.Add(kind);

            stack.Children.Add(row);
            index++;
        }

        if (addresses.Count > 1)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "This PC has more than one network connection. Start with the first; " +
                       "if the phone cannot reach it, try the next one down.",
                Style = (Style)FindResource("Caption"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 12, 0, 0),
            });
        }

        // A cable is the answer when there is no usable Wi-Fi, when the network blocks
        // devices from seeing each other, or when someone simply would rather not put
        // this on a wireless network at all. Nothing else about linking changes: the
        // tether is a network, so the same discovery and the same handshake run over it.
        bool tethered = addresses.Any(a => a.IsUsb);
        stack.Children.Add(new TextBlock
        {
            Text = tethered
                ? "A phone is connected by cable. That is the entry above marked USB, and " +
                  "it is the most direct link there is -- no Wi-Fi involved."
                : "No Wi-Fi, or a network that keeps devices apart? Plug the phone in by " +
                  "cable and turn on USB tethering on it. Lockwell links over that the " +
                  "same way, and it will appear here as a USB connection.",
            Style = (Style)FindResource("Caption"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource(tethered ? "Accent" : "Faint"),
            Margin = new Thickness(0, 12, 0, 0),
        });

        card.Child = stack;
        return card;
    }

    /// <summary>
    /// Check whether Windows will even let the phone's connection reach us, and say so
    /// before the user spends a timeout finding out. A dropped connection is
    /// indistinguishable from a network fault at the phone's end, so the PC is the only
    /// place this can be diagnosed honestly.
    /// </summary>
    private async Task CheckFirewallAsync(Panel host)
    {
        FirewallVerdict verdict = await Task.Run(WindowsFirewall.Inspect);
        if (!verdict.WouldDropIncoming) return;

        await Dispatcher.InvokeAsync(() =>
        {
            // The dialog may have been closed while the check was running.
            if (Overlay.Visibility != Visibility.Visible) return;
            host.Children.Add(BuildFirewallWarning(verdict, host));
        });
    }

    private Border BuildFirewallWarning(FirewallVerdict verdict, Panel host)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(18, 16, 18, 16),
            Margin = new Thickness(0, 12, 0, 0),
            Background = (Brush)FindResource("Bg2"),
            BorderBrush = (Brush)FindResource("Warning"),
            BorderThickness = new Thickness(1),
        };

        var stack = new StackPanel();

        var heading = new StackPanel { Orientation = Orientation.Horizontal };
        heading.Children.Add(new TextBlock
        {
            // Escaped rather than a literal glyph: literal icon characters in this
            // project have been mangled into unrenderable text by editing before.
            Text = "",
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 14,
            Foreground = (Brush)FindResource("Warning"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        });
        heading.Children.Add(new TextBlock
        {
            Text = "Windows Firewall will block this",
            Style = (Style)FindResource("Body"),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        stack.Children.Add(heading);

        string explanation = verdict.State == FirewallState.Blocked
            ? "Windows asked once whether to allow Lockwell through the firewall, and the " +
              "answer was no. It wrote that down as a permanent rule and it will never ask " +
              "again, so this is the only place it can be put right.\n\n" +
              "Until it is, your phone cannot reach this PC at all. It will wait, time out, " +
              "and blame the network, which is not what is wrong."
            : "Nothing currently permits incoming connections to Lockwell, and Windows " +
              "refuses them by default. Your phone will wait, time out, and blame the " +
              "network, which is not what is wrong.";

        if (WindowsFirewall.OnPublicNetwork())
        {
            explanation += " Windows also has this network marked as public, which denies " +
                           "incoming connections on principle.";
        }

        stack.Children.Add(new TextBlock
        {
            Text = explanation,
            Style = (Style)FindResource("Caption"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
        });

        stack.Children.Add(new TextBlock
        {
            Text = "The fix allows one port, incoming, from this network only. Nothing " +
                   "outside your network can reach Lockwell through it, and it opens no " +
                   "other program.",
            Style = (Style)FindResource("Caption"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });

        var result = new TextBlock
        {
            Style = (Style)FindResource("Caption"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 10, 0, 0),
        };

        var fix = new Button
        {
            Style = (Style)FindResource("AccentButton"),
            Content = "Allow Lockwell through the firewall",
            Height = 36,
            MinWidth = 240,
            HorizontalAlignment = HorizontalAlignment.Left,
            FontSize = 12,
            Margin = new Thickness(0, 14, 0, 0),
        };
        fix.Click += async (_, _) =>
        {
            fix.IsEnabled = false;

            // Changing firewall policy needs administrator rights, so Windows asks in its
            // own dialog. Declining is a legitimate answer and leaves everything as it was.
            if (!WindowsFirewall.TryRepairElevated())
            {
                result.Text = "Nothing was changed. You can allow it yourself in Windows " +
                              "Defender Firewall, or run the command shown in the docs.";
                result.Foreground = (Brush)FindResource("Muted");
                result.Visibility = Visibility.Visible;
                fix.IsEnabled = true;
                return;
            }

            result.Text = "Asking Windows to apply the change. Give it a moment, then " +
                          "re-check.";
            result.Foreground = (Brush)FindResource("Muted");
            result.Visibility = Visibility.Visible;

            // Re-inspect rather than claim success: the elevation prompt may have been
            // declined after it appeared, and saying "fixed" when it is not would send
            // the user back to hunting an imaginary network fault.
            await Task.Delay(TimeSpan.FromSeconds(3));
            FirewallVerdict after = await Task.Run(WindowsFirewall.Inspect);

            if (!after.WouldDropIncoming)
            {
                host.Children.Clear();
                var ok = new TextBlock
                {
                    Text = "Firewall updated. Incoming connections to Lockwell are allowed " +
                           "on this network.",
                    Style = (Style)FindResource("Caption"),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)FindResource("Accent"),
                    Margin = new Thickness(0, 12, 0, 0),
                };
                host.Children.Add(ok);
                return;
            }

            result.Text = "Still blocked. The change was not applied, or another rule is " +
                          "also blocking it.";
            result.Foreground = (Brush)FindResource("Warning");
            fix.IsEnabled = true;
        };

        stack.Children.Add(fix);
        stack.Children.Add(result);

        card.Child = stack;
        return card;
    }

    // ------------------------------------------------------------ listening

    /// <summary>
    /// Wait for a phone, for as long as this dialog is open.
    ///
    /// Two things changed here after watching a real pairing fail. It used to accept one
    /// connection and then stop listening entirely, so a single mistyped character ended
    /// the attempt and left the PC looking like it had gone away. And it gave up after
    /// five minutes with no warning, which is not long enough to walk to another room and
    /// find your phone. Now it keeps accepting until it succeeds or the user closes the
    /// dialog, and every failed attempt says what was wrong and goes back to waiting.
    /// </summary>
    private async Task ListenForPairingAsync(TextBlock status)
    {
        if (_pairing is null) return;
        PairingSession pairing = _pairing;

        StopListening();
        _linkCancel = new CancellationTokenSource();
        CancellationToken token = _linkCancel.Token;

        // Answer "any Lockwell PCs there?" for exactly as long as we are waiting to pair,
        // and no longer. Announcing all day would tell the whole network which machine
        // holds a vault, for no benefit.
        _ = LocalDiscovery.RespondAsync(() => new DiscoveryBeacon
        {
            Name = Trust.SelfName,
            Fingerprint = Trust.Identity.Fingerprint,
            Port = SyncProtocol.DefaultPort,
            AcceptingPairing = true,
        }, token);

        try
        {
            _linkListener = new TcpListener(IPAddress.Any, SyncProtocol.DefaultPort);
            _linkListener.Start();
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            await Dispatcher.InvokeAsync(() => SetLinkStatus(
                "Another copy of Lockwell is already waiting for a device. Close it and try again."));
            return;
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() => SetLinkStatus("Could not listen: " + ex.Message));
            return;
        }

        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using TcpClient client = await _linkListener.AcceptTcpClientAsync(token);
                    using NetworkStream stream = client.GetStream();

                    await Dispatcher.InvokeAsync(() => SetLinkStatus("Device found. Verifying..."));

                    // Introduce ourselves first: the phone needs this public key both to
                    // authenticate us and to salt the pairing code it was given.
                    await PairingGreeting.SendAsync(
                        stream, Trust.Identity.PublicKey, Trust.SelfName, token);

                    // Derived per attempt, because a rotation replaces the code and every
                    // later guess has to be checked against the one now on screen.
                    byte[] secret = pairing.Secret();

                    HandshakeResult result = await Handshake.RespondAsync(
                        stream, Trust.Identity, HandshakeMode.Pair, secret, cancellationToken: token);

                    pairing.RecordSuccess();

                    // Show the code and wait. The person is holding the phone, so they
                    // confirm there; this end only needs to display the same number and
                    // do what it is told.
                    await Dispatcher.InvokeAsync(() => ShowSessionCode(result, status));

                    using var session = new SyncSession(stream, result);
                    (bool accepted, string deviceName) =
                        await PairingConfirmation.ReceiveAsync(session, token);

                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (!accepted)
                        {
                            RestoreCodeCard();

                            // Redrawing replaced the label, so report through the new one.
                            if (_linkStatus is not null)
                            {
                                _linkStatus.Text = "The codes did not match on the phone, so " +
                                                   "nothing was linked. Still waiting.";
                            }
                            return;
                        }

                        if (deviceName.Length == 0) deviceName = "Phone";
                        CompletePairing(result, deviceName);
                    });

                    if (accepted) return;
                    continue;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (HandshakeException ex)
                {
                    // A failed attempt is not the end of the session. Say what went wrong
                    // and go straight back to waiting, so the user can simply retype.
                    bool wrongCode = ex.Reason == HandshakeFailure.BadPairingSecret;
                    bool rotated = wrongCode && pairing.RecordFailure();

                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (rotated && _codeText is not null)
                        {
                            _codeText.Text = pairing.Code;
                            SetLinkStatus(
                                "Too many wrong codes, so this one has been replaced. " +
                                "Type the new code above.");
                            return;
                        }

                        SetLinkStatus(ex.Reason switch
                        {
                            HandshakeFailure.BadPairingSecret =>
                                $"That code did not match. {pairing.AttemptsRemaining} " +
                                "attempt(s) left before a new code is generated.",
                            HandshakeFailure.VersionMismatch =>
                                "That device runs a different version of Lockwell. Still waiting.",
                            _ => "That device could not be verified. Nothing was linked -- still waiting.",
                        });
                    });
                }
                catch (Exception ex) when (ex is IOException or SocketException)
                {
                    await Dispatcher.InvokeAsync(() => SetLinkStatus(
                        "A device disconnected part-way through. Still waiting."));
                }
            }
        }
        finally
        {
            StopListening();
        }
    }

    /// <summary>
    /// Show the number the phone is also showing, and wait.
    ///
    /// Nothing is confirmed here. The person comparing the two screens is holding the
    /// phone, so that is where the question is asked; this end only has to display the
    /// same number and act on the answer. Asking twice was extra work that verified
    /// nothing extra.
    /// </summary>
    private void ShowSessionCode(HandshakeResult result, TextBlock status)
    {
        OverlayTitle.Text = "Check this code on your phone";
        OverlayBody.Children.Clear();
        _fieldEditors.Clear();

        AddCompressParagraph(
            "Your phone is showing a six-digit code. It must be the same as the one below. " +
            "If it is not, tap \u201cThey do not match\u201d on the phone: something else answered.");

        var fp = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18, 16, 18, 16),
            Margin = new Thickness(0, 14, 0, 0),
            Background = (Brush)FindResource("Bg1"),
            BorderBrush = (Brush)FindResource("Accent"),
            BorderThickness = new Thickness(1),
        };
        fp.Child = new TextBlock
        {
            // Derived from the finished handshake, so both devices arrive at the same
            // number. This used to show the peer's identity fingerprint, which meant each
            // screen displayed a different key and the two could never match.
            Text = result.SessionCode,
            FontSize = 38,
            FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Consolas"),
            Foreground = (Brush)FindResource("Accent"),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        OverlayBody.Children.Add(fp);

        OverlayBody.Children.Add(new TextBlock
        {
            Text = "Waiting for you to confirm on the phone...",
            Style = (Style)FindResource("Caption"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 0),
        });

        OverlaySave.Visibility = Visibility.Collapsed;
        _overlaySave = null;
    }

    /// <summary>Put the pairing code back after a rejected attempt, so another try can be made.</summary>
    private void RestoreCodeCard()
    {
        // The socket is still open and still waiting, so this only redraws.
        BuildLinkingUi(startListening: false);
    }

    /// <summary>The phone said the codes match. Add the device and say so.</summary>
    private void CompletePairing(HandshakeResult result, string deviceName)
    {
        OverlayTitle.Text = "Device linked";
        OverlayBody.Children.Clear();
        _fieldEditors.Clear();

        var nameBox = AddLabeledInputWithPlaceholder(
            "Name this device", "e.g. My phone", deviceName, topMargin: 4);

        // Choosing the window here, while the user is looking at the device and thinking
        // about what it is, beats leaving it to a global setting they will never revisit.
        // A phone in a pocket and a machine used at weekends want different answers.
        OverlayBody.Children.Add(new TextBlock
        {
            Text = "DROP THIS LINK IF IT GOES QUIET FOR",
            Style = (Style)FindResource("Overline"),
            Margin = new Thickness(0, 20, 0, 8),
        });

        var expiryRow = new StackPanel { Orientation = Orientation.Horizontal };

        RadioButton ExpiryOption(string text, bool selected)
        {
            var radio = new RadioButton
            {
                Content = text,
                GroupName = "newDeviceExpiry",
                IsChecked = selected,
                Foreground = (Brush)FindResource("Text"),
                Margin = new Thickness(0, 0, 18, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            expiryRow.Children.Add(radio);
            return radio;
        }

        RadioButton week = ExpiryOption("7 days", false);
        RadioButton month = ExpiryOption("30 days", true);
        RadioButton quarter = ExpiryOption("90 days", false);
        RadioButton never = ExpiryOption("Never", false);

        OverlayBody.Children.Add(expiryRow);

        OverlayBody.Children.Add(new TextBlock
        {
            Text = "You can change this per device later. Expiring a link removes nothing " +
                   "from the device; it just has to be paired again.",
            Style = (Style)FindResource("Caption"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });

        OverlaySave.Visibility = Visibility.Visible;
        OverlaySave.Content = "Done";

        _overlaySave = () =>
        {
            string name = nameBox.Text.Trim();
            if (name.Length == 0) { ShowInlineOverlayError("Give the device a name."); return; }

            int? days = null;
            bool neverExpires = never.IsChecked == true;

            if (week.IsChecked == true) days = 7;
            else if (month.IsChecked == true) days = 30;
            else if (quarter.IsChecked == true) days = 90;

            Trust.Add(result.PeerPublicKey, name, "phone", days, neverExpires);
            CloseOverlay();
            RefreshDevices();
        };
    }

    private void StopListening()
    {
        _pairing = null;
        _codeText = null;

        try { _linkListener?.Stop(); } catch { /* already down */ }
        _linkListener = null;

        try { _linkCancel?.Cancel(); } catch { /* already cancelled */ }
        _linkCancel?.Dispose();
        _linkCancel = null;
    }
}
