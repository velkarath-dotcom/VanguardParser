using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using VGParserMod.Patches;
using UnityEngine;

namespace VGParserMod;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInProcess("VanguardGalaxy.exe")]
public class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "vgparser";
    public const string PluginName = "VG Parser";
    // BepInEx parses PluginVersion through System.Version which rejects SemVer
    // pre-release suffixes, so stick to the plain N.N.N form.
    public const string PluginVersion = "0.0.1";

    internal static Plugin Instance { get; private set; } = null!;
    internal static ManualLogSource Log { get; private set; } = null!;
    internal static ConfigEntry<bool> WriteLogSetting { get; private set; } = null!;
    internal static ConfigEntry<string> LogFileNameSetting { get; private set; } = null!;
    internal static ConfigEntry<string> PanelPositionSetting { get; private set; } = null!;

    private Harmony _harmony = null!;

    private void Awake()
    {
        Instance = this;
        Log = Logger;
        WriteLogSetting = Config.Bind("Logging", "writeLog", false, "If true, damage logs are written to disk.");
        LogFileNameSetting = Config.Bind("Logging", "logFileName", "combatlog.txt", "Filename used for the damage log when file logging is enabled.");
        PanelPositionSetting = Config.Bind("Panel", "panelPosition", "24,-120", "Saved panel position as x,y anchored coordinates.");

        _harmony = new Harmony(PluginGuid);
        DamageLoggingPatches.Register(_harmony);
        _harmony.PatchAll();

        Log.LogInfo($"{PluginName} v{PluginVersion} loaded ({_harmony.GetPatchedMethods().Count()} patches)");
    }


    private void Update()
    {
        // Outside the 4 Hz gate below: the check hands its result over exactly once and `Pump` is what collects
        // it, so throttling this would only delay the row by up to a quarter second for no gain.
        // _notice?.Pump();

        if (Time.realtimeSinceStartup - _lastPanelDraw < 0.25f) return;
        Panel.Raise(true);
        _lastPanelDraw = Time.realtimeSinceStartup;
        var dps = DamageLoggingPatches.GetPlayerDps();
        var damage = DamageLoggingPatches.GetPlayerOutgoingTotalDamage();
        var categories = DamageLoggingPatches.GetDamageByCategory();
        if (Panel.IsOpen) Panel.Refresh(dps, damage, categories);
    }

    private float _lastPanelDraw;

    private void OnDestroy()
    {
        _harmony?.UnpatchSelf();
    }
}