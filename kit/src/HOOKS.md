# Hooks for the QuietThings API

These are the only changes a QuietTizen launcher needs. They are written against upstream QuietTizen (https://github.com/jss8649/quiet-tizen, commit cf518d7): `launcher/src/Home.cs` and `launcher/src/Home.csproj`. Your copy may differ; adapt the hook to your code, never the two kit source files and never the API contract.

Do not touch anything in `kit/src`. Copy `LanApi.cs` and `LanApiCore.cs` next to `Home.cs` unmodified.

Scope: API only. No UI changes, no `#if NETCOREAPP3_1` blocks, no `inputs.json` handling, no `MainWindow` change and no `MaxHdmiSource` change belong here.

## 1. Home.csproj: compile the two files

The project sets `EnableDefaultCompileItems` to false, so the files must be listed. Upstream line:

```xml
<ItemGroup><Compile Include="Home.cs" /><Compile Include="AppLibrary.cs" /></ItemGroup>
```

becomes:

```xml
<ItemGroup><Compile Include="Home.cs" /><Compile Include="AppLibrary.cs" /><Compile Include="LanApi.cs" /><Compile Include="LanApiCore.cs" /></ItemGroup>
```

## 2. Home.cs: a paused flag

Add the field, set `paused=true` as the first statement of `OnPause` and `paused=false` as the first statement of `OnResume`. The API reports it as the `paused` field in `/health` and `/state`.

```csharp
 volatile bool paused;
 protected override void OnPause(){paused=true;base.OnPause();editing="";clockTimer?.Stop();WriteState("paused");}
```

For `OnResume`, put `paused=false;` before `base.OnResume();` and leave the rest of the method as it is. Upstream, the start of the line is `protected override void OnResume(){base.OnResume();...`, which becomes `protected override void OnResume(){paused=false;base.OnResume();...`.

## 3. Home.cs: volume and mute wrappers

Both run on the UI thread and refresh the cached values the launcher shows. `sound_manager_set_master_volume`, `sound_manager_get_master_volume`, `sound_manager_set_master_mute` and `sound_manager_get_master_mute` are already declared in upstream Home.cs.

```csharp
 bool SetVolume(int level){int r=sound_manager_set_master_volume(Math.Max(0,Math.Min(100,level)));if(r==0&&sound_manager_get_master_volume(out var v)==0)currentVolume=v;return r==0;}
 bool SetMute(bool mute){int r=sound_manager_set_master_mute(mute?1:0);if(r==0&&sound_manager_get_master_mute(out var m)==0)currentMute=m;return r==0;}
```

If your launcher names its cached volume and mute fields differently, use those names.

## 4. Home.cs: SelectSource returns the result

`SelectSource` must return the `connect_source` result (negative means failure) instead of `void`. Callers stay unchanged. Upstream line:

```csharp
 void SelectSource(int source){int r=connect_source(source);if(r<0){ShowError("Input could not be switched. Open Advanced settings to check the connection.");WriteState("source_failed:"+r);return;}lastSource=source;Launch("org.tizen.tv-viewer");}
```

becomes:

```csharp
 int SelectSource(int source){int r=connect_source(source);if(r<0){ShowError("Input could not be switched. Open Advanced settings to check the connection.");WriteState("source_failed:"+r);return r;}lastSource=source;Launch("org.tizen.tv-viewer");return r;}
```

## 5. Home.cs: FallbackName

The API asks the launcher for a name for a port when the TV itself reports none. A launcher with no `inputs.json` returns `null`, and the API then uses "HDMI n".

```csharp
 string FallbackName(int port){return null;}
```

Optional UI work, not needed for the API: a launcher that keeps its own input names can return them from `FallbackName` and use `LanApiCore.WithFallbackNames(LanApi.ReadSources(),FallbackName)` and `LanApiCore.NameOf(list,port)` to show the same names on its cards. Skip this unless you want it.

## 6. Home.cs: start the API

The last line of `OnCreate`, after the existing statements:

```csharp
  try{new LanApi(SelectSource,()=>paused,SetVolume,SetMute,FallbackName).Start();}catch{}
```

This must run on the UI thread: the `LanApi` constructor captures `SynchronizationContext.Current`, and the API later marshals every source, volume and mute call back onto it. `OnCreate` runs on the UI thread, so keep the line there and do not move it into a timer callback or a task.

## Ports

Upstream Home.cs wires 3 HDMI sources (`lastSource` 13 to 15). The API always reports ports 1 to 4 with source ids 13 to 16. On a 3-port wiring, port 4 simply reads as not connected. Do not change the API to match the launcher.

## Checking the result

Run `kit/tools/predeploy.py` against your launcher folder. It checks that both files are listed in `Home.csproj`, that the hook text above is present in `Home.cs`, and that the project builds.
