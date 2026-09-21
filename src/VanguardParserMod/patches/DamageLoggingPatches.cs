using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Behaviour.Equipment.Turret;
using Behaviour.Weapons;
using Behaviour.Unit;
using HarmonyLib;
using UnityEngine;
using VGParserMod;

namespace VGParserMod.Patches;

public static class DamageLoggingPatches
{
    private const string UiInfoTextParentTypeName = "Behaviour.UI.UIInfoTextParent, Assembly-CSharp";
    private static readonly Dictionary<string, DamageCategory> DamageByCategory = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConditionalWeakTable<DamageData, object> TorpedoDamageMarkers = new();
    private static readonly ConditionalWeakTable<DamageData, object> TorpedoExplosionDamageMarkers = new();
    private static readonly TimeSpan PlayerDamageResetThreshold = TimeSpan.FromSeconds(20);
    private static double PlayerOutgoingTotalDamage = 0d;
    private static DateTime PlayerOutgoingDamageFirstSeenUtc = DateTime.UtcNow;
    private static DateTime LastPlayerDamageDoneUtc = DateTime.UtcNow;
    private static double PlayerIncomingTotalDamage = 0d;
    private static DateTime PlayerIncomingDamageFirstSeenUtc = DateTime.UtcNow;
    private static DateTime LastPlayerDamageTakenUtc = DateTime.UtcNow;
    private static float PlayerDamagePerSecondValue = 0f;

    public static  Dictionary<string, DamageCategory> GetDamageByCategory()
    {
        return DamageByCategory;
    }
    public static float GetPlayerDps()
    {
        return PlayerDamagePerSecondValue;
    }

    public static double GetPlayerOutgoingTotalDamage()
    {
        return PlayerOutgoingTotalDamage;
    }
    public static float UpdatePlayerDps()
    {
        var now = DateTime.UtcNow;
        if ((now - LastPlayerDamageTakenUtc) > PlayerDamageResetThreshold)
        {
            PlayerIncomingTotalDamage = 0d;
            PlayerIncomingDamageFirstSeenUtc = now;
        }

        var elapsedSeconds = Math.Max((now - PlayerOutgoingDamageFirstSeenUtc).TotalSeconds, 0.001d);
        PlayerDamagePerSecondValue = (float)(PlayerOutgoingTotalDamage / elapsedSeconds);
        return PlayerDamagePerSecondValue;
    }

    private static void WriteLog(string message)
    {
        try
        {
            if (Plugin.WriteLogSetting == null || !Plugin.WriteLogSetting.Value)
            {
                return;
            }

            var fileName = Plugin.LogFileNameSetting != null && !string.IsNullOrWhiteSpace(Plugin.LogFileNameSetting.Value)
                ? Plugin.LogFileNameSetting.Value
                : "combatlog.txt";
            var fullPath = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), fileName);
            using var stream = new System.IO.StreamWriter(fullPath, append: true);
            stream.WriteLine(message);
        }
        catch
        {
            // Ignore file logging failures and fall back to console so gameplay is not interrupted.
        }
    }

    public static void ClearCurrentDamage()
    {
        DamageByCategory.Clear();
        PlayerOutgoingTotalDamage = 0d;
        PlayerOutgoingDamageFirstSeenUtc = DateTime.UtcNow;
        PlayerDamagePerSecondValue = 0f;
    }

    public static void Register(Harmony harmony)
    {
        try
        {
            var takeDamage = typeof(AbstractUnit).GetMethod(nameof(AbstractUnit.TakeDamage), new[] { typeof(DamageData) });
            if (takeDamage == null)
            {
                Console.WriteLine("[VGParserMod] DamageLoggingPatches: AbstractUnit.TakeDamage(DamageData) could not be located.");
                return;
            }

            var takeDamagePostfix = new HarmonyMethod(typeof(DamageLoggingPatches).GetMethod(nameof(LogDamageTakenPostfix), BindingFlags.Static | BindingFlags.NonPublic)!);
            harmony.Patch(takeDamage, postfix: takeDamagePostfix);

            var processHit = typeof(Torpedo).GetMethod("ProcessHit", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(Collider2D) }, null);
            if (processHit != null)
            {
                harmony.Patch(processHit, prefix: new HarmonyMethod(typeof(DamageLoggingPatches).GetMethod(nameof(MarkTorpedoProcessHitPrefix), BindingFlags.Static | BindingFlags.NonPublic)!));
            }

            var explode = typeof(Torpedo).GetMethod("Explode", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(DamageData) }, null);
            if (explode != null)
            {
                harmony.Patch(explode, prefix: new HarmonyMethod(typeof(DamageLoggingPatches).GetMethod(nameof(MarkTorpedoExplosionPrefix), BindingFlags.Static | BindingFlags.NonPublic)!));
            }

            var areaDamageFactory = typeof(Source.Util.AreaDamageHelper).GetMethod("CreateNewDamageData", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(DamageData), typeof(Transform), typeof(float), typeof(Collider2D), typeof(bool) }, null);
            if (areaDamageFactory != null)
            {
                harmony.Patch(areaDamageFactory, postfix: new HarmonyMethod(typeof(DamageLoggingPatches).GetMethod(nameof(MarkAreaDamageFromTorpedoPostfix), BindingFlags.Static | BindingFlags.NonPublic)!));
            }

            var areaDamageFactoryLegacy = typeof(Source.Util.AreaDamageHelper).GetMethod("CreateNewDamageData", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(DamageData), typeof(bool), typeof(Transform), typeof(float), typeof(bool), typeof(Collider2D) }, null);
            if (areaDamageFactoryLegacy != null)
            {
                harmony.Patch(areaDamageFactoryLegacy, postfix: new HarmonyMethod(typeof(DamageLoggingPatches).GetMethod(nameof(MarkAreaDamageFromTorpedoPostfixLegacy), BindingFlags.Static | BindingFlags.NonPublic)!));
            }

            Console.WriteLine("[VGParserMod] DamageLoggingPatches: registered AbstractUnit.TakeDamage(DamageData), torpedo lifecycle, and torpedo area-damage patches.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VGParserMod] DamageLoggingPatches registration failed: {ex}");
        }
    }

    private static void MarkTorpedoProcessHitPrefix(Torpedo __instance)
    {
        MarkTorpedoDamage(__instance);
    }

    private static void MarkTorpedoExplosionPrefix(Torpedo __instance, DamageData newDamageData)
    {
        MarkTorpedoDamage(newDamageData);
        MarkTorpedoExplosionDamage(newDamageData);
    }

    private static void MarkTorpedoDamage(Torpedo torpedo)
    {
        if (torpedo == null)
        {
            return;
        }

        var damageDataField = typeof(Torpedo).GetField("damageData", BindingFlags.Instance | BindingFlags.NonPublic);
        var damageData = damageDataField?.GetValue(torpedo) as DamageData;
        if (damageData != null)
        {
            TorpedoDamageMarkers.GetValue(damageData, _ => new object());
        }
    }

    private static void MarkTorpedoDamage(DamageData damageData)
    {
        if (damageData != null)
        {
            TorpedoDamageMarkers.GetValue(damageData, _ => new object());
        }
    }

    private static void MarkTorpedoExplosionDamage(DamageData damageData)
    {
        if (damageData != null)
        {
            TorpedoExplosionDamageMarkers.GetValue(damageData, _ => new object());
        }
    }

    private static void MarkAreaDamageFromTorpedoPostfix(ref DamageData? __result, DamageData damageData, Transform transform, float damageRadius, Collider2D? collider = null, bool dropOff = true)
    {
        if (__result != null && IsTorpedoDamage(damageData))
        {
            MarkTorpedoDamage(__result);
            MarkTorpedoExplosionDamage(__result);
        }
    }

    private static void MarkAreaDamageFromTorpedoPostfixLegacy(ref DamageData? __result, DamageData damageData, bool canMineCore, Transform transform, float damageRadius, bool surface = true, Collider2D? collider = null)
    {
        if (__result != null && IsTorpedoDamage(damageData))
        {
            MarkTorpedoDamage(__result);
            MarkTorpedoExplosionDamage(__result);
        }
    }

    private static void CleanupTorpedoMarkers(DamageData data)
    {
        if (data == null)
        {
            return;
        }

        var isExplosion = TorpedoExplosionDamageMarkers.TryGetValue(data, out _);
        var hasTorpedoSource = data.sourceUnit is Torpedo || (data.source != null && data.source.GetComponentInParent<Torpedo>() != null);

        // Keep the direct-hit marker alive until the torpedo explosion has been processed; otherwise the first
        // torpedo can be dropped and only later torpedoes ever show up in the summary.
        if (hasTorpedoSource && !isExplosion)
        {
            return;
        }

        if (TorpedoDamageMarkers.TryGetValue(data, out _))
        {
            TorpedoDamageMarkers.Remove(data);
        }

        if (isExplosion)
        {
            TorpedoExplosionDamageMarkers.Remove(data);
        }
    }

    private static void LogDamageAmountPostfix(DamageData data, Transform parent)
    {
        if (data == null || data.damageAmount <= 0d)
        {
            return;
        }

        ProcessDamageLog(data, data.targetUnit as AbstractUnit);
    }

    private static void LogDamageTakenPostfix(AbstractUnit __instance, DamageData damageData)
    {
        if (damageData == null || damageData.damageAmount <= 0d)
        {
            return;
        }

        ProcessDamageLog(damageData, __instance);
    }

    private static bool IsTorpedoDamage(DamageData data)
    {
        if (data == null)
        {
            return false;
        }

        if (TorpedoDamageMarkers.TryGetValue(data, out _))
        {
            return true;
        }

        if (data.sourceUnit is Torpedo)
        {
            return true;
        }

        if (data.source != null && data.source.GetComponentInParent<Torpedo>() != null)
        {
            return true;
        }

        if (data.sourceTurret != null && data.sourceTurret.GetComponentInParent<Torpedo>() != null)
        {
            return true;
        }

        return false;
    }

    private static void ProcessDamageLog(DamageData data, AbstractUnit? targetUnitOverride)
    {
        try
        {
            var now = DateTime.UtcNow;
            var damageAmount = data.damageAmount;
            var damageType = data.type;
            var sourceTurret = data.sourceTurret;
            var sourceUnit = data.sourceUnit;
            var targetUnit = targetUnitOverride ?? data.targetUnit as AbstractUnit;
            var critCount = data.critCount;
            var isReflectedDamage = data.reflectedDamage;
            var isDamageOverTime = data.isDamageOverTime;
            var isFighter = sourceUnit?.isCarrierFighter ?? false;
            var isTorpedoDamage = IsTorpedoDamage(data);
            var fromPlayer = sourceUnit != null && sourceUnit.IsPlayer();
            var toPlayer = targetUnit != null && targetUnit.IsPlayer();
            if (fromPlayer)
            {
                LastPlayerDamageDoneUtc = now;
                var turretId = sourceTurret?.GetInstanceID();
                var sourceId = data.source?.GetInstanceID();
                var categoryState = default(DamageCategory);
                if (isTorpedoDamage)
                {
                    const string torpedoCategoryKey = "Torpedo";
                    if (!DamageByCategory.TryGetValue(torpedoCategoryKey, out var torpedoState))
                    {
                        torpedoState = new DamageCategory(torpedoCategoryKey, damageType);
                        DamageByCategory[torpedoCategoryKey] = torpedoState;
                    }

                    torpedoState.BaseDamageType = damageType;
                    if (TorpedoExplosionDamageMarkers.TryGetValue(data, out _))
                    {
                        torpedoState.AddExtraDamage(damageType, damageAmount);
                        WriteLog($"[{now:yyyy-MM-ddTHH:mm:ss.fffZ}] {sourceUnit?.displayName ?? "null"} Torpedo explosion dealt {damageAmount} {damageType} damage to {targetUnit?.targetName ?? "null"} critCount: {critCount} (extra)");
                    }
                    else
                    {
                        torpedoState.BaseTotal += damageAmount;
                        WriteLog($"[{now:yyyy-MM-ddTHH:mm:ss.fffZ}] {sourceUnit?.displayName ?? "null"} Torpedo direct hit dealt {damageAmount} {damageType} damage to {targetUnit?.targetName ?? "null"} critCount: {critCount} (base)");
                    }
                    categoryState = torpedoState;
                }
                else if (isReflectedDamage)
                {
                    var reflectedDamageLabel = $"Reflected {damageType} damage";
                    WriteLog($"[{now:yyyy-MM-ddTHH:mm:ss.fffZ}] {sourceUnit?.displayName ?? "null"} dealt {damageAmount} {damageType} reflect damage to {targetUnit?.targetName ?? "null"}");
                    if (!DamageByCategory.TryGetValue(reflectedDamageLabel, out var reflectedState))
                    {
                        reflectedState = new DamageCategory(reflectedDamageLabel, damageType);
                        DamageByCategory[reflectedDamageLabel] = reflectedState;
                    }
                    reflectedState.BaseTotal += damageAmount;
                    categoryState = reflectedState;
                }
                else if (isDamageOverTime)
                {
                    var damageOverTimeLabel = $"{damageType} damage over time";
                    WriteLog($"[{now:yyyy-MM-ddTHH:mm:ss.fffZ}] {sourceUnit?.displayName ?? "null"} dealt {damageAmount} {damageType} DoT tick to {targetUnit?.targetName ?? "null"}");

                    if (!DamageByCategory.TryGetValue(damageOverTimeLabel, out var dotState))
                    {
                        dotState = new DamageCategory(damageOverTimeLabel, damageType);
                        DamageByCategory[damageOverTimeLabel] = dotState;
                    }
                    dotState.BaseTotal += damageAmount;
                    categoryState = dotState;
                }
                else if (turretId.HasValue)
                {
                    var label = sourceTurret?.item?.displayName ?? sourceTurret?.name ?? "unknown turret";
                    if (isFighter)
                    {
                        label = $"{sourceUnit?.displayName ?? "null"} - {label}";
                    }
                    if (isTorpedoDamage)
                    {
                        label = $"{label} [Torpedo]";
                    }
                    var id = sourceTurret?.item?.GetInstanceID() ?? data.sourceTurret?.GetInstanceID();
                    var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                    WriteLog($"[{timestamp}] {sourceUnit?.displayName ?? "null"} {label} [{id}] dealt {damageAmount} {damageType} damage to {targetUnit?.targetName ?? "null"} critCount: {critCount}");

                    if (!DamageByCategory.TryGetValue(id.ToString(), out var turretState))
                    {
                        turretState = new DamageCategory(label, damageType);
                        DamageByCategory[id.ToString()] = turretState;
                    }
                    turretState.BaseDamageType = damageType;
                    turretState.BaseTotal += damageAmount;
                    categoryState = turretState;
                }
                else if (sourceId.HasValue)
                {
                    var sourceTurretFromGameObject = data.source != null
                        ? data.source.GetComponentInParent<AbstractTurret>()
                        : null;
                    var sourceUnitFromGameObject = data.source != null
                        ? data.source.GetComponentInParent<AbstractUnit>()
                        : null;
                    var resolvedTurret = data.sourceTurret ?? sourceTurretFromGameObject;
                    var name = resolvedTurret != null
                        ? (resolvedTurret.item?.displayName ?? resolvedTurret.name)
                        : sourceUnitFromGameObject != null
                            ? sourceUnitFromGameObject.displayName
                            : data.source?.name ?? "unknown source";
                    var id = resolvedTurret != null
                        ? resolvedTurret.item?.GetInstanceID()
                        : data.source?.GetInstanceID();
                    var label = $"{name}";
                    if (isFighter)
                    {
                        label = $"{sourceUnit?.displayName ?? "null"} - {label}";
                    }
                    if (isTorpedoDamage)
                    {
                        label = $"{label} [Torpedo]";
                    }
                    WriteLog($"[{now:yyyy-MM-ddTHH:mm:ss.fffZ}] {sourceUnit?.displayName ?? "null"} {name} [{id}]procced {damageAmount} {damageType} damage to {targetUnit?.targetName ?? "null"} critCount: {critCount}");
                    if (!DamageByCategory.TryGetValue(id.ToString(), out var procState))
                    {
                        procState = new DamageCategory(label, damageType);
                        DamageByCategory[id.ToString()] = procState;
                    }
                    procState.AddExtraDamage(damageType, damageAmount);
                    categoryState = procState;
                }
                else
                {
                    WriteLog($"[{now:yyyy-MM-ddTHH:mm:ss.fffZ}] {sourceUnit?.displayName ?? "null"} UNKNOWN dealt {damageAmount} {damageType} damage to {targetUnit?.targetName ?? "null"} critCount: {critCount}");
                    if (!DamageByCategory.TryGetValue("Unknown source", out var fallbackState))
                    {
                        fallbackState = new DamageCategory("Unknown source", damageType);
                        DamageByCategory["Unknown source"] = fallbackState;
                    }

                    categoryState = fallbackState;
                }

                PlayerOutgoingTotalDamage += damageAmount;
                var playerElapsedSeconds = Math.Max((now - PlayerOutgoingDamageFirstSeenUtc).TotalSeconds, 0.001d);
                PlayerDamagePerSecondValue = (float)(PlayerOutgoingTotalDamage / playerElapsedSeconds);

                categoryState.hitCount++;
                categoryState.maxHit = Math.Max(categoryState.maxHit, damageAmount);
                categoryState.minHit = (categoryState.minHit == 0d) ? damageAmount : Math.Min(categoryState.minHit, damageAmount);
                categoryState.dps = categoryState.GetTotalDamage() / playerElapsedSeconds;
                CleanupTorpedoMarkers(data);
            }
            else if (toPlayer)
            {
                LastPlayerDamageTakenUtc = now;
                PlayerIncomingTotalDamage += damageAmount;
                if (!isDamageOverTime)
                {
                    PlayerDamagePerSecondValue = UpdatePlayerDps();
                }

                // var timestamp = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                // WriteLog($"[{timestamp}] {sourceUnit?.displayName ?? "null"} {sourceTurret?.name ?? "null"} dealt {damageAmount} damage of type {damageType} to player (critCount: {critCount}, player total: {PlayerIncomingTotalDamage}, player dps: {PlayerDamagePerSecondValue:F2}/s)");
                CleanupTorpedoMarkers(data);
            } else {
                // We don't care about damage from NPCs to other NPCs for now, so we won't log it.
                CleanupTorpedoMarkers(data);
            }
        }
        catch (Exception ex)
        {
            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            WriteLog($"[{timestamp}] DamageLoggingPatches: failed to log DamageData.damageAmount: {ex}\nStack trace:\n{ex.StackTrace}");
        }
    }
}