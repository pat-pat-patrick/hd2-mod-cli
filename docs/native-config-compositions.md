# Experimental native configuration compositions

This opt-in backend manages version-pinned replacements for `data/game/*.dl_bin`.
It does **not** decrypt game files or compute arbitrary field merges. A recipe
provides validated output images for supported feature combinations. Each mod
keeps its own library entry, enable switch and manifest options.

For example, two independent features that affect the entity table need four
images: original, feature A, feature B, and A+B. A feature for another target
has its own original and modified images. Those two tables cover eight switch
combinations without a combined mod entry in the library.

No game files, encryption keys, real mod payloads or machine-specific recipes
are included in this proposal. The tests use small synthetic images and a fake
game folder. Existing libraries behave as before when no recipe is installed.

## Recipe version 1

`native-configs.json` lives in the manager's settings home. The initial schema
uses absolute paths for images; mod archive paths are relative to that mod's
folder. Relative image paths and a normal import flow are open design questions.

```json
{
  "version": 1,
  "targets": [
    {
      "target": "game/example.dl_bin",
      "features": [
        {
          "id": "feature_a",
          "mod_id": "<library-mod-id>",
          "manifest_guid": "<manifest-guid>",
          "payload": "Install/0123456789abcdef.patch_0",
          "archives": [
            {
              "path": "Install/0123456789abcdef.patch_0",
              "sha256": "<sha256-of-the-original-mod-archive>"
            }
          ]
        }
      ],
      "variants": [
        {
          "features": [],
          "file": "<absolute-path-to-original-image>",
          "sha256": "<sha256-of-original-image>"
        },
        {
          "features": ["feature_a"],
          "file": "<absolute-path-to-modified-image>",
          "sha256": "<sha256-of-modified-image>"
        }
      ]
    }
  ]
}
```

- A feature is active when its mod is enabled and its manifest choices include
  the folder holding `payload`.
- `mod_id` binds an existing entry. `manifest_guid` also lets a removed mod be
  recognised when the same manifest is imported again. Multiple matching copies
  are refused.
- Exactly one variant must match the active feature IDs for a target. An empty
  feature list is the original baseline.
- `archives` lists the hash-pinned legacy payload/installer archives that this
  backend replaces. They and their companions are omitted from the patch plan.
  Unregistered archives added to such a mod are refused.
- An unregistered HD2FM1 payload for a recipe's target is refused rather than
  competing with the native composition.

## Deploy and restore

```text
read switches and manifest choices
  -> select one known image per target
  -> verify image hashes and current game files
  -> deploy ordinary archives without the replaced installers
  -> stage native replacements and rollback copies
  -> replace native targets; save ownership
```

The current game file must equal one of the recipe's known images. A game update
or an external edit is refused; the backend does not restore an old baseline over
an unknown file. Deploy is blocked while the game is running if it would change
native files or their installation ownership.

`native-config-deployed.json` records the game folder, output hash, baseline hash
and active mod IDs per target. Disabling a feature recomposes the remaining
features. Disabling/removing all features restores the baseline on deploy.
`purge` also restores tracked native files before deleting patch archives.

Known `HD2FileMods.state` entries are removed when these targets are adopted, so
the old installer cannot undo the native deployment. Entries for other targets
remain untouched. The game does not need an install/uninstall launch-and-exit
cycle for configurations handled by this backend.

The GUI marks these entries as configuration mods, includes native work in the
pending count, and explains their deploy/restore behaviour. `state`, `status`,
`deploy --dry-run` and `check --deep` include native configuration results.

## Failure handling and draft boundaries

Native outputs are staged and hash-checked before replacing any native target.
If a later native replacement fails, earlier native replacements are rolled
back. This is not yet an atomic transaction spanning **ordinary archives and
native configurations**: ordinary patch changes precede native commit. A failed
native phase may therefore leave archive work pending reconciliation on retry.

Review is requested on:

1. The recipe/import interface, portability and who supplies versioned images.
2. Failure recovery across the entire deploy, including interruption/crash
   recovery and a rollback failure itself.
3. Image storage/relocation and the upgrade experience when a game version is
   no longer recognised.
4. In-game validation. Automated tests cover deployment and restoration; they
   do not establish gameplay compatibility.

Before reverting to a manager version without this backend, disable the native
features and deploy with this version so the original files are restored.
