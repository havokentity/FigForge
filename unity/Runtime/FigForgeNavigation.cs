// =============================================================================
// FigForge — public entry point for navigation guards. A stateless facade over the
// active UIFrameManager's per-instance guard registry, so nothing survives a domain
// reload. Register guards at runtime once a UIFrameManager exists — e.g. in a bootstrap
// MonoBehaviour's Start(), or via a FigForgeNavGuard component (collected at Start).
//
// The generated `UIFrames.Guard<T>` / `UIFrames.GuardAll` helpers forward here; this class
// also works without regenerating the UIFrames accessors.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace FigForge
{
    public static class FigForgeNavigation
    {
        /// <summary>Guard navigation INTO the screen of type TFrame (a generated frame class).</summary>
        public static void AddGuard<TFrame>(NavGuard guard) where TFrame : FigForgeFrame
        {
            var key = KeyOf(typeof(TFrame));
            if (string.IsNullOrEmpty(key)) return; // KeyOf already warned
            var m = UIFrameManager.Resolve();
            if (m == null) { WarnNoManager(); return; }
            m.AddGuard(key, guard);
        }

        /// <summary>Guard a specific screen instance (e.g. UIFrames.Checkout).</summary>
        public static void AddGuard(FigForgeFrame frame, NavGuard guard)
        {
            if (frame == null) return;
            var m = UIFrameManager.Resolve();
            if (m == null) { WarnNoManager(); return; }
            m.AddGuard(frame, guard);
        }

        /// <summary>Guard a screen by its key (the sanitized Figma name).</summary>
        public static void AddGuard(string screenKey, NavGuard guard)
        {
            var m = UIFrameManager.Resolve();
            if (m == null) { WarnNoManager(); return; }
            m.AddGuard(screenKey, guard);
        }

        /// <summary>Guard EVERY navigation (auth, feature flags …). Runs before per-screen guards.</summary>
        public static void AddGlobalGuard(NavGuard guard)
        {
            var m = UIFrameManager.Resolve();
            if (m == null) { WarnNoManager(); return; }
            m.AddGlobalGuard(guard);
        }

        public static bool RemoveGuard<TFrame>(NavGuard guard) where TFrame : FigForgeFrame
        {
            var key = KeyOf(typeof(TFrame));
            var m = UIFrameManager.Resolve();
            return !string.IsNullOrEmpty(key) && m != null && m.RemoveGuard(key, guard);
        }

        public static bool RemoveGuard(FigForgeFrame frame, NavGuard guard)
        {
            var m = UIFrameManager.Resolve();
            return frame != null && m != null && m.RemoveGuard(frame.ScreenKey, guard);
        }

        public static bool RemoveGuard(string screenKey, NavGuard guard)
        {
            var m = UIFrameManager.Resolve();
            return m != null && m.RemoveGuard(screenKey, guard);
        }

        public static bool RemoveGlobalGuard(NavGuard guard)
        {
            var m = UIFrameManager.Resolve();
            return m != null && m.RemoveGlobalGuard(guard);
        }

        public static void ClearGuards() => UIFrameManager.Resolve()?.ClearGuards();

        /// <summary>React to a blocked navigation (alternative to the FigForgeNavBlockedHandler component).</summary>
        public static void AddBlockedHandler(Action<NavContext, NavDecision> handler)
            => UIFrameManager.Resolve()?.AddBlockedHandler(handler);

        public static void RemoveBlockedHandler(Action<NavContext, NavDecision> handler)
            => UIFrameManager.Resolve()?.RemoveBlockedHandler(handler);

        // ---- type -> screen key (reads the generated `public const string Key`) --------
        static readonly Dictionary<Type, string> _keyCache = new Dictionary<Type, string>();

        static string KeyOf(Type frameType)
        {
            if (frameType == null) return "";
            if (_keyCache.TryGetValue(frameType, out var cached)) return cached;

            var field = frameType.GetField("__ScreenKey",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            var key = field != null ? field.GetValue(null) as string : null;
            if (string.IsNullOrEmpty(key))
                Debug.LogWarning($"[FigForge] FigForgeNavigation: frame type '{frameType.Name}' has no generated " +
                                 "'__ScreenKey' const — guard not registered. Re-import the frame to regenerate " +
                                 "accessors, or register with AddGuard(frame, ...) / AddGuard(\"key\", ...).");
            _keyCache[frameType] = key ?? "";
            return key ?? "";
        }

        static void WarnNoManager()
            => Debug.LogWarning("[FigForge] FigForgeNavigation: no active UIFrameManager — register guards after the " +
                                "scene loads (e.g. in a component's Start()).");
    }
}
