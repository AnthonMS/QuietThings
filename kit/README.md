# QuietThings kit

This kit adds the QuietThings API to the QuietTizen launcher you already run on your Samsung TV. The API is a small token-protected HTTP service inside the launcher that lets Home Assistant read the TV's state, switch HDMI inputs and set volume and mute. The kit patches your launcher; it does not ship or replace it, and your own AI does the patching.

You need a working QuietTizen install with Developer Mode already set up, the four Samsung framework DLLs (`Tizen.Applications.Common`, `Tizen.NUI`, `Tizen.System`, `Tizen`) already pulled from your TV for building, the .NET 6 SDK, Python 3.12 or newer, and a way to push files to the TV and run a script as root (QuietTizen's `tv` helper, or your own route). Your launcher source (`Home.cs` and `Home.csproj`) should be at hand.

Nothing is installed until you say so, and every step can be undone.

## Steps

1. Download this `kit` folder to your PC.
2. Open your AI assistant in your QuietTizen project and give it `kit/AGENTS.md`. It reads `CONTRACT.md` and `src/HOOKS.md` as it goes.
3. The AI copies the two source files in, applies the hooks to `Home.cs` and `Home.csproj`, and runs `tools/predeploy.py`. Any failed check stops the process.
4. The AI generates a token, stages the built launcher DLL and the token on the TV, and runs the scripts in `recipes/`. This restarts the launcher app on the TV, so the AI should ask you first.
5. The AI confirms the API answers from your PC (`tools/api_client.py health`).

## Home Assistant

Install the `quietthings` integration (see the main README). When you add it, enter the TV's IP address, the API port (default 18765), the same token the AI generated (the token file it wrote on your PC) and a name for the TV. The MAC address and broadcast address (used for power on) are detected automatically. Treat the token like a password: do not paste it into chats, issues or logs.

## Undo

`recipes/rollback-api.sh NAME` puts back the launcher DLL that was installed before, using the backup name given at install time. To switch the API off without touching the launcher, delete `lan-api.token` from the launcher's data directory on the TV; every request is then refused.

`CONTRACT.md` is the fixed API contract. `NOTICE.md` has the license notices.
