using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace IOSPort
{
    // The game sets a script execution order on some classes in its precompiled DLLs, but the ripped project only
    // keeps the orders of the game's own scripts (in their .cs.meta files), so these ran at 0. tk2dSprite running
    // after other components broke anything that touches a sprite in Awake: a PlayMaker action that changes a
    // sprite's material (CharacterAmbientLightLerp) threw while its FSM started, leaving that FSM dead (sleeping
    // Choristers in Song_20 that never woke). Values read from the PC game's MonoScripts (globalgamemanagers.assets).
    public static class ExecutionOrders
    {
        static readonly (string assembly, string type, int order)[] Orders =
        {
            ("TeamCherry.Splines", "TeamCherry.Splines.SplineBase", 31990),
            ("TeamCherry.Splines", "TeamCherry.Splines.SplineBaseCallbackHooks", 31993),
            ("TeamCherry.TK2D", "tk2dAnimatedSprite", -30099),
            ("TeamCherry.TK2D", "tk2dBaseSprite", -31001),
            ("TeamCherry.TK2D", "tk2dCamera", -31115),
            ("TeamCherry.TK2D", "tk2dCameraAnchor", -31114),
            ("TeamCherry.TK2D", "tk2dFontData", -31050),
            ("TeamCherry.TK2D", "tk2dSlicedSprite", -30800),
            ("TeamCherry.TK2D", "tk2dSprite", -31000),
            ("TeamCherry.TK2D", "tk2dSpriteAnimator", -30095),
            ("TeamCherry.TK2D", "tk2dSpriteAnimatorCallbackHooks", -30093),
            ("TeamCherry.TK2D", "tk2dSpriteCollectionData", -31100),
            ("TeamCherry.TK2D", "tk2dStaticSpriteBatcher", -30500),
            ("TeamCherry.TK2D", "tk2dTextMesh", -30090),
            ("TeamCherry.TK2D", "tk2dTileMap", -30085),
            ("TeamCherry.TK2D", "tk2dTiledSprite", -30800),
            ("TeamCherry.TK2D", "tk2dUpdateManager", 31000),
            ("Unity.InputSystem", "UnityEngine.InputSystem.PlayerInput", -100),
            ("Unity.TextMeshPro", "TMPro.TextContainer", -110),
            ("Unity.TextMeshPro", "TMPro.TextMeshPro", -105),
            ("Unity.TextMeshPro", "TMPro.TextMeshProUGUI", -100),
            ("UnityEngine.UI", "UnityEngine.EventSystems.EventSystem", -1000),
            ("UnityEngine.UI", "UnityEngine.UI.ToggleGroup", 10),
        };

        public static void Apply()
        {
            var scripts = MonoImporter.GetAllRuntimeMonoScripts();
            int set = 0, missing = 0;
            foreach (var (assembly, type, order) in Orders)
            {
                var script = scripts.FirstOrDefault(s =>
                {
                    var c = s != null ? s.GetClass() : null;
                    return c != null && c.FullName == type && c.Assembly.GetName().Name == assembly;
                });
                if (script == null)
                {
                    missing++;
                    Debug.LogWarning($"[IOSPort] execution order: {type} ({assembly}) not found");
                    continue;
                }
                if (MonoImporter.GetExecutionOrder(script) == order)
                    continue;
                try
                {
                    MonoImporter.SetExecutionOrder(script, order);
                    set++;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[IOSPort] execution order: couldn't set {type}: {e.Message}");
                }
            }
            Debug.Log($"[IOSPort] execution orders: {set} set, {Orders.Length - set - missing} already right, {missing} not found");
        }
    }
}
