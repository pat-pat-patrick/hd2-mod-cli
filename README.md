# HD2 Mod Quick Deploy

A mod manager for Helldivers 2 built around two things the usual managers do
not do:

- **Incremental deploys.** It never empties the game folder to copy everything
  back. It compares what the load order needs with what is in the game folder
  and touches only the files that differ.
- **Hard links.** A deployed file is the library's own file under a second
  name: nothing is copied, nothing is written, no space is taken twice.

So a typical deploy takes **under half a second** (0.13–0.29 s measured) and
writes **no mod data at all**: only file names and directory entries change,
plus a deploy record of about 100 KB. A purge-and-copy manager writes the whole
mod set every time.

It also looks after the Lua mod loaders, so the player never has to think about
their order:

- **One loader, chosen once.** Boot Mod Loader, Chef Shared Loader and the like
  are not in the mod list. One is chosen in a panel, for every profile, and a
  deploy always puts it last, so its start-up script is the one the game runs.
- **Two loaders working together.** Mods written only for Chef Shared Loader are
  recognised from their scripts. With Boot Mod Loader 0.13.0 or later in effect,
  a deploy also puts Chef Shared Loader in the game, and Boot Mod Loader hands
  those mods over to it through the hand-off Chef Shared Loader provides for
  other loaders. The hand-off is Chef's; this program's part is telling which
  mods need it and deploying it. Tried in game on 2026-10-26: Boot Mod Loader
  started its 8 mods, Chef Shared Loader started the one made for it, and no mod
  was started twice.
- **Start-up scripts that are not a loader's.** A mod that brings its own `boot`
  (Objective Tracker 0.6.7) is deployed off the start-up slot and run by Boot
  Mod Loader.

It comes in two parts that share one library and one set of settings:

- **HD2 Mod Quick Deploy** — the window, for players;
- **hd2qd** — the command line, for mod makers and scripts.

[中文说明在下面](#中文)

![HD2 Mod Quick Deploy](docs/screenshot.png)

## How much writing and time it saves

Measured on the author's library: 68 mods, 284 patch files, 2.84 GB in the
game folder, library and game on one NTFS drive.

| You change | A purge-and-copy manager writes | HD2 Mod Quick Deploy writes | Deploy time |
|---|---|---|---|
| Nothing | 2.84 GB | **0 B** | 0.13–0.18 s |
| One mod's version (a new zip) | 2.84 GB | **0 B** — 1 hard link, 1 delete | 0.14–0.29 s |
| A mod turned off | 2.84 GB | **0 B** — 109 renames, 69 deletes | 0.23 s |
| The same mod turned back on | 2.84 GB | **0 B** — 109 renames, 69 hard links | 0.23–0.25 s |
| A mod moved in the load order | 2.84 GB | **0 B** — 82 renames | 0.19–0.21 s |
| Space taken in the game folder | 2.84 GB of copies | **0 B** — hard links | |

"0 B" is mod data: renames, links and deletes still change a few KB of file
system entries, and each deploy saves its record (about 100 KB). The times
include starting `hd2qd.exe` (26 ms) and were taken with the files already in
Windows' cache; slower are the first deploy after Windows starts (not
measured), the first comparison of two large identical files (read once, then
remembered) and a new zip, which is unpacked once (0.58 s for a 55 MB zip).

Ten such changes in one session write 28 GB to the drive with
purge-and-copy, and only file entries here. The window counts it for you on its *Storage* page
(*Writes saved*): three deploys on that library saved 8.51 GB.

Coming from another manager: one click on *Make hard links* (or
`hd2qd relink`) turned its 284 copies into hard links in 2.5 s, writing
nothing and giving 2.84 GB back.

When the library is on another drive than the game, hard links are not
possible and changed files are copied; the deploy is still incremental, so
updating a 5 MB mod writes 5 MB, not 2.84 GB.

## How it does it

- a file that is already right is left alone;
- a mod moved in the load order, or renumbered because another was turned off,
  is **renamed**, not copied;
- a new file is a **hard link** to the library when both are on one drive (a
  copy otherwise);
- a file that is no longer wanted is deleted.

## Requirements

- Windows 10 (1903 or later) or Windows 11.
- Nothing to install: both programs need only the .NET Framework 4.8 that
  Windows comes with.

## The window

### The first start

1. Open `HD2 Mod Quick Deploy.exe`.
2. It finds the game folder by itself (from HD2 Arsenal's settings or Steam's
   library list). If it cannot, it asks for it: choose the game folder, its
   `data` or its `bin` folder.
3. If your library is empty, it lists the mod managers found on this computer
   (HD2 Arsenal, HD2ModManager, Echelon) and offers to import their lists: the mods,
   their order, on/off and options. Their files are only read, and a mod found
   in more than one is imported once. HD2 Arsenal's list is read in its earlier
   shape and in the one of 0.37 (the mods kept once, the profiles naming them).
   A list that is found but cannot be read is said, with where it is; it is never
   left out as if there were none.
4. Mods that are on another drive than the game are pointed out, with what
   moving them gives (see *Mod folders*). Nothing is moved unless you choose to.

### What is where

| Part | What it shows and does |
|---|---|
| Title bar | How many mods, how many are on, how many changes wait for a deploy; **Deploy / Storage / Labels** to switch pages; a gear for the settings (the language, whether the mod that wins is shown at the bottom of the list or at the top, whether the lines that explain things are shown, the jump button for a fast wheel, this program's updates, who made it and where it comes from); a red dot on the gear when a newer version of this program is out; the hard-linked files in the game. |
| Library (left) | Filters: all mods, those with changes to deploy, Lua mods, off, with conflicts, any that cannot be read, and the ones whose scripts **will not run**. |
| Deploy target (left) | The library and game folders, and whether they share a drive (hard links) or not (copies). **Change…** picks another game folder. When the game holds copies that could be hard links, **Make hard links** frees their space without writing anything. |
| Filter (left of the search box) | Narrows the list to your own labels: a mod shows when it carries any of the labels ticked. Alt+click a label to hide the mods that carry it. |
| Search (above the list) | Finds mods by any word in their metadata, not only the name: the author's description, options and their descriptions, folders, patch file names (archive ids), Nexus mod ids, *lua*, *loader*, and your own labels. Several words: all must match. There is no need to click it first: typing on the list goes to it. |
| Mod list (middle) | The load order, top to bottom: **a mod lower in the list wins**. Each row: its number, its switch, its picture, its name, and under the name its version, what is chosen, a **LUA** tag when it runs scripts in the game and a **LOADER** tag when it replaces one of the game's own scripts the way a mod loader does; on the right a mark that checks this one mod for updates, its size and how it is deployed. Dropping a zip or a mod folder on the list installs it. Turning the wheel fast shows **To the top** or **To the end** beside the pointer (a setting, on by default). Double-click a mod to give it a name of your own (the name it came with stays in its details); Ctrl or Shift takes several mods at once: drag one of them and they all move together, in the order they have; the right button turns them all on or off, moves them, removes them or deletes them from the disk. |
| Options (open a row with its arrow) | A mod's options as cards with their pictures, names and descriptions. A group is *pick one* (Ctrl+click for more); parts can each be on or off. |
| Labels page | Your own labels for mods, with how many mods carry each one: make one, rename it, delete it, and tick the mods that carry it. |
| Details (right) | The author's description (or, when the mod folder has none, what its site says), the load order, the folder, **Delete** (red), **Remove** and **Open folder**; what the next deploy does to this mod (kept, renamed, new), its conflicts with other mods (each resource by its type, and by its name when HD2SDK is installed, e.g. "Model · Laser Sight W/ Flashlight"), the resources another mod ships byte for byte alike (shared, not fought over), and its source: **Match the source** asks the site what the mod is called and fills in its name, version, author, description and picture. |
| Footer | What a deploy would do; **Deploy** and **Launch game**. A deploy never needs a purge first: it changes only what differs. **Restore vanilla** is at the bottom of the deploy target card, for playing without mods. |

### Everyday use

1. **Add a mod**: drop its zip or folder on the list, or *Add mod…* → a zip or
   a folder. It goes to the bottom of the list, so it wins over the others. A
   folder that holds several mods, each in a folder of its own (another
   manager's mod folder), is offered as a library folder instead of being taken
   as one mod.
2. **Turn it on or off** with its switch, and **choose its options** on its
   cards. Every click shows at once; the window works it out in the background.
3. **Change the load order** by dragging a row (or several taken together).
4. **Deploy**. The footer says what was kept, renamed, written and deleted, and
   how long it took.
5. **Launch game** starts the game through Steam. If changes are not deployed
   yet, it offers to deploy them first.

A new version of a mod: right-click it → *New version from a zip / a folder*.
It keeps its place in the list and its options.

**Remove** only takes a mod out of the library; its files stay where they
are. **Delete** (the red button, or right-click → *Delete from the disk…*)
takes it off the disk for good: out of the library, out of the game (a deploy
runs at once) and its folder deleted, not moved to the Recycle Bin. The zip it
was added from is kept. It refuses, changing nothing, while the game runs or
when the folder is not the mod's own (a drive, the game folder, or a folder
other mods in the library use).

### Mod folders (the Storage page)

The **Storage** page lists the folders that hold mods, as Steam lists its
library folders: each with its drive, free space, how many mods it holds and
their size, and whether it is on the game's drive.

- **Profiles** (Settings, off by default): the mod library holds every mod,
  and each profile some of them, with its own load order, on/off and options.
  The list's title is the profile in use and opens the others, the mod library
  (where a mod is put into the profile, or taken out, from its right-click menu)
  and making, renaming and deleting profiles. Switching deploys at once.
  When HD2 Arsenal or Echelon keeps several profiles, the import asks whether
  to bring in every one as a profile of its own or only the one in use there.
  An import never changes the profile in use.
- **Game folder** (at the top): where the game is and whether Helldivers 2 is
  really there. *Change…* chooses it by hand; *Find automatically* looks in the
  Steam libraries on this computer.
- **Default**: added zips go there. *Make default* chooses another; *Remove
  from list* takes a folder you added off the list (its files stay).
- **Add library folder…** adds a folder and, as Steam does with a library
  folder, looks inside: another manager's mods kept there (HD2 Arsenal's,
  HD2ModManager's even when its storage was moved, Echelon's) are offered with
  that manager's list, so their order, on/off and options come too; any other
  mod folder in it is offered as it stands. Mods already in the library are
  never added twice.
- **Another manager's mod folder comes in only through its list**, since the
  order, the switches and the options are kept there. The folder is recognised
  by the marks each manager leaves on its mod folders:

  | Manager | Its mod folders | Its list |
  |---|---|---|
  | HD2 Arsenal | Names ending in `_AR` and six digits | `%LOCALAPPDATA%\hd2arsenal\hd2a_data.json` |
  | HD2ModManager | A `manifest.json` with a Guid in each, some written by it for the mods it imported | `enabled.json` beside its `Mods` folder |
  | Echelon | Names ending in ` (` and twelve hex digits `)`, the files in numbered set folders | `%APPDATA%\Echelon\library.json` |

  When the list is not where that manager keeps it, or lists none of the
  folder's mods, the window says whose folder it is and asks for the file;
  nothing is added loose. When the list is found and cannot be read, it says
  that instead.
- **Change path…**: a folder you moved yourself, or one on a drive that got
  another letter. The mods in it follow it; nothing on disk is moved.
- Folders that mods are in are listed by themselves (HD2 Arsenal's, HD2ModManager's, Echelon's).
- **Deleting mods**: choose a folder, tick its mods (the list is sorted by size
  first), *Delete…*: their folders go, and they leave the library and the game.
- **Moving mods**: choose a folder, tick its mods, *Move to…* another folder.
  *Move* deletes the old folders; *Copy* keeps them, for a manager that still
  uses them. Afterwards the window says how many were moved and where, and the
  game folder's copies of them become hard links.

**Mods on another drive than the game** are deployed as copies: they take
their space a second time and every change writes them again. The window
points this out, after an import or an added folder and at the top of the
Storage page, and handles each choice:

| You choose | What happens |
|---|---|
| Keep them where they are | Nothing is moved. They are deployed as copies, and that folder is not pointed out again. They can still be moved on the Storage page at any time. |
| Move | They are moved to the default folder when it is on the game's drive, else to `HD2 Mods` on the game's drive; the question names the folder. The old folders are deleted. |
| Copy | As Move, but the old folders stay (the mods then take space twice). |

### Update checks

**Check for updates** (above the list) checks every mod on the channel it came
from:

| Source | How it is checked | How it is updated |
|---|---|---|
| GitHub | The repository's latest release. | **Update** downloads the release's zip, 7z or rar. |
| Nexus Mods | Signed in to Nexus Mods (see *Nexus Mods*): one request for the game's mods updated in the last month, then one for each of your mods that changed since its last check. | Premium accounts: **Update** downloads the new file. Free accounts: **Open page**, then *Mod Manager Download* on the site. |
| Another site | Not checked. | **Open page**. |

- The source is recorded when a mod arrives: a file downloaded with a browser
  (Windows keeps the address it came from), or HD2 Arsenal's records for the
  mods it installed.
- When it is not recorded, the mod's details offer **Set source…**: paste the
  mod's Nexus Mods page or GitHub repository.
- Mods with updates get a yellow tag in the list and their own filter, **Updates**.
- An update keeps the mod's place in the load order, on/off and options; then
  deploy.
- **Skip this version**: the update is no longer offered, and the next newer
  version is offered again. *Offer it again* takes the skip back.
  `hd2qd skip MOD [--clear]`.
- **Previous version** (Settings → *Keep the previous version of script mods*,
  off by default): when a mod with Lua scripts gets a new version (a zip, a
  download or an update), the version it had stays on disk, one per mod. Its
  details show *Previous version* with the version and the date, and **Roll back
  to the previous version** swaps the two, so the newer one can come back the
  same way; then deploy. Mods without scripts keep nothing: they are large, and
  a broken version of them rarely stops a game. The versions kept are listed on
  the Storage page (*Previous versions kept*), where each can be opened, rolled
  back to or deleted. `hd2qd keep-previous-on`, `keep-previous-off`,
  `previous [--delete]`, `rollback MOD`.

### Nexus Mods

In builds with the Nexus Mods part (ours, and `build.bat --with-nexus`):

- **Signing in.** *Sign in to Nexus Mods* on the Download page opens Nexus Mods'
  own page in your browser, where you allow this program (OAuth 2.0 with PKCE).
  The program never sees your password. The sign-in is kept on this computer
  only, encrypted for your Windows account, and renewed by itself. *Sign out*
  forgets it here; the access can also be taken back on Nexus Mods.
- **Downloads.** The site's *Mod Manager Download* button hands the file to this
  program once it is the program for those links (one button on the Download
  page takes them, another gives them back to the program that had them).
  Premium accounts can also update a mod from the list directly.
- **By number.** A mod's number alone (`16662`, as it is often passed around,
  also `N站16662`) or its page's address, typed in *Find by Nexus number* on the
  Download page, shows what its page says: name, author, version, numbers and
  words, and whether the library has it already, with a button to its Files tab.
  It needs no sign-in (Nexus Mods' public GraphQL API). `hd2qd nexus-info 16662`
  says the same.
- **Requests.** Every request follows something you clicked, and carries this
  program's name and version. Nothing is asked of Nexus Mods at start-up or in
  the background.

### Lua mods before a deploy

The game runs one start-up script (`boot`): the one in the highest patch. Boot
Mod Loader, Chef Shared Loader, Objective Tracker and Medic3038's installer
each bring one, so only one of them runs. The window reads every mod's Lua
scripts before a deploy and says what will happen; it never changes another
mod's files and never chooses for you.

| What it checks | What you see |
|---|---|
| **A start-up script that is no loader's** | A mod that brings its own `boot` but starts only itself (Objective Tracker 0.6.7) would take the game's one start-up slot, and the load order would decide whether it or the loader runs. With Boot Mod Loader 0.12.0 or later in effect, a deploy takes that script off the slot: the game gets a copy of the archive in which the script, marked `-- HD2-Guest-Boot: <mod>`, is run by Boot Mod Loader after the mods. The library is not changed, and the load order does not matter. With an older loader, or none, nothing is changed. |
| **Script names** | Each mod's details list its scripts by the names Boot Mod Loader's log and Mod Lag Watchdog print, such as `mods/max/maelstrom_traverse`. Typing such a name into the search finds the mod. |
| **The loader** | Loaders (Boot Mod Loader, Chef Shared Loader, Bingus Shared Loader, MDL3) are not in the mod list: the panel on the left lists them, and one is chosen there for every profile; no loader is a choice too. A deploy puts the chosen one after every mod, so its start-up script is the one the game runs, and leaves the other loaders out: their place in the list and their own switches do not matter. A loader's name opens its details. The panel shows only each loader's state (in effect, helping, not used), and no panel appears when the library has no loader. Its **i** button opens the details: for every loader, the one in effect too, which mods would not start with it, and why (Chef Shared Loader only, a script over Chef's 4 MB limit, a start-up script replaced by another), or that every Lua mod starts; for Chef helping, the mods it starts. Every line in them can be selected and copied. A click on a loader's ring shows at once and is saved in the background. |
| **Chef Shared Loader as a helper** | With Boot Mod Loader 0.13.0 or later in effect, Chef Shared Loader in the library, and a mod on that runs only on Chef (it says so, or its script calls the `chef` interface Chef provides), a deploy also puts Chef in the game, just before Boot Mod Loader. Boot Mod Loader starts its own mods, then hands the ones made for Chef to it through Chef's documented hand-off. Their settings are in Chef's F9 menu; the other mods keep the settings window. The panel shows Chef as helping. |
| **Start-up scripts** | Which mod's `boot` the game runs, and whose does not. |
| **Scripts too big for Chef Shared Loader** | Chef Shared Loader 1.2.1 reads no Lua script over 4 MB. A mod it would start with a bigger script is marked **will not start**, with its size; the loader panel's details name those mods before Chef is chosen. Boot Mod Loader has no such limit. |
| **Start-up scripts under Chef Shared Loader** | Chef Shared Loader runs the start-up scripts deployed before it as well, so with Chef in effect a mod's own `boot` that starts only itself is in no conflict either; its details say Chef runs it. |
| **The loader each mod needs** | Read from the script's first lines. `-- HD2-Addon:` starts on Boot Mod Loader or Chef Shared Loader; `-- HD2-Requires: chef-shared-loader` (or `-- HD2-Boot:`) only on Chef Shared Loader. The details name only the loaders your library has. The list says when no loader is in effect and when a mod needs one that is not. |
| **Scripts the game's Lua cannot run** | Since the 2026-10-20 update the game runs plain Lua 5.1, without LuaJIT. A script that does not compile (goto, LuaJIT's `ULL` numbers, Lua 5.2+ operators, a BOM, more than 200 locals or 60 upvalues in a function, and the rest), LuaJIT bytecode, or `ffi`, `bit` or `jit` used with no fallback is marked **Cannot run**, with what it uses (FFI, LuaJIT's 64-bit numbers, goto, …) and one sentence on just that: the game does not accept or provide it at present, nor do the loaders in the library. The syntax check follows PUC Lua 5.1's own lexer and parser; it agreed with PUC Lua 5.1 on all 15,394 scripts it was compared on (`tests/lua_syntax_vs_puc.py`). Comments and strings never count. |
| **Two mods with one name** | Two mods declaring the same `mods/<author>/<name>`: only the later one runs. |
| **Translation packs** | A pack (the same author's folder, the mod's name and a language, wrapping the old menus' calls, as ActionCam简体中文 does) is one small line under the mod it translates, with its own tick, and is kept right after that mod in the load order. Turning that mod off turns its packs off, and turning it on again brings back the ones it took; a pack turned off or on by hand stays as it is. When that mod is not installed, the list says the translation does not apply. |
| **The last session** | Boot Mod Loader's log of the last launch: which mods started, how long each took, and which failed or did not compile, with the reason. |

All of it is read outside the game, before a deploy: nothing runs in the game. The list never waits for it: what was found is kept in `lua_scan.json`, and an archive not read yet is read in the background, its findings shown a moment later.

### Good to know

- **Use one manager for deploying.** A deploy from another manager would undo
  this one's, and the other way round.
- **Do not edit a deployed patch file in place**: as a hard link it is the same
  file as the library's, so the library would change too. Deleting one
  (*Restore vanilla*) does not touch the library.
- A deploy or *Restore vanilla* waits until the game is closed.
- The window's language is its own setting (in the settings window, kept for
  the next start); until one is chosen, it follows Windows.
- `HD2 Mod Quick Deploy.exe --select TEXT` opens the first mod whose name holds
  TEXT.
- **Asking about a problem?** Settings → *Problem report* → *Export problem
  report* writes one zip to the desktop and puts a short summary on the
  clipboard: send the zip with your question (`hd2qd report` does the same).
- **hd2qd can be run by an AI assistant.** Everything is kept in
  `%USERPROFILE%\.hd2-mod-cli`, not in AppData: a program started by a packaged
  app (most desktop AI assistants are) has a folder it makes under `%APPDATA%`
  put in that app's private copy, which no other program reads, and the library
  would split in two without any error. If `HD2MOD_HOME` points under AppData
  all the same, hd2qd checks before it writes and refuses, saying where the
  writes would have gone.

## The command line: hd2qd

The same library and the same deploy, for mod makers and scripts. Put
`hd2qd.exe` in a folder on your PATH, or type its full path.

    hd2qd init                 # finds the game (from HD2 Arsenal's settings or Steam)
    hd2qd import-arsenal       # optional: take HD2 Arsenal's mods, order, on/off and options
    hd2qd status               # what a deploy would change
    hd2qd deploy

A change made on the command line shows in an open window half a second later.

| Command | What it does |
|---|---|
| `init [--game DIR] [--mods-dir DIR]` | Set the game folder (found automatically when left out; the game folder, its `data` or its `bin`) and where zips are unpacked. |
| `import-arsenal [--file F] [--profile ID \| --profiles] [--replace]` | Add HD2 Arsenal's deployed profile to the library: names, folders, order, on/off, options; mods already in it are skipped (`--replace`: the library becomes HD2 Arsenal's). Mods stay in HD2 Arsenal's folders. `--profiles`: every profile, each a profile here with its own mods, order, on/off and options; the profile in use stays in use. |
| `list [--files] [--paths]` | The load order, top to bottom. The last one wins. `--files` shows each mod's patch files as they would be numbered. |
| `add PATH [--name N] [--copy] [--whole]` | Add a mod at the bottom: a zip (unpacked once into the library folder) or a folder (used where it is, so a mod you are building can be deployed straight from its build folder). A folder of several mods is refused as a library folder (`folder-add`); `--whole` takes it as one mod all the same. |
| `replace MOD PATH` | A new version of a mod: same place in the order, the same options by name. |
| `remove MOD` | Take a mod out of the library; its files stay. |
| `check-updates` | Check every mod from GitHub for a newer release. |
| `self-update` | Whether a newer version of this program is on GitHub (read only). The official build counts releases only; the development build counts pre-releases too. |
| `update MOD` | Download a mod's newer version from its GitHub releases and put it in its place. |
| `skip MOD [--clear]` | Stop offering a mod's newest version until a newer one comes (`--clear`: offer it again). |
| `keep-previous-on` / `keep-previous-off` | From now on, a mod with Lua scripts keeps the version it had when a new one comes, for a rollback (off: no more are kept; the ones kept stay). |
| `previous [MODS] [--delete] [--all]` | The previous versions kept; `--delete` with mods, or `--delete --all`, deletes them (the mods keep their current versions). |
| `rollback MOD` | A mod goes back to its previous version, and the version it had is kept instead; deploy afterwards. |
| `source MOD URL` / `source MOD --clear` | Set where a mod comes from: a Nexus Mods page, a GitHub repository or another page. |
| `import-hd2mm [--dir DIR] [--replace]` | Add HD2ModManager's mods, order, on/off and options to the library (read only; its folder-name and object options both). |
| `import-echelon [--file FILE] [--profiles] [--replace]` | Add Echelon's mods, on/off and options from its own library.json, in its active profile's order (read only). `--profiles`: every profile, each with its own mods, order and on/off; the option choices are the active profile's, the only ones Echelon's files hold. |
| `folders` | The mod folders: the default, the ones added, and the folders mods are in, with their drives. |
| `folder-add PATH [--import]` / `folder-remove PATH` / `folder-default PATH` | Add a library folder and say which other managers' lists and which other mods it holds (`--import` brings them in), take one off the list (its files stay), choose the default. Another manager's mod folder whose list is not found names the list to give with `import-… --file` (`--dir` for HD2ModManager). |
| `folder-path PATH NEW` / `add-loose PATH` | A library folder is somewhere else now: it and its mods follow. Add the mod folders in a library folder that no manager lists, where they are. |
| `relocate MOD... --into FOLDER [--keep]` | Move mods into another mod folder (`--keep`: copy, the old folders stay); the game's copies of them become hard links. |
| `delete MOD... --yes` | Delete mods from the disk for good: out of the library and the game (one deploy), their folders deleted. |
| `enable MOD...` / `disable MOD...` | Turn mods on or off. |
| `move MOD... --top / --bottom / --to N / --before M / --after M` | Change the load order. Several mods move as one block, in the order they have. |
| `profiles-on` / `profiles-off` / `profiles` | Profiles: off, one list; on, the library holds every mod and each profile some of them, with its own load order, on/off and options (the first time on, a profile `Default` of every mod). `profiles` lists them. |
| `profile-new NAME [--empty]` / `profile-use NAME` / `profile-rename NAME NEW` / `profile-delete NAME` | Make a profile (a copy of the one in use, or empty), switch to one (it deploys), rename, delete. |
| `profile-add MOD...` / `profile-remove MOD...` | Put library mods into the profile in use, or take them out (they stay in the library). |
| `options MOD` | A mod's options and sub-options, numbered. |
| `set MOD OPTION on/off` | Turn an option on or off. `OPTION/SUB` or `2.3` for a sub-option; `--only` turns that sub-option on and the option's others off. |
| `status [--verify]` | What a deploy would change, file by file. Changes nothing. |
| `deploy [--dry-run] [--copy] [--verify]` | Make the game folder match the load order. |
| `relink [--dry-run]` | Make the copies in the game folder (another manager's, or `--copy`) hard links to the library, where both are on one drive: their space comes back and nothing is written. |
| `purge --yes` | Delete every patch file from the game folder: vanilla. The library keeps its files. |
| `which TEXT` | Which mod a patch file or archive comes from. |
| `rename MOD NAME` | Call a mod something else in the library; its folder and files are left alone. |
| `match MOD [--all]` | Ask a mod's GitHub repository what it is called and fill that in: name, version, author, description, picture. |
| `tags` | Your own labels, with how many mods carry each. |
| `tag MOD NAME...` / `untag MOD NAME...` | Put labels on a mod, or take them off (`--all` takes them all off). |
| `tag-rename OLD NEW` / `tag-delete NAME` | Rename a label everywhere, or take it off every mod. |
| `loader <name\|none>` | The loader in effect, for every profile: a loader of the library by name or id, or `none`. A deploy puts it after every mod and leaves the other loaders out. |
| `lua` | The mods' Lua before a deploy: whose start-up script runs, which loader that is, what each mod needs, what the game's Lua cannot run, the last session and the last crash (read only). |
| `lua-syntax FILE...` | Whether Lua files compile on the game's Lua (PUC 5.1, no LuaJIT): OK, or the line and why. |
| `conflicts [-v]` | Resources that more than one mod ships, and which one wins; the ones every mod ships byte for byte alike are named as shared, not as a conflict. `-v` lists each resource's type and id, and its name from HD2SDK's name list when HD2SDK is installed. |
| `state [--verify]` | Everything the window shows, as JSON. |
| `report [--out PATH]` | A zip to send with a question (on the desktop unless `--out`): the state, the settings, the deploy history, the Nexus Mods download history, Boot Mod Loader's log and the names of the game's crash dumps. Never the dumps themselves and never the sign-in; your user folder is written as `%USERPROFILE%`. A short summary is printed too. |

`MOD` is the mod's number in `list`, its name, or a unique part of its name.

## How a deploy works

- **Numbering** is HD2 Arsenal's: mods from the top of the list to the bottom;
  inside a mod, its included folders in option order (an option's `Include`,
  then its enabled sub-options'); inside a folder, its patch files by their own
  number. Every archive counts from `patch_0` with no gaps, and each
  `.gpu_resources` / `.stream` file goes with its patch file. A mod without a
  manifest deploys its top folder, or, when that holds no patch file, every
  folder that does.
- **A file already right** is the same file (a hard link); else what the last
  deploy recorded for it, when neither file changed since; else the same bytes.
  Size and time alone are not trusted: a mod's option variants are often built
  in the same second with the same size. `--verify` compares bytes for every
  file.
- **Safety**: files are deleted, moved through temporary names, then linked or
  copied, so a name is never overwritten while another file still needs to
  move away from it.

## Files

Everything is kept in `%USERPROFILE%\.hd2-mod-cli` (`HD2MOD_HOME` overrides it):

| File | What it holds |
|---|---|
| `config.json` | The game folder and where zips are unpacked. |
| `library.json` | The mods, their order, on/off and options, and where each came from (for update checks). |
| `deployed.json` | What the last deploy put in the game folder. |
| `same.json` | Large files already compared, so they are not read again. |
| `gui.json` | The window's own settings (its language, which way the list is shown, the explaining lines). |
| `deploy_history.json` | When each of the last 50 deploys ran and which mods it put in the game, so that a crash can be matched with them. |
| `lua_scan.json` | What each patch archive's Lua scripts were found to be, by the archive's size and time, so a start does not read and check them again. |
| `window_state.json` | The list as the window last showed it, drawn at once at the next start while the library and the game folder are read again (Deploy waits for that read). |
| `thumbs\` | A 256-pixel copy of each mod's picture, so a start reads a few kilobytes instead of the original. |
| `self_update.json` | The last check of this program's own releases, and whether the window checks by itself (at most once a day). |
| `*.json.bak` | The version each file had before the last save. Mods can be downloaded again; what you chose — the mods, their order, their options, their labels — cannot, and it is a few kilobytes. To go back, rename the `.bak` over the file. |

The **default mod folder** (where added zips are unpacked, each as its own
folder; the archives are not kept) is not in AppData. It is
the one you set (*Make default* on the Storage page, or `folder-default`); until then HD2
Arsenal's mod folder when it is installed, else `HD2 Mods` on the game's drive,
so that deploys can use hard links.

## Building and tests

`build.bat` runs the tests, then builds both programs into `release\`. It needs
the .NET SDK 8 or later (`dotnet` on PATH, or set `DOTNET` to its
`dotnet.exe`).

| Folder | What it is |
|---|---|
| `core\` | Everything a command does, shared by both programs. |
| `cli\` | The command line, `hd2qd.exe`. |
| `gui\QuickDeploy\` | The window (WPF). |
| `tests\` | xUnit tests against a fake game folder in `%TEMP%`. |

The Nexus Mods part is left out unless asked for: `build.bat --with-nexus`. Signing in
to Nexus Mods needs the client id Nexus Mods issues to each registered application
(email support@nexusmods.com, as their OAuth guide says). Put it in
`nexus_client_id.txt` beside `build.bat`; that file is not in the repository. Our own
builds carry ours; a build you publish registers its own application and its own name.

Versions up to 0.1 were a Python script, kept at the git tag `python-0.1`; up
to 0.2 the command was called `hd2mod`. The settings files are the same, so
nothing needs migrating.

## License

MIT, see [LICENSE](LICENSE).

## 中文

Helldivers 2 的 mod 管理器，具备两项一般管理器所没有的特性：

- **增量部署**：不会在每次部署时先清空游戏目录、再全部复制回去，而是对比加载顺序所需的文件与游戏目录中已有的文件，仅处理有差别的文件。
- **硬链接**：部署到游戏目录的文件即 mod 库中的同一文件，仅多一个文件名。不复制、不写入数据，也不重复占用硬盘空间。

因此，一次典型的部署**不到半秒**（实测 0.13–0.29 秒），且**不写入任何 mod 数据**：仅更改文件名和目录项，另写入约 100 KB 的部署记录。“清空再全部复制”的管理器则每次都需重新写入整套 mod。

此外，本程序会处理 Lua mod 的加载器，玩家无需考虑加载器的顺序：

- **只选一个加载器**：Boot Mod Loader、Chef Shared Loader 等加载器不出现在 mod 列表中，而是在面板中选择一个，对所有配置档案通用。部署时它总是放在最后，游戏运行的就是它的启动脚本。
- **两个加载器协同工作**：程序会从脚本中识别仅能由 Chef Shared Loader 启动的 mod。生效的是 Boot Mod Loader 0.13.0 或更新版本时，部署会把 Chef Shared Loader 一并放入游戏目录，再由 Boot Mod Loader 通过 Chef Shared Loader 为其他加载器提供的交接方式，把这些 mod 交给它启动。交接方式由 Chef 提供；本程序负责判断哪些 mod 需要它，并完成部署。2026-10-26 游戏内实测：Boot Mod Loader 启动了自己的 8 个 mod，Chef Shared Loader 启动了为它编写的 1 个，没有任何 mod 被重复启动。
- **非加载器的启动脚本**：自带 `boot` 的 mod（如 Objective Tracker 0.6.7）部署时会移出启动入口，改由 Boot Mod Loader 运行。

本程序分为两部分，共用同一个 mod 库和同一份设置：

- **HD2 Mod Quick Deploy**：图形界面，面向玩家；
- **hd2qd**：命令行，面向 mod 作者和脚本。

![HD2 Mod Quick Deploy](docs/screenshot-zh.png)

### 写盘量与用时

以下为作者 mod 库的实测结果：68 个 mod、284 个 patch 文件，游戏目录中共 2.84 GB，mod 库与游戏位于同一 NTFS 分区。

| 操作 | “清空再全部复制”的写盘量 | HD2 Mod Quick Deploy 写盘量 | 部署用时 |
|---|---|---|---|
| 无改动 | 2.84 GB | **0 B** | 0.13–0.18 秒 |
| 更新一个 mod 的版本（新 zip） | 2.84 GB | **0 B**：新增 1 个硬链接，删除 1 个文件 | 0.14–0.29 秒 |
| 停用一个 mod | 2.84 GB | **0 B**：重命名 109 个，删除 69 个 | 0.23 秒 |
| 重新启用该 mod | 2.84 GB | **0 B**：重命名 109 个，新增 69 个硬链接 | 0.23–0.25 秒 |
| 调整一个 mod 的加载顺序 | 2.84 GB | **0 B**：重命名 82 个 | 0.19–0.21 秒 |
| 游戏目录额外占用空间 | 2.84 GB 复制件 | **0 B**：硬链接 | |

“0 B”指 mod 数据：重命名、建立硬链接和删除仍会改动几 KB 的文件系统目录项，每次部署另写入约 100 KB 的部署记录。用时包括启动 `hd2qd.exe` 的 26 毫秒，测量时文件已在 Windows 缓存中。以下情况会稍慢：开机后的首次部署（未实测）；首次比较两个内容相同的大文件（仅读取一次，结果会被记录）；新 zip 需先解压一次（55 MB 的 zip 用时 0.58 秒）。

连续调整十次，“清空再复制”需向硬盘写入 28 GB，本程序仅更改目录项。“存储”页面的“累计节省写盘”会统计节省量：在上述 mod 库上部署三次，共节省 8.51 GB。

从其他管理器迁移时，点击“改为硬链接”（或运行 `hd2qd relink`），可将原有的 284 个复制件改为硬链接：用时 2.5 秒，不写入数据，释放 2.84 GB 空间。

mod 库与游戏不在同一分区时无法使用硬链接，有变化的文件将被复制，但部署仍为增量：更新一个 5 MB 的 mod 只写入 5 MB，而非 2.84 GB。

### 实现方式

- 已正确的文件保持不变；
- 调整加载顺序，或因停用某个 mod 导致后续文件重新编号时，仅重命名，不复制；
- mod 库与游戏位于同一分区时，新文件以硬链接放入（不同分区时复制）；
- 仅删除不再需要的文件。

### 运行要求

- Windows 10（1903 及以上）或 Windows 11；
- 无需安装其他组件，使用系统自带的 .NET Framework 4.8。

### 首次启动

1. 打开 `HD2 Mod Quick Deploy.exe`。
2. 程序会自动查找游戏目录（依据 HD2 Arsenal 的设置或 Steam 的库列表）；未找到时会提示选择，可选择游戏目录本身或其中的 `data`、`bin` 文件夹。
3. mod 库为空时，程序列出在本机找到的 mod 管理器（HD2 Arsenal、HD2ModManager、Echelon），询问是否导入其列表（mod、加载顺序、开关、可选内容）。导入仅读取其文件；同一个 mod 只导入一次。HD2 Arsenal 的列表可读取旧格式和 0.37 的格式（mod 统一保存一份，配置档案按编号引用）。找到了配置文件却无法读取时，会说明情况和文件位置，不会当作没有找到。
4. 与游戏不在同一分区的 mod 会被提示，并说明移动的好处（见“MOD 库文件夹”）。未经选择不会移动任何文件。
5. 首次启动时会提示：安装 mod 无需先清空。

### 界面说明

| 位置 | 内容与功能 |
|---|---|
| 标题栏 | mod 数、启用数、待部署的变更数；**部署 / 存储 / 标签** 切换页面；齿轮按钮打开设置（界面语言、列表中优先的 mod 显示在下方还是上方、是否显示说明文字、快速滚动时的跳转按钮、本程序的更新、作者与项目地址），有新版本时齿轮右上角显示红点；游戏目录中的硬链接文件 |
| 左侧 · MOD 库 | 筛选：全部、待部署、Lua mod、已停用、冲突、读取出错，以及脚本**不会运行**的 mod |
| 左侧 · 部署目标 | mod 库与游戏目录，以及二者是否位于同一分区（硬链接）或不同分区（复制）。**更改…** 用于更换游戏目录；游戏目录中存在可改为硬链接的复制件时，**改为硬链接** 可在不写入数据的情况下释放空间。卡片底部为 **恢复原版** |
| 筛选（搜索框左侧） | 按自己贴的标签过滤：mod 带有任一所选标签即显示。按住 Alt 键点击标签，可隐藏带有该标签的 MOD |
| 搜索（列表上方） | 按元数据中的任意词查找 mod，不限于名称：作者说明、可选内容及其说明、文件夹、patch 文件名（档案编号）、Nexus 编号、*lua*。输入多个词时须全部匹配。无需先点击搜索框：在列表上直接输入即可 |
| 中间 · mod 列表 | 加载顺序自上而下，**越靠下优先级越高**。每行显示：序号、开关、图片、名称，名称下方为版本、已选内容、**LUA** 标签（会在游戏中运行脚本的 mod）与 **LOADER** 标签（替换了游戏自带脚本、相当于自带加载器的 mod）；右侧为单独检查该 mod 更新的图标、大小与部署方式。将 zip 压缩包或 mod 文件夹拖到列表上即可安装。快速转动滚轮时，指针旁会出现 **回到顶部** 或 **跳到底部**（可在设置中关闭，默认开启）。双击 mod 可自定义名称（原名保留在详情中）；按住 Ctrl 或 Shift 可多选：拖动其中一个，选中的 mod 会按原有顺序一起移动；右键可一起启用、停用、移动、移除或从硬盘删除 |
| 可选内容（点击行左侧箭头展开） | 以卡片显示图片、名称和作者说明。一组内为单选（Ctrl+点击可多选）；附加内容可分别开关 |
| 标签页 | 自己给 mod 贴的标签，显示每个标签上有多少 mod：新建、重命名、删除，并勾选属于该标签的 mod |
| 右侧 · 详情 | 作者说明（mod 文件夹中没有说明时，显示来源网站的说明）、加载顺序、文件夹，以及 **删除**（红色外框）、**移除**、**打开文件夹**；下次部署对该 mod 的处理（保留、重命名、新增），与其他 mod 的资源冲突（逐项列出资源类型；安装了 HD2SDK 时还会显示名称，如“模型 · Laser Sight W/ Flashlight”），以及与其他 mod 内容完全相同的资源（属于共用，不算冲突），以及来源：点击 **匹配来源信息** 可从网站取回该 mod 的名称、版本、作者、说明和图片 |
| 底部 | 部署将执行的操作；**部署**、**启动游戏**。部署前无需清空，部署只处理有差别的文件 |

### 日常使用

1. **添加 MOD**：将 zip 压缩包或 mod 文件夹拖到列表上，或点击“添加 mod…”选择 zip 或文件夹。新 mod 位于列表底部，优先级最高。如果文件夹中有多个 mod、各占一个子文件夹（例如其他管理器存放 mod 的文件夹），会提示作为 MOD 库文件夹加入，而不会当作一个 mod。
2. **启用或停用 MOD、选择可选内容**：点击开关或卡片。界面立即更新，计算在后台完成。
3. **调整加载顺序**：拖动行（多选后可一起拖动）。
4. **部署**：底部显示保留、重命名、新增、删除的文件数及用时。
5. **启动游戏**：通过 Steam 启动；存在未部署的变更时，会询问是否先部署。

更新 mod 版本：右键该 mod，选择“换新版本（zip）”或“换新版本（文件夹）”，加载顺序和可选内容保持不变。

**移除** 仅将其从库中移除，文件保留在原处。**删除**（红色外框按钮，或右键选择“从硬盘删除…”）会将其从硬盘上彻底移除：从库中移除、立即部署一次以将其移出游戏目录，并永久删除其文件夹（不进入回收站）。添加时使用的 zip 不会删除。游戏运行时，或文件夹并非该 mod 自身的文件夹（盘符、游戏目录、库中其他 mod 也在使用的文件夹）时，删除将被拒绝，且不做任何更改。

### MOD 库文件夹（存储页）

**存储** 页按照 Steam 游戏库文件夹的方式列出存放 mod 的文件夹，每个文件夹显示所在分区、可用空间、其中的 mod 数量与大小，以及是否与游戏位于同一分区。

- **配置文件**（在设置中开启，默认关闭）：mod 库保存全部 mod，每个配置文件包含其中一部分，并有各自的加载顺序、开关与可选内容。列表标题即当前配置，点击可切换配置、查看 mod 库（右键 mod 可加入或移出当前配置），以及新建、重命名、删除配置。切换后立即部署。HD2 Arsenal 或 Echelon 有多个配置时，导入前会询问：将全部配置分别导入为配置文件，还是只导入其当前使用的配置。导入不会更换当前使用的配置。
- **游戏目录**（最上方）：显示游戏位置，以及该位置是否确为 Helldivers 2。“更改…”可手动选择；“自动识别”会在本机的 Steam 库中查找。
- **默认文件夹**：添加的 zip 放入此处。“设为默认”可更换默认文件夹；“从列表移除”仅将添加的文件夹移出列表，其中的文件保留。
- **添加库文件夹…**：添加文件夹后，像 Steam 识别游戏库文件夹一样检查其中内容。其他管理器存放在此的 mod（HD2 Arsenal、HD2ModManager——即使其存储目录已更改——以及 Echelon）会按该管理器的列表导入，加载顺序、开关与可选内容一并保留；其他 mod 文件夹则原地加入。库中已有的 mod 不会重复加入。
- **其他管理器存放 mod 的文件夹只按其配置文件导入**，因为加载顺序、开关和可选内容都记录在配置文件中。程序依据各管理器在 mod 文件夹上留下的特征来识别：

  | 管理器 | mod 文件夹的特征 | 配置文件 |
  |---|---|---|
  | HD2 Arsenal | 文件夹名以 `_AR` 加 6 位数字结尾 | `%LOCALAPPDATA%\hd2arsenal\hd2a_data.json` |
  | HD2ModManager | 每个 mod 都有带 Guid 的 `manifest.json`，其中有些是它为导入的 mod 生成的 | 其 `Mods` 文件夹旁的 `enabled.json` |
  | Echelon | 文件夹名以“空格（12 位十六进制）”结尾，文件放在编号文件夹中 | `%APPDATA%\Echelon\library.json` |

  配置文件不在该管理器通常保存的位置、或其中没有记录此文件夹的 mod 时，程序会说明这是哪个管理器的文件夹，并请你选择配置文件，不会散装加入。找到了配置文件但无法读取时，则说明这一情况。
- **更改路径…**：文件夹被手动移动，或所在分区的盘符改变时使用。其中的 mod 会随之指向新位置，磁盘上的文件不做移动。
- 含有 mod 的文件夹会自动列出（如 HD2 Arsenal、HD2ModManager、Echelon 的文件夹）。
- **删除 MOD**：选择文件夹，勾选其中的 mod（默认按大小排列），点击“删除…”：文件夹将被删除，这些 mod 也会从 mod 库与游戏中移除。
- **移动 MOD**：选择文件夹，勾选其中的 mod，点击“移动到…”并选择目标文件夹。“移动”将删除原文件夹；“复制”保留原文件夹，供仍在使用的其他管理器使用。完成后程序会说明移动的数量与位置，并将游戏目录中对应的复制件改为硬链接。

**与游戏不在同一分区的 mod** 只能以复制方式部署：额外占用一份空间，每次变更都需重新写入。程序会在导入或添加文件夹后、以及存储页顶部提示此情况，并按选择处理：

| 选择 | 结果 |
|---|---|
| 保持现有位置 | 不移动任何文件，以复制方式部署，此文件夹不再提示。之后仍可随时在存储页移动。 |
| 移动 | 移动到默认文件夹（位于游戏所在分区时），否则移动到游戏所在分区的 `HD2 Mods`；提示中会写明目标文件夹。原文件夹删除。 |
| 复制 | 与“移动”相同，但保留原文件夹（mod 将占用两份空间）。 |

### 检查更新

点击列表上方的 **检查更新**，程序将按各 mod 的来源渠道检查新版本：

| 来源 | 检查方式 | 更新方式 |
|---|---|---|
| GitHub | 仓库的最新发布版本（Release）。 | 点击 **更新** 下载该版本的 zip、7z 或 rar。 |
| Nexus Mods | 登录 Nexus Mods 后（见“Nexus Mods”）：先请求一次该游戏最近一个月有更新的 mod，再只对上次检查后有变化的 mod 各请求一次。 | 高级会员：点击 **更新** 直接下载新文件。普通会员：点击 **打开页面**，在网站上点击“Mod Manager Download”。 |
| 其他网站 | 不检查。 | 点击 **打开页面** 查看。 |

- mod 的来源在加入时自动记录：用浏览器下载的文件（Windows 会保存下载地址），以及 HD2 Arsenal 为其安装的 mod 保存的记录。
- 未记录来源的 mod，可在详情中点击 **设置来源…**，填写该 mod 的 Nexus Mods 页面或 GitHub 仓库地址。
- 有更新的 mod 在列表中显示黄色标记，并可通过 **有更新** 筛选查看。
- 更新后，mod 的加载顺序、开关和可选内容保持不变；之后执行部署即可生效。
- **跳过此版本**：不再提示这次更新，出现更新的版本时会再次提示。点击“取消跳过”可撤销。命令行为 `hd2qd skip MOD [--clear]`。
- **上一版本**（设置 →“为脚本 MOD 保留上一版本”，默认关闭）：含 Lua 脚本的 mod 换新版本时（拖入 zip、下载或更新），原来的版本保留在磁盘上，每个 mod 保留一个。详情中显示“上一版本”的版本号和日期，点击 **回滚到上一版本** 会将两个版本互换，因此也可以用同样的方式换回新版本；之后执行部署即可生效。不含脚本的 mod 不保留：它们体积较大，坏掉的版本也很少导致游戏无法运行。保留的版本列在存储页的“保留的上一版本”中，可以打开、回滚或删除。命令行为 `hd2qd keep-previous-on`、`keep-previous-off`、`previous [--delete]`、`rollback MOD`。

### Nexus Mods

包含 Nexus Mods 部分的版本（我们发布的版本，以及 `build.bat --with-nexus` 构建的版本）：

- **登录**：在“下载”页面点击“登录 Nexus Mods”，会在浏览器中打开 Nexus Mods 自己的页面，在那里允许本程序即可（OAuth 2.0 + PKCE）。本程序不会接触你的密码。登录信息仅保存在本机，以你的 Windows 账户加密，并会自动续期。“退出登录”只在本机删除登录信息；也可以在 Nexus Mods 网站上收回本程序的访问权限。
- **下载**：本程序接管“Mod Manager Download”链接后，网站上的这个按钮会把文件交给本程序（“下载”页面上一个按钮用于接管，另一个用于交还给原来的程序）。高级会员还可以在列表中直接更新 mod。
- **按编号查找**：在“下载”页面的“按 Nexus 编号查找”中输入 mod 编号（如 `16662`、`N站16662`）或 mod 页面地址，即可看到该 mod 页面上的名称、作者、版本、下载与点赞数、简介，以及是否已在 mod 库中，并可一键前往其 Files 页面下载。无需登录（使用 Nexus Mods 的公开 GraphQL 接口）。命令行为 `hd2qd nexus-info 16662`。
- **请求**：每次请求都由你的操作发起，并带有本程序的名称和版本。启动时和后台都不会向 Nexus Mods 发送请求。

### LUA MOD 部署前检查

游戏只运行一个启动脚本（`boot`），即补丁编号最高的那个。Boot Mod Loader、Chef Shared Loader、Objective Tracker 和 Medic3038 的安装器都带有启动脚本，因此只有其中一个会运行。窗口在部署前读取每个 mod 的 Lua 脚本，说明将会发生的情况；不会修改其他 mod 的文件，也不会替玩家做选择。

| 检查内容 | 显示方式 |
|---|---|
| **非加载器的启动脚本** | 自带 `boot`、但只启动它自己的 mod（如 Objective Tracker 0.6.7）会占用游戏唯一的启动入口，由加载顺序决定它和加载器谁运行。生效的是 Boot Mod Loader 0.12.0 或更新版本时，部署会把这个脚本从入口上移开：游戏目录中是该存档的副本，脚本标记为 `-- HD2-Guest-Boot: <mod>`，由 Boot Mod Loader 在其他 mod 之后运行。mod 库中的文件不变，加载顺序也不影响结果。加载器版本较旧或没有加载器时，不做任何改动。 |
| **脚本名称** | 每个 mod 的详情列出其脚本，名称与 Boot Mod Loader 日志和 Mod Lag Watchdog 中显示的一致，如 `mods/max/maelstrom_traverse`。在搜索框中输入这样的名称即可找到对应的 mod。 |
| **加载器** | 加载器（Boot Mod Loader、Chef Shared Loader、Bingus Shared Loader、MDL3）不在 mod 列表中，而在左侧面板中列出，并在那里选择一个，对所有配置档案通用；也可以选择不使用加载器。部署时，选中的加载器放在所有 mod 之后，游戏运行的就是它的启动脚本；其他加载器不部署。它们在列表中的位置和各自的开关都不影响结果。点击加载器的名称可查看详情。面板上只显示各加载器的状态（生效中、协助中、未使用）；库中没有加载器时不显示该面板。点击面板的 **i** 按钮可查看详细信息：每个加载器（包括生效中的那个）选用时哪些 mod 不会启动及原因（仅能由 Chef Shared Loader 启动、脚本超过 Chef 的 4 MB 上限、启动脚本被其他 mod 的取代），或者所有 Lua mod 均可启动；Chef 协助时，列出它启动的 mod。其中每一行都可以选择和复制。点击加载器前的圆圈会立即显示切换，保存在后台进行。 |
| **Chef Shared Loader 协助** | 生效的是 Boot Mod Loader 0.13.0 或更新版本、库中有 Chef Shared Loader、并且启用了仅能由 Chef 启动的 mod（mod 自身声明，或脚本调用了 Chef 提供的 `chef` 接口）时，部署会把 Chef 一并放入游戏目录，位于 Boot Mod Loader 之前。Boot Mod Loader 先启动自己的 mod，再通过 Chef 公开的交接方式，把只认 Chef 的 mod 交给它启动。这些 mod 的设置在 Chef 的 F9 菜单中，其他 mod 仍使用设置窗口。左侧面板中 Chef 显示为“协助中”。 |
| **启动脚本** | 游戏运行的是哪个 mod 的 `boot`，哪些不会运行。 |
| **超出 Chef Shared Loader 上限的脚本** | Chef Shared Loader 1.2.1 不读取超过 4 MB 的 Lua 脚本。将由它启动、但脚本超过此大小的 mod 会标为 **不会启动**，并注明脚本大小；选用 Chef 之前，加载器面板的详细信息中就会列出这些 mod。Boot Mod Loader 没有这一限制。 |
| **Chef Shared Loader 下的启动脚本** | Chef Shared Loader 也会运行部署在它之前的启动脚本，因此 Chef 生效时，只启动自身的 mod 自带的 `boot` 同样不构成冲突；该 mod 的详情会说明由 Chef 运行。 |
| **每个 MOD 需要的加载器** | 读取脚本开头几行。`-- HD2-Addon:` 可由 Boot Mod Loader 或 Chef Shared Loader 启动；`-- HD2-Requires: chef-shared-loader`（或 `-- HD2-Boot:`）仅能由 Chef Shared Loader 启动。详情中只列出库中已有的加载器。没有生效的加载器、需要的加载器未生效时，均会提示。 |
| **游戏无法运行的脚本** | 2026-10-20 更新后，游戏使用的是 Lua 5.1，而非 LuaJIT。无法编译的脚本（goto、LuaJIT 的 `ULL` 数字、Lua 5.2 及以后的运算符、BOM、单个函数超过 200 个局部变量或 60 个上值等）、LuaJIT 字节码、以及没有备用方案地使用 `ffi`、`bit`、`jit` 的脚本，标为 **无法运行**，并说明它用到了什么（FFI、LuaJIT 的 64 位整数写法、goto 等），再用一句话说明游戏目前不接受或不提供这一项，库中的加载器也未提供。语法检查按照 PUC Lua 5.1 自身的词法和语法分析器实现，与 PUC Lua 5.1 对比的 15,394 个脚本结论全部一致（`tests/lua_syntax_vs_puc.py`）。注释与字符串中的内容不计。 |
| **同名 MOD** | 两个 mod 声明同一个 `mods/<作者>/<名称>` 时，只有加载顺序靠后的那个会运行。 |
| **翻译包** | 翻译包（与原 mod 同一作者目录、名称为原 mod 名加语言后缀，并包装旧菜单的登记调用，例如 ActionCam简体中文）显示为原 mod 下方的一行小字，带有自己的勾选框，并在加载顺序中保持紧接在原 mod 之后。关闭原 mod 时，它的翻译包一起关闭；重新打开原 mod 时，随它关闭的翻译包一起恢复；手动开关过的翻译包保持原样。原 mod 未安装时，提示翻译不会生效。 |
| **上一局** | Boot Mod Loader 上次启动游戏时的日志：哪些 mod 已启动、各自用时，以及哪些启动失败或无法编译，并附原因。 |

以上检查均在游戏外、部署前完成，不在游戏中运行任何内容。列表不会因此等待：检查结果保存在 `lua_scan.json` 中，尚未读取的文件在后台读取，结果稍后显示。

### 注意事项

- **请仅使用一个管理器部署。** 使用其他管理器部署会覆盖本程序的部署，反之亦然。
- **请勿直接修改游戏目录中已部署的 patch 文件**：硬链接与 mod 库中的文件为同一文件，修改会同时影响 mod 库。恢复原版不影响 mod 库。
- 游戏运行时无法部署或恢复原版。
- 界面语言为本程序独立的设置（设置窗口中选择，下次启动时保留）；未设置时跟随 Windows 的语言。
- **反馈问题时**：在设置页“问题反馈”中点击“导出诊断包”，桌面上会生成一个 zip 文件，简要摘要同时复制到剪贴板；提问时附上该 zip 即可（命令行为 `hd2qd report`）。诊断包不含崩溃转储和 Nexus Mods 登录信息，Windows 用户文件夹会写成 `%USERPROFILE%`。
- **hd2qd 可以由 AI 助手调用。** 所有数据保存在 `%USERPROFILE%\.hd2-mod-cli`，而不是 AppData：由打包应用（多数桌面版 AI 助手都是）启动的程序，在 `%APPDATA%` 下新建的文件夹会被 Windows 放进该应用的私有目录，其他程序都读不到，mod 库会在没有任何报错的情况下分成两份。如果仍将 `HD2MOD_HOME` 设在 AppData 下，hd2qd 会在写入前检查，并拒绝执行、说明写入本会落在何处。

### 命令行 hd2qd

    hd2qd init
    hd2qd import-arsenal     # 仅读取 HD2 Arsenal 的数据，将其 mod、加载顺序、开关和可选内容加入库中
    hd2qd import-hd2mm       # 同上，来源为 HD2ModManager
    hd2qd import-echelon     # 同上，来源为 Echelon
    hd2qd folders            # 列出 mod 库文件夹
    hd2qd status             # 显示部署将执行的更改，不做修改
    hd2qd check-updates      # 检查来自 GitHub 的 mod 是否有新版本
    hd2qd rollback MOD       # 回滚到保留的上一版本（先用 keep-previous-on 开启保留）
    hd2qd lua                # 部署前检查 Lua mod：启动脚本、加载器、无法运行的脚本、上一局、上次崩溃
    hd2qd self-update        # 检查本程序是否有新版本（正式版只看正式发布，开发版也看预发布）
    hd2qd report             # 导出诊断包（zip，默认在桌面），反馈问题时附上
    hd2qd deploy

完整命令列表见英文部分的表格。设置与记录保存在 `%USERPROFILE%\.hd2-mod-cli`。每个文件保存时会把上一版留为同名的 `.bak`：mod 可以重新下载，而你的选择（哪些 mod、顺序、可选内容、标签）不能，且只有几十 KB。需要回退时，把 `.bak` 改名覆盖原文件即可。

**默认 mod 库文件夹**（添加的 zip 解压为此处的文件夹，不保留压缩包）不位于 AppData：优先使用已设置的文件夹（存储页的“设为默认”，或 `folder-default`）；未设置时使用 HD2 Arsenal 的 mod 文件夹；未安装 HD2 Arsenal 时使用游戏所在分区的 `HD2 Mods`，以便部署时使用硬链接。
