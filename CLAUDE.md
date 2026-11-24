# Chameleon: working rules

* **Always ship the plug-in binary.** After any change to the code or project files, run
  `dotnet build -c Release` (it copies the Rhino 8 build to `dist/Chameleon.rhp`) and commit the
  updated `dist/Chameleon.rhp` in the same commit as the code change. The user installs new
  versions by pulling or downloading this file, so it must never lag behind the source.
* Bump `<Version>` in `Chameleon.csproj` for each user-facing change.
* Keep `README.md` in sync with commands, options and layer names.
* The plug-in can't be run in the cloud container (there's no Rhino there). Compile-check against
  RhinoCommon, and say plainly that a change hasn't been tested in Rhino.
