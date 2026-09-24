using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Behaviour.Equipment.Turret;
using Behaviour.Equipment.Turret.Projectile;
using Behaviour.Weapons;
using HarmonyLib;
using UnityEngine;

namespace VGParserMod.Patches;

public static class CannonPatches
{
    internal static readonly ConditionalWeakTable<DamageData, object> CannonExplosionDamageMarkers = new();
    internal static readonly ConditionalWeakTable<DamageData, AbstractTurret> CannonExplosionSourceTurrets = new();

    public static void Register(Harmony harmony)
    {
        try
        {
            var explode = typeof(CannonProjectile).GetMethod("Explode", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(DamageData) }, null);
            if (explode != null)
            {
                harmony.Patch(explode, prefix: new HarmonyMethod(typeof(CannonPatches).GetMethod(nameof(MarkCannonExplosionPrefix), BindingFlags.Static | BindingFlags.NonPublic)!));
            }

            var areaDamageFactory = typeof(Source.Util.AreaDamageHelper).GetMethod("CreateNewDamageData", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(DamageData), typeof(Transform), typeof(float), typeof(Collider2D), typeof(bool) }, null);
            if (areaDamageFactory != null)
            {
                harmony.Patch(areaDamageFactory, postfix: new HarmonyMethod(typeof(CannonPatches).GetMethod(nameof(MarkAreaDamageFromCannonPostfix), BindingFlags.Static | BindingFlags.NonPublic)!));
            }

            Console.WriteLine("[VGParserMod] CannonPatches: registered cannon explosion attribution patch.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VGParserMod] CannonPatches registration failed: {ex}");
        }
    }

    private static void MarkCannonExplosionPrefix(CannonProjectile __instance, DamageData newDamageData)
    {
        if (__instance == null || newDamageData == null)
        {
            return;
        }

        try
        {
            var damageDataField = typeof(CannonProjectile).GetField("damageData", BindingFlags.Instance | BindingFlags.NonPublic);
            var parentDamageData = damageDataField?.GetValue(__instance) as DamageData;
            if (parentDamageData != null)
            {
                var parentTurret = parentDamageData.sourceTurret ?? (
                    parentDamageData.source != null ? parentDamageData.source.GetComponentInParent<AbstractTurret>() : null);
                if (parentTurret != null)
                {
                    CannonExplosionSourceTurrets.GetValue(newDamageData, _ => parentTurret);
                }
            }
        }
        catch
        {
            // Ignore reflection failures; the marker is the important part for attribution.
        }

        CannonExplosionDamageMarkers.GetValue(newDamageData, _ => new object());
    }

    private static void MarkAreaDamageFromCannonPostfix(ref DamageData? __result, DamageData damageData, Transform transform, float damageRadius, Collider2D? collider = null, bool dropOff = true)
    {
        if (__result == null || !CannonExplosionDamageMarkers.TryGetValue(damageData, out _))
        {
            return;
        }

        CannonExplosionDamageMarkers.GetValue(__result, _ => new object());

        var sourceTurret = TryGetAssociatedTurret(damageData);
        if (sourceTurret != null)
        {
            CannonExplosionSourceTurrets.GetValue(__result, _ => sourceTurret);
        }
    }

    public static AbstractTurret? TryGetAssociatedTurret(DamageData data)
    {
        if (data == null)
        {
            return null;
        }

        if (CannonExplosionSourceTurrets.TryGetValue(data, out var turret))
        {
            return turret;
        }

        return data.sourceTurret ?? (data.source != null ? data.source.GetComponentInParent<AbstractTurret>() : null);
    }

    public static bool IsCannonExplosionDamage(DamageData data)
    {
        if (data == null)
        {
            return false;
        }

        return CannonExplosionDamageMarkers.TryGetValue(data, out _);
    }
}
