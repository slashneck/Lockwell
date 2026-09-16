# Changelog

All notable changes to Lockwell. Newest first.

---

## 1.2.0 - 2026-09-16

### Lockwell is open source

- **The source code is public, under the GPLv3.** A vault app asks for more trust than
  almost any other kind of program, and "trust us" is a poor answer when the whole point
  is that nobody has to. Everything is now readable: the Argon2id parameters, the fact
  that your master password never becomes a `string` the garbage collector could leave
  lying in memory, the single network call the app makes, and the absence of any recovery
  backdoor. You can build it yourself and compare.

  Nothing about the encryption changed, and nothing needed to. Lockwell's security has
  never rested on the code being secret. It rests on your master password and on Argon2id
  and AES-256-GCM, all of which are public algorithms that are stronger for having been
  picked apart in the open. If reading the source made your vault easier to break, the
  design would have been wrong to begin with.

  The licence is deliberate: anyone may use, study, change and share Lockwell, and anyone
  who ships a modified build has to publish their changes too.

- **Credits where they were missing.** Third-party components are listed with their
  licences in THIRD-PARTY-NOTICES.md, and the licence now ships with the app rather than
  living only in the repository.

### Updates

- **Lockwell can tell you when there is a new version.** It checks once a day, a few
  seconds after you unlock, and never interrupts what you are doing. A badge appears on
  Settings. That is the whole of it.

- **Nothing installs unless it is signed.** Every release carries a manifest signed with a
  key that is kept offline, and the matching public key is built into the app. A release
  that is not signed by that key is refused, no matter where it came from or what the
  download page says about it. The size and the SHA-256 of the package are checked too,
  and setup checks all three again before it writes a single file, because the app's word
  for it is not good enough in the process that does the writing.

- **Downloads happen when you say so.** The update is fetched in the background, but it is
  never installed until you click. Silently swapping the files under a running vault app
  was considered and rejected.

- **You can turn the check off.** Settings, Updates. With it off, Lockwell makes no
  network connections at all.

- **The threat model now says this out loud.** It used to claim Lockwell made no network
  calls whatsoever, which stopped being true the moment this feature existed. It now
  describes exactly what the check sends, what GitHub can see, and why installing an
  update is the riskiest thing the app does.

### Setup

- **Setup was redesigned.** It shares the app's palette and type instead of looking like
  a different product, and the steps are listed down the side so you can see where you
  are and what is left. The window is larger and the copy is shorter.

- **The terms screen is a plain summary of the licence**, plus the two things worth
  knowing before you trust a vault with anything: there is no way to recover your data if
  you lose both the password and the recovery key, and setup fetches a signed package over
  the network.

- **No effects over text anywhere in setup.** The shell had a drop shadow sitting above
  every word in the window, which is the same bug that was fixed three times in the app.
  The check that catches it now reads setup's markup too.

### Security & privacy

- **A large part of the app was shipping unobfuscated, and it was the wrong part.**
  `Lockwell.Core` holds every line of the cryptography, the vault format and the whole
  pairing and transfer stack, and it was never listed in the obfuscation config, so
  release builds shipped it untouched. It is obfuscated now, in the same pass as the rest
  of the app so cross-references stay consistent. Two checks were added to the release
  build: one that fails if the core's string constants are readable, and one that creates,
  encrypts and reopens a vault through the obfuscated code before the build is allowed to
  pass, because an unreadable build that cannot open your vault would be far worse than a
  readable one.

- **Release packages are checked for anything identifying the machine that built them.**
  Not just files that should not be there, but the contents of the shipped binaries, in
  both ASCII and UTF-16, because an absolute source path baked into an assembly is a leak
  that no directory listing would ever show. Builds no longer emit those paths in the
  first place.

### Linking

- **The pairing code is six digits.** It was ten characters from a 32-symbol alphabet,
  which is more entropy than the job needs and more typing than anyone wants. Six digits
  gets a number pad on the phone and is read off a screen across a room without squinting.

  Six digits is safe here because of what the code actually holds up, which is worth being
  precise about. It is not what keeps a transfer secret: that is the key exchange between
  the two devices, and someone who learned the code still has neither private key. It is
  not what proves who you connected to either: that is the fingerprint both screens show at
  the end, which anyone standing in the middle cannot fake on both sides at once. The code
  exists to stop a stranger on your network starting a pairing at all, so the only request
  you ever see is one you began.

  It is now backed by a guess limit that is written down rather than assumed. Three wrong
  answers and the code is thrown away and replaced on screen. **This closes a hole made
  earlier in this release:** the code's length used to rely on the PC accepting a single
  connection and then closing, and when that was changed so a typo no longer ended the
  whole attempt, the limit vanished with it and left pairing open to unlimited guessing.

- **Linking over a USB cable.** Turn on USB tethering on the phone and the cable becomes a
  network, which Lockwell links over exactly as it does Wi-Fi -- same discovery, same
  handshake, same encryption. Nothing goes near a wireless network at all. A tethered
  phone is recognised, labelled "USB cable" rather than being mistaken for a virtual
  adapter, and offered first, because a direct cable is the best link there is. Both apps
  now say so on the linking screen instead of leaving it to be discovered.


- **Your phone finds your PC by itself.** Typing an IP address was the worst step in
  linking: it asks someone to read a number off one screen and copy it into another, it
  fails silently on a machine with more than one address, and it gives no clue when the
  other end simply is not listening. The phone now asks the local network who is there and
  lists the PCs that answer, by name, with "ready to pair" beside them. You tap yours.
  Typing an address still works and is one button away, for networks that block broadcasts.

  A PC only answers while its own "Link a device" window is open. A vault app that
  announced itself to the network all day would be telling everyone which machine is worth
  attacking, for nothing in return. The reply carries a name, a fingerprint and a port;
  no vault data, and not even the public key, which still arrives over the encrypted
  connection to a device that actually connected. Being found proves nothing on its own:
  the same one-time code and the same fingerprint comparison still apply.

- **One mistyped character no longer ends the attempt.** The PC used to accept a single
  connection and then stop listening entirely, so a typo left it looking like it had gone
  away, and the only way forward was to close the window and start again with a new code.
  It now keeps waiting until you close it, and a failed attempt says what was wrong and
  goes straight back to waiting.

- **The PC no longer gives up after five minutes.** That is not long enough to walk to
  another room and find your phone. It waits as long as the window is open.

- **The firewall warning now says what actually happened.** When Windows asks whether to
  allow an app through the firewall and the answer is no, it writes that down permanently
  and never asks again. Lockwell now says exactly that, because it is the only place left
  that can put it right. It removes the blocking rules and adds the two narrow allows the
  feature needs, incoming only, from the local network only.

- Verified between a real PC and a real phone over real Wi-Fi: the phone found the PC with
  no address typed, and both ends showed the same fingerprint.

### Phone app

- **Settings is a settings screen now, not a form.** It was a column of full-width
  outlined buttons, which reads as a list of things to fill in rather than a list of things
  you occasionally change, and each button's label had to be a whole sentence. Related
  settings are now grouped inside one surface separated by hairlines, with the setting on
  the left and its current value on the right, so the eye can skip a whole group instead of
  reading every line. Fingerprint unlock became a switch, which is what it always was.

- **The Devices tab explains itself.** It had one paragraph and a button. It now has a
  designed empty state that says the vault is complete on its own rather than implying
  something is missing, and three short rows covering what linking actually does: no
  server, each device keeps its own key, and pairing on its own moves nothing.

- **A shared set of design tokens.** Grouped-list surfaces, dividers, row titles, row
  values, chevrons and a section heading with its spacing built in, so screens agree with
  each other instead of each inventing its own margins.


- **Every kind of media is accepted now.** Two things were wrong. "Photo or video" used the
  system photo picker, which takes exactly one item and refuses video outright, so it could
  add neither several photos nor any video at all. And the type of each file was taken from
  whatever the picker reported, which is frequently "application/octet-stream" or nothing,
  so GIFs and MP4s landed in the vault as anonymous files with no preview and no player.
  Files are now identified by their extension, covering GIF, WebP, HEIC, AVIF, MP4, MOV,
  MKV, WebM, FLAC, Opus and the rest, and several can be picked at once.

- **Animated GIFs and WebPs animate.** They were being decoded to a single frame on the way
  to the screen, which is a reasonable thing to do to a photograph and the wrong thing to
  do to an animation.

### Desktop

- **The sharpness fix was only half applied, and the other half was the worse half.**
  Cards had their drop shadow removed last release; `HeroCard` kept its, and `HeroCard` is
  the unlock screen, the profile gate, the create-vault screen, every dialog in the app and
  the whole entry detail pane. So the screens that were still soft were the first one you
  see and the one you read every day.

  It was doing something worse there than blurring text. An effect casts its shadow from
  the alpha of the bitmap it renders the subtree into, and a hero card's own fill was white
  at 2 to 8 percent, so what got silhouetted was not the card. It was the contents: the
  word Lockwell on the unlock screen, every field row on an entry, and the Unlock button
  each carried their own black smear, which is not a shadow anyone designed. The card now
  has a fill dark enough to read as raised on its own, and nothing behind the text.

- **Three interaction states blurred the one thing you were looking at.** Hovering a ghost
  button, focusing a text box and selecting a row each added an accent glow to the border
  that wraps that control's own label, so the button you pointed at, the box you typed
  into and the row you picked all went soft at the moment of use. All three now shift
  their border and fill instead, which is visible for the same reason and costs nothing.

- **Checkboxes are Lockwell's now.** There was no checkbox style at all, so every one in
  the app was the stock Windows control: a white square with a black tick, on settings, the
  create-vault screen, the launch scan, the risk gate before unlocking, the linking rules
  and the compress dialog. It was the one control that looked borrowed.

- **Keyboard focus is visible.** Nothing in the app drew a focus state, so tabbing through
  a dialog showed only Windows' dotted rectangle, and on a custom-drawn button that reads
  as a rendering fault rather than as "you are here". Buttons, inputs, links and
  checkboxes each show focus in their own shape now.

- **Tooltips match the app.** All 46 of them were the pale system tooltip.

- **The scrollbar is finished.** The thumb ran edge to edge with no inset, which is why the
  settings dialog looked like it had a loose bar floating over its text, and it hard-coded
  a vertical track, so a horizontal scrollbar would have rendered as a vertical smear. It
  is inset in a gutter now, brightens under the pointer, and lays itself out either way up.

- **The brand gradient means something again.** The teal-to-violet gradient was the default
  colour for every title in the app, including the name of each individual entry. A
  gradient that appears on everything stops reading as the brand and starts reading as
  what titles happen to look like. It is the wordmark and the home screen now; page titles
  and item titles are solid, and agree with the page titles that already were.

- **The sidebar entry count disagreed with itself.** The same labelled number had two
  writers. One counted everything in the vault, the other counted the rows in the All items
  list, which groups media into folders rather than listing files, so the figure changed as
  you moved between sections and neither reading was the one on the label. It counts the
  whole vault now, which is also the sum of the two figures on the home screen.

- **The maximize button showed the wrong icon until you clicked away.** The glyph was
  refreshed when the window lost focus rather than when its state changed. The window also
  kept its rounded corners and hairline border while maximized, leaving a bite out of each
  screen corner; it squares off now.

- **A check that the blur cannot come back a third time.** Section 56 reads the desktop
  markup and fails on any effect that covers text, whether it is set on a style, on an
  element, or applied by a hover or focus trigger. A screenshot cannot catch this -- a
  capture rasterises text without ClearType either way, so the broken and fixed builds look
  identical in a PNG -- which is exactly why it had to be found by reading the source twice
  already. Effects on shapes, on empty borders and behind a single icon glyph stay allowed.

### Fixed

- **Sending items stopped working: "tick at least one file to send", when files were
  picked.** Introduced when the picker was rebuilt. The old picker collected its checkboxes
  when you pressed the button, and that step began by emptying the queue. The new picker
  fills the queue as you click tiles, but the emptying line stayed -- so pressing the button
  threw away the whole selection and then reported it as empty.

- **Auto-deletion never happened.** Choosing "remove from the phone after 1 day" did
  nothing. The sending side took the number of days, built the entry to send, and never
  wrote the expiry onto it, so the phone received an item with no expiry and kept it
  forever. Nothing failed and nothing was logged; the only symptom was a file that would
  not go away. The receiving half had been correct all along, which is what made it
  invisible.

- **The desktop looked slightly out of focus.** Cards carried a drop shadow, and in WPF an
  effect forces the element's whole subtree -- all of its text -- through an intermediate
  bitmap before compositing. Text rendered that way loses ClearType and gets resampled.
  Since almost everything sits in a card, almost every label in the app was soft. On a
  near-black background the shadow was contributing nothing visible anyway; depth now comes
  from the border and fill, and the text is sharp.

### Sending files

- **Media tiles show what a device already has.** The mark was on the entry list only, but
  media is what actually gets sent, so a folder of photos gave no clue which ones the phone
  was already holding.


- **You can see what has actually been sent.** Items carry a mark once a linked device has
  taken them, on the PC list and on the phone's tiles. The phone also shows an upward arrow
  for something waiting to go on the next sync, so "queued" and "gone" are different
  states rather than both being invisible.

  Marks are written when a transfer completes, not when something is queued. A device that
  never asked for an item does not have it, and recording intentions instead of facts would
  make the indicator worse than none.


- **The code shown when linking never matched, and pairing went ahead anyway.** Each device
  displayed the *other* one's identity fingerprint: the PC showed the phone's, the phone
  showed the PC's. Those are two different keys, so the two screens could never agree no
  matter how correct the pairing was, and since nothing was actually compared, a mismatch
  changed nothing. The step looked like a security check and verified nothing.

  Both devices now show a six-digit code derived from the finished handshake itself. Both
  sides compute it from the same inputs, so they agree only if they genuinely talked to
  each other, and anyone sitting in the middle runs two separate handshakes and cannot make
  both ends land on the same number. A mismatch is now the signal it was always supposed
  to be.

### Linking

- **Confirming happens once, on the device in your hand.** It used to ask on both screens.
  The PC has nothing to verify on its own -- it cannot see the phone -- so only the person
  holding both can compare, and they only need to say so once. The phone asks; the answer
  travels back through the tunnel the handshake already established, which nothing on the
  network can forge or flip. A refusal is sent too, so the PC can say the codes were
  rejected instead of reporting a vague connection problem.

### Sections

- **Two preset sections are gone.** "Banking & finance" and "Work & tools" did nothing a
  Login section does not already do. Existing vaults keep either of them if it still holds
  something -- removing a preset must never remove a secret -- and only the empty ones are
  dropped.

- **"Crypto & wallets" is now "Crypto", and is about the two things worth locking away.**
  A new item asks for an address, a seed phrase and a private key. The wallet's name is not
  a secret and an exchange login belongs in a Login section, so neither takes up a field
  any more. The section icon is a key.

- **A seed phrase is shown as numbered words, not a line of text.** Written out in one line
  it cannot be checked: nobody can confirm word nine of twenty-four in a wall of text, and
  checking is the only reason to open one. The words are numbered in a grid, the way every
  wallet prints them, hidden until asked for and hidden again on leaving.

- **Sections you make yourself sit under their own heading, which folds.** A vault with a
  dozen custom sections used to bury the handful that are always there.

### Secure notes

- **Attachment names are hidden until you ask.** The name is frequently the secret --
  "github-recovery-codes.txt" says exactly what it is and roughly what it is worth to anyone
  glancing at the screen. A tile now shows the kind of file and its size, and opens out to
  the real name when clicked.

- **Deleting an attachment is a button.** It was on a right-click, which is not a thing
  anybody finds.

### Security

- **An unlocked vault can no longer end up in a crash dump.** Windows Error Reporting can
  write a copy of a crashing program's memory to disk and offer to upload it; for an open
  vault that file contains the data key and whatever was decrypted at the time, and it
  outlives the process. Lockwell now excludes itself from error reporting and suppresses the
  crash dialog. This asks Windows not to collect a dump -- it cannot stop a debugger, and
  anyone able to attach one can already read the memory directly.

- **The phone has a threat model.** `docs/THREAT-MODEL-MOBILE.md`, written like the desktop
  one: what it protects against, what it does not, and the parts people get wrong -- in
  particular that overwriting files does not work on flash storage, and that the real risk
  after deleting a photo is the copies elsewhere, not the remnants on the chip.

### Phone app

- **The media tab reads as a gallery.** Hairline gutters instead of card spacing, no frame
  drawn around a photo, and no filename printed across every tile -- a picture is its own
  label, and captions are kept for the things that have no preview. Item counts on the
  folder row.

### Sending files

- **Send straight from the picture you are looking at.** Open something, press Send to
  device, done. With one device linked it goes there; with several it asks which. It is the
  same queue the Devices screen uses, so nothing leaves until that device connects and asks.

- **Rename from the item, not from the list.** Renaming lived only in the folder listing,
  which made naming an unnamed file oddly circular: open it to see what it is, close it,
  find the same tile again in the list, rename it there. The name belongs to the thing on
  screen.

- **Lockwell offers to send a smaller copy.** When a queued file has not been compressed --
  a PNG, a bitmap, a scan -- it offers to shrink it for the transfer. Formats that are
  already lossy are left alone and the question is not even asked, because re-encoding a
  JPEG or an MP4 trades real quality for a few percent.

  **The original is never touched.** The smaller version is made in memory, sent, and gone;
  it is never written back to the vault and there is no compressed copy left behind to
  clean up. Your vault keeps the file it always had, at full quality, and only the transfer
  is smaller. If shrinking fails, or turns out to save almost nothing, the original is sent
  instead.

### Phone app

- **Arrows for moving between items.** Swiping already worked, but nothing said so, and a
  video surface or the scrubber could swallow a sideways drag before the gesture fired.
  Arrows appear at the edges whenever there is more than one item, and they keep working
  while zoomed in, where swiping deliberately means panning instead.


- **You can see what you are sending.** The picker was a list of titles with checkboxes,
  which could not answer the only question being asked: what is this? A camera roll is full
  of files called IMG_4821 and a column of those is guesswork. It is now the vault's own
  folders, browsable, with real thumbnails. Click a file to pick it, open a folder to look
  inside, right-click a folder to take everything in it. The running total shows how many
  files and how much data is queued.

### Media

- **Animations play at a chosen speed, and it sticks.** The rate now defaults to 20 frames
  per second rather than following the file, because a great many animated files declare
  timings no viewer honours -- 0ms and 10ms are common -- and a steady rate looks closer to
  right far more often than the file's own answer. The chosen rate is remembered rather than
  reset on the next picture, which was the actual complaint: setting one and moving on put
  it straight back.


- **Linking a phone failed with a message blaming the network.** The real cause was
  Windows Firewall. When its "allow this app?" prompt is answered with Cancel, or simply
  ignored until it goes away, Windows writes a permanent rule that blocks the app, and
  a blocked rule beats any rule that allows it. The phone's connection was dropped
  before Lockwell ever saw it, which looks from the phone exactly like being on the
  wrong network. It reported "check both devices are on the same network", and the
  network was fine.

  The Link a device screen now checks before it starts waiting, says plainly that the
  firewall is what is wrong, and offers to fix it. The fix removes the stale blocking
  rules and allows one port, incoming, from the local network only. It needs
  administrator rights, so Windows asks in its own prompt; declining changes nothing
  and is reported as such rather than claimed as success. The same screen can take the
  rule away again, so nothing Lockwell opens is left open without a way back.

  Lockwell itself still runs as a normal program. Running the whole app as
  administrator would not have helped anyway, because firewall rules match on the
  program, not on its privileges.

- **The address shown for linking was a guess, and on some PCs the wrong one.** It was
  found by asking the operating system which network card it would use to reach the
  public internet. A PC with both a cable and Wi-Fi connected has more than one answer,
  and that method returns whichever one happens to win, which may be an address the
  phone cannot reach at all, such as a virtual adapter belonging to a hypervisor.

  Every address the PC can be reached on is now listed, labelled with the connection it
  belongs to, most likely first. Nothing is hidden and nothing is guessed. The listener
  always accepted connections on any of them; only the advice was wrong.

- **The Devices screen would have crashed the first time a device was linked.** A colour
  used to draw the device row existed only in the phone app's palette, not the desktop
  one, and asking for a missing colour throws rather than falling back. The code path
  had never run because linking had never succeeded.

- The compress button on media folders was invisible. Three Segoe icon glyphs had been
  written as literal characters and got mangled into unrenderable text somewhere in
  editing — the buttons were present and clickable, just drawing nothing. The sort
  direction arrows were affected the same way, as were a few "·" separators. All icons
  are now written as escape sequences, which cannot be corrupted the same way.

- **Decrypted media was being left in the app's cache.** Sharing a file out of the vault
  makes the platform copy it somewhere of its own choosing under the cache directory, and
  the routine that clears scratch files only ever looked in Lockwell's own folder. Plain,
  decrypted copies of vault items were therefore sitting on the phone across locks and
  restarts. Four such files were found on a real device during testing, the oldest from a
  previous day.

  The purge now clears the whole cache directory, which is disposable by definition, and
  runs on lock and on launch as before. Verified on the device: four plaintext files
  before, none after.

  The files were in app-private storage throughout, so no other app could read them and
  nothing was indexed by the media scanner. It was still a decrypted copy outliving the
  lock, which is not what the app says it does.

### Phone app

- **A real media library, instead of one flat list.**
  - **Folders**, which can be nested, renamed, and moved. A folder can never be moved
    inside itself; the move is refused rather than repaired afterwards, because allowing
    it would orphan everything underneath.
  - **Deleting a folder keeps what was in it.** The contents move up to where the folder
    was. Deleting the contents too is a separate choice, worded separately, that says how
    many items it will destroy. Tidying up a container is not a reason to lose a photo.
  - **Sorting** by date added, name, size or type, in either direction, remembered per
    vault rather than reset every time the app opens.
  - **A gallery grid** with real previews, a breadcrumb showing where you are, and an
    actions button on every tile.
  - **Item details**: type, exact size, when it was added, which folder it is in, whether
    it came from a PC, and when it expires.
  - Items with a pending expiry now carry a countdown badge in the grid, so nothing ever
    disappears without having said it would.

- **Photos open inside the app, and never become a file.** Tapping an item opens a
  full-screen viewer with pinch-zoom, double-tap zoom, and swiping between items. The
  picture travels from the encrypted attachment, to a byte array, to a bitmap, to the
  screen. It is never written to storage, so no gallery, media scanner, file manager or
  cloud backup can reach it. Previews in the grid work the same way, decoded at the size
  actually needed rather than at full resolution.

  Closing the viewer clears the decoded image, and locking the vault clears every
  decoded preview, so a locked vault holds nothing in the clear.

- **An option to delete the phone's own copy after adding something to the vault.** Off
  unless asked for, offered per item, and only ever after the encrypted copy has been
  written and read back successfully. Verifying first is not ceremony: deleting the
  original before confirming the vault copy is readable would turn a failed import into
  data loss.

  **A limitation, stated rather than hidden:** when a picture is chosen through the
  system photo picker, Android hands the app a copy rather than the original, so Lockwell
  cannot delete it from the gallery itself. It now says exactly that instead of deleting
  a scratch file and reporting success, which is what an earlier version of this change
  would have done. Deleting the real original needs Android's own delete-request flow,
  which is not built yet.

- **A stray light grey rectangle above the item list is gone.** It was a spacer holding
  room for the floating add button, which inherited a near-white background from the
  .NET project template. The spacer has been replaced with ordinary padding, which also
  fixes the spacing being absent when the vault was empty, and the template's rule that
  caused it is now transparent so it cannot happen again.

- **Dialogs are Lockwell's own.** Alerts, confirmations, text prompts and choice lists
  were drawn by Android using Android's theme, and arrived as a flat light grey panel
  on top of near-black pages. They are now built from the app's own components, with
  the same colours, corner radius, spacing and type as the rest of the app, and they
  fade in over a dimmed page. All forty-five of them.

- **The .NET template's purple is gone from the system UI.** The status bar was
  #512BD4, and so were the cursor, selection handles and the underline beneath the
  focused text field on every screen. The status bar now matches the page background,
  so the app runs to the top of the display, and the text fields use Lockwell's
  teal.

### Under the hood

- Local network addresses are worked out in `Lockwell.Core` and covered by the
  verification harness, including the wired-and-wireless case that caused the bad
  advice above. The harness is at 199 checks.

- The pairing handshake has now been run between a real PC and a real phone over a real
  network, rather than only over loopback. Both devices showed the same fingerprint and
  the link completed.
- **Crypto moved into a shared `Lockwell.Core` project.** The Windows app and the
  coming Android app now run the identical Argon2id and AES-256-GCM code rather than
  two implementations that could drift apart.
- **The verification harness is part of the solution.** It previously lived in a temp
  folder and was lost when that folder was cleared, which is a poor home for a crypto
  project's only safety net. Run it with `dotnet run --project Lockwell.Tests`.
- **Local device sync: pairing handshake and encrypted transport**
  (`Lockwell.Core/Sync`). Device identity keys, the pairing QR payload, a three-message
  authenticated handshake, and a ChaCha20-Poly1305 tunnel. Tested over real loopback
  TCP, including the cases that must fail: a guessed pairing secret, an unpaired
  device, a substituted PC answering in place of the real one, and a single flipped
  bit in a frame. Not yet connected to any UI. See `docs/SYNC-PROTOCOL.md`.
- **Android companion app running on a real device** (`Lockwell.Mobile`, .NET MAUI).
  Creates its own encrypted phone vault using the same Argon2id (256 MiB) and
  AES-256-GCM as the desktop app, verified on ARM hardware.
  - **Uninstalling removes everything.** All data lives in the app's private internal
    storage, never `/sdcard` or shared storage, which survive uninstall and leave
    orphaned files behind. Verified end to end: after uninstall the package and its
    entire data directory are gone.
  - Cloud backup and device-to-device extraction are disabled in the manifest, so a
    phone vault can never be swept into a Google account backup.
  - The phone vault has its **own** key, derived from a password set on the phone. The
    PC's data key is never sent to it, so a lost phone reveals nothing about the PC
    vault.
  - **Fingerprint and face unlock**, offered right after you set a password. A
    biometric check releases a key held in the Android Keystore, which unwraps the
    vault key. The password is never stored and never replaced: Argon2id remains the
    only route in if biometrics are turned off, and Android destroys the key by design
    if the enrolled fingerprints change. Uses the platform BiometricPrompt, so no
    third-party biometric dependency is involved.
  - Lockwell's own padlock icon replaces the .NET template artwork, and the interface
    uses the desktop app's colours and typography rather than the MAUI defaults.
  - Minimum Android version is 9.0 (API 28), which is what the built-in BiometricPrompt
    requires.

**The phone app is a vault in its own right, not a companion.** It works with no PC
involved; linking is one feature among several.

  - **Several vaults on one phone**, each with its own name, icon, password and key.
    Create, open, rename and delete them from a vault list. Opening one reveals nothing
    about another, so work and personal can share a phone safely.
  - **Add photos, videos and files** straight from the phone. They are encrypted as they
    land, and can be renamed, exported through the share sheet, or deleted.
  - **Three sections inside a vault**: Items, Devices and Settings.
  - Settings covers fingerprint unlock, changing the password, renaming, storage usage,
    and deleting the vault.
  - Deleting a vault needs the name typed to confirm. Changing the password turns
    fingerprint unlock off, because the stored key was wrapped for the old one.
  - Leaving a vault locks it, rather than leaving it open behind the list.

---

## 1.1.0 — 2026-07-27

Large update focused on the media vault, storage management, and closing two real
privacy gaps. **Existing vaults open unchanged** — nothing in this release alters the
vault format or requires a migration.

### Security & privacy

- **The master password is never held in a .NET string.** It now travels as a
  `SecureString` from the text box all the way into the key derivation, is decoded to
  bytes in unmanaged memory for the moment Argon2id needs it, then wiped. Previously a
  copy could linger in the heap, a crash dump, or the page file until the app closed —
  strings are immutable and cannot be scrubbed. The KDF deliberately has no `string`
  overload, so the leak cannot be reintroduced by accident.
- **Password comparison is constant-time** and never materialises either value.
- **Temp files no longer reveal what you opened.** Scratch files created for video and
  audio playback were named after the attachment id, so a snapshot of `%TEMP%` showed
  exactly which vault items had been viewed, with stable names across sessions. They
  now use random names.
- **Scratch files are purged on startup**, not only on lock and exit — a crash or power
  loss can no longer leave decrypted media sitting on disk until the next lock. If a
  file is still held open by the player, it is queued for deletion at reboot instead of
  being silently left behind.
- **GIFs no longer touch the disk.** They were being written out decrypted purely to
  hand an API a file path; they are now decoded from memory like every other image.
- **Backup timestamps moved inside the encrypted vault.** The date of your last export
  was being recorded in plaintext in `settings.json`, where anyone could read how often
  you back up. It now lives in the encrypted vault and travels with it, so a restored
  backup keeps its real history. Existing timestamps are preserved.
- **Decoded thumbnails are cleared when the vault locks**, and evicted when a file is
  deleted.
- Recovery key text is cleared from the interface after use.
- `THREAT-MODEL.md` expanded with honest sections on what playback exposes, and why
  overwriting a file before deleting it is far weaker on an SSD than it sounds.

### Media vault

- **Drag and drop.** Hold and drag any file or folder onto a folder to move it in.
  Uses the system drag threshold, so a normal click still opens the item. Breadcrumbs
  are drop targets too, so you can drag something back *out* without navigating away.
- **Drag to reorder.** The middle of a tile means "put it inside"; the edges mean
  "insert beside it", with the highlight showing which. Reordering switches the sort to
  Custom automatically, so a hand-made arrangement is not silently discarded.
- **Drag ghost.** A shrunken copy of the tile follows the cursor with a badge showing
  whether the spot under the pointer will accept the drop.
- **Sorting** by name, age, size, or custom, with a direction toggle. Remembered
  between sessions. Folder sizes include everything nested beneath them.
- **Rename from the file itself**, no longer only from folder settings.
- **Video no longer freezes the app when opened.** Decrypting a large file and writing
  it out was happening on the UI thread; it now runs in the background with a progress
  indicator, and cleans up correctly if you navigate away mid-decrypt.
- Volume slider restyled to match the seek bar, with a live percentage.
- Click the empty space around a photo to dismiss it, with a scale-and-fade animation.
- Tile actions are visible at rest instead of appearing only on hover.

### Storage management

- **Storage breakdown.** Click the size on the home screen to drill from the whole
  vault into sections, folders, and individual files — always sorted biggest first,
  with proportional bars and breadcrumbs at every level.
- **By file type view.** Groups every attachment by format and says plainly whether it
  is worth re-encoding. Already-compressed formats like MP3 and JPEG gain almost
  nothing and lose quality; WAV, FLAC, PNG, and BMP are where the space is.

### Compression (new)

Make individual files smaller, on your terms.

- **Per item**, from the storage breakdown, the media viewer, or the tile actions.
- **Or a whole folder at once**, including everything in its subfolders — from the
  "Compress all" button on any folder in the storage breakdown, or the folder tile.
  Never automatic: you always choose.
- **Nothing is downloaded and nothing leaves your PC.** Images use the JPEG and PNG
  encoders built into .NET; audio and video use `Windows.Media.Transcoding`, which is
  part of Windows itself. No third-party codec, no bundled binary, no network code.
- **Runs entirely in memory.** Unlike playback, compression never writes a decrypted
  file to disk at any point.
- **See the result before committing.** "Test this setting" performs the real encode
  and shows the actual before/after size, plus a preview of the image itself.
- **Keeps the original by default**, adding the smaller version alongside so you can
  compare. Replacing in place is opt-in and clearly labelled as permanent.
- **Refuses to make things worse.** Re-encoding can produce a *larger* file; anything
  saving less than 5% is rejected rather than losing detail for nothing.
- **Transparency is detected** — a PNG with an alpha channel stays PNG, because JPEG
  would flatten it onto a solid background.
- Five quality presets and an optional resolution cap, remembered between sessions.
- A one-time explanation of what compression does, dismissible for good. The
  confirmation showing real numbers is never suppressed.

**Bulk runs specifically:**

- The exact scope is stated before anything starts — file count, total size, and a
  per-format breakdown marking which files will likely be skipped.
- **Every file is judged individually.** Anything already efficient is left untouched,
  so running a folder of MP3s or JPEGs does not quietly degrade them. Running the same
  folder repeatedly cannot spiral into generation loss.
- **Stoppable at any point.** Work already completed is saved; the rest is untouched.
- Progress is saved as it goes, so an interruption never loses finished work.
- A summary reports what actually happened: compressed, left alone, unreadable, and
  total space saved.
- Bulk replaces originals — keeping a copy of every file would double the folder — so
  it requires an explicit tick-box confirmation and shows your last backup date.

### Home screen (new)

- Lands here on unlock instead of dropping straight into a list of everything.
- Shows the vault you are in, item and media counts, and the real encrypted size on
  disk.
- **Last backup indicator** in plain language ("Yesterday, 09:15", "6 days ago"),
  turning amber past 30 days, with an export button.

### Devices and pairing (desktop)

- **A dedicated Devices section**, reached from the sidebar. Lists linked phones and
  computers with when each was last seen, when it was linked, and its fingerprint.
  Rename, unlink, and rename this PC.
- **Link a device** shows a short pairing code to type on the phone, then asks you to
  confirm the fingerprint shown on both screens before trusting anything. Matching
  fingerprints are what rule out something else answering on the network.
- The pairing code is stretched with Argon2id before it becomes the handshake secret,
  is valid for one attempt, and expires in minutes. The listening port is only open
  while the linking screen is on screen.
- **Pairings expire after 30 days of silence** by default, and expired ones are dropped
  whenever the Devices screen opens. Can be turned off.
- Unlinking never reaches out to delete anything already on the other device, and the
  dialog says so rather than implying it does.

### Linking (both sides)

- **The phone can now link to a PC.** Enter the PC's address and the code it shows,
  compare the fingerprint on both screens, and confirm. The same handshake from
  `Lockwell.Core` runs on both ends.
- The PC introduces itself with its public key before the handshake, because the phone
  needs it both to authenticate the PC and to salt the pairing code. Sending it in the
  clear is safe: an impostor substituting their own key derives a different secret and
  cannot reach the phone's. The code is what makes the exchange mean anything, and the
  fingerprint comparison is what proves which key was actually used.
- **Links are per vault.** Each vault on the phone has its own device identity, so
  linking a work PC to a work vault creates no relationship to a personal one — they
  cannot even be correlated by public key.
- Tested end to end over loopback, including a mistyped code linking nothing and an
  impostor who knows the PC's public key still getting nowhere.

### Transfer

- **Items can now be moved from a PC to a linked phone.** On the PC: Devices, pick a
  device, Send items, tick what it may receive. On the phone: Devices, tap the PC,
  Sync now.
- The phone dials out and the PC listens, because a PC's address is stable and already
  stored on the phone from linking, while a phone's moves around.
- **The receiver decides what arrives.** The PC offers a list of metadata; the phone
  replies with the ids it wants; only those are streamed. A sender cannot push
  something the receiver did not ask for, which is what keeps "the phone only holds
  what you chose" true from both ends.
- **Nothing already held is re-sent.** Items carry a hash of their contents, so an
  identical copy is skipped without transferring a byte — and the PC never even
  decrypts it.
- **Arrivals are re-encrypted with the phone's own key.** The PC's data key never
  crosses the wire, so a compromised phone still reveals nothing about the PC vault.
- Contents that do not match the offered hash are refused and never written.
- Queueing is not sending: nothing leaves until a device connects, proves it is one
  this vault already trusts, and asks for it by name.
- Large files are chunked and reassembled; verified byte-identical across frames.
- **Both directions in one sync.** After the PC hands over what you queued, the phone
  offers back anything you marked with "Send to PC". One handshake, one session.
- **Per-item expiry.** When sending, choose never, 1, 7 or 30 days. The phone stores an
  absolute instant and enforces it on its own clock, so it works with no network and no
  further contact with the PC. Items show how long they have left.

### If a phone goes missing

- **An opt-in wipe for a phone that stops syncing.** Off by default. If enabled and the
  phone has not synced within the chosen window (7, 14, 30 or 90 days), it removes items
  a PC sent it. Self-enforced on the phone's own clock, so it needs no network and no
  cooperation from wherever the phone now is.
- **It never deletes anything created on the phone.** Those may be the only copy, and
  destroying the only copy of something is not a security measure. Only items whose
  original still sits on the sending PC are removed. Covered by tests.
- Honest limits, unchanged from the plan: both timers trust the phone's clock, and
  "lost" is indistinguishable from "away from the network for a while".

### Media

- **Animated WebP now plays and loops.** WPF's imaging shows the first frame of an
  animated WebP and stops, which is why these looked like stills. Frames are now decoded
  with Windows' newer imaging stack and played with each frame's own delay. Applies to
  any multi-frame image, not just WebP.
- `.webp` and `.webm` were already accepted by the importer; verified both decode on a
  current Windows install.

### Fixes

- The new-folder dialog no longer flashes several times when it opens. Focusing the
  text box while the card was still animating made the scroll viewer fight the
  entrance transform.
- The new-folder dialog is no longer undersized, has a close button, and supports Esc
  and Enter. The folder name field uses a proper placeholder instead of pre-filled
  text you had to delete.
- The sidebar no longer flashes white during uploads. Disabling the shell made the
  section list fall back to its default light background.
- Fixed the misaligned edge under media filenames — a rounded container does not clip
  its contents in WPF, so the caption bar's corners overhung the tile.
- Fixed the backup card clipping its own text underneath the export button.
- **Corrected misleading backup wording.** It previously implied a backup protects you
  from forgetting your master password. It does not — the backup is encrypted with that
  same password. Only your recovery key covers that. A backup protects against drive
  failure, deletion, or a lost PC.
- Searching the media vault no longer re-decrypts every visible image on each
  keystroke. Thumbnails are cached and search input is debounced.
- Dialogs no longer break when one is opened while another is still closing.
- Compressing, deleting, or renaming from the storage breakdown no longer throws you
  back to "All items". Refreshing the sidebar was auto-selecting the first section,
  which navigated away from any view that has no sidebar selection.
- Fixed the backup icon on the home screen rendering as empty boxes. The glyph had been
  written into the source as mojibake rather than an escape, so the font had nothing to
  draw. Both occurrences now use `\uXXXX` escapes, which cannot be corrupted by an
  editor re-encoding the file.
- Fixed the screenshot harness capturing the content area as transparent, which came out
  as a white page. It renders the content element, which needed its own background.

### Under the hood

- Minimum target moved to Windows 10 build 19041 (version 2004) to reach the built-in
  media encoder. This matches the minimum version already stated in the README, so
  supported systems are unchanged.
- Folder-tree and reordering logic moved out of the interface so it can be tested
  directly. Dropping a folder into its own subtree — which would detach that branch and
  everything in it — is now impossible, and covered by tests.
- Added a screenshot harness (`--store-shots`) that builds a throwaway vault of
  invented content and drives the real interface to capture the store listing images.
  It relies on a new `LOCKWELL_USER_DATA` redirect, which a normal launch never sets,
  so automation cannot reach a real vault.

---

## 1.0.0

Initial release.

- Local-only encrypted vault: Argon2id key derivation, AES-256-GCM authenticated
  encryption, envelope design with an optional recovery key.
- Categories, search, and a media vault with folders and an in-app viewer.
- Encrypted backup export and restore. Multiple vault profiles.
- Screen-capture exclusion, preflight environment scan, auto-lock on idle and on
  minimise, clipboard auto-clear.
