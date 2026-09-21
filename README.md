# VanguardParser BepInEx Mod Project

This workspace contains a minimal BepInEx-style plugin project for VanguardParser.

## Structure

- `src/VanguardParserMod/` — plugin source and project file

## Build

```sh
dotnet restore
 dotnet build
```

## Logging config

The plugin supports optional file logging via BepInEx config:

- `Logging.writeLog` — boolean, default `false`
- `Logging.logFileName` — string, default `combatlog.txt`

When `writeLog` is `true`, the plugin writes each damage log entry to the file name configured in `logFileName` in the current working directory.

## Install

Copy the compiled `VanguardParserMod.dll` into your BepInEx plugin folder for the game runtime.
