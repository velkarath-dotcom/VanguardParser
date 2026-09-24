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
    private static readonly TimeSpan PlayerDamageResetThreshold = TimeSpan.FromSeconds(20);
    private static double PlayerOutgoingTotalDamage = 0d;
    private static DateTime? PlayerOutgoingDamageFirstSeenUtc = null;
    private static DateTime? LastPlayerDamageDoneUtc = null;
    private static double PlayerIncomingTotalDamage = 0d;
    private static DateTime? PlayerIncomingDamageFirstSeenUtc = null;
    private static DateTime? LastPlayerDamageTakenUtc = null;
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
        if (LastPlayerDamageTakenUtc == null || (now - LastPlayerDamageTakenUtc.Value) > PlayerDamageResetThreshold)
        {
            PlayerIncomingTotalDamage = 0d;
            PlayerIncomingDamageFirstSeenUtc = now;
        }

        var firstOutgoingDamageUtc = PlayerOutgoingDamageFirstSeenUtc ?? now;
        var elapsedSeconds = Math.Max((now - firstOutgoingDamageUtc).TotalSeconds, 0.001d);
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
        PlayerOutgoingDamageFirstSeenUtc = null;
        PlayerDamagePerSecondValue = 0f;
    }

    public static void Register(Harmony harmony)
    {
        try
        {
            var uiInfoTextParentType = Type.GetType(UiInfoTextParentTypeName, throwOnError: false);
            if (uiInfoTextParentType == null)
            {
                Console.WriteLine($"[VGParserMod] DamageLoggingPatches: {UiInfoTextParentTypeName} could not be located.");
                return;
            }

            MethodInfo? showDamageNumber = null;
            foreach (var candidate in uiInfoTextParentType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (candidate.Name == "ShowDamageNumber")
                {
                    showDamageNumber = candidate;
                    break;
                }
            }

            if (showDamageNumber == null)
            {
                Console.WriteLine("[VGParserMod] DamageLoggingPatches: UIInfoTextParent.ShowDamageNumber could not be located.");
                return;
            }

            harmony.Patch(showDamageNumber, prefix: new HarmonyMethod(typeof(DamageLoggingPatches).GetMethod(nameof(LogDamageNumberPrefix), BindingFlags.Static | BindingFlags.NonPublic)!));

            Console.WriteLine("[VGParserMod] DamageLoggingPatches: registered UIInfoTextParent.ShowDamageNumber patch.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VGParserMod] DamageLoggingPatches registration failed: {ex}");
        }
    }

    private static bool HasParentComponent<T>(object? source) where T : Component
    {
        if (source == null)
        {
            return false;
        }

        try
        {
            if (source is Component component)
            {
                if (component.gameObject == null)
                {
                    return false;
                }

                return component.GetComponentInParent<T>() != null;
            }

            if (source is GameObject gameObject)
            {
                if (gameObject == null)
                {
                    return false;
                }

                return gameObject.GetComponentInParent<T>() != null;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static void LogDamageNumberPrefix(MethodBase __originalMethod, object[] __args)
    {
        if (__args == null || __args.Length == 0)
        {
            return;
        }

        DamageData? damageData = null;
        foreach (var arg in __args)
        {
            if (arg is DamageData candidate)
            {
                damageData = candidate;
                break;
            }
        }

        if (damageData == null || damageData.totalDamageAmount <= 0d)
        {
            return;
        }

        ProcessDamageLog(damageData, damageData.targetUnit as AbstractUnit);
    }

    private static bool IsTorpedoDamage(DamageData data)
    {
        return TorpedoPatches.IsTorpedoDamage(data);
    }

    private static bool IsCannonExplosionDamage(DamageData data)
    {
        return CannonPatches.IsCannonExplosionDamage(data);
    }

    private static void ProcessDamageLog(DamageData data, AbstractUnit? targetUnitOverride)
    {
        try
        {
            var now = DateTime.UtcNow;

            var originalDamageAmount = data.totalDamageAmount;
            var damageAmount = data.damageAmount;
            var resistedPercent = originalDamageAmount > 0d
                ? (damageAmount == originalDamageAmount ? 0d : 1d - (damageAmount / originalDamageAmount))
                : 0d;
            var damageType = data.type;
            var sourceTurret = data.sourceTurret;
            var sourceUnit = data.sourceUnit;
            var targetUnit = targetUnitOverride ?? data.targetUnit as AbstractUnit;
            var critCount = data.critCount;
            var isReflectedDamage = data.reflectedDamage;
            var isDamageOverTime = data.isDamageOverTime;
            var isFighter = sourceUnit?.isCarrierFighter ?? false;
            var isDrone = sourceUnit is Drone || HasParentComponent<Drone>(data.source) || HasParentComponent<Drone>(sourceTurret);
            var isTorpedoDamage = IsTorpedoDamage(data);
            var isCannonExplosionDamage = IsCannonExplosionDamage(data);
            var fromPlayer = sourceUnit != null && sourceUnit.IsPlayer();
            var toPlayer = targetUnit != null && targetUnit.IsPlayer();
            if (fromPlayer)
            {
                if (PlayerOutgoingDamageFirstSeenUtc == null)
                {
                    PlayerOutgoingDamageFirstSeenUtc = now;
                }                
                LastPlayerDamageDoneUtc = now;
                var playerTurretId = sourceTurret?.GetInstanceID();
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
                    if (TorpedoPatches.TorpedoExplosionDamageMarkers.TryGetValue(data, out _))
                    {
                        torpedoState.AddExtraDamage(damageType, damageAmount);
                        WriteLog($"[{now:yyyy-MM-ddTHH:mm:ss.fffZ}] {sourceUnit?.displayName ?? "null"} Torpedo explosion dealt {damageAmount} [{resistedPercent:P} resisted] {damageType} damage to {targetUnit?.targetName ?? "null"} critCount: {critCount} (extra)");
                    }
                    else
                    {
                        torpedoState.BaseTotal += damageAmount;
                        WriteLog($"[{now:yyyy-MM-ddTHH:mm:ss.fffZ}] {sourceUnit?.displayName ?? "null"} Torpedo direct hit dealt {damageAmount} [{resistedPercent:P} resisted] {damageType} damage to {targetUnit?.targetName ?? "null"} critCount: {critCount} (base)");
                    }
                    categoryState = torpedoState;
                }
                else if (isCannonExplosionDamage)
                {
                    var cannonExplosionSourceTurret = data.sourceTurret ?? CannonPatches.TryGetAssociatedTurret(data);
                    var cannonTurretId = cannonExplosionSourceTurret?.item?.GetInstanceID() ?? cannonExplosionSourceTurret?.GetInstanceID();
                    var label = cannonExplosionSourceTurret?.item?.displayName ?? cannonExplosionSourceTurret?.name ?? "unknown turret";
                    if (isFighter)
                    {
                        label = $"{sourceUnit?.displayName ?? "null"} - {label}";
                    }

                    if (cannonTurretId.HasValue)
                    {
                        if (!DamageByCategory.TryGetValue(cannonTurretId.Value.ToString(), out var turretState))
                        {
                            turretState = new DamageCategory(label, damageType);
                            DamageByCategory[cannonTurretId.Value.ToString()] = turretState;
                        }

                        turretState.AddExtraDamage(damageType, damageAmount);
                        categoryState = turretState;
                    }
                    else
                    {
                        var fallbackKey = sourceUnit != null ? sourceUnit.GetInstanceID().ToString() : "Cannon explosion";
                        if (!DamageByCategory.TryGetValue(fallbackKey, out var fallbackState))
                        {
                            fallbackState = new DamageCategory(label, damageType);
                            DamageByCategory[fallbackKey] = fallbackState;
                        }

                        fallbackState.BaseDamageType = damageType;
                        fallbackState.AddExtraDamage(damageType, damageAmount);
                        categoryState = fallbackState;
                    }

                    WriteLog($"[{now:yyyy-MM-ddTHH:mm:ss.fffZ}] {sourceUnit?.displayName ?? "null"} {label} explosion dealt {damageAmount} [{resistedPercent:P} resisted] {damageType} damage to {targetUnit?.targetName ?? "null"} critCount: {critCount} (extra)");
                }
                else if (isReflectedDamage)
                {
                    var reflectedDamageLabel = $"Reflected {damageType} damage";
                    WriteLog($"[{now:yyyy-MM-ddTHH:mm:ss.fffZ}] {sourceUnit?.displayName ?? "null"} dealt {damageAmount} [{resistedPercent:P} resisted] {damageType} reflect damage to {targetUnit?.targetName ?? "null"}");
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
                    // There is a source for this damage, but the source is the turret that applied the first stack of DoT
                    // so all dot damage will accumulate under that turret's ID. 
                    // To make it easier to see how much DoT damage was done, we accumulate all DoT damage under a separate "damage over time" category.
                    var damageOverTimeLabel = $"{damageType} damage over time";
                    WriteLog($"[{now:yyyy-MM-ddTHH:mm:ss.fffZ}] {sourceUnit?.displayName ?? "null"} dealt {damageAmount} [{resistedPercent:P} resisted] {damageType} DoT tick to {targetUnit?.targetName ?? "null"}");

                    if (!DamageByCategory.TryGetValue(damageOverTimeLabel, out var dotState))
                    {
                        dotState = new DamageCategory(damageOverTimeLabel, damageType);
                        DamageByCategory[damageOverTimeLabel] = dotState;
                    }
                    dotState.BaseTotal += damageAmount;
                    categoryState = dotState;
                }
                else if (isDrone)
                {
                    // Drones die all the time so the panel gets flooded with different laser turrets, group them all together
                    var droneLabel = $"{damageType} drone";
                    WriteLog($"[{now:yyyy-MM-ddTHH:mm:ss.fffZ}] {sourceUnit?.displayName ?? "null"} dealt {damageAmount} [{resistedPercent:P} resisted] {damageType} to {targetUnit?.targetName ?? "null"}");

                    if (!DamageByCategory.TryGetValue(droneLabel, out var droneState))
                    {
                        droneState = new DamageCategory(droneLabel, damageType);
                        DamageByCategory[droneLabel] = droneState;
                    }
                    droneState.BaseTotal += damageAmount;
                    categoryState = droneState;
                }
                else if (playerTurretId.HasValue)
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
                    WriteLog($"[{timestamp}] {sourceUnit?.displayName ?? "null"} {label} [{id}] dealt {damageAmount}/{originalDamageAmount} [{resistedPercent:P} resisted] {damageType} damage to {targetUnit?.targetName ?? "null"} critCount: {critCount}");

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
                    WriteLog($"[{now:yyyy-MM-ddTHH:mm:ss.fffZ}] {sourceUnit?.displayName ?? "null"} {name} [{id}]procced {damageAmount}/{originalDamageAmount} [{resistedPercent:P} resisted] {damageType} damage to {targetUnit?.targetName ?? "null"} critCount: {critCount}");
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
                    WriteLog($"[{now:yyyy-MM-ddTHH:mm:ss.fffZ}] {sourceUnit?.displayName ?? "null"} UNKNOWN dealt {damageAmount} [{resistedPercent:P} resisted] {damageType} damage to {targetUnit?.targetName ?? "null"} critCount: {critCount}");
                    if (!DamageByCategory.TryGetValue("Unknown source", out var fallbackState))
                    {
                        fallbackState = new DamageCategory("Unknown source", damageType);
                        DamageByCategory["Unknown source"] = fallbackState;
                    }

                    categoryState = fallbackState;
                }

                PlayerOutgoingTotalDamage += damageAmount;

                var firstOutgoingDamageUtc = PlayerOutgoingDamageFirstSeenUtc ?? now;
                var playerElapsedSeconds = Math.Max((now - firstOutgoingDamageUtc).TotalSeconds, 0.001d);
                PlayerDamagePerSecondValue = (float)(PlayerOutgoingTotalDamage / playerElapsedSeconds);

                categoryState.hitCount++;
                categoryState.CritCount += critCount;
                categoryState.maxHit = Math.Max(categoryState.maxHit, damageAmount);
                categoryState.minHit = (categoryState.minHit == 0d) ? damageAmount : Math.Min(categoryState.minHit, damageAmount);
                categoryState.dps = categoryState.GetTotalDamage() / playerElapsedSeconds;
                TorpedoPatches.CleanupTorpedoMarkers(data);
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
                TorpedoPatches.CleanupTorpedoMarkers(data);
            } else {
                // We don't care about damage from NPCs to other NPCs for now, so we won't log it.
                TorpedoPatches.CleanupTorpedoMarkers(data);
            }
        }
        catch (Exception ex)
        {
            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            WriteLog($"[{timestamp}] DamageLoggingPatches: failed to log DamageData.damageAmount: {ex}\nStack trace:\n{ex.StackTrace}");
        }
    }
}