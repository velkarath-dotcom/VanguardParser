using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Behaviour.Equipment.Turret;
using Behaviour.Weapons;
using Behaviour.Unit;
using HarmonyLib;
using Source.Combat;
using UnityEngine;
using VGParserMod;

namespace VGParserMod.Patches;

public static class DamageLoggingPatches
{
    private const string UiInfoTextParentTypeName = "Behaviour.UI.UIInfoTextParent, Assembly-CSharp";
    private static readonly Dictionary<string, DamageCategory> DamageByCategory = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, ShipDamageCategory> PlayerShipCategories = new();
    private static readonly Dictionary<int, ShipDamageCategory> FleetShipCategories = new();
    private static ShipDamageCategory? CurrentPlayerShipCategory;
    private static readonly ConditionalWeakTable<DamageData, OriginalDamageSnapshot> OriginalDamageAmounts = new();

    private sealed class OriginalDamageSnapshot
    {
        public float Amount { get; }

        public OriginalDamageSnapshot(float amount)
        {
            Amount = amount;
        }
    }

    private static readonly TimeSpan PlayerDamageResetThreshold = TimeSpan.FromSeconds(20);
    private static double PlayerOutgoingTotalDamage = 0d;
    private static DateTime? PlayerOutgoingDamageFirstSeenUtc = null;
    private static DateTime? LastPlayerDamageDoneUtc = null;
    private static double PlayerIncomingTotalDamage = 0d;
    private static DateTime? PlayerIncomingDamageFirstSeenUtc = null;
    private static DateTime? LastPlayerDamageTakenUtc = null;
    private static float PlayerDamagePerSecondValue = 0f;

    public static Dictionary<string, DamageCategory> GetDamageByCategory()
    {
        if (HasFleetShips())
        {
            var groupedByShip = new Dictionary<string, DamageCategory>(StringComparer.OrdinalIgnoreCase);
            foreach (var shipCategory in GetFleetShipCategories())
            {
                var shipTotal = new DamageCategory(shipCategory.ShipName, shipCategory.DamageCategories.FirstOrDefault()?.BaseDamageType ?? DamageType.Kinetic);
                foreach (var category in shipCategory.DamageCategories)
                {
                    shipTotal.BaseTotal += category.GetTotalDamage();
                    shipTotal.ResistedDamage += category.GetTotalResistedDamage();
                    shipTotal.hitCount += category.hitCount;
                    shipTotal.CritCount += category.CritCount;
                    shipTotal.maxHit = Math.Max(shipTotal.maxHit, category.maxHit);
                    shipTotal.minHit = shipTotal.minHit == 0d ? category.minHit : Math.Min(shipTotal.minHit, category.minHit);
                }

                shipTotal.dps = shipCategory.Dps;
                groupedByShip[shipCategory.Id.ToString()] = shipTotal;
            }

            return groupedByShip;
        }

        return DamageByCategory;
    }
    public static float GetPlayerDps()
    {
        return PlayerDamagePerSecondValue;
    }

    public static ShipDamageCategory? GetCurrentPlayerShipCategory()
    {
        return CurrentPlayerShipCategory;
    }

    public static ShipDamageCategory[] GetFleetShipCategories()
    {
        var categories = new List<ShipDamageCategory>();
        var flagship = GameplayManager.Instance?.spaceShip;
        if (flagship != null && PlayerShipCategories.TryGetValue(flagship.GetInstanceID(), out var flagshipCategory))
        {
            categories.Add(flagshipCategory);
        }

        categories.AddRange(FleetShipCategories.Values);
        return categories.ToArray();
    }

    public static bool HasFleetShips()
    {
        return GameplayManager.Instance != null && GameplayManager.Instance.fleetSpaceShips.Count > 0;
    }

    public static double GetPlayerOutgoingTotalDamage()
    {
        return PlayerOutgoingTotalDamage;
    }

    private static ShipDamageCategory? GetOrCreatePlayerShipCategory(AbstractUnit? sourceUnit)
    {
        var shipId = sourceUnit != null ? sourceUnit.GetInstanceID() : 0;
        var shipName = sourceUnit?.displayName ?? "Player ship";

        if (!PlayerShipCategories.TryGetValue(shipId, out var shipCategory))
        {
            shipCategory = new ShipDamageCategory(shipName, shipId);
            PlayerShipCategories[shipId] = shipCategory;
        }
        else if (!string.IsNullOrWhiteSpace(shipName) && !string.Equals(shipCategory.ShipName, shipName, StringComparison.OrdinalIgnoreCase))
        {
            shipCategory.ShipName = shipName;
        }

        CurrentPlayerShipCategory = shipCategory;
        return shipCategory;
    }

    private static ShipDamageCategory? GetOrCreateFleetShipCategory(AbstractUnit? sourceUnit)
    {
        if (sourceUnit == null)
        {
            return null;
        }

        var ship = sourceUnit as SpaceShip;
        if (ship == null)
        {
            return null;
        }

        if (GameplayManager.Instance == null || !GameplayManager.Instance.IsFleetShip(ship))
        {
            return null;
        }

        var shipId = ship.GetInstanceID();
        if (!FleetShipCategories.TryGetValue(shipId, out var shipCategory))
        {
            shipCategory = new ShipDamageCategory(sourceUnit.displayName, shipId);
            FleetShipCategories[shipId] = shipCategory;
        }
        else if (!string.IsNullOrWhiteSpace(ship.displayName) && !string.Equals(shipCategory.ShipName, ship.displayName, StringComparison.OrdinalIgnoreCase))
        {
            shipCategory.ShipName = ship.displayName;
        }

        return shipCategory;
    }

    private static bool IsFleetShipSource(AbstractUnit? sourceUnit)
    {
        return sourceUnit is SpaceShip ship
            && GameplayManager.Instance != null
            && GameplayManager.Instance.IsFleetShip(ship);
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
        PlayerShipCategories.Clear();
        FleetShipCategories.Clear();
        CurrentPlayerShipCategory = null;
        PlayerOutgoingTotalDamage = 0d;
        PlayerOutgoingDamageFirstSeenUtc = null;
        PlayerDamagePerSecondValue = 0f;
    }

    public static void Register(Harmony harmony)
    {
        try
        {
            var takeDamage = typeof(AbstractUnit).GetMethod(nameof(AbstractUnit.TakeDamage), BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(DamageData) }, null);
            if (takeDamage != null)
            {
                harmony.Patch(takeDamage, prefix: new HarmonyMethod(typeof(DamageLoggingPatches).GetMethod(nameof(CaptureOriginalDamagePrefix), BindingFlags.Static | BindingFlags.NonPublic)!));
            }

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

    private static void CaptureOriginalDamagePrefix(DamageData damageData)
    {
        try
        {
            if (damageData == null)
            {
                return;
            }

            OriginalDamageAmounts.Remove(damageData);
            OriginalDamageAmounts.Add(damageData, new OriginalDamageSnapshot(damageData.totalDamageAmount));
        }
        catch (Exception ex)
        {
            LogPatchFailure(nameof(CaptureOriginalDamagePrefix), ex);
        }
    }

    private static void LogPatchFailure(string patchName, Exception exception)
    {
        try
        {
            var message = $"[VGParserMod] {patchName} failed; allowing the game method to continue: {exception}";
            Console.WriteLine(message);
            WriteLog(message);
        }
        catch
        {
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
        try
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

            var originalDamageAmount = OriginalDamageAmounts.TryGetValue(damageData, out var snapshot)
                ? snapshot.Amount
                : damageData.totalDamageAmount;
            ProcessDamageLog(damageData, damageData.targetUnit as AbstractUnit, originalDamageAmount);
        }
        catch (Exception ex)
        {
            LogPatchFailure(nameof(LogDamageNumberPrefix), ex);
        }
    }

    private static bool IsTorpedoDamage(DamageData data)
    {
        return TorpedoPatches.IsTorpedoDamage(data);
    }

    private static bool IsCannonExplosionDamage(DamageData data)
    {
        return CannonPatches.IsCannonExplosionDamage(data);
    }

    private static string GetTurretDisplayName(AbstractTurret? turret)
    {
        if (turret == null)
        {
            return "unknown turret";
        }

        try
        {
            var itemProperty = turret.GetType().GetProperty("item", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var item = itemProperty?.GetValue(turret);
            var displayNameProperty = item?.GetType().GetProperty("displayName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (displayNameProperty?.GetValue(item) is string displayName && !string.IsNullOrWhiteSpace(displayName))
            {
                return displayName;
            }
        }
        catch
        {
        }

        try
        {
            var equipmentName = turret.GetName();
            if (!string.IsNullOrWhiteSpace(equipmentName))
            {
                return equipmentName;
            }
        }
        catch
        {
        }

        return string.IsNullOrWhiteSpace(turret.name) ? "unknown turret" : turret.name;
    }

    private static void RecordDroneDamageForShip(
        ShipDamageCategory shipCategory,
        DamageType damageType,
        double damage,
        double resistedDamage,
        int critCount,
        double elapsedSeconds)
    {
        var name = $"{damageType} drone";
        var droneCategory = shipCategory.DamageCategories.FirstOrDefault(category =>
            string.Equals(category.Name, name, StringComparison.OrdinalIgnoreCase));
        if (droneCategory == null)
        {
            droneCategory = new DamageCategory(name, damageType);
            shipCategory.DamageCategories.Add(droneCategory);
        }

        droneCategory.BaseTotal += damage;
        droneCategory.ResistedDamage += resistedDamage;
        droneCategory.hitCount++;
        droneCategory.CritCount += critCount;
        droneCategory.maxHit = Math.Max(droneCategory.maxHit, damage);
        droneCategory.minHit = droneCategory.minHit == 0d ? damage : Math.Min(droneCategory.minHit, damage);
        droneCategory.dps = droneCategory.GetTotalDamage() / Math.Max(elapsedSeconds, 0.001d);
    }

    private static void ProcessDamageLog(DamageData data, AbstractUnit? targetUnitOverride, float originalDamageAmountOverride)
    {
        try
        {
            var now = DateTime.UtcNow;

            var originalDamageAmount = originalDamageAmountOverride;
            var damageAmount = data.damageAmount;
            var resistedDamage = Math.Max(0d, originalDamageAmount - damageAmount);
            var resistedPercent = originalDamageAmount > 0d
                ? resistedDamage / originalDamageAmount
                : 0d;
            var damageType = data.type;
            var sourceTurret = data.sourceTurret;
            var sourceUnit = data.sourceUnit;
            var droneSourceUnit = sourceUnit as Drone
                ?? data.source?.GetComponentInParent<Drone>()
                ?? sourceTurret?.GetComponentInParent<Drone>();
            var shipSourceUnit = droneSourceUnit?.droneCommander ?? sourceUnit;
            var targetUnit = targetUnitOverride ?? data.targetUnit as AbstractUnit;
            var critCount = data.critCount;
            var isReflectedDamage = data.reflectedDamage;
            var isDamageOverTime = data.isDamageOverTime;
            var isFighter = sourceUnit?.isCarrierFighter ?? false;
            var isDrone = sourceUnit is Drone || HasParentComponent<Drone>(data.source) || HasParentComponent<Drone>(sourceTurret);
            var isTorpedoDamage = IsTorpedoDamage(data);
            var isCannonExplosionDamage = IsCannonExplosionDamage(data);
            var fromPlayer = (sourceUnit != null && sourceUnit.IsPlayer())
                || (shipSourceUnit != null && shipSourceUnit.IsPlayer())
                || IsFleetShipSource(shipSourceUnit);
            var toPlayer = targetUnit != null && targetUnit.IsPlayer();
            if (fromPlayer)
            {
                if (PlayerOutgoingDamageFirstSeenUtc == null)
                {
                    PlayerOutgoingDamageFirstSeenUtc = now;
                }                
                LastPlayerDamageDoneUtc = now;
                var firstOutgoingDamageUtc = PlayerOutgoingDamageFirstSeenUtc ?? now;
                var playerElapsedSeconds = Math.Max((now - firstOutgoingDamageUtc).TotalSeconds, 0.001d);
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
                        torpedoState.AddExtraDamage(damageType, damageAmount, resistedDamage, critCount, playerElapsedSeconds);
                        WriteLog($"[{now:yyyy-MM-ddTHH:mm:ss.fffZ}] {sourceUnit?.displayName ?? "null"} Torpedo explosion dealt {damageAmount} [{resistedPercent:P} resisted] {damageType} damage to {targetUnit?.targetName ?? "null"} critCount: {critCount} (extra)");
                    }
                    else
                    {
                        torpedoState.BaseTotal += damageAmount;
                        torpedoState.ResistedDamage += resistedDamage;
                        WriteLog($"[{now:yyyy-MM-ddTHH:mm:ss.fffZ}] {sourceUnit?.displayName ?? "null"} Torpedo direct hit dealt {damageAmount} [{resistedPercent:P} resisted] {damageType} damage to {targetUnit?.targetName ?? "null"} critCount: {critCount} (base)");
                    }
                    categoryState = torpedoState;
                }
                else if (isCannonExplosionDamage)
                {
                    var cannonExplosionSourceTurret = data.sourceTurret ?? CannonPatches.TryGetAssociatedTurret(data);
                    var cannonTurretId = cannonExplosionSourceTurret?.GetInstanceID();
                    var label = GetTurretDisplayName(cannonExplosionSourceTurret);
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

                        turretState.AddExtraDamage(damageType, damageAmount, resistedDamage, critCount, playerElapsedSeconds);
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
                        fallbackState.AddExtraDamage(damageType, damageAmount, resistedDamage, critCount, playerElapsedSeconds);
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
                    reflectedState.ResistedDamage += resistedDamage;
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
                    dotState.ResistedDamage += resistedDamage;
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
                    droneState.ResistedDamage += resistedDamage;
                    categoryState = droneState;
                }
                else if (playerTurretId.HasValue)
                {
                    var label = GetTurretDisplayName(sourceTurret);
                    if (isFighter)
                    {
                        label = $"{sourceUnit?.displayName ?? "null"} - {label}";
                    }
                    if (isTorpedoDamage)
                    {
                        label = $"{label} [Torpedo]";
                    }
                    var id = sourceTurret?.GetInstanceID() ?? data.sourceTurret?.GetInstanceID();
                    var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                    WriteLog($"[{timestamp}] {sourceUnit?.displayName ?? "null"} {label} [{id}] dealt {damageAmount}/{originalDamageAmount} [{resistedPercent:P} resisted] {damageType} damage to {targetUnit?.targetName ?? "null"} critCount: {critCount}");

                    if (!DamageByCategory.TryGetValue(id.ToString(), out var turretState))
                    {
                        turretState = new DamageCategory(label, damageType);
                        DamageByCategory[id.ToString()] = turretState;
                    }
                    turretState.BaseDamageType = damageType;
                    turretState.BaseTotal += damageAmount;
                    turretState.ResistedDamage += resistedDamage;
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
                        ? GetTurretDisplayName(resolvedTurret)
                        : sourceUnitFromGameObject != null
                            ? sourceUnitFromGameObject.displayName
                            : data.source?.name ?? "unknown source";
                    var id = resolvedTurret != null
                        ? resolvedTurret.GetInstanceID()
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
                    procState.AddExtraDamage(damageType, damageAmount, resistedDamage, critCount, playerElapsedSeconds);
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

                    fallbackState.BaseTotal += damageAmount;
                    fallbackState.ResistedDamage += resistedDamage;
                    categoryState = fallbackState;
                }

                PlayerOutgoingTotalDamage += damageAmount;

                PlayerDamagePerSecondValue = (float)(PlayerOutgoingTotalDamage / playerElapsedSeconds);

                categoryState.hitCount++;
                categoryState.CritCount += critCount;
                categoryState.maxHit = Math.Max(categoryState.maxHit, damageAmount);
                categoryState.minHit = (categoryState.minHit == 0d) ? damageAmount : Math.Min(categoryState.minHit, damageAmount);
                categoryState.dps = categoryState.GetTotalDamage() / playerElapsedSeconds;

                var fleetShipCategory = GetOrCreateFleetShipCategory(shipSourceUnit);
                var shipCategory = fleetShipCategory ?? GetOrCreatePlayerShipCategory(shipSourceUnit);
                if (shipCategory != null)
                {
                    shipCategory.RecordDamage(damageAmount, now);
                    if (isDrone)
                    {
                        RecordDroneDamageForShip(shipCategory, damageType, damageAmount, resistedDamage, critCount, playerElapsedSeconds);
                    }
                    else if (!shipCategory.DamageCategories.Any(existingCategory => ReferenceEquals(existingCategory, categoryState)))
                    {
                        shipCategory.DamageCategories.Add(categoryState);
                    }
                }

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