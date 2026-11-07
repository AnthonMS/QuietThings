# Instructions for an AI applying the QuietThings kit

You are adding the QuietThings API to the user's existing QuietTizen launcher. The kit patches their launcher; it never ships or replaces it. Assume the QuietTizen prerequisites (Developer Mode, a working install, the framework DLLs) are already met. If they are not, stop and tell the user.

Read `kit/README.md`, `kit/CONTRACT.md` and `kit/src/HOOKS.md` before changing anything.

## Rules that do not bend

- Do not change port 18765, the endpoint paths, the JSON field names, Bearer auth or `api_version` (the block "Fixed, do not change" in `CONTRACT.md`).
- Do not edit `kit/src/LanApi.cs` or `kit/src/LanApiCore.cs`. `predeploy.py` fails on a hash mismatch. If a hook does not fit the user's `Home.cs`, adapt the hook, never the kit source or the contract.
- The token is a secret. Never paste it into a chat, a log, an issue or a command line you echo. Scripts and tools print its path, never its value.
- Work only on the TV the user names, by its explicit IP address. Do not scan the network.
- One SDB or root session at a time, each under a 30 second budget. A failed script may have done part of its work: inspect before retrying, and never overwrite a first backup to make a retry succeed.
- Do not reboot or put the TV in standby inside a root session.
- Stop on any model, runtime or firmware mismatch you notice, explain the gap and ask.
- Installing restarts the launcher app on the user's live TV. Ask before that step.

## Phases

1. Inventory. Find the user's launcher source: `launcher/src/Home.cs` and `Home.csproj`. If the source is lost, stop and tell the user: the TV holds only the compiled launcher, which cannot be turned back into `Home.cs`, so they need to recover or re-download their QuietTizen source first. Note the target framework (`net6.0` or `netcoreapp3.1`) and where the four framework DLLs are.
2. Patch. Copy `kit/src/LanApi.cs` and `kit/src/LanApiCore.cs` next to `Home.cs`, unmodified. Apply `kit/src/HOOKS.md` to `Home.cs` and `Home.csproj`. Keep the user's other changes.
3. Pre-deploy checks on the PC:

   ```
   python kit/tools/predeploy.py --launcher <launcher src dir> --framework-dir <framework dir> --target-framework <net6.0|netcoreapp3.1> --out <build dir>
   ```

   Every non-skipped check must print `PASS`. Stop on any `FAIL` and fix the cause. The run prints the SHA-256 of the built `BixbyPromotion.dll` (also written to `BixbyPromotion.sha256` in the build dir); you need that value for the install. Read the closing list of what predeploy did not verify and tell the user.
4. Token. Generate it with `python kit/tools/predeploy.py --launcher <launcher src dir> --make-token <path>` (`--launcher` is required on every run, and the other checks run too); the file is written with restricted permissions and the value is not printed. Keep it out of the repository.
5. Stage. Push the built DLL and the token file to the TV with `tv push` or the user's own push route. The recipes take a bare file name (a name containing `/` is rejected) and look for it in `/home/owner/share/tmp/sdk_tools` and then in its `quiet-tizen` subfolder, which is where upstream's `tv push` stages. If the same name exists in both places they print `staged_ambiguous` and stop: remove one copy and rerun. If it exists in neither they print `staged_missing`. Upstream's `tv` helper may refuse TVs other than its tested model; if so, use the user's own route.
6. Install, one session each, with the user's approval for the first:
   - `kit/recipes/install-api.sh NAME STAGED_FILE EXPECTED_SHA256`, where `NAME` is a backup name not used before, `STAGED_FILE` is the staged DLL's file name and `EXPECTED_SHA256` is the hash predeploy printed. A `backup_mismatch` result means that name is taken: choose a new one.
   - `kit/recipes/install-token.sh STAGED_TOKEN`. Compare the owner and label it prints with what you expect; a mismatch usually shows up as a 401 later.
   - `kit/recipes/verify-api.sh`. Expect `token_present=yes` from the script's own check of the token file. The listener note it also prints has its own `token_present=` line, written when the listener started, so it can read False if the token was installed afterwards; that is not a failure (the API reloads the token within about 10 s).
7. Verify from the PC, at least 300 ms apart (the API refuses more than 4 requests per second):

   ```
   python kit/tools/api_client.py --host <tv ip> --token-file <path> health
   ```

   then `state`, and with the user's agreement `hdmi 2`, `volume 20`, `mute off`. Expect `api_version` 1 and a state that matches the TV.
8. Summarize the evidence: the DLL hash, the backup name, what was and was not verified, and how to undo.

## Rollback

`kit/recipes/rollback-api.sh NAME` restores the DLL saved under that backup name and checks its hash. Run it if the launcher misbehaves after the swap. To disable the API alone, delete `lan-api.token` on the TV.

## Home Assistant

The user adds the `quietthings` integration in Home Assistant with the TV's IP address, the API port (default 18765), the token (give them the token file path, not the value in chat), and a name. The MAC address and broadcast address (used for power on) are detected automatically; Home Assistant asks for the MAC only if it cannot find it. A 401 from the API means the token on the TV and the one in Home Assistant differ.
