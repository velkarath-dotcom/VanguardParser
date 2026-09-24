using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Behaviour.Unit;
using Behaviour.Weapons;
using HarmonyLib;
using UnityEngine;

namespace VGParserMod.Patches;

public static class TorpedoPatches
{
    internal static readonly ConditionalWeakTable<DamageData, object> TorpedoDamageMarkers = new();
    internal static readonly ConditionalWeakTable<DamageData, object> TorpedoExplosionDamageMarkers = new();

    public static void Register(Harmony harmony)
    {
        try
        {
            var processHit = typeof(Torpedo).GetMethod("ProcessHit", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(Collider2D) }, null);
            if (processHit != null)
            {
                harmony.Patch(processHit, prefix: new HarmonyMethod(typeof(TorpedoPatches).GetMethod(nameof(MarkTorpedoProcessHitPrefix), BindingFlags.Static | BindingFlags.NonPublic)!));
            }

            var explode = typeof(Torpedo).GetMethod("Explode", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(DamageData) }, null);
            if (explode != null)
            {
                harmony.Patch(explode, prefix: new HarmonyMethod(typeof(TorpedoPatches).GetMethod(nameof(MarkTorpedoExplosionPrefix), BindingFlags.Static | BindingFlags.NonPublic)!));
            }

            var areaDamageFactory = typeof(Source.Util.AreaDamageHelper).GetMethod("CreateNewDamageData", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(DamageData), typeof(Transform), typeof(float), typeof(Collider2D), typeof(bool) }, null);
            if (areaDamageFactory != null)
            {
                harmony.Patch(areaDamageFactory, postfix: new HarmonyMethod(typeof(TorpedoPatches).GetMethod(nameof(MarkAreaDamageFromTorpedoPostfix), BindingFlags.Static | BindingFlags.NonPublic)!));
            }

            var areaDamageFactoryLegacy = typeof(Source.Util.AreaDamageHelper).GetMethod("CreateNewDamageData", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(DamageData), typeof(bool), typeof(Transform), typeof(float), typeof(bool), typeof(Collider2D) }, null);
            if (areaDamageFactoryLegacy != null)
            {
                harmony.Patch(areaDamageFactoryLegacy, postfix: new HarmonyMethod(typeof(TorpedoPatches).GetMethod(nameof(MarkAreaDamageFromTorpedoPostfixLegacy), BindingFlags.Static | BindingFlags.NonPublic)!));
            }

            Console.WriteLine("[VGParserMod] TorpedoPatches: registered torpedo lifecycle and torpedo area-damage patches.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VGParserMod] TorpedoPatches registration failed: {ex}");
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

    public static void CleanupTorpedoMarkers(DamageData data)
    {
        if (data == null)
        {
            return;
        }

        var isExplosion = TorpedoExplosionDamageMarkers.TryGetValue(data, out _);
        var hasTorpedoSource = data.sourceUnit is Torpedo || HasParentComponent<Torpedo>(data.source);

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

    public static bool IsTorpedoDamage(DamageData data)
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

        if (HasParentComponent<Torpedo>(data.source))
        {
            return true;
        }

        if (data.sourceTurret != null && HasParentComponent<Torpedo>(data.sourceTurret))
        {
            return true;
        }

        return false;
    }
}
